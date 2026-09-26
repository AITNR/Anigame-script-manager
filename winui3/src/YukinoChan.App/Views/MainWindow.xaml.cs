// -*- coding: utf-8 -*-
using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 拖拽改大小/挪位置时 AppWindow.Changed 会连续触发，这里等手停下来再落盘，
    /// 免得一次拖拽往 config.json 写几十遍。
    /// </summary>
    private const int PlacementSaveDebounceMs = 800;

    private readonly DispatcherQueueTimer _placementSaveTimer;

    public MainWindow()
    {
        InitializeComponent();

        Title = "雪乃酱 / 二游脚本助手";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        // 记忆上次窗口大小：先把上次记下的位置/尺寸恢复到当前显示器工作区内，
        // 之后再监听变化持续记录（恢复过程本身不触发写盘，注册顺序即是这个意思）。
        RestoreWindowPlacement();

        _placementSaveTimer = DispatcherQueue.CreateTimer();
        _placementSaveTimer.Interval = TimeSpan.FromMilliseconds(PlacementSaveDebounceMs);
        _placementSaveTimer.IsRepeating = false;
        _placementSaveTimer.Tick += (_, _) => PersistWindowPlacement();

        AppWindow.Changed += OnAppWindowChanged;

        try
        {
            var iconPath = System.IO.Path.Combine(Services.AppPaths.AssetsDir, "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // 图标缺失不应阻止启动
        }

        VM.ShutdownPromptRequested += OnShutdownPromptRequested;
        VM.RequestNavigation += (_, tag) => NavigateTo(tag);

        // 会话通道（M4 / 计划书 §8.1）：开始执行后左侧菜单动态插入每个通道一项，
        // 结束后保留（还能切回去看画面与事件），只有新一轮执行重建时才整体换掉。
        VM.SessionsChanged += (_, _) => RebuildChannelMenuItems();

        // M5：窗口关闭时释放内嵌连接（ycn_rdp_disconnect 语义 = 会话保留；原生事件循环线程须退出，
        // 否则非后台线程会拖住进程退出）
        Closed += (_, _) =>
        {
            // 关窗时把最后一次几何同步写掉：防抖定时器很可能还来不及触发
            _placementSaveTimer.Stop();
            PersistWindowPlacement();
            VM.DisposeEmbedClient();
        };

        // 激活态跟踪 + 错过通知的补发：本地多用户 RDP 接管期间主控端会话被锁，
        // 锁定会话不弹 Toast 横幅（通知只进操作中心）。
        // 完成时 MainViewModel 会把通知落盘（PendingNotificationStore），
        // 这里在窗口重新激活（用户切回来 / 应用重启）时消费补发。
        Activated += OnWindowActivated;

        if (Content is FrameworkElement root)
        {
            root.KeyDown += OnRootKeyDown;
        }

        NavigateTo("home");

        // M5/M6 自检通道：自动导航到「会话通道」页（页面 Loaded 里自动连接、画面挂在本页）
        if (Program.EmbedVmArgs is not null)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => NavigateTo("channels"));
        }
    }

    public MainViewModel VM => App.ViewModel;

    /// <summary>窗口当前是否处于激活态（会话锁定时会失活）。</summary>
    public static bool IsWindowActive { get; private set; }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        IsWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;

        if (IsWindowActive)
        {
            VM.PresentPendingNotification();
        }
    }

    // ---------------- 窗口位置 / 尺寸记忆 ----------------

    /// <summary>
    /// 按 config.json 里记录的几何恢复窗口；首次运行（没记录过）则用默认尺寸并在主屏居中。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var saved = VM.Config.Window;

        // 记过位置就按那个坐标找显示器（多屏场景），否则用主屏
        DisplayArea? area = null;
        if (saved.X is not null && saved.Y is not null)
        {
            area = DisplayArea.GetFromPoint(
                new PointInt32(saved.X.Value, saved.Y.Value),
                DisplayAreaFallback.Nearest);
        }

        area ??= DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

        var work = area?.WorkArea ?? default;
        var placement = WindowPlacement.Resolve(
            saved.X,
            saved.Y,
            saved.Width,
            saved.Height,
            new WindowPlacement.Bounds(work.X, work.Y, work.Width, work.Height));

        AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));

        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        // 兜住"手一抖把窗口拖到 400x300"这种再也调不回来的情况；
        // 小屏上最小值不能超过工作区，否则窗口会被强制撑出屏幕。
        if (work.Width > 0)
        {
            presenter.PreferredMinimumWidth = Math.Min(WindowPlacement.MinWidth, work.Width);
        }

        if (work.Height > 0)
        {
            presenter.PreferredMinimumHeight = Math.Min(WindowPlacement.MinHeight, work.Height);
        }

        if (saved.Maximized)
        {
            presenter.Maximize();
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange)
        {
            return;
        }

        // 重复调用即刷新倒计时：手停下 PlacementSaveDebounceMs 后才真正写盘
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    /// <summary>
    /// 把当前窗口几何交给 ViewModel 落盘。
    /// 最小化状态的坐标是假的（约 -32000），跳过；最大化/全屏时只记"是不是最大化"，
    /// 保留上一次的还原尺寸。
    /// </summary>
    private void PersistWindowPlacement()
    {
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;

            switch (presenter?.State)
            {
                case OverlappedPresenterState.Minimized:
                    return;
                case OverlappedPresenterState.Maximized:
                    VM.SaveWindowPlacement(null, maximized: true);
                    return;
                default:
                    var position = AppWindow.Position;
                    var size = AppWindow.Size;
                    VM.SaveWindowPlacement(
                        new WindowPlacement.Placement(position.X, position.Y, size.Width, size.Height),
                        maximized: false);
                    return;
            }
        }
        catch
        {
            // 记窗口几何失败不该影响用户操作
        }
    }

    private string _currentTag = string.Empty;

    /// <summary>左侧菜单里「会话通道」分组与各项用的 tag 前缀（M4）。</summary>
    private const string ChannelTagPrefix = "ch:";

    /// <summary>
    /// 本轮各通道所在分组的标题。
    /// 刻意不叫「会话通道」—— 那是上面那个静态管理项的文案，两个同名会让人以为点错了。
    /// </summary>
    private const string ChannelMenuHeaderText = "执行中的通道";

    /// <summary>
    /// 按本轮会话重建左侧菜单里的通道项。
    ///
    /// 为什么不在 XAML 里绑集合：菜单项要"插到指定位置 + 带分组标题 + 结束后保留"，
    /// 由窗口统一重建比双向同步可靠得多，也避开 NavigationView 选中态与集合变更的竞态。
    /// </summary>
    private void RebuildChannelMenuItems()
    {
        RemoveChannelMenuItems();

        var channels = VM.Sessions.Where(s => s.IsRemote).ToList();
        if (channels.Count == 0)
        {
            return;
        }

        // 紧跟在「会话通道」（管理/预检页）后面：那里已经从画面页变成了通道配置页
        var anchor = -1;
        for (var i = 0; i < NavView.MenuItems.Count; i++)
        {
            if (NavView.MenuItems[i] is NavigationViewItem item
                && item.Tag is string tag
                && tag == "channels")
            {
                anchor = i;
                break;
            }
        }

        var insertAt = anchor >= 0 ? anchor + 1 : NavView.MenuItems.Count;

        NavView.MenuItems.Insert(insertAt, new NavigationViewItemHeader { Content = ChannelMenuHeaderText });
        insertAt++;

        foreach (var session in channels)
        {
            var item = new NavigationViewItem
            {
                Tag = ChannelTagPrefix + session.ChannelId,
                Content = session.DisplayName,
                Icon = new SymbolIcon(Symbol.Link),
            };

            ToolTipService.SetToolTip(item, $"{session.TargetHost} / {session.TargetUser}");
            NavView.MenuItems.Insert(insertAt, item);
            insertAt++;
        }
    }

    private void RemoveChannelMenuItems()
    {
        for (var i = NavView.MenuItems.Count - 1; i >= 0; i--)
        {
            var item = NavView.MenuItems[i];

            var isChannelItem = item is NavigationViewItem nav
                && nav.Tag is string tag
                && tag.StartsWith(ChannelTagPrefix, StringComparison.Ordinal);

            var isHeader = item is NavigationViewItemHeader header
                && header.Content is string text
                && text == ChannelMenuHeaderText;

            if (isChannelItem || isHeader)
            {
                NavView.MenuItems.RemoveAt(i);
            }
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        if (string.Equals(_currentTag, tag, StringComparison.Ordinal))
        {
            return;
        }

        _currentTag = tag;

        // 会话通道页：把通道 id 作为导航参数带过去
        if (tag.StartsWith(ChannelTagPrefix, StringComparison.Ordinal))
        {
            ContentFrame.Navigate(typeof(Views.RdpChannelPage), tag[ChannelTagPrefix.Length..]);
            VM.UpdateMascotForPage("channel");
            SyncNavigationSelection(tag);
            return;
        }

        switch (tag)
        {
            case "tasks":
                ContentFrame.Navigate(typeof(Views.TasksPage));
                break;
            case "stats":
                ContentFrame.Navigate(typeof(Views.StatsPage));
                break;
            case "logs":
                ContentFrame.Navigate(typeof(Views.LogsPage));
                break;
            case "channels":
                ContentFrame.Navigate(typeof(Views.ChannelsPage));
                break;
            case "settings":
                ContentFrame.Navigate(typeof(Views.SettingsPage));
                break;
            default:
                ContentFrame.Navigate(typeof(Views.HomePage));
                break;
        }

        VM.UpdateMascotForPage(tag);
        SyncNavigationSelection(tag);
    }

    private void SyncNavigationSelection(string tag)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string itemTag && itemTag == tag)
            {
                NavView.SelectedItem = navItem;
                return;
            }
        }

        foreach (var item in NavView.FooterMenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string itemTag && itemTag == tag)
            {
                NavView.SelectedItem = navItem;
                return;
            }
        }
    }

    // ---------------- 控制栏 ----------------

    private void OnStartClicked(object sender, RoutedEventArgs e) => VM.Start();

    private void OnPauseClicked(object sender, RoutedEventArgs e) => VM.Pause();

    private void OnResumeClicked(object sender, RoutedEventArgs e) => VM.Resume();

    private void OnStopClicked(object sender, RoutedEventArgs e) => VM.Stop();

    private void OnEmergencyStopClicked(object sender, RoutedEventArgs e) => VM.EmergencyStop();

    private void OnSaveClicked(object sender, RoutedEventArgs e) => VM.SaveConfig();

    private void OnCancelShutdownClicked(object sender, RoutedEventArgs e) => VM.CancelShutdown();

    private async void OnShutdownPromptRequested(object? sender, int delaySeconds)
    {
        var dialog = new Views.ShutdownCountdownDialog(delaySeconds)
        {
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            VM.CancelShutdown();
        }
    }

    // ---------------- 快捷键：F8 停止 / Ctrl+Alt+F8 紧急停止 ----------------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.F8)
        {
            if (ctrl && alt)
            {
                VM.EmergencyStop();
            }
            else
            {
                VM.Stop();
            }

            e.Handled = true;
        }
    }
}
