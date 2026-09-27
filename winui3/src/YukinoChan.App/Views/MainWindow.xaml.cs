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

        // 会话通道（M4 / 计划书 §8.1）：「会话通道」是**父项**，下面挂子菜单 ——
        //   父项本身        = 多画面页（全部通道的网格）；
        //   子项「多画面」  = 同一个页面（给一个确定能点的入口）；
        //   子项「通道名」  = 该通道单独的画面页；
        //   子项「通道管理…」= 配置 / 凭据 / 部署代理 / 预检。
        // 子项按「配置里启用的通道 + 本轮在跑的通道」重建，配置一改就跟着变。
        VM.SessionsChanged += (_, _) => RebuildChannelMenuItems();
        VM.PropertyChanged += OnViewModelPropertyChanged;
        VM.Channels.CollectionChanged += (_, _) => RebuildChannelMenuItems();

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

        // 子菜单先按当前配置铺一遍（配置里已启用的通道一启动就该出现在「会话通道」下面）
        RebuildChannelMenuItems();

        // M5/M6 自检通道：自动导航到「通道管理」页（页面 Loaded 里自动连接、画面挂在本页）
        if (Program.EmbedVmArgs is not null)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => NavigateTo(ChannelMgmtTag));
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

    /// <summary>左侧菜单里各条通道项用的 tag 前缀（M4）。</summary>
    private const string ChannelTagPrefix = "ch:";

    /// <summary>
    /// 「通道管理…」子项 tag —— 配置 / 凭据 / 部署代理 / 预检（<see cref="Views.ChannelsPage"/>）。
    /// 刻意不再叫 "channels"：那个 tag 现在是「会话通道」父项（多画面）的。
    /// </summary>
    private const string ChannelMgmtTag = "channel-mgmt";

    /// <summary>
    /// 「多画面」子项 tag。与父项同一个页面，但**标签不同** ——
    /// 父项点击能否触发导航取决于 NavigationView 的内部处理（带子项时通常只展开），
    /// 留一个确定能点、且选中态回显不会和父项打架的入口。
    /// </summary>
    private const string MultiViewTag = "multiview";

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 通道配置整体换过（ReloadChannels 会 raise Channels）→ 子菜单跟着重建
        if (e.PropertyName == nameof(MainViewModel.Channels))
        {
            RebuildChannelMenuItems();
        }
    }

    /// <summary>
    /// 重建「会话通道」父项下的子菜单。
    ///
    /// 为什么不在 XAML 里绑集合：子项要先列配置通道、再补在跑的通道、末尾还要钉一个「通道管理…」，
    /// 由窗口统一重建比双向同步可靠得多，也避开 NavigationView 选中态与集合变更的竞态。
    /// </summary>
    private void RebuildChannelMenuItems()
    {
        // 可能来自后台线程（会话集合变化）——碰控件一律回 UI 线程
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RebuildChannelMenuItems);
            return;
        }

        var parent = ChannelsNavItem;

        parent.MenuItems.Clear();
        parent.MenuItems.Add(new NavigationViewItem
        {
            Tag = MultiViewTag,
            Content = "多画面（全部通道）",
            Icon = new SymbolIcon(Symbol.ViewAll),
        });

        foreach (var (id, name, tooltip) in EnumerateChannelMenuEntries())
        {
            var item = new NavigationViewItem
            {
                Tag = ChannelTagPrefix + id,
                Content = name,
                Icon = new SymbolIcon(Symbol.Link),
            };

            ToolTipService.SetToolTip(item, tooltip);
            parent.MenuItems.Add(item);
        }

        parent.MenuItems.Add(new NavigationViewItemSeparator());
        parent.MenuItems.Add(new NavigationViewItem
        {
            Tag = ChannelMgmtTag,
            Content = "通道管理…",
            Icon = new SymbolIcon(Symbol.Setting),
        });

        // 重建会把原来的项对象整体换掉 → 选中态需按 tag 复原，
        // 否则用户正停在某条通道页时，菜单会"莫名其妙没选中任何一项"。
        if (_currentTag.Length > 0)
        {
            SyncNavigationSelection(_currentTag);
        }
    }

    /// <summary>
    /// 菜子里该列哪些通道：① 配置里启用中的通道（不跑任务也能点进去手动连画面看）；
    /// ② 本轮在跑的通道（含配置里已删 / 已停用的 —— 在跑就必须看得见）。
    /// </summary>
    private List<(string Id, string Name, string Tooltip)> EnumerateChannelMenuEntries()
    {
        var entries = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in VM.Config.Rdp.Channels)
        {
            if (!channel.Enabled || string.IsNullOrWhiteSpace(channel.Id) || !seen.Add(channel.Id))
            {
                continue;
            }

            entries.Add((channel.Id, channel.DisplayName, $"{channel.Host} / {channel.User}"));
        }

        foreach (var session in VM.Sessions.Where(s => s.IsRemote))
        {
            if (!seen.Add(session.ChannelId))
            {
                continue;
            }

            entries.Add((
                session.ChannelId,
                session.DisplayName,
                $"{session.TargetHost} / {session.TargetUser} · {session.StatusText}"));
        }

        return entries;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    /// <summary>
    /// 带子项的父项（「会话通道」）被点击时**不改变选中态**，只会展开并触发这里。
    /// 不接这个事件的话，父项就真的"点不动"。
    /// （叶子项两个事件都会来，<see cref="NavigateTo"/> 按 tag 去重，不会重复导航。）
    /// </summary>
    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag && tag.Length > 0)
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

        // 单条通道页：把通道 id 作为导航参数带过去
        if (tag.StartsWith(ChannelTagPrefix, StringComparison.Ordinal))
        {
            ContentFrame.Navigate(typeof(Views.RdpChannelPage), tag[ChannelTagPrefix.Length..]);
            ChannelsNavItem.IsExpanded = true;
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
            case MultiViewTag:
                // 「会话通道」父项与它的「多画面」子项：同一个页面
                ContentFrame.Navigate(typeof(Views.RdpMultiViewPage));
                break;
            case ChannelMgmtTag:
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
        var target = FindNavItemByTag(NavView.MenuItems, tag)
            ?? FindNavItemByTag(NavView.FooterMenuItems, tag);

        if (target is not null)
        {
            NavView.SelectedItem = target;
        }
    }

    /// <summary>按 tag 找菜单项（含父项的子项 —— 通道项现在挂在「会话通道」下面）。</summary>
    private static NavigationViewItem? FindNavItemByTag(IEnumerable<object> items, string tag)
    {
        foreach (var item in items)
        {
            if (item is not NavigationViewItem nav)
            {
                continue;
            }

            if (nav.Tag is string itemTag && string.Equals(itemTag, tag, StringComparison.Ordinal))
            {
                return nav;
            }

            var nested = FindNavItemByTag(nav.MenuItems, tag);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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

        // 走统一入口：排队 + 吞异常，避免和别的对话框撞上直接抛 COMException 崩进程
        var result = await Helpers.DialogHelper.ShowAsync(dialog);
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
