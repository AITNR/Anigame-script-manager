// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Models;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class RdpPage : Page
{
    public RdpPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public MainViewModel VM => App.ViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 画面宿主固定在 MainWindow，本页只负责发指令。
        SyncHostHint();
        VM.RefreshRdpReadiness();
        SyncReadinessSeverity();
        SyncResolutionHint();
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
}
