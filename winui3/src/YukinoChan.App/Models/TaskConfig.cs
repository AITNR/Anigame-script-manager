// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using YukinoChan.Helpers;

namespace YukinoChan.Models;

/// <summary>
/// 单个任务配置。
/// 直接实现 INotifyPropertyChanged，可同时用于 JSON 序列化与 XAML 双向绑定。
/// 配置键名与 Python 版完全一致，保证 config.json 双向兼容。
/// </summary>
public sealed class TaskConfig : ObservableObject, ICloneable
{
    private bool _enabled = true;
    private string _name = "新任务";
    private string _scriptPath = string.Empty;
    private int _order = 1;
    private int _timeoutMinutes = 30;
    private string _timeoutAction = TimeoutActions.KillAndContinue;
    private bool _useArgs;
    private string _args = string.Empty;
    private string _waitMode = WaitModes.DirectProcess;
    private string _waitProcessName = string.Empty;
    private int _confirmEnterDelaySeconds;
    private string _channelId = string.Empty;
    private string _channelDisplay = "本地执行";
    private string _concurrentGroup = string.Empty;
    private string _concurrentPolicy = ConcurrentPolicies.WaitAll;
    private bool _enableWatchdog;
    private List<string> _processKeywords = new();
    private List<string> _windowKeywords = new();
    private string _launcherProcess = string.Empty;
    private string _mainProcess = string.Empty;
    private string _gameProcess = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    [JsonPropertyName("script_path")]
    public string ScriptPath
    {
        get => _scriptPath;
        set => SetProperty(ref _scriptPath, value);
    }

    [JsonPropertyName("order")]
    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

    [JsonPropertyName("timeout_minutes")]
    public int TimeoutMinutes
    {
        get => _timeoutMinutes;
        set => SetProperty(ref _timeoutMinutes, value);
    }

    [JsonPropertyName("timeout_action")]
    public string TimeoutAction
    {
        get => _timeoutAction;
        set => SetProperty(ref _timeoutAction, TimeoutActions.Normalize(value));
    }

    [JsonPropertyName("use_args")]
    public bool UseArgs
    {
        get => _useArgs;
        set => SetProperty(ref _useArgs, value);
    }

    [JsonPropertyName("args")]
    public string Args
    {
        get => _args;
        set => SetProperty(ref _args, value);
    }

    [JsonPropertyName("wait_mode")]
    public string WaitMode
    {
        get => _waitMode;
        set => SetProperty(ref _waitMode, WaitModes.Normalize(value));
    }

    [JsonPropertyName("wait_process_name")]
    public string WaitProcessName
    {
        get => _waitProcessName;
        set => SetProperty(ref _waitProcessName, value);
    }

    [JsonPropertyName("confirm_enter_delay_seconds")]
    public int ConfirmEnterDelaySeconds
    {
        get => _confirmEnterDelaySeconds;
        set => SetProperty(ref _confirmEnterDelaySeconds, value);
    }

    /// <summary>
    /// 执行通道：任务在哪个会话通道里跑。
    /// 留空 = 本地执行（跑在主控端当前会话里，与改造前的行为一致）。
    /// </summary>
    [JsonPropertyName("channel_id")]
    public string ChannelId
    {
        get => _channelId;
        set => SetProperty(ref _channelId, (value ?? string.Empty).Trim());
    }

    /// <summary>
    /// 通道的显示名（**运行期填充，不落盘**）：任务只存 <see cref="ChannelId"/>，
    /// 列表上要给人看的是名字 —— 由 <c>MainViewModel.RefreshChannelChoices()</c> 从通道配置反查后写进来。
    /// </summary>
    [JsonIgnore]
    public string ChannelDisplay
    {
        get => _channelDisplay;
        set => SetProperty(ref _channelDisplay, value ?? string.Empty);
    }

    [JsonPropertyName("concurrent_group")]
    public string ConcurrentGroup
    {
        get => _concurrentGroup;
        set => SetProperty(ref _concurrentGroup, value);
    }

    [JsonPropertyName("concurrent_policy")]
    public string ConcurrentPolicy
    {
        get => _concurrentPolicy;
        set => SetProperty(ref _concurrentPolicy, ConcurrentPolicies.Normalize(value));
    }

    [JsonPropertyName("enable_watchdog")]
    public bool EnableWatchdog
    {
        get => _enableWatchdog;
        set => SetProperty(ref _enableWatchdog, value);
    }

    [JsonPropertyName("process_keywords")]
    public List<string> ProcessKeywords
    {
        get => _processKeywords;
        set => SetProperty(ref _processKeywords, KeywordHelper.Normalize(value));
    }

    [JsonPropertyName("window_keywords")]
    public List<string> WindowKeywords
    {
        get => _windowKeywords;
        set => SetProperty(ref _windowKeywords, KeywordHelper.Normalize(value));
    }

    [JsonPropertyName("launcher_process")]
    public string LauncherProcess
    {
        get => _launcherProcess;
        set => SetProperty(ref _launcherProcess, value);
    }

    [JsonPropertyName("main_process")]
    public string MainProcess
    {
        get => _mainProcess;
        set => SetProperty(ref _mainProcess, value);
    }

    [JsonPropertyName("game_process")]
    public string GameProcess
    {
        get => _gameProcess;
        set => SetProperty(ref _gameProcess, value);
    }

    // ---------- 仅供 UI 绑定的文本代理 ----------

    [JsonIgnore]
    public string ProcessKeywordsText
    {
        get => KeywordHelper.ToText(_processKeywords);
        set
        {
            var normalized = KeywordHelper.Normalize(value);
            if (SetProperty(ref _processKeywords, normalized))
            {
                OnPropertyChanged(nameof(ProcessKeywords));
            }
        }
    }

    [JsonIgnore]
    public string WindowKeywordsText
    {
        get => KeywordHelper.ToText(_windowKeywords);
        set
        {
            var normalized = KeywordHelper.Normalize(value);
            if (SetProperty(ref _windowKeywords, normalized))
            {
                OnPropertyChanged(nameof(WindowKeywords));
            }
        }
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"任务{Order}" : Name;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ScriptPath);

    /// <summary>超时秒数（0 表示不限时）。</summary>
    [JsonIgnore]
    public int TimeoutSeconds => Math.Max(0, TimeoutMinutes) * 60;

    public TaskConfig Clone()
    {
        var copy = (TaskConfig)MemberwiseClone();
        copy._processKeywords = new List<string>(_processKeywords);
        copy._windowKeywords = new List<string>(_windowKeywords);
        return copy;
    }

    object ICloneable.Clone() => Clone();

    /// <summary>把当前实例规整为合法值（枚举兜底、序号连续）。</summary>
    public void Sanitize(int fallbackOrder)
    {
        TimeoutAction = TimeoutActions.Normalize(TimeoutAction);
        WaitMode = WaitModes.Normalize(WaitMode);
        ConcurrentPolicy = ConcurrentPolicies.Normalize(ConcurrentPolicy);
        TimeoutMinutes = Math.Max(0, TimeoutMinutes);
        ConfirmEnterDelaySeconds = Math.Max(0, ConfirmEnterDelaySeconds);
        Order = Math.Max(1, Order == 0 ? fallbackOrder : Order);
        ChannelId = (ChannelId ?? string.Empty).Trim();
        ConcurrentGroup = (ConcurrentGroup ?? string.Empty).Trim();
        ProcessKeywords = KeywordHelper.Normalize(ProcessKeywords);
        WindowKeywords = KeywordHelper.Normalize(WindowKeywords);
    }
}
