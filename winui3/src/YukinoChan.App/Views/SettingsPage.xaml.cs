// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private CancellationTokenSource? _themeHintCts;

    public SettingsPage()
    {
        InitializeComponent();

        // 离开页面就别再往回写提示控件了
        Unloaded += (_, __) => _themeHintCts?.Cancel();

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

    /// <summary>
    /// ⚠️ 这里**不能**用 ContentDialog 报"已切换"。
    /// 改窗口根元素的 <c>RequestedTheme</c> 会让 ComboBox 重新模板化，SelectionChanged 会再触发一次；
    /// 于是同一个交互里连着两次 ShowAsync —— 第二次必抛 COMException 0x80000019
    /// （"Only a single ContentDialog can be open at any time"）。调用方若是 <c>_ = …</c> 的
    /// fire-and-forget 写法，未观察异常直接掀掉进程：实测表现就是"知道了"点不动，过一会儿窗口崩掉。
    /// 切换效果本来就肉眼可见，反馈改成页面内的一行文字，几秒后自动收起。
    /// </summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ThemeBox.SelectedItem is not KeyValuePair<string, string> item)
        {
            return;
        }

        // 值没变 = 程序性重设（改 RequestedTheme 会让 ComboBox 重新模板化，进而再触发一次
        // SelectionChanged），别当成用户操作 —— 否则会重复落盘 + 重复提示。
        if (VM.Config.Theme == item.Key)
        {
            return;
        }

        _syncing = true;
        try
        {
            VM.Config.Theme = item.Key;
            ApplyTheme(item.Key);
            VM.SaveConfig();

            ShowThemeHint($"已切换到「{item.Value}」。");
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowThemeHint(string text)
    {
        ThemeHintText.Text = text;
        ThemeHintText.Visibility = Visibility.Visible;

        // 连点下拉框时只留最后一次的计时
        _themeHintCts?.Cancel();
        var cts = new CancellationTokenSource();
        _themeHintCts = cts;

        _ = HideThemeHintLaterAsync(cts);
    }

    private async Task HideThemeHintLaterAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);

            if (!cts.IsCancellationRequested)
            {
                ThemeHintText.Visibility = Visibility.Collapsed;
            }
        }
        catch (TaskCanceledException)
        {
            // 已被下一次切换取代
        }
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
