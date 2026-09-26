// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using YukinoChan.Models;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

/// <summary>
/// 一条会话通道的页面：画面（单画面 / 多画面网格）+ 该通道自己的进度 / 心跳 / 事件。
///
/// 状态直接取自 <see cref="RdpChannelSession"/>（每通道一份，不是全局单例），
/// 所以两条通道的页面各看各的，不会互相覆盖 —— 这正是 M3 把状态下沉的目的。
///
/// 画面（M6）：
/// <list type="bullet">
/// <item>内嵌连接按通道分家（每条通道各一个 client），所以**多路画面可以同时存在**；</item>
/// <item>「单画面」= 只摆本通道一格（占满，可全屏，键盘一并接管）；</item>
/// <item>「多画面」= 2×2 网格并列全部会话通道，每页最多 4 格（>4 分页）——
///       4 路是有意的同时渲染上限，再多 CPU/GPU 扛不住（计划书 §M6 风险项）；</item>
/// <item>每格可「弹出窗口」到独立普通窗口（用户自己并排摆放）。</item>
/// </list>
/// </summary>
public sealed partial class RdpChannelPage : Page, System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>多画面网格每页的格数（2×2）。</summary>
    private const int GridPageSize = 4;

    private readonly ObservableCollection<RdpTaskEvent> _emptyEvents = new();
    private readonly List<RdpSurfaceCell> _cells = new();

    private RdpChannelSession? _session;

    /// <summary>导航参数带来的通道 id（<c>_sessions</c> 里还没有时用它兜底）。</summary>
    private string _channelId = string.Empty;

    private DispatcherQueueTimer? _refreshTimer;

    /// <summary>本页是否还挂在可视树里（弹出窗口关闭回调据此决定要不要立即刷界面）。</summary>
    private bool _pageAlive;

    /// <summary>
    /// 全屏编排（全屏窗口 + 键盘接管 + 宿主渲染挂起）。
    /// 宿主固定为第 0 格 —— 单画面模式下它就是本通道那一格；
    /// 多画面模式下不给全屏（会盖掉别的路），用「弹出窗口」代替。
    /// </summary>
    private RdpFullScreenCoordinator? _fullScreen;

    /// <summary>当前是否多画面网格。</summary>
    private bool _multiView;

    /// <summary>多画面分页页码（0 起）。</summary>
    private int _gridPage;

    /// <summary>事件卡是否收起（收起后画面能多占 140px）。</summary>
    private bool _eventsCollapsed;

    /// <summary>
    /// 页面自身的属性变化通知。
    ///
    /// 这些绑定属性都是从 <see cref="_session"/> 现算的，页面不持有可变状态，
    /// 所以不逐个属性发通知 —— 1Hz 刷一拍、报"全体变了"（属性名传 null）就够了，
    /// 也顺手让 x:Bind 的 OneWay 变得名正言顺（否则编译器会报 WMC1506）。
    /// </summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public RdpChannelPage()
    {
        InitializeComponent();

        _cells.AddRange(new[] { Cell0, Cell1, Cell2, Cell3 });
        foreach (var cell in _cells)
        {
            cell.PopOutRequested += OnCellPopOut;
            cell.FullScreenRequested += OnCellFullScreen;
            cell.DisconnectRequested += OnCellDisconnect;
            cell.ConnectRequested += OnCellConnect;
        }

        // 画面上的双击 / F11 也走同一条全屏路径（浮层按钮只是显式入口）
        _fullScreen = new RdpFullScreenCoordinator(Cell0.View, App.ViewModel.AppendLog, OnFullScreenStateChanged);

        UpdateViewModeButtons();
    }

    private void RaiseAllChanged() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));

    private MainViewModel VM => App.ViewModel;

    // ---------------- 绑定属性（1Hz 由 Bindings.Update() 推送） ----------------

    /// <summary>本页对应的通道配置（<c>_sessions</c> 里没有时回退到配置）。</summary>
    private RdpChannel? Channel =>
        _session?.Channel
        ?? VM.Config.Rdp.Channels.FirstOrDefault(c =>
            string.Equals(c.Id, _channelId, StringComparison.OrdinalIgnoreCase));

    public string ChannelName =>
        _session?.DisplayName
        ?? (Channel?.DisplayName ?? (string.IsNullOrEmpty(_channelId) ? "会话通道" : _channelId));

    public string TargetText
    {
        get
        {
            var channel = Channel;
            if (channel is null)
            {
                return string.IsNullOrEmpty(_channelId)
                    ? "通道不存在或已从配置里删除。"
                    : $"通道「{_channelId}」已从配置里删除。";
            }

            var host = string.IsNullOrWhiteSpace(channel.Host) ? "本机" : channel.Host;
            var user = string.IsNullOrWhiteSpace(channel.User) ? "（未配账户）" : channel.User;
            var bridgeDir = _session?.Bridge.BridgeDir
                ?? RdpChannelPaths.Resolve(channel.BridgePath, channel.User);
            return $"{host}  /  {user}      桥目录：{bridgeDir}";
        }
    }

    public string StatusText => _session?.StatusText ?? VM.SurfaceStateText(_channelId);

    public string HeartbeatText => _session is null ? "－" : $"心跳：{_session.HeartbeatText}";

    public string TaskText => _session is null ? "任务：－" : $"任务：{_session.TaskDisplay}";

    public string ProgressText => _session is null ? "－" : $"{_session.Progress} / {_session.Total}";

    public string ElapsedText => _session?.ElapsedText ?? "00:00";

    public double ProgressValue => _session?.Progress ?? 0;

    public double ProgressMax => Math.Max(1, _session?.Total ?? 1);

    public ObservableCollection<RdpTaskEvent> Events => _session?.Events ?? _emptyEvents;

    // ---------------- 生命周期 ----------------

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _channelId = e.Parameter as string ?? string.Empty;
        _session = VM.Sessions.FirstOrDefault(s =>
            s.IsRemote && string.Equals(s.ChannelId, _channelId, StringComparison.OrdinalIgnoreCase));

        // 通道存在就以其 id 为准（大小写 / 空白归一）
        if (_session is not null)
        {
            _channelId = _session.ChannelId;
        }

        _pageAlive = true;

        // 默认单画面：进通道页的第一眼就该是本通道的大画面
        _multiView = false;
        _gridPage = 0;
        UpdateViewModeButtons();
        LayoutSurfaces();
        RaiseAllChanged();

        // 与主控端 1Hz 轮询同节奏刷新展示，避免每通道再开一套通知机制
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += (_, _) =>
        {
            LayoutSurfaces();
            RaiseAllChanged();
        };
        _refreshTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageAlive = false;
        _refreshTimer?.Stop();
        _refreshTimer = null;

        // 只解绑订阅、不结束全屏：全屏窗口是独立窗口，切页不该把它关掉
        _fullScreen?.Detach();

        // 只解挂、不断开：会话与画面流照旧，切回来（或弹出窗口）继续用同一个 client
        foreach (var cell in _cells)
        {
            cell.SetClient(null);
        }

        base.OnNavigatedFrom(e);
    }

    // ---------------- 画面网格 ----------------

    /// <summary>
    /// 按当前模式（单画面 / 多画面）摆放格位、挂上各通道的内嵌画面，并刷新角标。
    /// 每拍调用是安全的：<see cref="RdpSurfaceCell.SetClient"/> 对同一实例会短路，
    /// 不会把画面重挂成闪断。
    /// </summary>
    private void LayoutSurfaces()
    {
        var ids = ResolveSurfaceIds();

        var pageCount = Math.Max(1, (ids.Count + GridPageSize - 1) / GridPageSize);
        _gridPage = Math.Clamp(_gridPage, 0, pageCount - 1);

        var pageIds = _multiView
            ? ids.Skip(_gridPage * GridPageSize).Take(GridPageSize).ToList()
            : ids;

        foreach (var cell in _cells)
        {
            cell.Visibility = Visibility.Collapsed;
            cell.ShowFullScreenButton = !_multiView;
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];

            if (i >= pageIds.Count)
            {
                // 本页用不到的格位：解挂，别让它在后台白渲染
                cell.SetClient(null);
                continue;
            }

            var id = pageIds[i];

            cell.Assign(id, VM.ChannelDisplayName(id));
            PlaceCell(cell, i, pageIds.Count);

            // 画面已被独立窗口占用的通道：本格让位（两个渲染器不能挂同一个 client）
            cell.SetClient(VM.IsSurfacePoppedOut(id) ? null : VM.GetSurfaceClient(id));
            cell.UpdateChrome();
        }

        // 分页条：只在多画面且不止一页时出现
        PagerBar.Visibility = _multiView && pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"{_gridPage + 1} / {pageCount}";
        PrevPageButton.IsEnabled = _gridPage > 0;
        NextPageButton.IsEnabled = _gridPage < pageCount - 1;

        // 一路可显示的通道都没有时的空态
        var empty = ids.Count == 0;
        SurfaceEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            SurfaceEmptyText.Text = VM.IsEmbeddedMode
                ? "这条通道还没有画面。点上面的「连接本通道」建连，或者先开始执行任务。"
                : "当前客户端模式是「独立窗口（mstsc）」，画面在系统自带的远程桌面窗口里，应用内不显示。";
        }
    }

    /// <summary>当前该显示哪些通道的画面。</summary>
    private List<string> ResolveSurfaceIds()
    {
        if (_multiView)
        {
            return VM.SurfaceGridChannelIds.ToList();
        }

        return string.IsNullOrEmpty(_channelId) ? new List<string>() : new List<string> { _channelId };
    }

    /// <summary>把第 index 格放进 2×2 网格里合适的位置（1 / 2 / 3 格时自动跨行列）。</summary>
    private static void PlaceCell(RdpSurfaceCell cell, int index, int count)
    {
        switch (count)
        {
            case 1:
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, 0);
                Grid.SetRowSpan(cell, 2);
                Grid.SetColumnSpan(cell, 2);
                break;

            case 2:
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, index);
                Grid.SetRowSpan(cell, 2);
                Grid.SetColumnSpan(cell, 1);
                break;

            case 3 when index == 0:
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, 0);
                Grid.SetRowSpan(cell, 2);
                Grid.SetColumnSpan(cell, 1);
                break;

            case 3:
                Grid.SetRow(cell, index == 1 ? 0 : 1);
                Grid.SetColumn(cell, 1);
                Grid.SetRowSpan(cell, 1);
                Grid.SetColumnSpan(cell, 1);
                break;

            default:
                Grid.SetRow(cell, index / 2);
                Grid.SetColumn(cell, index % 2);
                Grid.SetRowSpan(cell, 1);
                Grid.SetColumnSpan(cell, 1);
                break;
        }

        cell.Visibility = Visibility.Visible;
    }

    // ---------------- 交互 ----------------

    private void UpdateViewModeButtons()
    {
        SingleViewButton.IsChecked = !_multiView;
        MultiViewButton.IsChecked = _multiView;
    }

    private void OnSingleViewClicked(object sender, RoutedEventArgs e)
    {
        _multiView = false;
        _gridPage = 0;
        UpdateViewModeButtons();
        LayoutSurfaces();
    }

    private void OnMultiViewClicked(object sender, RoutedEventArgs e)
    {
        _multiView = true;
        _gridPage = 0;
        UpdateViewModeButtons();
        LayoutSurfaces();
    }

    private void OnPrevPageClicked(object sender, RoutedEventArgs e)
    {
        _gridPage = Math.Max(0, _gridPage - 1);
        LayoutSurfaces();
    }

    private void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        _gridPage++;
        LayoutSurfaces();
    }

    private void OnConnectPreviewClicked(object sender, RoutedEventArgs e)
        => VM.ConnectChannelSurface(_channelId);

    private void OnOpenBridgeClicked(object sender, RoutedEventArgs e)
    {
        var dir = _session?.Bridge.BridgeDir
            ?? RdpChannelPaths.Resolve(Channel?.BridgePath, Channel?.User);

        var bridge = RdpBridge.For(dir);
        bridge.EnsureDirectory();
        bridge.OpenBridgeFolder();
    }

    private void OnCellConnect(object? sender, string channelId)
        => VM.ConnectChannelSurface(channelId);

    /// <summary>「断开」：只释放该通道的内嵌连接，目标会话与代理照常保留。</summary>
    private void OnCellDisconnect(object? sender, string channelId)
    {
        VM.DisposeSurface(channelId);
        LayoutSurfaces();
        RaiseAllChanged();
    }

    /// <summary>「全屏」：单画面下新窗口接管同一个内嵌连接（键盘一并接管，F11 / 双击画面退出）。</summary>
    private void OnCellFullScreen(object? sender, string channelId)
    {
        // 多画面下双击 / 全屏 = 把这一路弹成独立窗口（全屏会盖掉别的路）
        if (_multiView)
        {
            PopOutSurface(channelId);
            return;
        }

        _fullScreen?.Toggle();
    }

    /// <summary>「弹出窗口」：把这路画面交到一个普通独立窗口，用户自己并排摆放。</summary>
    private void OnCellPopOut(object? sender, string channelId) => PopOutSurface(channelId);

    private void PopOutSurface(string channelId)
    {
        var client = VM.GetSurfaceClient(channelId);
        if (client is null)
        {
            return;
        }

        if (VM.IsSurfacePoppedOut(channelId))
        {
            VM.AppendLog($"[通道：{VM.ChannelDisplayName(channelId)}] 这路画面已经在独立窗口里了。");
            return;
        }

        // 顺序要紧：先把本格让出来（解挂），再让新窗口接管同一个 client，
        // 否则两个渲染器会同时挂在一条会话上互相打架。
        VM.MarkSurfacePoppedOut(channelId, true);
        LayoutSurfaces();

        var window = new RdpChannelWindow(client, VM.ChannelDisplayName(channelId));

        window.Closed += (_, _) =>
        {
            // 同样先解挂、再归还，页面才会重新接管
            window.DetachView();
            VM.MarkSurfacePoppedOut(channelId, false);

            if (_pageAlive)
            {
                LayoutSurfaces();
            }
        };

        window.Activate();
        VM.AppendLog($"[通道：{VM.ChannelDisplayName(channelId)}] 画面已弹出到独立窗口。");
    }

    /// <summary>事件卡折叠/展开 —— 收起后画面能多吃 140px。</summary>
    private void OnToggleEventsClicked(object sender, RoutedEventArgs e)
    {
        _eventsCollapsed = !_eventsCollapsed;
        EventList.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ToggleEventsButton.Content = _eventsCollapsed ? "展开" : "收起";
    }

    /// <summary>全屏状态变化：切第 0 格的按钮文案（画面本身由协调器挂起/恢复渲染）。</summary>
    private void OnFullScreenStateChanged(bool active) => Cell0.SetFullScreenActive(active);
}
