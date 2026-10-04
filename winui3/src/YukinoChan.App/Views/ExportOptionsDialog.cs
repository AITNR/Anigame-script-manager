// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Models;

namespace YukinoChan.Views;

/// <summary>
/// 「导出配置」的范围选择对话框（本体设置 / 任务设置 / 远程用户设置，含密码）。
///
/// 为什么用代码搭而不是独立 xaml 页面：ContentDialog 的内容要在 ShowAsync 之前构造好，
/// 独立页面还得处理 Connect 与 XamlRoot，收益不抵事（<see cref="ShutdownCountdownDialog"/>
/// 那种纯展示才值得开页面）。这里只需要几个复选框 + 一个互斥的密码项。
///
/// ⚠️ 密码那项勾上时会给一行醒目的红字提醒：导出文件是<b>明文密码</b>。
/// 用户点了"确定"就代表他清楚这件事，所以不再二次确认 —— 但提醒必须给。
/// </summary>
internal sealed class ExportOptionsDialog
{
    private readonly CheckBox _appSettings = new() { Content = "本体设置（主题、关机与退出策略、截图开关、窗口大小）" };
    private readonly CheckBox _tasks = new() { Content = "任务设置（脚本、参数、所属通道、并发组）", IsChecked = true };
    private readonly CheckBox _remote = new() { Content = "远程用户设置（RDP 主机、账户名、通道配置）", IsChecked = true };
    private readonly CheckBox _passwords = new() { Content = "一并导出密码（明文写在文件里）", IsChecked = true };
    private readonly TextBlock _warning = new();
    private readonly TextBlock _summary = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly ContentDialog _dialog;

    public ExportOptionsDialog()
    {
        // ⚠️ WinUI 3 的 CheckBox 只有 Checked / Unchecked 两个事件 ——
        // 既没有 UWP 那个 Toggled，也没有 IsCheckedChanged。
        // 写错事件名会报 CS1061，并**连带**带出一串 XamlCompiler WMC 错误
        // （RdpView / RdpChannel / ChannelOverviewItem 全都"解析不到"），
        // 那些 WMC 报错全是这一个 CS 错误的下游症状，别顺着去查 XAML。
        HookToggle(_appSettings, () => Refresh());
        HookToggle(_tasks, () => Refresh());
        HookToggle(_passwords, () => Refresh());
        HookToggle(_remote, () =>
        {
            // 不勾远程设置时，密码无从谈起 —— 直接禁用并取消勾选，
            // 免得导出一个"勾了密码但没有远程用户"的糊涂文件。
            _passwords.IsEnabled = _remote.IsChecked == true;
            if (_remote.IsChecked != true)
            {
                _passwords.IsChecked = false;
            }

            Refresh();
        });

        _warning.Text = "⚠ 勾了密码，导出文件里就是明文密码，任何拿到文件的人都能用它登录你的远程机器。";
        _warning.TextWrapping = TextWrapping.Wrap;
        _warning.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.Colors.IndianRed);
        _warning.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "这次想带走哪些内容？",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });
        panel.Children.Add(_appSettings);
        panel.Children.Add(_tasks);
        panel.Children.Add(_remote);
        panel.Children.Add(_passwords);
        panel.Children.Add(_warning);
        panel.Children.Add(_summary);

        _dialog = new ContentDialog
        {
            Title = "导出配置",
            Content = panel,
            PrimaryButtonText = "选择位置并导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        Refresh();
    }

    /// <summary>把回调挂到 CheckBox 的勾选与取消勾选上（WinUI 3 没有单一"值变了"事件）。</summary>
    private static void HookToggle(CheckBox box, Action onChanged)
    {
        box.Checked += (_, _) => onChanged();
        box.Unchecked += (_, _) => onChanged();
    }

    /// <summary>用户确认后要用的勾选结果；取消时为 null。</summary>
    public ExportOptions? Result { get; private set; }

    private void Refresh()
    {
        var options = CurrentOptions();
        _warning.Visibility = options.IncludePasswords
            ? Visibility.Visible
            : Visibility.Collapsed;
        _summary.Text = "将导出：" + PreviewText(options);
    }

    private ExportOptions CurrentOptions() => new()
    {
        IncludeAppSettings = _appSettings.IsChecked == true,
        IncludeTasks = _tasks.IsChecked == true,
        IncludeRemoteAccounts = _remote.IsChecked == true,
        IncludePasswords = _remote.IsChecked == true && _passwords.IsChecked == true,
    };

    /// <summary>把勾选结果拼成一句人话，让用户导出前就能复核一遍。</summary>
    private static string PreviewText(ExportOptions options)
    {
        if (!options.IncludeTasks && !options.IncludeAppSettings && !options.IncludeRemoteAccounts)
        {
            return "（什么都没勾，会得到一个空配置）";
        }

        var parts = new System.Collections.Generic.List<string>();
        if (options.IncludeTasks)
        {
            parts.Add("任务设置");
        }

        if (options.IncludeAppSettings)
        {
            parts.Add("本体设置");
        }

        if (options.IncludeRemoteAccounts)
        {
            parts.Add(options.IncludePasswords ? "远程用户（含明文密码）" : "远程用户（无密码）");
        }

        return string.Join("、", parts);
    }

    /// <summary>
    /// 弹出对话框。返回 true 表示用户点了导出，此时 <see cref="Result"/> 有效。
    ///
    /// 方法名<b>故意不叫 ShowAsync</b>：本类没有 ContentDialog 可关，叫 ShowAsync 会
    /// 和 `ContentDialog.ShowAsync()` 混淆，冒烟里那条「不许出现裸 ShowAsync()」的
    /// 断言就是这么被自己绊到的。内部那行真正的对话框调用仍然走 DialogHelper。
    /// </summary>
    public async System.Threading.Tasks.Task<bool> ShowDialogAsync()
    {
        if (YukinoChan.App.MainWindow?.Content is not FrameworkElement host)
        {
            return false;
        }

        _dialog.XamlRoot = host.XamlRoot;
        var result = await Helpers.DialogHelper.ShowAsync(_dialog);
        if (result != ContentDialogResult.Primary)
        {
            Result = null;
            return false;
        }

        Result = CurrentOptions();
        Result.Normalize();
        return true;
    }
}
