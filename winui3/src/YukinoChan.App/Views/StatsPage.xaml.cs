// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
}
