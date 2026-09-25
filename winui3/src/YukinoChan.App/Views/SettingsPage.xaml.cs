// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Helpers;
using YukinoChan.Models;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class SettingsPage : Page
{
    private bool _syncing;

    public SettingsPage()
    {
        InitializeComponent();

        _syncing = true;
        try
        {
            ShutdownDelayBox.Value = VM.Config.ShutdownDelaySeconds;

            var theme = VM.Config.Theme;
            var index = 0;
            var found = 0;
            foreach (var item in AppConfig.ThemeItems)
            {
                if (item.Key == theme)
                {
                    found = index;
                    break;
                }

                index++;
            }

            ThemeBox.SelectedIndex = found;
        }
        finally
        {
            _syncing = false;
        }

        VersionText.Text = $"开发基线：v2.0 WinUI 3 · .NET {Environment.Version}";
    }

    public MainViewModel VM => App.ViewModel;

    public IReadOnlyList<KeyValuePair<string, string>> ThemeItems => AppConfig.ThemeItems;

    private void OnShutdownDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        VM.Config.ShutdownDelaySeconds = (int)Math.Round(double.IsNaN(args.NewValue) ? 60 : args.NewValue);
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ThemeBox.SelectedItem is not KeyValuePair<string, string> item)
        {
            return;
        }

        VM.Config.Theme = item.Key;
        ApplyTheme(item.Key);
        VM.SaveConfig();
        await DialogHelper.ShowMessageAsync("主题", $"已切换到「{item.Value}」。");
    }

    private static void ApplyTheme(string theme)
    {
        if (App.MainWindow?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void OnSave(object sender, RoutedEventArgs e) => VM.SaveConfig();

    private async void OnExport(object sender, RoutedEventArgs e) => await VM.ExportConfigAsync();

    private async void OnImport(object sender, RoutedEventArgs e) => await VM.ImportConfigAsync();

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.BaseDir);

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.LogDir);

    private void OnOpenStatsFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.StatsDir);

    private async void OnSponsor(object sender, RoutedEventArgs e)
    {
        await DialogHelper.ShowMessageAsync(
            "赞赏支持",
            "感谢喜欢雪乃酱～\n\n如果它确实帮你省下了时间，可以在 Bilibili 主页找到支持入口，或者到 GitHub 点个 Star、提个 Issue。\n\n每一份反馈都会变成下一个版本的动力。");
    }
}
