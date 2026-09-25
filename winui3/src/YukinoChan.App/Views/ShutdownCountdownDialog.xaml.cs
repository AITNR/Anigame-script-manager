// -*- coding: utf-8 -*-
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace YukinoChan.Views;

/// <summary>
/// 任务完成后的可取消关机提示。
/// 与 Python 版一致：先发送延迟关机命令，再弹出置顶窗口；只有『取消关机』才会执行 shutdown /a。
/// </summary>
public sealed partial class ShutdownCountdownDialog : ContentDialog
{
    private readonly DispatcherQueueTimer _timer;
    private int _remaining;

    public ShutdownCountdownDialog(int delaySeconds)
    {
        InitializeComponent();

        _remaining = delaySeconds > 0 ? delaySeconds : 60;
        CountdownBar.Maximum = _remaining;
        CountdownBar.Value = _remaining;
        CountdownTitle.Text = _remaining == 60 ? "⚠ 1 分钟后将关机" : $"⚠ {_remaining} 秒后将关机";
        CountdownLabel.Text = $"剩余：{_remaining} 秒";

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = System.TimeSpan.FromSeconds(1);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, object e)
    {
        _remaining = System.Math.Max(0, _remaining - 1);
        CountdownLabel.Text = $"剩余：{_remaining} 秒";
        CountdownBar.Value = _remaining;

        if (_remaining <= 0)
        {
            _timer.Stop();
        }
    }
}
