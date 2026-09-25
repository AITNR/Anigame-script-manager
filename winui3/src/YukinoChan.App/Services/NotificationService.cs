// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;

namespace YukinoChan.Services;

/// <summary>
/// Windows 系统通知（Toast）。
/// 用 Windows App SDK 自带的 AppNotification，非打包（unpackaged）模式下同样可用，
/// 不需要额外 NuGet 包。
///
/// 重要：Toast 只在"发出通知的那个用户会话"里显示。跨用户会话场景（RDP）下，
/// 目标会话里的通知由 Agent 发出，主控端另需通过共享状态文件自行弹出，
/// 两边都要发，用户才能在当前正在看的桌面上看到。
///
/// 通知属于"锦上添花"，任何失败都不能影响任务执行，因此全部调用都吞掉异常，
/// 由调用方降级到应用内气泡 / 日志。
/// </summary>
public static class NotificationService
{
    private static DispatcherQueue? _dispatcher;
    private static bool _registered;
    private static bool _disabled;
    private static string _lastError = string.Empty;

    /// <summary>通知是否可用（注册成功且未发生致命错误）。</summary>
    public static bool IsAvailable => !_disabled && _registered;

    public static string LastError => _lastError;

    /// <summary>在 UI 线程调用一次。重复调用安全。</summary>
    public static void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        if (_registered || _disabled)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // 非打包模式下若缺少通知所需的注册信息，这里会失败；
            // 静默降级，由调用方改用应用内提示。
            _disabled = true;
            _lastError = ex.Message;
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // 当前不需要响应通知点击，注册回调只是为了让通知可正常投递
    }

    /// <summary>弹出一条系统通知。返回是否成功投递。</summary>
    public static bool Show(string title, string body)
    {
        if (_disabled || !_registered)
        {
            return false;
        }

        try
        {
            var payload = BuildPayload(title, body);
            Post(() =>
            {
                var notification = new AppNotification(payload);
                AppNotificationManager.Default.Show(notification);
            });
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return false;
        }
    }

    /// <summary>任务完成专用通知：标题带序号，正文带耗时。</summary>
    public static bool ShowTaskDone(string taskName, int index, int total, int elapsedSeconds, bool abnormal)
    {
        var prefix = abnormal ? "任务异常结束" : "任务已完成";
        var head = total > 0 && index > 0 ? $"{prefix}（{index}/{total}）" : prefix;
        var body = $"{taskName}　耗时 {Helpers.FormatHelper.FormatSeconds(elapsedSeconds)}";
        return Show(head, body);
    }

    public static bool ShowSummary(string title, string body) => Show(title, body);

    private static string BuildPayload(string title, string body)
    {
        var lines = body.Split('\n');
        var text = new System.Text.StringBuilder();
        text.Append("<text>").Append(Escape(title)).Append("</text>");
        for (var i = 0; i < lines.Length && i < 3; i++)
        {
            text.Append("<text>").Append(Escape(lines[i])).Append("</text>");
        }

        return "<toast><visual><binding template=\"ToastGeneric\">" + text + "</binding></visual></toast>";
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    private static void Post(Action action)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }
            });
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }
}
