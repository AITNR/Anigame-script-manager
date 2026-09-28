// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Models;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class StatsPage : Page
{
    public StatsPage()
    {
        InitializeComponent();
        VM.RefreshStats();
    }

    public MainViewModel VM => App.ViewModel;

    private void OnRefresh(object sender, RoutedEventArgs e) => VM.RefreshStats();

    private void OnOpenFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.StatsDir);

    /// <summary>
    /// 打开桥目录根：会话通道的统计快照（stats.json）就落在各账户子目录里，
    /// 本机那份 runtime_stats 是空的时，排障要来这里看。
    /// </summary>
    private void OnOpenBridgeFolder(object sender, RoutedEventArgs e) =>
        VM.OpenDirectory(RdpChannelPaths.BridgeRoot());
}
