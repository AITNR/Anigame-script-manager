// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        ShowProblemReportIfNeeded();
    }

    public MainViewModel VM => App.ViewModel;

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
}
