// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Models;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class RdpPage : Page
{
    public RdpPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 画面控件诊断直接进运行日志（两条连接路径共用，便于无人值守验收）
        EmbedView.DiagnosticLog = VM.AppendLog;

        // M5：VM 的内嵌连接实例变化 → 画面控件接管/交还（事件可能在任意线程触发）
        VM.EmbedClientChanged += OnEmbedClientChanged;
    }

    private void OnEmbedClientChanged()
    {
        // 事件可能来自原生线程：调度到 UI 线程再碰控件
        var enqueued = DispatcherQueue.TryEnqueue(() => ApplyEmbedClientState());
        _ = enqueued;
    }

    /// <summary>把 VM 当前内嵌连接状态同步到本页画面与按钮（新建/切回页面共用）。</summary>
    private void ApplyEmbedClientState()
    {
        var client = VM.ActiveEmbedClient;
        if (client is not null)
        {
            // 先退订再订阅：AttachClient 可能对同一 view 实例多次发生（重连链）
            EmbedView.FullScreenToggleRequested -= OnFullScreenToggleRequested;
            EmbedView.FullScreenToggleRequested += OnFullScreenToggleRequested;
            EmbedView.AttachClient(client);
            EmbedConnectButton.IsEnabled = false;
            EmbedDisconnectButton.IsEnabled = true;
            EmbedFullScreenButton.IsEnabled = true;
            EmbedStatusText.Text = "已接管内嵌画面。";
            StartEmbedStats();
        }
        else
        {
            // 只解除画面绑定，不断开会话（会话生命周期归 VM 管）
            EmbedView.DetachClient();
            EmbedConnectButton.IsEnabled = true;
            EmbedDisconnectButton.IsEnabled = false;
            EmbedFullScreenButton.IsEnabled = false;
            _embedStatsTimer?.Stop();
            EmbedStatusText.Text = "内嵌连接已断开（目标会话保留）。";
            if (_fullScreen)
            {
                ExitFullScreen();
            }
        }
    }

    private void OnFullScreenToggleRequested(object? sender, EventArgs e) => ToggleFullScreen();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面切换（Frame.Navigate 每次新建页面实例）：解除画面绑定与事件订阅。
        // 内嵌连接归 VM 持有，不受页面生命周期影响；切回本页时 OnLoaded 重新接管。
        VM.EmbedClientChanged -= OnEmbedClientChanged;
        EmbedView.FullScreenToggleRequested -= OnFullScreenToggleRequested;
        EmbedView.DetachClient();
        _embedStatsTimer?.Stop();
    }

    public MainViewModel VM => App.ViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 画面宿主固定在 MainWindow，本页只负责发指令。
        SyncHostHint();
        VM.RefreshRdpReadiness();
        SyncReadinessSeverity();
        SyncResolutionHint();

        // M6：页面重建（切换页面）后恢复画面——连接归 VM 持有从未断开，
        // 这里只是把新页面的画面控件重新接管上来（这就是「切页不断连」的关键）
        if (VM.ActiveEmbedClient is not null)
        {
            ApplyEmbedClientState();
        }

        // M5/M6 自检通道：--embed-vm 走正式 VM 连接路径（client_mode=embedded →
        // ConnectSurface → TryStartEmbeddedConnect → EmbedClientChanged → 画面接管）
        var autoVm = Program.EmbedVmArgs;
        if (autoVm is { Length: 3 })
        {
            CollapseNonEmbedSections();
            VM.RdpSettings.TargetHost = autoVm[0];
            HostInput.Text = autoVm[0];
            VM.RdpSettings.TargetUser = autoVm[1];
            VM.RdpSettings.ClientMode = ClientModes.Embedded;
            RdpCredentialStore.Write(autoVm[0], autoVm[1], autoVm[2]);
            _ = DispatcherQueue.TryEnqueue(() => VM.ConnectSurface());
        }
    }

    /// <summary>自检模式：折叠预览卡片以外的全部内容，画面直接进视口。</summary>
    private void CollapseNonEmbedSections()
    {
        if (EmbedCard.Parent is Microsoft.UI.Xaml.Controls.Panel root)
        {
            foreach (var child in root.Children)
            {
                if (child is Microsoft.UI.Xaml.FrameworkElement fe && !ReferenceEquals(child, EmbedCard))
                {
                    fe.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    /// <summary>把当前分辨率档位显示在标题右边。</summary>
    private void SyncResolutionHint() => ResolutionHint.Text = VM.RdpResolutionLabel;

    /// <summary>换档位时同步标题旁的文案，并提示需要重连才生效。</summary>
    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 页面初始化时 SelectedValue 绑定会触发一次，此时没实际改动，日志侧会忽略
        SyncResolutionHint();
    }

    // ---- 画面控制：全部转发给 ViewModel，宿主由主窗口负责 ----

    private void OnConnectSurface(object sender, RoutedEventArgs e)
    {
        VM.ConnectSurface();
        SyncReadinessSeverity();
        SyncResolutionHint();
    }

    private void OnDisconnectSurface(object sender, RoutedEventArgs e)
    {
        VM.DisconnectSurface();
        SyncReadinessSeverity();
        SyncResolutionHint();
    }

    /// <summary>把地址填回本机环回，并同步提示文案。</summary>
    private void OnUseLocalHost(object sender, RoutedEventArgs e)
    {
        VM.RdpSettings.TargetHost = RdpTargets.DefaultHost;
        HostInput.Text = RdpTargets.DefaultHost;
        SyncHostHint();
        VM.RefreshRdpReadiness();
        SyncReadinessSeverity();
    }

    private void OnHostTextChanged(object sender, TextChangedEventArgs e) => SyncHostHint();

    /// <summary>告诉用户当前填的地址是本机还是远程，以及各自的注意事项。</summary>
    private void SyncHostHint()
    {
        var host = RdpTargets.Normalize(VM.RdpSettings.TargetHost);
        HostHint.Text = RdpTargets.IsLocal(host)
            ? $"当前是本机（{host}）：连接后会切到本机的另一个 Windows 账户，账户与会话都能在这里直接查验。"
            : $"当前是远程主机（{host}）：对方机器也要装雪乃酱、开启远程桌面、部署会话代理，并且指令桥目录要指向双方都能读写的共享位置。";
    }

    /// <summary>预检正常就显示绿色，有阻塞项就显示警告色。</summary>
    private void SyncReadinessSeverity()
    {
        var text = VM.RdpReadinessText;
        ReadinessBar.Severity = text.Contains("就绪") ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
    }

    private void OnSaveCredential(object sender, RoutedEventArgs e)
    {
        VM.SaveRdpCredential(PasswordInput.Password);
        PasswordInput.Password = string.Empty;
        SyncReadinessSeverity();
    }

    private void OnClearCredential(object sender, RoutedEventArgs e)
    {
        VM.ClearRdpCredential();
        SyncReadinessSeverity();
    }

    private void OnEnableRemoteDesktop(object sender, RoutedEventArgs e) => VM.EnableRemoteDesktopCommand.Execute(null);

    private void OnDeployAgent(object sender, RoutedEventArgs e) => VM.DeployAgentCommand.Execute(null);

    private void OnRemoveAgent(object sender, RoutedEventArgs e) => VM.RemoveAgentCommand.Execute(null);

    private void OnRefreshReadiness(object sender, RoutedEventArgs e)
    {
        VM.RefreshRdpReadiness();
        SyncReadinessSeverity();
    }

    private void OnOpenBridgeFolder(object sender, RoutedEventArgs e) => VM.OpenBridgeFolderCommand.Execute(null);

    // ---- 内嵌画面连接（embedded 模式下与「连接目标账户」等效，统一走 VM 路径）----
    // M6 修复：连接生命周期归 VM（页面切换不断开），连接参数与「连接目标账户」同源，
    // 重连必然连回同一目标用户——不再存在页面私有 client

    private void OnEmbedConnect(object sender, RoutedEventArgs e)
    {
        if (!VM.IsEmbeddedMode)
        {
            EmbedStatusText.Text = "当前是 mstsc 独立窗口模式；要内嵌画面请先把连接方式切到「内嵌」。";
            return;
        }
        VM.ConnectSurface();
    }

    /// <summary>画面帧数/键盘计数状态栏（每秒刷新）。</summary>
    private void StartEmbedStats()
    {
        _embedStatsTimer?.Stop();
        _embedStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _embedStatsTimer.Tick += (_, _) =>
            EmbedStatusText.Text = $"画面 {EmbedView.FramesRendered} 帧 | 键盘 {EmbedView.KeysForwarded} 键";
        _embedStatsTimer.Start();
    }

    // ---- M3：全屏 + WH_KEYBOARD_LL 键盘接管 ----

    private LowLevelKeyboardHook? _kbdHook;
    private RdpFullScreenWindow? _fullWindow;
    private bool _fullScreen;
    private DispatcherTimer? _embedStatsTimer;

    private void OnEmbedFullScreenToggle(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (_fullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        if (_fullScreen || _fullWindow is not null)
        {
            return;
        }

        var client = EmbedView.CurrentClient;
        if (client is null)
        {
            return;
        }

        // 全屏窗口创建自己的 RdpView 接管同一会话（元素不跨窗口移动）
        _fullWindow = new RdpFullScreenWindow(client);
        _fullWindow.View.FullScreenToggleRequested += (_, _) => ToggleFullScreen();
        _fullWindow.Closed += (_, _) =>
        {
            // 用户用 Alt+F4/系统关闭时也要摘钩子并归还画面（统一出口）
            _fullWindow?.View.DetachClient();
            if (_fullScreen)
            {
                _fullScreen = false;
                _kbdHook?.Remove();
                VM.AppendLog("内嵌预览退出全屏（窗口关闭），键盘接管已停用");
            }
            _fullWindow = null;
            EmbedFullScreenButton.Content = "全屏";
        };
        _fullWindow.Activate();

        // 页面内渲染器挂起，避免全屏期间双路渲染（CPU 翻倍）
        EmbedView.SuspendRendering();

        _kbdHook ??= new LowLevelKeyboardHook(InterceptFullScreenKey, VM.AppendLog);
        _kbdHook.Install();
        _fullScreen = true;
        EmbedFullScreenButton.Content = "退出全屏";
        EmbedStatusText.Text = "全屏中：键盘全部转投远端（F11 退出）";
        VM.AppendLog("内嵌预览进入全屏，键盘接管已启用");
    }

    private void ExitFullScreen()
    {
        if (!_fullScreen)
        {
            return;
        }

        // 钩子纪律：退出全屏立即摘钩（§5.3.1）
        _kbdHook?.Remove();
        _fullScreen = false;

        var w = _fullWindow;
        _fullWindow = null;
        // Closed 回调里完成 DetachClient + 状态复位（统一出口）
        w?.Close();

        EmbedView.ResumeRendering();
        EmbedFullScreenButton.Content = "全屏";
        EmbedStatusText.Text = "已退出全屏。";
        VM.AppendLog("内嵌预览退出全屏，键盘接管已停用");
    }

    /// <summary>LL 钩子回调（UI 线程）：全屏期间所有键转投远端；返回 true 拦截本地。</summary>
    private bool InterceptFullScreenKey(ulong virtualKey, ushort scanCode, bool extended, bool isDown)
    {
        if (!_fullScreen)
        {
            return false;
        }

        // F11（0x7A）：退出全屏。down 触发一次，up 只拦不放
        if (virtualKey == 0x7A)
        {
            if (isDown)
            {
                var enqueued = DispatcherQueue.TryEnqueue(ExitFullScreen);
            }
            return true;
        }

        // 其余键（含 Win / Alt+Tab / Ctrl+Esc）：转投远端并拦截本地。
        // LL 钩子带扫描码与扩展标志，与页面内 KeyDown 走同一张映射表
        var (sc, ext) = RdpInputMapper.MapKey(scanCode, extended, virtualKey);
        if (sc == 0)
        {
            return false; // 未知键放行（避免本地失灵）
        }
        return EmbedView.SendKey(isDown, ext, sc);
    }

    private void OnEmbedDisconnect(object sender, RoutedEventArgs e)
    {
        EmbedView.Disconnect();
        EmbedConnectButton.IsEnabled = true;
        EmbedDisconnectButton.IsEnabled = false;
        EmbedStatusText.Text = "已断开。";
    }
}
