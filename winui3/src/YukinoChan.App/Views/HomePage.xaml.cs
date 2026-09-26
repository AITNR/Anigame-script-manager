// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class HomePage : Page
{
    /// <summary>会话通道概览的刷新计时器（只在主页可见时跑）。</summary>
    private DispatcherQueueTimer? _overviewTimer;

    public HomePage()
    {
        InitializeComponent();
        ShowProblemReportIfNeeded();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MainViewModel VM => App.ViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VM.RefreshChannelOverview();
        Bindings.Update();

        // 通道会话的状态是不可观察的普通属性，靠 1Hz 重建快照让界面跟上。
        // 用主页自己的计时器（而不是挂到 VM 上）—— 离开主页就停，不白跑。
        _overviewTimer = DispatcherQueue.CreateTimer();
        _overviewTimer.Interval = TimeSpan.FromSeconds(1);
        _overviewTimer.IsRepeating = true;
        _overviewTimer.Tick += (_, _) =>
        {
            VM.RefreshChannelOverview();
            Bindings.Update();
        };
        _overviewTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _overviewTimer?.Stop();
        _overviewTimer = null;
    }

    private void ShowProblemReportIfNeeded()
    {
        var report = VM.LoadAbnormalReport();
        if (report is null)
        {
            ProblemReportBar.IsOpen = false;
            return;
        }

        ProblemReportText.Text = report.Summary;
        ProblemReportBar.IsOpen = true;
    }

    private void OnProblemReportClosed(InfoBar sender, object args) => VM.DismissAbnormalReport();

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.LogDir);

    private void OnOpenTasks(object sender, RoutedEventArgs e) => VM.Navigate("tasks");

    private void OnOpenStats(object sender, RoutedEventArgs e) => VM.Navigate("stats");

    private void OnOpenLogs(object sender, RoutedEventArgs e) => VM.Navigate("logs");

    /// <summary>点通道概览的一行 → 跳到那条通道的画面页（本地执行组 tag 为空，点了不跳）。</summary>
    private void OnOpenChannelRow(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && tag.Length > 0)
        {
            VM.Navigate(tag);
        }
    }
}
