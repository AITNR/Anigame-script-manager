// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

/// <summary>
/// 「多画面同屏」里的一格画面（M6）：通道名 + 状态 + 一块 <see cref="RdpView"/> + 无画面时的占位。
///
/// 通道页按「单画面 / 多画面」摆 1 格或最多 2×2 格，每格各挂自己那条通道的内嵌连接。
/// 之所以一通道一连接：原生侧一个 client 同一时刻只能挂一个渲染视图，
/// 让两条通道共用一个 client 会互相打断画面。
/// </summary>
public sealed partial class RdpSurfaceCell : UserControl
{
    private RdpEmbeddedClient? _client;
    private string _channelId = string.Empty;

    public RdpSurfaceCell()
    {
        InitializeComponent();

        // 画面上的双击 / F11 全屏请求 —— 带上本格通道 id，页面才知道该全屏哪一路
        SurfaceView.FullScreenToggleRequested += (_, _) => FullScreenRequested?.Invoke(this, _channelId);
    }

    /// <summary>本格对应的通道 id（空串 = 尚未分配）。</summary>
    public string ChannelId => _channelId;

    /// <summary>本格的画面控件（全屏协调器要用它当宿主）。</summary>
    public RdpView View => SurfaceView;

    /// <summary>本格当前挂着的连接；null = 无画面。</summary>
    public RdpEmbeddedClient? CurrentClient => _client;

    /// <summary>「弹出独立窗口」被点击。</summary>
    public event EventHandler<string>? PopOutRequested;

    /// <summary>全屏被请求（浮层按钮 / 双击画面 / F11）。</summary>
    public event EventHandler<string>? FullScreenRequested;

    /// <summary>「断开」被点击。</summary>
    public event EventHandler<string>? DisconnectRequested;

    /// <summary>占位里的「连接到这条通道」被点击。</summary>
    public event EventHandler<string>? ConnectRequested;

    /// <summary>本格是否显示「全屏」按钮（只有单画面模式才给 —— 多画面下用「弹出窗口」）。</summary>
    public bool ShowFullScreenButton
    {
        set => FullScreenButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>全屏状态变化（切按钮文案）。</summary>
    public void SetFullScreenActive(bool active)
        => FullScreenButton.Content = active ? "退出全屏" : "全屏";

    private static MainViewModel VM => App.ViewModel;

    /// <summary>把本格分配给某条通道（只设身份与标题，不碰画面）。</summary>
    public void Assign(string channelId, string displayName)
    {
        _channelId = channelId ?? string.Empty;
        TitleText.Text = displayName;
        TopBar.Visibility = _channelId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 挂上 / 解下画面。传 null 表示本格没有画面（占位会显示出来）。
    /// 同一个实例重复调用是空操作 —— 1Hz 刷新不会把画面重挂成闪断。
    /// </summary>
    public void SetClient(RdpEmbeddedClient? client)
    {
        if (ReferenceEquals(_client, client))
        {
            return;
        }

        _client = client;

        if (client is null)
        {
            SurfaceView.DetachClient();
        }
        else
        {
            SurfaceView.AttachClient(client);
        }

        UpdateChrome();
    }

    /// <summary>刷新状态文案与按钮可用性（1Hz 由页面调）。</summary>
    public void UpdateChrome()
    {
        var hasClient = _client is not null;

        Placeholder.Visibility = hasClient ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.IsEnabled = hasClient;
        PopOutButton.IsEnabled = hasClient;
        FullScreenButton.IsEnabled = hasClient;

        StateText.Text = VM.SurfaceStateText(_channelId);

        if (hasClient)
        {
            return;
        }

        if (_channelId.Length == 0)
        {
            PlaceholderText.Text = "这一格还没有分配通道。";
        }
        else if (VM.IsSurfacePoppedOut(_channelId))
        {
            PlaceholderText.Text = "这路画面正在独立窗口里显示。\n关掉那个窗口，画面会自动回到这里。";
        }
        else if (!VM.IsEmbeddedMode)
        {
            PlaceholderText.Text =
                "当前客户端模式是「独立窗口（mstsc）」，画面在系统自带的远程桌面窗口里，应用内不显示。\n" +
                "想在这里看到内嵌画面，请到「会话通道」页把客户端模式改为「内嵌」。";
        }
        else
        {
            PlaceholderText.Text = "这条通道还没有画面。\n点下面的按钮建连（开始执行时也会自动连上）。";
        }
    }

    private void OnConnectClicked(object sender, RoutedEventArgs e)
        => ConnectRequested?.Invoke(this, _channelId);

    private void OnPopOutClicked(object sender, RoutedEventArgs e)
        => PopOutRequested?.Invoke(this, _channelId);

    private void OnFullScreenClicked(object sender, RoutedEventArgs e)
        => FullScreenRequested?.Invoke(this, _channelId);

    private void OnDisconnectClicked(object sender, RoutedEventArgs e)
        => DisconnectRequested?.Invoke(this, _channelId);
}
