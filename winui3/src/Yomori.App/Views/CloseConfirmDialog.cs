// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Yomori.Views;

/// <summary>
/// 点关闭按钮时的询问弹窗：最小化到状态栏 / 退出 / 取消。
///
/// <para>用代码构建而不是 XAML：本工程里所有 ContentDialog 都是"临时造一个、ShowAsync 完就扔"，
/// 走 <see cref="Helpers.DialogHelper.ShowAsync"/> 的全局闸门；做成 XAML 页面资源反而要处理
/// 命名元素与资源字典的解析顺序（见 Themes/Styles.xaml 那次 WMC0001 的教训）。</para>
///
/// <para><b>两个勾选框互斥</b>，但它们不是"多选"而是"选一种记忆方式"：</para>
/// <list type="bullet">
///   <item>记住我的选择 —— 把这次点的按钮记成固定策略；</item>
///   <item>任务执行时隐藏到状态栏，空闲时退出 —— 记住"按状态自动决定"这条策略。</item>
/// </list>
/// <para>同时勾上就等于让程序同时记两条互相打架的规则，所以只能留一个。</para>
///
/// <para>注意 WinUI 的 <see cref="CheckBox"/> **没有 Toggled 事件**，只有 Checked / Unchecked，
/// 而程序化把 IsChecked 置 false 同样会触发 Unchecked —— 直接互斥会无限递归。
/// 所以这里用一个 <c>_syncing</c> 守卫掐掉"由我自己触发的那次"。</para>
/// </summary>
public sealed class CloseConfirmDialog : ContentDialog
{
    private readonly Func<bool> _isRunning;
    private readonly CheckBox _remember;
    private readonly CheckBox _smart;
    private DispatcherQueueTimer? _stateTimer;
    private bool _syncing;

    public CloseConfirmDialog(Func<bool> isRunning, string detail)
    {
        _isRunning = isRunning;

        Title = "关闭Yomori？";
        PrimaryButtonText = "最小化到状态栏";
        SecondaryButtonText = "退出";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;

        _remember = new CheckBox
        {
            Content = "记住我的选择",
        };

        _smart = new CheckBox
        {
            Content = "任务执行时隐藏到状态栏，空闲时退出",
        };

        var smartHint = new TextBlock
        {
            Text = "跑任务时只是收进状态栏，任务跑完再自动退出。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(28, -6, 0, 0),
        };

        var panel = new StackPanel
        {
            Spacing = 10,
        };

        panel.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Opacity = 0.25,
        });

        panel.Children.Add(_remember);
        panel.Children.Add(_smart);
        panel.Children.Add(smartHint);

        Content = panel;

        _remember.Checked += (_, _) => OnChecked(_remember, _smart);
        _smart.Checked += (_, _) => OnChecked(_smart, _remember);

        // 弹窗开着的时候任务可能刚好跑完（或用户刚点了开始）——
        // 勾着智能模式时按钮文案必须跟着真实状态走，不能让"写着退出、按下去却隐藏"这种事发生。
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is not null)
        {
            _stateTimer = queue.CreateTimer();
            _stateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _stateTimer.IsRepeating = true;
            _stateTimer.Tick += (_, _) => RefreshSmartButtonText();
            _stateTimer.Start();
        }

        Closed += (_, _) =>
        {
            _stateTimer?.Stop();
            _stateTimer = null;
        };
    }

    /// <summary>用户勾了"记住我的选择"。</summary>
    public bool RememberChoice => _remember.IsChecked == true;

    /// <summary>用户勾了"任务执行时隐藏到状态栏，空闲时退出"。</summary>
    public bool SmartMode => _smart.IsChecked == true;

    private void OnChecked(CheckBox source, CheckBox other)
    {
        if (_syncing)
        {
            return;
        }

        if (source.IsChecked == true && other.IsChecked == true)
        {
            _syncing = true;
            try
            {
                other.IsChecked = false;
            }
            finally
            {
                _syncing = false;
            }
        }

        RefreshSmartButtonText();
    }

    /// <summary>
    /// 勾着智能模式时，主按钮必须写成"按下去真会发生的事"：
    /// 正在跑 → 隐藏；空闲 → 退出。同时把"退出"这个次按钮收起来（此刻它和主按钮会冲突）。
    /// </summary>
    private void RefreshSmartButtonText()
    {
        if (!SmartMode)
        {
            if (PrimaryButtonText != "最小化到状态栏")
            {
                PrimaryButtonText = "最小化到状态栏";
                SecondaryButtonText = "退出";
            }

            return;
        }

        var running = SafeIsRunning();
        var wanted = running ? "隐藏到状态栏" : "退出";

        if (PrimaryButtonText != wanted)
        {
            PrimaryButtonText = wanted;
        }

        if (!string.IsNullOrEmpty(SecondaryButtonText))
        {
            SecondaryButtonText = string.Empty;
        }
    }

    private bool SafeIsRunning()
    {
        try
        {
            return _isRunning();
        }
        catch
        {
            return false;
        }
    }
}
