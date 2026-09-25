// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using YukinoChan.Helpers;
using YukinoChan.Models;
using YukinoChan.Services;

namespace YukinoChan.ViewModels;

public sealed class StatRow
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public string AverageText { get; set; } = "00:00";
    public string LastText { get; set; } = "00:00";
    public string LastTime { get; set; } = string.Empty;
}

/// <summary>
/// 全局视图模型：配置、任务队列、运行状态、日志、统计、看板娘状态机。
/// 所有来自执行器线程的回调都会先切回 UI 线程再更新绑定属性。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 4000;

    private readonly ConfigManager _configManager;
    private readonly FileLogger _logger;
    private readonly RuntimeStatsManager _statsManager;
    private readonly DispatcherQueue _dispatcher;

    private ScriptRunner? _runner;
    private Task? _runTask;
    private DispatcherQueueTimer? _bubbleRestoreTimer;
    private DispatcherQueueTimer? _errorReleaseTimer;

    private bool _isRunning;
    private string _statusText = "空闲";
    private string _currentTaskName = "-";
    private string _elapsedText = "00:00";
    private string _progressText = "0 / 0";
    private TaskConfig? _selectedTask;

    private string _mascotState = MascotStates.Idle;
    private string _mascotImagePath = string.Empty;
    private string _bubbleText = "雪乃酱待命中～";
    private string _bubbleKind = "normal";
    private int _bubblePriority;
    private int _mascotErrorCount;
    private DateTimeOffset _mascotErrorLockedUntil = DateTimeOffset.MinValue;
    private string _mascotLastNonErrorState = MascotStates.Idle;
    private int _guideBubbleIndex;
    private int _runTaskErrorCount;

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _configManager = new ConfigManager(AppPaths.ConfigPath);
        _logger = new FileLogger(AppPaths.LogDir);
        _statsManager = new RuntimeStatsManager(AppPaths.StatsDir);

        Config = _configManager.Load();
        Tasks = new ObservableCollection<TaskConfig>(Config.Tasks);

        LogLines = new ObservableCollection<string>();
        StatRows = new ObservableCollection<StatRow>();
    }

    // ---------------- 配置 ----------------

    public AppConfig Config { get; }

    public ObservableCollection<TaskConfig> Tasks { get; }

    public TaskConfig? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }
    }

    public bool HasSelection => SelectedTask is not null;

    public bool HasNoSelection => SelectedTask is null;

    /// <summary>下拉选项源：Key 为配置值，Value 为中文显示名。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> WaitModeItems => WaitModes.Items;

    public IReadOnlyList<KeyValuePair<string, string>> TimeoutActionItems => TimeoutActions.Items;

    public IReadOnlyList<KeyValuePair<string, string>> ConcurrentPolicyItems => ConcurrentPolicies.Items;

    public IReadOnlyList<KeyValuePair<string, string>> ThemeItems => AppConfig.ThemeItems;

    public IReadOnlyList<string> GuideBubbleTexts { get; } = new List<string>
    {
        "还没有配置任务哦～",
        "可以点击左侧\n“设置”导入配置。",
        "也可以在任务执行页里\n手动设置配置。",
        "配置完成后，点击\n开始执行就可以啦。",
        "第一次使用的话，\n可以先观察一下运行状态哦～",
    };

    // ---------------- 运行状态 ----------------

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool CanEdit => !IsRunning;

    public bool CanStart => !IsRunning;

    public bool CanPause => IsRunning;

    public bool CanStop => IsRunning;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentTaskName
    {
        get => _currentTaskName;
        private set => SetProperty(ref _currentTaskName, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public ObservableCollection<string> LogLines { get; }

    public ObservableCollection<StatRow> StatRows { get; }

    // ---------------- 看板娘 ----------------

    public string MascotState
    {
        get => _mascotState;
        private set => SetProperty(ref _mascotState, value);
    }

    public string MascotImagePath
    {
        get => _mascotImagePath;
        private set => SetProperty(ref _mascotImagePath, value);
    }

    /// <summary>供 Image.Source 直接绑定使用的位图；无素材时为 null，由占位内容接管。</summary>
    public BitmapImage? MascotImage
    {
        get => _mascotImage;
        private set => SetProperty(ref _mascotImage, value);
    }

    private BitmapImage? _mascotImage;

    public string BubbleText
    {
        get => _bubbleText;
        private set => SetProperty(ref _bubbleText, value);
    }

    public string BubbleKind
    {
        get => _bubbleKind;
        private set => SetProperty(ref _bubbleKind, value);
    }

    public event EventHandler<int>? ShutdownPromptRequested;

    public event EventHandler<string>? RequestNavigation;

    // ---------------- 启动 ----------------

    public void Initialize()
    {
        AppendLog("程序启动。");
        AppendLog($"配置文件：{_configManager.ConfigPath}");
        AppendLog($"日志目录：{_logger.LogDir}");
        AppendLog($"资源目录：{AppPaths.AssetsDir}");

        SetMascotState(MascotStates.Idle, force: true);
        RefreshStats();
        SelectedTask = Tasks.Count > 0 ? Tasks[0] : null;

        if (!HasUsableTaskConfig())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(900);
                Post(() => ShowFirstConfigGuide(force: true));
            });
        }

        if (Config.AutoStartTasks)
        {
            AppendLog("已启用程序启动后自动开始执行任务。");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Post(Start);
            });
        }
    }

    // ---------------- 任务编辑 ----------------

    public void AddTask()
    {
        var nextOrder = Tasks.Count + 1;
        var task = new TaskConfig
        {
            Enabled = true,
            Name = $"新任务{nextOrder}",
            ScriptPath = string.Empty,
            Order = nextOrder,
            TimeoutMinutes = 30,
            TimeoutAction = TimeoutActions.KillAndContinue,
            WaitMode = WaitModes.DirectProcess,
            ConcurrentPolicy = ConcurrentPolicies.WaitAll,
        };

        Tasks.Add(task);
        NormalizeOrders();
        SelectedTask = task;
    }

    public void DeleteSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        Tasks.Remove(SelectedTask);
        NormalizeOrders();
        SelectedTask = Tasks.Count == 0
            ? null
            : Tasks[Math.Min(index, Tasks.Count - 1)];
    }

    public void MoveSelectedUp()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index <= 0)
        {
            return;
        }

        Tasks.Move(index, index - 1);
        NormalizeOrders();
    }

    public void MoveSelectedDown()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index < 0 || index >= Tasks.Count - 1)
        {
            return;
        }

        Tasks.Move(index, index + 1);
        NormalizeOrders();
    }

    private void NormalizeOrders()
    {
        for (var i = 0; i < Tasks.Count; i++)
        {
            Tasks[i].Order = i + 1;
        }
    }

    public bool HasUsableTaskConfig()
    {
        foreach (var task in Tasks)
        {
            if (task.Enabled && !string.IsNullOrWhiteSpace(task.ScriptPath))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------- 配置读写 ----------------

    public void SaveConfig()
    {
        NormalizeOrders();
        Config.Tasks = new List<TaskConfig>(Tasks);
        WindowsStartupService.Apply(Config.WindowsStartup);

        try
        {
            _configManager.Save(Config);
            AppendLog("配置已保存。");
        }
        catch (Exception ex)
        {
            AppendLog($"保存配置失败：{ex.Message}");
            _ = DialogHelper.ShowMessageAsync("保存失败", $"保存配置失败：{ex.Message}");
        }
    }

    public async Task ExportConfigAsync()
    {
        NormalizeOrders();

        // 分享配置不携带开机自启动，避免导入者误开。
        var export = new AppConfig(new List<TaskConfig>(Tasks))
        {
            Theme = Config.Theme,
            ShutdownAfterDone = Config.ShutdownAfterDone,
            ShutdownDelaySeconds = Config.ShutdownDelaySeconds,
            AutoExitAfterDone = Config.AutoExitAfterDone,
            AutoStartTasks = Config.AutoStartTasks,
            WindowsStartup = false,
            EnableTimeoutScreenshot = Config.EnableTimeoutScreenshot,
        };

        var path = await DialogHelper.PickSaveFileAsync(
            "auto_script_config_share.json",
            ("JSON 配置文件", ".json"),
            ("所有文件", "."));

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(export, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new System.Text.UTF8Encoding(false));

            AppendLog($"配置已导出：{path}");
        }
        catch (Exception ex)
        {
            AppendLog($"导出配置失败：{ex.Message}");
        }
    }

    public async Task ImportConfigAsync()
    {
        var path = await DialogHelper.PickOpenFileAsync(("JSON 配置文件", ".json"), ("所有文件", "."));
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            var imported = JsonSerializer.Deserialize<AppConfig>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (imported is null)
            {
                throw new InvalidDataException("配置文件顶层结构不是对象");
            }

            imported.Sanitize();

            // 只导入任务配置；自动关机、开机自启动、截图开关等本机设置保持不变。
            Tasks.Clear();
            foreach (var task in imported.Tasks)
            {
                Tasks.Add(task);
            }

            NormalizeOrders();
            SelectedTask = Tasks.Count > 0 ? Tasks[0] : null;
            SaveConfig();

            AppendLog($"配置已导入并保存：{path}");
            AppendLog("导入说明：仅导入任务配置；自动关机、开机自启动、截图开关等本机设置保持不变。");

            if (HasUsableTaskConfig())
            {
                ShowTemporaryMascotMessage("配置导入完成啦～可以先试着开始执行。", 10, 30);
            }
            else
            {
                ShowFirstConfigGuide();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"导入配置失败：{ex.Message}");
            await DialogHelper.ShowMessageAsync("导入失败", $"导入配置失败：{ex.Message}");
        }
    }

    // ---------------- 执行控制 ----------------

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        SaveConfig();

        // 开启 RDP 模式时，任务交给目标账户会话里的代理执行，主控端只负责下发与监控
        if (RdpSettings.Enabled)
        {
            _ = ConnectAndRunAsync();
            return;
        }

        var enabledCount = 0;
        foreach (var task in Tasks)
        {
            if (task.Enabled)
            {
                enabledCount++;
            }
        }

        if (enabledCount == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "当前没有启用的任务。");
            ShowFirstConfigGuide();
            return;
        }

        if (!HasUsableTaskConfig())
        {
            _ = DialogHelper.ShowMessageAsync("提示", "还没有设置可执行的脚本配置。");
            ShowFirstConfigGuide();
            return;
        }

        IsRunning = true;
        _runTaskErrorCount = 0;
        StatusText = "启动确认中";
        CurrentTaskName = "-";
        ElapsedText = "00:00";
        ProgressText = $"0 / {enabledCount}";
        SetMascotState(MascotStates.Work, force: true);
        ShowTemporaryMascotMessage("正在启动任务，我会先帮你确认状态。", 8, 75);

        var snapshots = new List<TaskConfig>();
        foreach (var task in Tasks)
        {
            snapshots.Add(task.Clone());
        }

        _runner = new ScriptRunner(
            snapshots,
            Config.ShutdownAfterDone,
            Config.ShutdownDelaySeconds,
            _logger,
            _statsManager,
            Config.EnableTimeoutScreenshot);

        _runner.Logged += OnRunnerLogged;
        _runner.StatusChanged += OnRunnerStatus;
        _runner.ProgressChanged += OnRunnerProgress;
        _runner.TaskStarted += OnRunnerTaskStarted;
        _runner.TaskCompleted += OnRunnerTaskCompleted;
        _runner.ElapsedChanged += OnRunnerElapsed;
        _runner.TaskLaunchSucceeded += OnRunnerTaskLaunchSucceeded;
        _runner.TaskError += OnRunnerTaskError;
        _runner.ShutdownPrompt += OnRunnerShutdownPrompt;
        _runner.Finished += OnRunnerFinished;

        _runTask = _runner.RunAsync();
    }

    public void Pause()
    {
        _runner?.RequestPause();
    }

    public void Resume()
    {
        _runner?.RequestResume();
    }

    public void Stop()
    {
        _runner?.RequestStop();
    }

    public void EmergencyStop()
    {
        _runner?.RequestEmergencyStop();
    }

    public void CancelShutdown()
    {
        if (ShutdownService.Cancel())
        {
            AppendLog("已发送取消关机命令：shutdown /a");
        }
    }

    // ---------------- 执行器回调（均在 UI 线程执行） ----------------

    private void OnRunnerLogged(object? sender, string line) => Post(() =>
    {
        AppendLogLine(line);

        if (MascotService.IsErrorLogLine(line))
        {
            SetMascotState(MascotStates.Error, errorReason: MascotService.FriendlyErrorText(line));
        }
    });

    private void OnRunnerStatus(object? sender, string status) => Post(() =>
    {
        StatusText = status;

        if (status.Contains("异常") || status.Contains("错误") || status.Contains("失败") || status.Contains("报错"))
        {
            SetMascotState(MascotStates.Error, errorReason: "当前状态出现异常。雪乃酱会先停留在提示状态，方便你查看。");
        }
        else if (status is "已暂停" or "已停止" or "已完成" or "超时处理中")
        {
            SetMascotState(MascotStates.Rest);
        }
        else if (status.Contains("运行"))
        {
            SetMascotState(MascotStates.Work);
        }
    });

    private void OnRunnerProgress(object? sender, (int Current, int Total) progress) => Post(() =>
    {
        ProgressText = $"{progress.Current} / {progress.Total}";
    });

    private void OnRunnerTaskStarted(object? sender, (string Name, int Index, int Total) info) => Post(() =>
    {
        CurrentTaskName = info.Name;
        ElapsedText = "00:00";
        ProgressText = $"{info.Index} / {info.Total}";
    });

    private void OnRunnerElapsed(object? sender, int seconds) => Post(() =>
    {
        ElapsedText = FormatHelper.FormatSeconds(seconds);
    });

    private void OnRunnerTaskLaunchSucceeded(object? sender, string taskName) => Post(() =>
    {
        if (MascotState == MascotStates.Error)
        {
            return;
        }

        StatusText = "运行中";
        SetMascotState(MascotStates.Work, force: true);
        ShowTemporaryMascotMessage("启动成功啦～接下来我会帮你盯着进度。", 10, 80);
    });

    private void OnRunnerTaskError(object? sender, string message) => Post(() =>
    {
        _runTaskErrorCount++;
        SetMascotState(MascotStates.Error, errorReason: MascotService.FriendlyErrorText(message));
    });

    private void OnRunnerShutdownPrompt(object? sender, int delay) => Post(() =>
    {
        ShutdownService.Schedule(delay);
        AppendLog($"已发送延迟关机命令：shutdown /s /t {delay}");
        ShutdownPromptRequested?.Invoke(this, delay);
    });

    private void OnRunnerFinished(object? sender, bool stoppedOrError) => Post(() =>
    {
        IsRunning = false;
        _runner = null;
        _runTask = null;

        if (stoppedOrError)
        {
            if (StatusText.Contains("停止") && MascotState != MascotStates.Error)
            {
                StatusText = "已停止";
                SetMascotState(MascotStates.Rest);
                ShowTemporaryMascotMessage("已经按你的要求停止啦，后续任务不会继续执行。", 12, 60);
            }
            else
            {
                StatusText = "已完成（有异常）";
                var count = Math.Max(1, _runTaskErrorCount);
                SetMascotState(MascotStates.Error, errorReason: $"任务结束，发生了 {count} 次任务异常。");
            }
        }
        else
        {
            StatusText = "已完成";
            SetMascotState(MascotStates.Rest);
            ShowTemporaryMascotMessage("任务已经顺利完成啦～这轮工作收好啦。", 12, 60);
        }

        RefreshStats();

        if (Config.AutoExitAfterDone && !stoppedOrError)
        {
            AppendLog("已启用任务完成后自动退出程序，程序即将退出。");
            var timer = _dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                App.MainWindow?.Close();
            };
            timer.Start();
        }
    });

    // ---------------- 日志 ----------------

    public void AppendLog(string message)
    {
        var line = _logger.Log(message);
        AppendLogLine(line);
    }

    private void AppendLogLine(string line)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            Post(() => AppendLogLine(line));
            return;
        }

        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
    }

    public void ClearLogs()
    {
        LogLines.Clear();
    }

    // ---------------- 统计 ----------------

    public void RefreshStats()
    {
        StatRows.Clear();

        try
        {
            var history = _statsManager.LoadHistory();
            var rows = new List<StatRow>();
            foreach (var item in history)
            {
                rows.Add(new StatRow
                {
                    Name = item.Key,
                    Count = item.Value.Count,
                    AverageText = FormatHelper.FormatSeconds((long)item.Value.AverageSeconds),
                    LastText = FormatHelper.FormatSeconds(item.Value.LastSeconds),
                    LastTime = item.Value.LastTime,
                });
            }

            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            foreach (var row in rows)
            {
                StatRows.Add(row);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"读取统计失败：{ex.Message}");
        }
    }

    public void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"无法打开目录：{ex.Message}");
        }
    }

    public void Navigate(string target) => RequestNavigation?.Invoke(this, target);

    // ---------------- 看板娘状态机 ----------------

    public void SetMascotState(string state, bool force = false, string errorReason = "")
    {
        if (state is not (MascotStates.Idle or MascotStates.Work or MascotStates.Rest or MascotStates.Error))
        {
            state = MascotStates.Idle;
        }

        var now = DateTimeOffset.UtcNow;
        if (MascotState == MascotStates.Error && state != MascotStates.Error && !force)
        {
            if (now < _mascotErrorLockedUntil)
            {
                return;
            }
        }

        if (state == MascotStates.Error)
        {
            _mascotErrorCount++;
            var lockSeconds = _mascotErrorCount * ServiceErrorLockBase;
            _mascotErrorLockedUntil = now.AddSeconds(lockSeconds);
            if (MascotState != MascotStates.Error)
            {
                _mascotLastNonErrorState = MascotState;
            }

            MascotState = MascotStates.Error;
            LoadMascotImage(MascotStates.Error);
            SetBubble(errorReason.Length > 0 ? errorReason : MascotService.DefaultText(MascotStates.Error), 100, force: true);
            ScheduleErrorRelease(lockSeconds);
            return;
        }

        if (MascotState == MascotStates.Error && force)
        {
            _mascotErrorCount = 0;
            _mascotErrorLockedUntil = DateTimeOffset.MinValue;
        }

        if (MascotState == state && !force)
        {
            return;
        }

        MascotState = state;
        _mascotLastNonErrorState = state;
        _bubblePriority = MascotService.PriorityFor(state);
        LoadMascotImage(state);
        SetBubble(MascotService.DefaultText(state), _bubblePriority, force: true);
    }

    private const int ServiceErrorLockBase = 15;

    public void ShowTemporaryMascotMessage(string text, int durationSeconds = 10, int priority = 30)
    {
        if (MascotState == MascotStates.Error)
        {
            return;
        }

        SetBubble(text, priority, durationSeconds);
    }

    public void ShowFirstConfigGuide(bool force = false)
    {
        if (IsRunning || MascotState == MascotStates.Error)
        {
            return;
        }

        if (HasUsableTaskConfig())
        {
            return;
        }

        _guideBubbleIndex = Math.Max(0, Math.Min(_guideBubbleIndex, GuideBubbleTexts.Count - 1));
        SetBubble(GuideBubbleTexts[_guideBubbleIndex], 30, 12, force, bubbleKind: "guide");
    }

    public void OnMascotBubbleClicked()
    {
        if (BubbleKind != "guide")
        {
            return;
        }

        if (IsRunning || MascotState == MascotStates.Error || HasUsableTaskConfig())
        {
            return;
        }

        _guideBubbleIndex = (_guideBubbleIndex + 1) % GuideBubbleTexts.Count;
        SetBubble(GuideBubbleTexts[_guideBubbleIndex], 30, 12, force: true, bubbleKind: "guide");
    }

    public void UpdateMascotForPage(string key)
    {
        if (MascotState == MascotStates.Error || IsRunning)
        {
            return;
        }

        var text = key switch
        {
            "tasks" => "任务列表在这里。确认好之后，就可以开始执行啦。",
            "stats" => "这里能看看最近的运行情况。",
            "logs" => "运行记录会放在这里，出问题时再慢慢看就好。",
            "rdp" => "想切到别的账户跑任务的话，就在这里设置。",
            "settings" => "全局设置在这里，改完记得保存哦。",
            _ => "雪乃酱待命中～需要时可以展开卡片继续操作。",
        };

        SetBubble(text, 20, 8);
        if (!IsRunning)
        {
            SetMascotState(MascotStates.Idle);
        }
    }

    private void SetBubble(string text, int priority, int durationSeconds = 0, bool force = false, string bubbleKind = "normal")
    {
        if (!force && priority < _bubblePriority)
        {
            return;
        }

        _bubblePriority = priority;
        BubbleKind = bubbleKind;
        BubbleText = MascotService.NormalizeBubbleText(text, GuideBubbleTexts);

        if (durationSeconds <= 0)
        {
            return;
        }

        _bubbleRestoreTimer?.Stop();
        _bubbleRestoreTimer = _dispatcher.CreateTimer();
        _bubbleRestoreTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, durationSeconds));
        _bubbleRestoreTimer.Tick += (_, _) =>
        {
            _bubbleRestoreTimer?.Stop();
            RestoreMascotMessage();
        };
        _bubbleRestoreTimer.Start();
    }

    private void RestoreMascotMessage()
    {
        if (MascotState == MascotStates.Error)
        {
            return;
        }

        if (IsRunning)
        {
            _bubblePriority = 70;
            BubbleKind = "work";
            BubbleText = MascotService.NormalizeBubbleText(MascotService.DefaultText(MascotStates.Work));
        }
        else
        {
            _bubblePriority = MascotService.PriorityFor(MascotState);
            BubbleKind = MascotState;
            BubbleText = MascotService.NormalizeBubbleText(MascotService.DefaultText(MascotState));
        }
    }

    private void LoadMascotImage(string state)
    {
        var path = MascotService.PickImage(state);
        MascotImagePath = path ?? string.Empty;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MascotImage = null;
            return;
        }

        var bitmap = new BitmapImage { UriSource = new Uri(path) };
        MascotImage = bitmap;
    }

    private void ScheduleErrorRelease(int lockSeconds)
    {
        _errorReleaseTimer?.Stop();
        var timer = _dispatcher.CreateTimer();
        _errorReleaseTimer = timer;
        timer.Interval = TimeSpan.FromSeconds(Math.Max(1, lockSeconds));
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (MascotState != MascotStates.Error)
            {
                return;
            }

            var remaining = _mascotErrorLockedUntil - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                timer.Interval = remaining;
                timer.Start();
                return;
            }

            SetMascotState(IsRunning ? MascotStates.Work : MascotStates.Rest, force: true);
        };
        timer.Start();
    }

    // ---------------- 异常报告 ----------------

    public AbnormalReport? LoadAbnormalReport()
    {
        var path = Path.Combine(_logger.LogDir, "last_abnormal_report.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AbnormalReport>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            return null;
        }
    }

    public void OpenStartupProblemReportIfNeeded()
    {
        var report = LoadAbnormalReport();
        if (report is null)
        {
            return;
        }

        AppendLog($"上次运行报告：{report.Summary}");
    }

    public void DismissAbnormalReport()
    {
        var path = Path.Combine(_logger.LogDir, "last_abnormal_report.json");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"删除已读异常报告失败：{ex.Message}");
        }
    }

    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    // ==================================================================
    // RDP 跨用户会话：在本机另一个 Windows 账户的会话里执行任务队列
    // ==================================================================

    private const int MaxRdpEventLines = 200;

    private DispatcherQueueTimer? _rdpPollTimer;
    private long _lastRdpEventSeq;
    private bool _isRdpBusy;
    private string _rdpStatusText = "未连接";
    private string _rdpPhase = "idle";
    private string _rdpCurrentTask = "－";
    private int _rdpProgress;
    private int _rdpTotal;
    private int _rdpElapsedSeconds;
    private string _rdpHeartbeatText = "－";
    private bool _rdpIsStale;
    private DateTimeOffset? _rdpLastHeartbeat;
    private bool _rdpStaleReported;

    /// <summary>
    /// 本轮指令是否已经收尾（"跑完了"的六件事是否已执行）。
    /// 常驻代理会让完成信号从「Phase=done」与「Finished 事件」两条路都可能到达，
    /// 用它保证收尾只做一次；随新指令下发复位。
    /// </summary>
    private bool _rdpCompletionReported;

    /// <summary>当前下发指令的 Id —— 用于校验桥里的 status.json 是不是本指令写的。</summary>
    private string? _rdpCurrentCommandId;

    /// <summary>下发当前指令的时刻 —— 启动宽限期从这里开始算。</summary>
    private DateTimeOffset? _rdpCommandIssuedAt;

    /// <summary>「等待目标会话接管」的中性提示是否已经记过一条，避免每秒刷屏。</summary>
    private bool _rdpAwaitingAgentLogged;
    private string _rdpReadinessText = "尚未检查";
    private string _rdpSessionSummary = "－";
    private string _rdpAgentStatusText = "－";

    private RelayCommand? _enableRemoteDesktopCommand;
    private RelayCommand? _deployAgentCommand;
    private RelayCommand? _removeAgentCommand;
    private AsyncRelayCommand? _connectAndRunCommand;
    private RelayCommand? _disconnectSessionCommand;
    private RelayCommand? _logoffSessionCommand;
    private RelayCommand? _refreshRdpCommand;
    private RelayCommand? _openBridgeFolderCommand;

    public RdpConfig RdpSettings => Config.Rdp;

    public IReadOnlyList<KeyValuePair<string, string>> SessionFinishItems => SessionFinishModes.Items;

    public IReadOnlyList<KeyValuePair<string, string>> ResolutionItems => RdpResolutions.Items;

    /// <summary>下拉框用的分辨率档位；选中「自适应」时写 0 回配置。</summary>
    public string RdpResolutionKey
    {
        get => RdpResolutions.ToValue(RdpSettings.DesktopWidth, RdpSettings.DesktopHeight);
        set
        {
            if (string.Equals(RdpResolutionKey, value, StringComparison.Ordinal))
            {
                return;
            }

            var (width, height) = RdpResolutions.Parse(value);
            RdpSettings.DesktopWidth = width;
            RdpSettings.DesktopHeight = height;

            OnPropertyChanged();
            OnPropertyChanged(nameof(RdpResolutionLabel));

            AppendLog(width > 0 && height > 0
                ? $"远程桌面分辨率已设为 {width} × {height}（下次连接时生效）。"
                : "远程桌面分辨率已设为自动（按系统默认窗口尺寸）。");
        }
    }

    /// <summary>界面上显示的分辨率文案。</summary>
    public string RdpResolutionLabel => RdpResolutions.Label(RdpSettings.DesktopWidth, RdpSettings.DesktopHeight);

    /// <summary>内嵌画面回报连接状态（连上 / 登录完成 / 断开）时同步到进度区与日志。</summary>
    public void ReportRdpStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        RdpStatusText = text;
        AppendLog($"[远程桌面] {text}");

        if (text.Contains("已就绪"))
        {
            SetMascotState(MascotStates.Work, force: true);
            ShowTemporaryMascotMessage("远程桌面连上啦，任务交给那边的我盯着～", 10, 80);
        }
    }

    /// <summary>「连接目标账户」按钮是否可用。</summary>
    public bool CanConnectSurface => !IsRdpBusy;

    /// <summary>
    /// 只建立远程桌面会话（独立 mstsc 窗口），不下发任何任务。
    /// 目标账户在本机已经登录着的话直接复用，不再新建连接。
    /// </summary>
    public void ConnectSurface()
    {
        if (IsRdpBusy)
        {
            return;
        }

        RdpBridge.Configure(RdpSettings.BridgePath);

        var host = RdpTargets.Normalize(RdpSettings.TargetHost);
        var isLocal = RdpTargets.IsLocal(host);

        if (isLocal && !RdpSessionService.CheckReadiness(
                host, RdpSettings.TargetUser, RdpSettings.CredentialSaved, RdpSettings.BridgePath).UserExists)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"本机不存在账户「{RdpSettings.TargetUser}」。\n请先在 Windows 里创建该账户，再回到这里连接。");
            return;
        }

        IsRdpBusy = true;
        RdpStatusText = "正在连接目标会话…";

        try
        {
            // 已经登录着就不用再连了 —— 客户端版再连一次只会把那个桌面接管过来，
            // 当前桌面反而被锁定。这里直接当成"已连接"处理。
            if (isLocal && RdpSessionService.TryGetReusableSession(RdpSettings.TargetUser, out var existing))
            {
                var label = $"账户「{RdpSettings.TargetUser}」已经登录（会话 {existing!.Id}，{existing.StateText}）";
                RdpStatusText = $"{label}，已复用现有会话";
                AppendLog($"[远程桌面] {label}，本次不再新建连接。");
                AppendLog("[远程桌面] Windows 客户端版同时只允许一个交互式会话，重复连接会顶掉你正在使用的那个桌面。");
                RefreshRdpReadiness();
                return;
            }

            var (width, height) = RdpResolutionSize();
            var started = RdpSessionService.Connect(host, out var message, width, height);
            if (!started)
            {
                RdpStatusText = "连接失败";
                AppendLog($"[远程桌面] 连接失败：{message}");
                _ = DialogHelper.ShowMessageAsync("连接失败", message);
                return;
            }

            AppendLog($"[远程桌面] {message}");
            AppendLog("已打开远程桌面窗口。可以先在那里确认登录成功，再回到这里点「开始执行」下发任务。");
            RefreshRdpReadiness();
        }
        finally
        {
            IsRdpBusy = false;
        }
    }

    /// <summary>断开目标会话（任务与收尾设置不受影响）。</summary>
    public void DisconnectSurface()
    {
        if (!RdpTargets.IsLocal(RdpSettings.TargetHost))
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                $"目标是远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，断开操作只能在目标机器上执行（或用任务结束后的「断开会话」收尾方式）。");
            return;
        }

        var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
        if (session is null)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "找不到目标账户的会话，可能尚未连接或已注销。");
            return;
        }

        RdpSessionService.DisconnectSession(session.Id, out var message);
        RdpStatusText = "已请求断开目标会话";
        AppendLog($"[远程桌面] {message}");
        RefreshRdpReadiness();
    }

    /// <summary>重开一个远程桌面窗口，让新的分辨率档位生效。</summary>
    public void ResizeSurface()
    {
        var (width, height) = RdpResolutionSize();
        if (width <= 0 || height <= 0)
        {
            AppendLog("[远程桌面] 当前是自动分辨率，窗口尺寸由系统默认值决定。想固定尺寸请在下拉框里选一个档位。");
            return;
        }

        AppendLog($"[远程桌面] 分辨率档位为 {width} × {height}，下次点「连接目标账户」时生效。");
        AppendLog("[远程桌面] 远程桌面客户端不支持在已连接的窗口上动态改尺寸，需要断开后重新连接。");
    }

    /// <summary>取配置里的分辨率；0 × 0 表示交给系统默认。</summary>
    private (int Width, int Height) RdpResolutionSize()
    {
        var width = RdpResolutions.Normalize(
            RdpSettings.DesktopWidth, RdpResolutions.MinWidth, RdpResolutions.MaxWidth);
        var height = RdpResolutions.Normalize(
            RdpSettings.DesktopHeight, RdpResolutions.MinHeight, RdpResolutions.MaxHeight);
        return (width, height);
    }

    public ObservableCollection<RdpTaskEvent> RdpEvents { get; } = new();

    /// <summary>是否正在连接 / 等待目标会话。</summary>
    public bool IsRdpBusy
    {
        get => _isRdpBusy;
        private set
        {
            if (SetProperty(ref _isRdpBusy, value))
            {
                NotifyRdpCommands();
            }
        }
    }

    public string RdpStatusText
    {
        get => _rdpStatusText;
        private set => SetProperty(ref _rdpStatusText, value);
    }

    public string RdpPhase
    {
        get => _rdpPhase;
        private set
        {
            if (SetProperty(ref _rdpPhase, value))
            {
                OnPropertyChanged(nameof(RdpIsActive));
            }
        }
    }

    public bool RdpIsActive => _rdpPhase is "running";

    public string RdpCurrentTask
    {
        get => _rdpCurrentTask;
        private set => SetProperty(ref _rdpCurrentTask, value);
    }

    public int RdpProgress
    {
        get => _rdpProgress;
        private set => SetProperty(ref _rdpProgress, value);
    }

    public int RdpTotal
    {
        get => _rdpTotal;
        private set => SetProperty(ref _rdpTotal, value);
    }

    /// <summary>当前任务已运行秒数（由 Agent 每次心跳回传）。</summary>
    public int RdpElapsedSeconds
    {
        get => _rdpElapsedSeconds;
        private set
        {
            if (SetProperty(ref _rdpElapsedSeconds, value))
            {
                OnPropertyChanged(nameof(RdpElapsedText));
            }
        }
    }

    public string RdpElapsedText => FormatHelper.FormatSeconds(RdpElapsedSeconds);

    /// <summary>心跳描述，例如「3 秒前」「已失联 8 分 12 秒」。</summary>
    public string RdpHeartbeatText
    {
        get => _rdpHeartbeatText;
        private set => SetProperty(ref _rdpHeartbeatText, value);
    }

    /// <summary>Agent 是否已停止回传心跳（会话断开 / 代理被终止时的典型表现）。</summary>
    public bool RdpIsStale
    {
        get => _rdpIsStale;
        private set => SetProperty(ref _rdpIsStale, value);
    }

    public string RdpReadinessText
    {
        get => _rdpReadinessText;
        private set => SetProperty(ref _rdpReadinessText, value);
    }

    public string RdpSessionSummary
    {
        get => _rdpSessionSummary;
        private set => SetProperty(ref _rdpSessionSummary, value);
    }

    /// <summary>
    /// 会话代理状态文案：区分「还没部署」「已部署但当前不在线」「已部署且在线」。
    /// 与 <see cref="RdpReadinessText"/> 分开呈现 —— 环境就绪 ≠ 代理在跑，两件事别混在一起。
    /// </summary>
    public string RdpAgentStatusText
    {
        get => _rdpAgentStatusText;
        private set => SetProperty(ref _rdpAgentStatusText, value);
    }

    public string RdpBridgePath => RdpBridge.BridgeDir;

    /// <summary>当前目标是不是本机（决定界面上哪些按钮有意义）。</summary>
    public bool RdpTargetIsLocal => RdpTargets.IsLocal(RdpSettings.TargetHost);

    // ---------------- 命令 ----------------

    public ICommand EnableRemoteDesktopCommand =>
        _enableRemoteDesktopCommand ??= new RelayCommand(EnableRemoteDesktop);

    public ICommand DeployAgentCommand => _deployAgentCommand ??= new RelayCommand(DeployRdpAgent);

    public ICommand RemoveAgentCommand => _removeAgentCommand ??= new RelayCommand(RemoveRdpAgent);

    public ICommand ConnectAndRunCommand =>
        _connectAndRunCommand ??= new AsyncRelayCommand(ConnectAndRunAsync, () => !IsRunning && !IsRdpBusy);

    public ICommand DisconnectSessionCommand =>
        _disconnectSessionCommand ??= new RelayCommand(DisconnectTargetSession);

    public ICommand LogoffSessionCommand => _logoffSessionCommand ??= new RelayCommand(LogoffTargetSession);

    public ICommand RefreshRdpCommand => _refreshRdpCommand ??= new RelayCommand(RefreshRdpReadiness);

    public ICommand OpenBridgeFolderCommand => _openBridgeFolderCommand ??= new RelayCommand(() => RdpBridge.OpenBridgeFolder());

    private void NotifyRdpCommands()
    {
        _connectAndRunCommand?.RaiseCanExecuteChanged();
        _enableRemoteDesktopCommand?.RaiseCanExecuteChanged();
        _deployAgentCommand?.RaiseCanExecuteChanged();
        _removeAgentCommand?.RaiseCanExecuteChanged();
        _disconnectSessionCommand?.RaiseCanExecuteChanged();
        _logoffSessionCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanConnectSurface));
    }

    // ---------------- 预检与凭据 ----------------

    public void RefreshRdpReadiness()
    {
        // 桥目录可能刚被改过，每次检查前同步一次
        RdpBridge.Configure(RdpSettings.BridgePath);
        RdpBridge.EnsureDirectory();

        var readiness = RdpSessionService.CheckReadiness(
            RdpSettings.TargetHost, RdpSettings.TargetUser, RdpSettings.CredentialSaved, RdpSettings.BridgePath);

        var notes = new List<string>();
        if (readiness.EditionNote.Length > 0)
        {
            notes.Add(readiness.EditionNote);
        }

        notes.AddRange(readiness.Notes);

        if (readiness.IsLocal)
        {
            var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
            RdpSessionSummary = session is null
                ? "目标账户当前没有会话"
                : $"会话 {session.Id} · {session.UserName} · {session.StateText}";
        }
        else
        {
            RdpSessionSummary = $"远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}：会话状态无法在本机枚举";
        }

        RdpReadinessText = readiness.Problems.Count == 0
            ? (notes.Count == 0 ? "环境就绪，可以连接。" : $"环境就绪，可以连接。（{string.Join("；", notes)}）")
            : string.Join("；", readiness.Problems);

        RdpAgentStatusText = DescribeAgentStatus(readiness);

        RdpSettings.CredentialSaved = readiness.CredentialSaved;
        OnPropertyChanged(nameof(RdpBridgePath));
        OnPropertyChanged(nameof(RdpTargetIsLocal));
    }

    /// <summary>
    /// 把"已部署"（静态：快捷方式在不在）与"在线"（运行期：代理此刻在不在跑）拆成两类文案。
    /// 两者的下一步动作完全不同：没部署要去部署，已部署但不在线要注销目标账户后重新连接。
    /// </summary>
    private static string DescribeAgentStatus(RdpReadiness readiness)
    {
        if (!readiness.IsLocal)
        {
            return "远程主机：代理是否在线需在对方机器上确认";
        }

        if (!readiness.AgentDeployed)
        {
            return "尚未部署会话代理（点「部署会话代理」）";
        }

        return readiness.AgentOnline
            ? "会话代理已部署，且当前在线"
            : "会话代理已部署，但当前不在线（目标账户未登录，或代理未随会话启动）";
    }

    /// <summary>保存目标账户凭据到 Windows 凭据管理器。密码只进内存，不写配置文件。</summary>
    public void SaveRdpCredential(string password)
    {
        if (string.IsNullOrWhiteSpace(RdpSettings.TargetUser))
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请先填写目标账户名。");
            return;
        }

        if (password.Length == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请填写目标账户的登录密码。");
            return;
        }

        if (!RdpSessionService.SaveCredential(RdpSettings.TargetHost, RdpSettings.TargetUser, password, out var message))
        {
            _ = DialogHelper.ShowMessageAsync("保存失败", message);
            return;
        }

        // 存进去要能取回来才作数：内嵌控件连接时是靠取回密码来登录的
        if (RdpSessionService.TryLoadPassword(RdpSettings.TargetHost) is null)
        {
            _ = DialogHelper.ShowMessageAsync("保存异常",
                "凭据写入了，但读不回来，系统远程桌面可能登录失败。\n\n" +
                "常见原因是凭据管理器被组策略限制。");
            AppendLog("警告：凭据保存后无法读回，远程桌面可能无法自动登录。");
        }

        RdpSettings.CredentialSaved = true;
        RdpSessionService.PendingPassword = password;
        SaveConfig();
        AppendLog($"已保存目标账户「{RdpSettings.TargetUser}」的凭据（目标主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，存于 Windows 凭据管理器，配置文件不保存密码）。");
        RefreshRdpReadiness();
    }

    public void ClearRdpCredential()
    {
        RdpSessionService.DeleteCredential(RdpSettings.TargetHost);
        RdpSessionService.PendingPassword = null;
        RdpSettings.CredentialSaved = false;
        SaveConfig();
        AppendLog("已删除保存的目标账户凭据。");
        RefreshRdpReadiness();
    }

    private void EnableRemoteDesktop()
    {
        if (RdpSessionService.EnableRemoteDesktop(out var message))
        {
            AppendLog($"远程桌面：{message}");
        }
        else
        {
            _ = DialogHelper.ShowMessageAsync("开启失败", message);
            AppendLog($"开启远程桌面失败：{message}");
        }

        RefreshRdpReadiness();
    }

    private void DeployRdpAgent()
    {
        // 首次部署要把整个程序（约 140MB）复制到公共目录，同步做会卡住界面
        IsRdpBusy = true;
        RdpStatusText = "正在部署会话代理（首次会复制程序文件，稍等）…";
        AppendLog("正在部署会话代理：把程序复制到所有用户都能访问的公共目录…");

        var bridge = RdpSettings.BridgePath;

        Task.Run(() =>
        {
            var ok = RdpSessionService.DeployAgent(out var message, bridge);
            return (Ok: ok, Message: message);
        }).ContinueWith(task =>
        {
            var (ok, message) = task.Result;
            _dispatcher.TryEnqueue(() =>
            {
                if (ok)
                {
                    AppendLog($"会话代理：{message}");
                    RdpStatusText = "会话代理已部署";
                }
                else
                {
                    AppendLog($"[远程桌面] 会话代理部署失败：{message}");
                    RdpStatusText = "会话代理部署失败";
                    _ = DialogHelper.ShowMessageAsync("部署失败", message + "\n\n请以管理员身份重新启动雪乃酱后再试。");
                }

                IsRdpBusy = false;
                RefreshRdpReadiness();
            });
        });
    }

    private void RemoveRdpAgent()
    {
        RdpSessionService.RemoveAgent(out var message);
        AppendLog($"会话代理：{message}");
        RefreshRdpReadiness();
    }

    private void DisconnectTargetSession()
    {
        if (!RdpTargets.IsLocal(RdpSettings.TargetHost))
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                $"目标是远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，断开操作只能在目标机器上执行（或用任务结束后的「断开会话」收尾方式）。");
            return;
        }

        var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
        if (session is null)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "找不到目标账户的会话，可能尚未连接或已注销。");
            return;
        }

        RdpSessionService.DisconnectSession(session.Id, out var message);
        AppendLog(message);
        RefreshRdpReadiness();
    }

    private void LogoffTargetSession()
    {
        if (!RdpTargets.IsLocal(RdpSettings.TargetHost))
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                $"目标是远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，注销操作只能在目标机器上执行（或用任务结束后的「注销用户」收尾方式）。");
            return;
        }

        var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
        if (session is null)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "找不到目标账户的会话，可能尚未连接或已注销。");
            return;
        }

        // 【防护】注销会销毁会话，会话里的代理和脚本进程会被系统一起终止。
        // 这个按钮紧挨着「连接并执行」，正在跑任务时误点的代价很大，所以先确认。
        if (RdpIsActive || IsRdpBusy)
        {
            _ = ConfirmLogoffWhileRunningAsync(session.Id);
            return;
        }

        RdpSessionService.LogoffSession(session.Id, out var message);
        AppendLog(message);
        RefreshRdpReadiness();
    }

    /// <summary>任务还在跑时的注销确认。选「取消」则什么都不做。</summary>
    private async Task ConfirmLogoffWhileRunningAsync(uint sessionId)
    {
        try
        {
            var result = await DialogHelper.ShowMessageAsync(
                "确认注销目标账户？",
                $"目标账户「{RdpSettings.TargetUser}」的会话里还有任务在跑：\n" +
                $"　　{RdpCurrentTask}（{RdpProgress} / {RdpTotal}，已运行 {RdpElapsedText}）\n\n" +
                "注销会销毁该会话，会话里的代理与脚本进程会被系统一并终止，任务就此中断、进度也无法再回传。\n\n" +
                "如果只是想关掉远程桌面窗口、让任务继续在后台跑，请改用「断开目标会话」——" +
                "断开会保留会话和进程。",
                closeText: "取消",
                primaryText: "仍要注销");

            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                AppendLog("已取消注销目标账户。");
                return;
            }

            RdpSessionService.LogoffSession(sessionId, out var message);
            AppendLog(message);
            RefreshRdpReadiness();
        }
        catch (Exception ex)
        {
            AppendLog($"注销确认对话框失败：{ex.Message}");
        }
    }

    // ---------------- 主流程：连接并执行 ----------------

    /// <summary>
    /// 下发指令 → 用 mstsc 连接目标主机 → 等待目标会话建立（仅本机目标）→
    /// 目标账户登录后由会话代理自动接管执行，主控端轮询状态并弹通知。
    /// </summary>
    public async Task ConnectAndRunAsync()
    {
        // 桥目录要在读写指令之前生效，否则会写到本机 ProgramData 里，远程 Agent 读不到
        RdpBridge.Configure(RdpSettings.BridgePath);

        var host = RdpTargets.Normalize(RdpSettings.TargetHost);
        var isLocal = RdpTargets.IsLocal(host);

        var readiness = RdpSessionService.CheckReadiness(
            host, RdpSettings.TargetUser, RdpSettings.CredentialSaved, RdpSettings.BridgePath);

        if (isLocal && !readiness.UserExists)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"本机不存在账户「{RdpSettings.TargetUser}」。\n请先在 Windows 里创建该账户，再回到这里连接。");
            return;
        }

        if (!readiness.CredentialSaved)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"还没有保存目标账户的凭据（目标主机：{host}）。\n请先填写账户名与密码并保存。");
            return;
        }

        if (isLocal && !readiness.RemoteDesktopEnabled)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接", "系统尚未开启远程桌面，请先点击「开启远程桌面」。");
            return;
        }

        if (isLocal && !readiness.LikelySupported)
        {
            _ = DialogHelper.ShowMessageAsync("当前系统不支持",
                readiness.EditionNote + "\n\nWindows 家庭版不能作为远程桌面服务端，无法登录其它账户。");
            return;
        }

        if (!isLocal && RdpSettings.BridgePath.Trim().Length == 0)
        {
            _ = DialogHelper.ShowMessageAsync("还需要配置共享目录",
                $"目标 {host} 是远程主机，对方读不到本机的指令文件。\n\n" +
                "请先在「指令桥目录」里填一个两台机器都能读写的共享位置（例如 \\\\192.168.1.20\\YukinoBridge），" +
                "并在对方机器上也部署会话代理、指向同一个目录。\n\n" +
                "如果只想切本机另一个账户，把远程主机改回 127.0.0.1 就行。");
            return;
        }

        var enabled = Tasks.Where(t => t.Enabled).ToList();
        if (enabled.Count == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "当前没有启用的任务。");
            return;
        }

        if (!HasUsableTaskConfig())
        {
            _ = DialogHelper.ShowMessageAsync("提示", "还没有设置可执行的脚本配置。");
            return;
        }

        SaveConfig();

        var command = new RdpCommand
        {
            Tasks = enabled.Select(t => (TaskConfig)t.Clone()).ToList(),
            ShutdownAfterDone = Config.ShutdownAfterDone,
            ShutdownDelaySeconds = Config.ShutdownDelaySeconds,
            EnableTimeoutScreenshot = Config.EnableTimeoutScreenshot,
            SessionFinish = RdpSettings.SessionFinish,
            NotifyOnTaskDone = RdpSettings.NotifyOnTaskDone,
            NotifyOnAllDone = RdpSettings.NotifyOnAllDone,
        };

        RdpBridge.EnsureDirectory();
        if (!RdpBridge.WriteCommand(command))
        {
            _ = DialogHelper.ShowMessageAsync("下发失败",
                $"无法写入共享指令文件：{RdpBridge.CommandPath}\n\n请以管理员身份运行雪乃酱，或把程序放在所有用户都有写权限的目录。");
            return;
        }

        RdpBridge.ResetStatus(command.Id);
        _lastRdpEventSeq = 0;
        _rdpCompletionReported = false;
        RdpEvents.Clear();
        RdpProgress = 0;
        RdpTotal = enabled.Count;
        RdpElapsedSeconds = 0;
        RdpCurrentTask = "－";
        RdpPhase = "idle";
        RdpStatusText = "正在连接目标会话…";
        RdpHeartbeatText = "－";
        RdpIsStale = false;
        _rdpLastHeartbeat = null;
        _rdpStaleReported = false;

        // 记录本指令的归属与下发时刻：之后的每次轮询都用它校验 status.json 是不是本指令写的，
        // 并从下发时刻起算启动宽限期（代理接管要花几十秒，期间不该报失联）。
        _rdpCurrentCommandId = command.Id;
        _rdpCommandIssuedAt = DateTimeOffset.Now;
        _rdpAwaitingAgentLogged = false;

        AppendLog($"已下发远程执行指令 {command.Id}，共 {enabled.Count} 个任务，目标：{host} / {RdpSettings.TargetUser}。");

        // 目标账户已经登录着就不再新建连接。
        // Windows 客户端版同时只允许一个交互式会话，重复连接会把目标账户
        // 正在用的那个桌面接管过来（当前桌面则被踢到锁屏），
        // 而这对"把任务跑起来"毫无帮助 —— 代理直接在那个已有会话里跑就行。
        RdpSessionInfo? reusableSession = null;
        var hasReusableSession = isLocal
            && RdpSessionService.TryGetReusableSession(RdpSettings.TargetUser, out reusableSession);

        if (hasReusableSession)
        {
            AppendLog(
                $"检测到账户「{RdpSettings.TargetUser}」已经登录（会话 {reusableSession!.Id}，{reusableSession.StateText}），" +
                "直接复用它，本次不再新建远程桌面连接。");
        }
        else
        {
            // 用系统自带的远程桌面（mstsc）打开独立窗口，分辨率按设置项生成 .rdp
            var (rdpWidth, rdpHeight) = RdpResolutionSize();
            var started = RdpSessionService.Connect(host, out var connectMessage, rdpWidth, rdpHeight);

            if (!started)
            {
                _ = DialogHelper.ShowMessageAsync("连接失败", connectMessage);
                RdpStatusText = "连接失败";
                return;
            }

            AppendLog(connectMessage);
            if (isLocal)
            {
                AppendLog("提示：Windows 客户端版同时只允许一个交互式会话，连接后当前桌面会被锁定，任务在目标账户中执行。");
                AppendLog("任务跑在目标会话里：关闭远程桌面窗口时请选「断开连接」（会话和进程会保留，任务继续跑），不要选「注销」—— 注销会销毁会话，代理与脚本会被系统一并终止。");
            }
            else
            {
                AppendLog("提示：远程目标需要一个双方都能读写的共享目录来传递指令与进度，Agent 会在对方机器上自动接管任务。");
            }
        }

        IsRdpBusy = true;
        StartRdpPolling();

        if (isLocal)
        {
            // 本机目标：先等目标会话真的建立起来（WTS 能枚举到）
            var timeout = RdpSettings.ConnectTimeoutSeconds;
            var target = RdpSettings.TargetUser;
            var connected = await Task.Run(() => RdpSessionService.WaitForSession(
                target,
                timeout,
                CancellationToken.None));

            if (connected)
            {
                var session = reusableSession ?? RdpSessionService.FindSession(target);
                RdpStatusText = $"目标会话已就绪（会话 {session?.Id}），正在等待会话代理上线…";
                AppendLog(hasReusableSession
                    ? $"复用已有会话 {session?.Id}，无需等待登录。"
                    : "目标账户会话已建立。");
                AppendLog("会话代理会随目标账户登录自动启动并常驻等待指令，正在确认它是否在线…");

                // 【不再隔空拉起】代理随登录自启且常驻，主控端只负责确认它上线。
                // 复用已有会话这一路同样要确认 —— 账户本来就登录着，启动项不会重跑，代理很可能不在线。
                var onlineTarget = target;
                var agentOnline = await Task.Run(() => RdpSessionService.WaitForAgentOnline(
                    onlineTarget,
                    timeout,
                    CancellationToken.None));

                IsRdpBusy = false;

                if (agentOnline)
                {
                    RdpStatusText = "会话代理已上线，等待接管任务…";
                    AppendLog("已确认会话代理在线，任务将自动交接给它。");
                    SetMascotState(MascotStates.Work, force: true);
                    ShowTemporaryMascotMessage("代理已经在线待命，开始干活～", 10, 80);
                }
                else
                {
                    RdpStatusText = "目标账户已登录，但会话代理没有上线";
                    AppendLog($"等待会话代理上线超时（{timeout} 秒）：目标账户虽然在会话里，但代理没有随之启动。");
                    AppendLog("原因：代理只在目标账户「登录」那一刻由启动项拉起；账户本来就登录着时，启动项不会重跑，代理也就不在。");
                    SetMascotState(MascotStates.Error, errorReason: "会话代理没有上线，请注销目标账户后重新连接一次。");
                    _ = PromptLogoffAndReconnectAsync();
                }
            }
            else
            {
                IsRdpBusy = false;
                RdpStatusText = "等待目标会话超时";
                AppendLog($"等待目标会话超时（{timeout} 秒）。请确认凭据正确、目标账户允许远程登录，或手动在远程桌面窗口里完成登录。");
                SetMascotState(MascotStates.Error, errorReason: "等待目标账户会话超时，请检查凭据与远程桌面权限。");
            }
        }
        else
        {
            // 远程目标：会话不在本机，等不到也不用等，交给 status.json 轮询反映真实进度
            IsRdpBusy = false;
            RdpStatusText = $"已连接 {host}，等待对方会话代理接管…";
            AppendLog("远程目标无法在本机枚举会话，以下方回传的 status.json 为准；若长时间无进展，请检查对方是否已部署会话代理并指向同一共享目录。");
            SetMascotState(MascotStates.Work, force: true);
            ShowTemporaryMascotMessage("已经连上远程主机，等那边的我回传进度～", 10, 80);
        }

        RefreshRdpReadiness();
    }

    /// <summary>
    /// 代理没上线时的可操作引导：把「注销目标账户 → 重新连接」这条正解摆到用户面前。
    ///
    /// 为什么必须注销后重连：代理只在目标账户登录那一刻由公共启动目录的快捷方式拉起。
    /// 账户本来就登录着时，启动项不会重跑，任何"隔空拉起"的旁路都已经删除，
    /// 唯一能让代理起来的方式就是让账户重新登录一次。
    /// 注销直接复用现有的 WTS 注销能力（<see cref="RdpSessionService.LogoffSession"/>）。
    /// </summary>
    private async Task PromptLogoffAndReconnectAsync()
    {
        try
        {
            var result = await DialogHelper.ShowMessageAsync(
                "目标账户已登录，但会话代理没有上线",
                "目标账户已登录，但会话代理没有上线。\n\n" +
                "请用「注销目标账户」后重新连接一次 —— 登录时启动项会自动拉起代理。\n\n" +
                "原因：代理只在目标账户「登录」那一刻由启动项拉起；账户本来就登录着时，启动项不会重跑，代理也就不在。",
                closeText: "稍后再说",
                primaryText: "注销目标账户");

            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                AppendLog("已选择稍后处理；代理上线前任务不会被接管。");
                return;
            }

            var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
            if (session is null)
            {
                AppendLog("注销目标账户失败：找不到目标账户的会话，可能已经断开或注销。");
                return;
            }

            if (RdpSessionService.LogoffSession(session.Id, out var message))
            {
                AppendLog($"{message}请重新点击「连接并执行」——目标账户登录时启动项会自动拉起代理。");
            }
            else
            {
                AppendLog(message);
            }

            RefreshRdpReadiness();
        }
        catch (Exception ex)
        {
            AppendLog($"注销引导对话框失败：{ex.Message}");
        }
    }

    // ---------------- 状态轮询 ----------------

    private void StartRdpPolling()
    {
        _rdpPollTimer?.Stop();
        _rdpPollTimer = _dispatcher.CreateTimer();
        _rdpPollTimer.Interval = TimeSpan.FromSeconds(1);
        _rdpPollTimer.Tick += (_, _) => PollRdpStatus();
        _rdpPollTimer.Start();
    }

    private void StopRdpPolling()
    {
        _rdpPollTimer?.Stop();
        _rdpPollTimer = null;
    }

    private void PollRdpStatus()
    {
        var status = RdpBridge.TryReadStatus();
        if (status is null)
        {
            return;
        }

        RdpPhase = status.Phase;
        RdpProgress = status.Progress;
        RdpTotal = status.Total;
        RdpElapsedSeconds = status.ElapsedSeconds;
        RdpCurrentTask = string.IsNullOrEmpty(status.CurrentTask) ? "－" : status.CurrentTask;

        // 心跳：Agent 每秒重写 status.json，所以 UpdatedAt 能直接反映"它是不是还活着"。
        // 这正是"任务到底跑到哪了"最关键的信号 —— 之前的界面只显示"运行中"，
        // 会话断开后代理被杀，状态永远停在最后那一帧，看起来就是卡死不动。
        UpdateRdpHeartbeat(status);

        RdpStatusText = string.IsNullOrEmpty(status.StatusText) ? status.Phase : status.StatusText;

        // 本指令是否已收到"全部结束"事件、以及它是否异常收尾。
        // 常驻代理把 Phase 从 done 迅速写回 idle，靠 1Hz 轮询"恰好撞见终态相位"几乎不可能，
        // 所以完成判定必须能由事件驱动（见下）。
        var sawFinished = false;
        var finishedAbnormal = false;

        foreach (var ev in status.Events)
        {
            if (ev.Seq <= _lastRdpEventSeq)
            {
                continue;
            }

            _lastRdpEventSeq = ev.Seq;
            RdpEvents.Add(ev);
            while (RdpEvents.Count > MaxRdpEventLines)
            {
                RdpEvents.RemoveAt(0);
            }

            AppendLog($"[远程] {ev.Time} {ev.Headline}");

            // 主控端这一侧也要弹通知：目标会话里的 Toast 在当前桌面上是看不到的
            if (ev.Kind == RdpEventKinds.TaskDone && RdpSettings.NotifyOnTaskDone)
            {
                if (!NotificationService.ShowTaskDone(ev.TaskName, ev.Index, ev.Total, ev.ElapsedSeconds, ev.IsAbnormal))
                {
                    ShowTemporaryMascotMessage($"远程任务完成：{ev.TaskName}", 8, 70);
                }
            }

            // 只【记录】、不在这里收尾 —— 收尾统一放到循环之后（见下）。
            // 否则中途 StopRdpPolling 后，同一次调用还会继续走到后面的判定，容易重复触发。
            if (ev.Kind == RdpEventKinds.Finished)
            {
                sawFinished = true;
                finishedAbnormal |= ev.IsAbnormal;
            }
        }

        // 完成判定：两条路任一即可，都不依赖"恰好轮询到终态相位"这个时序前提 ——
        //   ① 收到本指令的 "finished" 事件：它持久化在 Events 里、跨 idle 心跳保留，最可靠；
        //   ② 恰好轮询到终态相位：logoff 收尾时代理被连带杀掉、done 留盘，这条会命中。
        // 两者可能同时命中，收尾动作由 ReportRdpCompletion 的幂等标志保证只执行一次。
        if (sawFinished || status.Phase is "done" or "stopped" or "error")
        {
            ReportRdpCompletion(status, finishedAbnormal || status.Phase == "error");
        }
    }

    /// <summary>
    /// 远程执行的收尾 —— "跑完了"这件事的全部用户感知都在这。
    /// 幂等：一整轮指令只收尾一次（完成信号可能来自 Finished 事件，也可能来自终态相位，
    /// 两者可能同时命中，必须保证「停轮询 / 刷统计 / 看板娘 / 汇总通知 / 结束日志 / 刷新预检」只做一次）。
    /// </summary>
    /// <param name="status">完成那一刻的状态快照（用于取结束文案）。</param>
    /// <param name="abnormal">本次是否异常收尾（error 相位，或带 abnormal 标记的 Finished 事件）。</param>
    private void ReportRdpCompletion(RdpStatus status, bool abnormal)
    {
        if (_rdpCompletionReported)
        {
            return;
        }

        _rdpCompletionReported = true;

        StopRdpPolling();
        RefreshStats();

        // 汇总正文优先取 Finished 事件的 Message。不要用 status.StatusText ——
        // 常驻代理 1Hz 的 idle 心跳会把它覆盖成「代理在线，等待指令。」，
        // 日志里已实证这个竞态真的命中过（汇总通知正文变成了一句废话）。
        var summary = status.Events.LastOrDefault(e => e.Kind == RdpEventKinds.Finished)?.Message;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = string.IsNullOrWhiteSpace(status.StatusText) ? "全部任务结束" : status.StatusText;
        }

        if (abnormal)
        {
            SetMascotState(MascotStates.Error, errorReason: "远程会话执行出现异常，请查看运行日志。");
        }
        else
        {
            SetMascotState(MascotStates.Rest, force: true);
            ShowTemporaryMascotMessage("远程任务全部结束啦～", 10, 60);
        }

        if (RdpSettings.NotifyOnAllDone)
        {
            var posted = NotificationService.ShowSummary("雪乃酱：远程任务结束", summary);

            // 本地多用户场景下主控端会话大概率处于锁定状态（mstsc 接管了控制台），
            // 锁定会话不弹 Toast 横幅，通知只会静默进操作中心。
            // 只要无法确认横幅已展示（发送失败或窗口非激活），就落一份暂存，
            // 等窗口重新激活时由 MainWindow 补发。
            if (!posted || !MainWindow.IsWindowActive)
            {
                PendingNotificationStore.Save("雪乃酱：远程任务结束", summary);
            }
        }

        AppendLog($"远程执行结束：{summary}（会话处理方式：{SessionFinishModes.Label(RdpSettings.SessionFinish)}）。");
        RefreshRdpReadiness();
    }

    /// <summary>
    /// 窗口重新激活（用户切回主用户桌面 / 应用重启）时补发锁屏期间错过的完成通知。
    /// 由 MainWindow 的 Activated 事件调用；TryConsume 天然幂等（取出即删）。
    /// </summary>
    public void PresentPendingNotification()
    {
        var pending = PendingNotificationStore.TryConsume();
        if (pending is null)
        {
            return;
        }

        NotificationService.ShowSummary(pending.Value.Title, pending.Value.Body);
        ShowTemporaryMascotMessage("补发一条刚才错过的完成通知～", 8, 60);
        AppendLog("窗口重新激活，已补发锁屏期间错过的完成通知。");
    }

    /// <summary>
    /// 依据 status.json 的 UpdatedAt 刷新心跳显示，并在长时间无更新时给出失联告警。
    ///
    /// 为什么必须有这个：Agent 和它启动的脚本都活在目标会话里，
    /// 会话一旦被注销（而不是"断开"），里面的进程会被系统连带终止，
    /// 那就再也没人写 status.json 了 —— Phase 会永远停在 running。
    /// 只靠"运行中"三个字根本分不清"任务在跑"和"代理已经死了"。
    ///
    /// 判定分三步，缺一不可（顺序很重要）：
    ///   ① 归属校验 —— 桥里的 status.json 是不是【当前这条指令】写的。不是就说明
    ///      代理还没接管（残留的是上一轮指令的状态），这是正常过渡态，绝不报失联。
    ///   ② 启动宽限期 —— 从下发指令起 N 秒内不报失联，覆盖"登录 + 拉起代理 + WinUI 初始化"。
    ///   ③ 时间戳判定 —— 过了宽限期后，才允许按"多久没更新"判定真正的失联。
    /// </summary>
    private void UpdateRdpHeartbeat(RdpStatus status)
    {
        var grace = RdpHeartbeat.ResolveGraceSeconds(RdpSettings.ConnectTimeoutSeconds);
        var result = RdpHeartbeat.Evaluate(
            status.UpdatedAt,
            status.Phase,
            DateTimeOffset.Now,
            _rdpCurrentCommandId,
            status.CommandId,
            _rdpCommandIssuedAt,
            grace);

        // ① 归属不匹配：桥里还是上一轮指令的 status（或本指令的状态尚未落地）。
        //    这是"等待目标会话接管"的过渡态 —— 绝不报失联，只给中性提示。
        if (result.AwaitingAgent)
        {
            RdpHeartbeatText = result.Text;
            RdpIsStale = false;

            if (!_rdpAwaitingAgentLogged)
            {
                _rdpAwaitingAgentLogged = true;
                AppendLog("等待目标会话中的代理接管（尚未收到本指令的心跳，属正常过渡）。");
            }

            return;
        }

        _rdpAwaitingAgentLogged = false;

        if (!result.Parsed)
        {
            RdpHeartbeatText = "未知";
            return;
        }

        RdpHeartbeatText = result.Text;

        if (!result.IsStale)
        {
            _rdpLastHeartbeat = result.UpdatedAt;

            // 心跳恢复（例如会话重连后代理重新接管、或宽限期里代理刚起来）时补一条「已恢复」，
            // 只报一次 —— 否则日志会永远停在"断开连接"那三行结论上，
            // 用户翻日志只会看到"断开连接"，其实任务一直在跑（这正是本次要修的假阳性观感）。
            if (_rdpStaleReported)
            {
                AppendLog("✓ 会话代理心跳已恢复，任务继续运行。");
                _rdpStaleReported = false;
            }

            RdpIsStale = false;
            return;
        }

        RdpIsStale = true;

        if (_rdpStaleReported)
        {
            // 只报一次，避免每秒刷屏
            return;
        }

        _rdpStaleReported = true;

        var last = (_rdpLastHeartbeat ?? result.UpdatedAt).ToString("HH:mm:ss");
        var summary = $"任务「{RdpCurrentTask}」停在 {RdpProgress} / {RdpTotal}（已运行 {RdpElapsedText}）";
        RdpStatusText = $"⚠ 代理已失联（最后心跳 {last}）";

        AppendLog($"⚠ 已超过 {RdpHeartbeat.TimeoutSeconds} 秒没收到会话代理心跳。最后状态：{summary}。");
        AppendLog("目标会话很可能已经断开或被注销 —— 会话里的代理与脚本进程会随之终止，所以进度不再更新。");
        AppendLog("如果是你主动断开的远程桌面：请用「断开连接」而不是「注销目标账户」，任务才能在后台继续跑。");

        SetMascotState(MascotStates.Error, errorReason: "会话代理已失联，请检查目标会话是否还在。");

        if (RdpSettings.NotifyOnAllDone)
        {
            NotificationService.ShowSummary("雪乃酱：会话代理失联", $"最后状态：{summary}");
        }
    }

    /// <summary>本地执行时每完成一个任务也发通知（由设置开关控制）。</summary>
    private void OnRunnerTaskCompleted(object? sender, TaskCompletedInfo info) => Post(() =>
    {
        AppendLog($"任务完成：{info.Name}（{FormatHelper.FormatSeconds(info.ElapsedSeconds)}）{info.Status}");

        if (RdpSettings.NotifyLocalTaskDone)
        {
            NotificationService.ShowTaskDone(info.Name, info.Index, info.Total, info.ElapsedSeconds, info.IsAbnormal);
        }
    });
}
