// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ScrollToBottom();
    }

    public MainViewModel VM => App.ViewModel;

    /// <summary>新日志进来时保持吸附在底部，方便观察实时输出。</summary>
    private void OnLogItemRendering(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemIndex >= VM.LogLines.Count - 1)
        {
            _ = DispatcherQueue.TryEnqueue(ScrollToBottom);
        }
    }

    private void ScrollToBottom()
    {
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => VM.ClearLogs();

    private void OnOpenFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.LogDir);
}
