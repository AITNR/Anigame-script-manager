// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using YukinoChan.Helpers;

namespace YukinoChan.Models;

/// <summary>全局应用配置，字段与 Python 版 config.json 完全一致。</summary>
public sealed class AppConfig : ObservableObject
{
    /// <summary>
    /// WinUI 3 主题。除 light / dark 之外，仍保留 Python 版皮肤名以便旧配置平滑迁移，
    /// 这些皮肤统一映射到 Fluent 默认主题。
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> ThemeItems = new List<KeyValuePair<string, string>>
    {
        new("system", "跟随系统"),
        new("light", "浅色"),
        new("dark", "深色"),
    };

    private static readonly HashSet<string> LegacyThemes = new() { "yukino", "campus", "fresh", "fantasy" };

    private bool _shutdownAfterDone;
    private int _shutdownDelaySeconds = 60;
    private bool _autoExitAfterDone;
    private bool _autoStartTasks;
    private bool _windowsStartup;
    private bool _enableTimeoutScreenshot = true;

    public AppConfig()
    {
    }

    public AppConfig(IEnumerable<TaskConfig> tasks)
    {
        Tasks = new List<TaskConfig>(tasks);
    }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "yukino";

    [JsonPropertyName("shutdown_after_done")]
    public bool ShutdownAfterDone
    {
        get => _shutdownAfterDone;
        set => SetProperty(ref _shutdownAfterDone, value);
    }

    [JsonPropertyName("shutdown_delay_seconds")]
    public int ShutdownDelaySeconds
    {
        get => _shutdownDelaySeconds;
        set => SetProperty(ref _shutdownDelaySeconds, value);
    }

    [JsonPropertyName("auto_exit_after_done")]
    public bool AutoExitAfterDone
    {
        get => _autoExitAfterDone;
        set => SetProperty(ref _autoExitAfterDone, value);
    }

    [JsonPropertyName("auto_start_tasks")]
    public bool AutoStartTasks
    {
        get => _autoStartTasks;
        set => SetProperty(ref _autoStartTasks, value);
    }

    [JsonPropertyName("windows_startup")]
    public bool WindowsStartup
    {
        get => _windowsStartup;
        set => SetProperty(ref _windowsStartup, value);
    }

    [JsonPropertyName("enable_timeout_screenshot")]
    public bool EnableTimeoutScreenshot
    {
        get => _enableTimeoutScreenshot;
        set => SetProperty(ref _enableTimeoutScreenshot, value);
    }

    [JsonPropertyName("tasks")]
    public List<TaskConfig> Tasks { get; set; } = new();

    /// <summary>RDP 跨用户会话相关设置。密码不落盘，只存账户名与"是否已保存凭据"。</summary>
    [JsonPropertyName("rdp")]
    public RdpConfig Rdp { get; set; } = new();

    /// <summary>主窗口位置/尺寸，用于"记住上次窗口大小"。整段缺失时按默认尺寸启动。</summary>
    [JsonPropertyName("window")]
    public WindowConfig Window { get; set; } = new();

    /// <summary>规范化：主题兜底、按 order 排序并重写连续序号。</summary>
    public void Sanitize()
    {
        Rdp ??= new RdpConfig();
        Rdp.Sanitize();

        Window ??= new WindowConfig();
        Window.Sanitize();

        var theme = (Theme ?? string.Empty).ToLowerInvariant();
        var allowed = false;
        foreach (var item in ThemeItems)
        {
            if (item.Key == theme)
            {
                allowed = true;
                break;
            }
        }

        Theme = allowed || LegacyThemes.Contains(theme) ? theme : "system";
        ShutdownDelaySeconds = Math.Max(0, ShutdownDelaySeconds);

        Tasks ??= new List<TaskConfig>();
        Tasks.Sort((a, b) => a.Order.CompareTo(b.Order));
        for (var i = 0; i < Tasks.Count; i++)
        {
            Tasks[i].Sanitize(i + 1);
            Tasks[i].Order = i + 1;
        }

        MigrateLegacyChannel();
    }

    /// <summary>
    /// 旧配置迁移：改造前只有一组全局 target_host / target_user，只能跑一路远程。
    /// 这里把它变成一个「默认通道」，并把没指定通道的任务都指过去 —— 升级后行为不变。
    ///
    /// 幂等：已经有通道（用户自己建过）或从没配过目标账户时，什么都不做。
    /// 注意只在 RDP 总开关打开时迁移：关掉开关时全部任务本来就跑本地，
    /// 此时凭空多出一个通道只会让人困惑。
    /// </summary>
    private void MigrateLegacyChannel()
    {
        if (!Rdp.Enabled || Rdp.Channels.Count > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Rdp.TargetUser))
        {
            return;
        }

        var channel = new RdpChannel
        {
            Id = RdpChannel.LegacyDefaultId,
            Name = Rdp.TargetUser,
            Host = Rdp.TargetHost,
            User = Rdp.TargetUser,
            BridgePath = Rdp.BridgePath,
            CredentialSaved = Rdp.CredentialSaved,
            SessionFinish = Rdp.SessionFinish,
            DesktopWidth = Rdp.DesktopWidth,
            DesktopHeight = Rdp.DesktopHeight,
            Enabled = true,
        };

        channel.Sanitize(1);
        Rdp.Channels.Add(channel);

        foreach (var task in Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.ChannelId))
            {
                task.ChannelId = channel.Id;
            }
        }
    }

    public AppConfig CloneWithTasks(IEnumerable<TaskConfig> tasks) => new(tasks)
    {
        Theme = Theme,
        ShutdownAfterDone = ShutdownAfterDone,
        ShutdownDelaySeconds = ShutdownDelaySeconds,
        AutoExitAfterDone = AutoExitAfterDone,
        AutoStartTasks = AutoStartTasks,
        WindowsStartup = WindowsStartup,
        EnableTimeoutScreenshot = EnableTimeoutScreenshot,
        Rdp = (RdpConfig)Rdp.Clone(),
        Window = (WindowConfig)Window.Clone(),
    };
}
