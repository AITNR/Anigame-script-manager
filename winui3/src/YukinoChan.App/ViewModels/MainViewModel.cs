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

                // 「执行中（N 块并行）」/「已完成」这类聚合文案取决于它
                RaisePanelChanged();
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
        ReloadChannels(refreshReadiness: false);
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

    /// <summary>
    /// 记录主窗口几何，用于下次启动时"记忆上次大小"。
    ///
    /// <paramref name="bounds"/> 为 null 表示当前处于最大化/最小化等"非还原"状态：
    /// 此时只更新 <see cref="WindowConfig.Maximized"/>，保留上一次的还原尺寸，
    /// 否则最大化状态下退出会把整屏尺寸当成小窗尺寸记下来。
    ///
    /// 这是随时可能发生的后台动作，所以：不写运行日志、不弹窗，值没变时连盘都不写。
    /// 注意它只改 <c>Config.Window</c>，不会把界面上尚未"保存配置"的任务改动一起落盘。
    /// </summary>
    public void SaveWindowPlacement(WindowPlacement.Placement? bounds, bool maximized)
    {
        var window = Config.Window;

        if (bounds is null)
        {
            if (window.Maximized == maximized)
            {
                return;
            }

            window.Maximized = maximized;
        }
        else
        {
            var placement = bounds.Value;
            if (window.X == placement.X && window.Y == placement.Y &&
                window.Width == placement.Width && window.Height == placement.Height &&
                window.Maximized == maximized)
            {
                return;
            }

            window.X = placement.X;
            window.Y = placement.Y;
            window.Width = placement.Width;
            window.Height = placement.Height;
            window.Maximized = maximized;
        }

        try
        {
            _configManager.Save(Config);
        }
        catch
        {
            // 窗口几何记不住不该影响用户关窗口
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

    /// <summary>
    /// 开始执行。任务按「执行通道」分组：未分配通道的走本地（主控端当前会话），
    /// 分配了通道的下发到各自的目标会话 —— **通道之间并行，通道内部仍按 order 串行**。
    ///
    /// 分组与截断全部由纯函数 <see cref="RdpChannelPlanner.Plan"/> 决定（可单测），
    /// 这里只负责把规划结果落到运行时。
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        SaveConfig();

        var plan = RdpChannelPlanner.Plan(Tasks, Config.Rdp.Channels, RdpSettings.Enabled);

        if (plan.IsEmpty)
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

        // 没启动的任务必须说明原因（通道停用 / 没配账户 / 超并发上限 / 引用了不存在的通道）。
        // 静默丢掉最糟 —— 用户会以为它们在跑。
        foreach (var skipped in plan.Skipped)
        {
            AppendLog("⚠ " + skipped.Describe());
        }

        // 并发上限单独再点一次名：这是唯一一种"看起来该跑、其实没跑"的截断，
        // 而且用户自己就能解决（合并通道 / 分两轮）。其余原因多半是配置笔误，上面那行已经说明白了。
        var overflow = plan.Skipped.Where(s => s.Reason.Contains("并发上限")).ToList();
        if (overflow.Count > 0)
        {
            var names = string.Join("、", overflow.Select(s => s.Channel?.DisplayName ?? "未命名通道"));
            AppendLog(
                $"⚠ 并发上限 {RdpNativeLimits.MaxSessions}：本轮有 {overflow.Count} 条通道被截断（{names}），" +
                "它们的任务这一轮不会执行 —— 想全跑就分两轮，或把任务合并到排在前面的通道。");
        }

        ResetSessions(plan);

        _runStoppedOrError = false;
        _runTaskErrorCount = 0;
        _runParts.Clear();
        _runStartedAt = DateTimeOffset.Now;
        _runEndedAt = null;
        _lastRunAbnormal = false;
        _pendingRuns = plan.ToLaunch.Count + (plan.LocalTasks.Count > 0 ? 1 : 0);

        if (_pendingRuns == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                "启用的任务都落在未启动的通道里（见运行日志里的原因），本轮没有可执行的任务。");
            return;
        }

        IsRunning = true;

        // 有通道被并发上限截断时弹一次说明（放在"本轮还有任务要跑"之后，免得和空任务提示叠两个窗）
        if (overflow.Count > 0)
        {
            _ = DialogHelper.ShowMessageAsync("并发上限",
                $"同时最多并行 {RdpNativeLimits.MaxSessions} 条会话通道，本轮有 {overflow.Count} 条被截断：\n\n" +
                string.Join("\n", overflow.Select(s => "· " + s.Describe())) +
                "\n\n被截断通道里的任务本轮不会执行。可以让这些通道分两轮跑，或把任务合并到排在前面的通道。");
        }

        if (plan.LocalTasks.Count > 0)
        {
            StartLocalRun(plan.LocalTasks);
        }

        if (plan.ToLaunch.Count > 0)
        {
            StartRdpPolling();
            AppendLog(
                $"本轮启动 {plan.ToLaunch.Count} 条会话通道并行执行：" +
                string.Join("、", plan.ToLaunch.Select(p => p.Channel.DisplayName)) + "。");

            // 只有远程通道、没有本地任务时，这里得把看板娘推进"工作"态 ——
            // 否则整轮跑下来看板娘一直显示"待命中"，与"正在执行"完全矛盾。
            if (plan.LocalTasks.Count == 0)
            {
                SetMascotState(MascotStates.Work, force: true);
                ShowTemporaryMascotMessage(
                    plan.ToLaunch.Count > 1
                        ? $"准备并行 {plan.ToLaunch.Count} 条会话通道，我会盯着进度～"
                        : "正在把任务交给会话通道，我会盯着进度～",
                    10, 80);
            }

            _ = RunChannelsAsync(plan.ToLaunch);
        }

        RaisePanelChanged();
    }

    /// <summary>
    /// 按本轮规划重建会话列表：本地执行组（若有本地任务）+ 每条要启动的通道。
    /// 上一轮的会话整体丢弃（菜单项与"已结束"状态在 M4 里按通道保留）。
    /// </summary>
    private void ResetSessions(ExecutionPlan plan)
    {
        _sessions.Clear();

        if (plan.LocalTasks.Count > 0)
        {
            var local = new RdpChannelSession(null, RdpBridge.ForChannel(null));
            local.BeginLocal(plan.LocalTasks.Count);
            _sessions.Add(local);
        }

        foreach (var entry in plan.ToLaunch)
        {
            var session = new RdpChannelSession(entry.Channel, RdpBridge.ForChannel(entry.Channel));
            session.BeginLocal(entry.Tasks.Count);   // 归属性（CommandId）等真正下发时再记
            _sessions.Add(session);
        }

        // 界面默认展示第一条远程通道（没有就展示本地组）
        _activeSession = _sessions.FirstOrDefault(s => s.IsRemote) ?? _sessions.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveSession));
        OnPropertyChanged(nameof(ActiveChannelName));
        OnPropertyChanged(nameof(RdpEvents));
        RaiseSessionsChanged();
        MirrorActiveSession();
    }

    /// <summary>
    /// 本地执行组：跑在主控端当前会话里。与改造前完全一致，只是任务集合来自规划结果
    /// （只含 channel_id 为空的那批）。
    /// </summary>
    private void StartLocalRun(IReadOnlyList<TaskConfig> tasks)
    {
        StatusText = "启动确认中";
        CurrentTaskName = "-";
        ElapsedText = "00:00";
        ProgressText = $"0 / {tasks.Count}";

        // 同一份进度也记进本地执行组：面板 / 主页概览 / 整轮汇总都从会话里读
        LocalGroup?.UpdateLocal(
            phase: "running",
            statusText: "启动确认中",
            progress: 0,
            total: tasks.Count,
            currentTask: "-",
            elapsedSeconds: 0);
        RaisePanelChanged();

        SetMascotState(MascotStates.Work, force: true);
        ShowTemporaryMascotMessage("正在启动任务，我会先帮你确认状态。", 8, 75);

        var snapshots = tasks.Select(t => t.Clone()).ToList();

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

    /// <summary>
    /// 各通道并行启动 —— 互不等待（各自去等自己的会话与代理上线），
    /// 所以慢的通道不会拖住快的通道。
    /// </summary>
    private async Task RunChannelsAsync(IReadOnlyList<ChannelPlan> plans)
    {
        var jobs = new List<Task>();

        foreach (var entry in plans)
        {
            var session = _sessions.FirstOrDefault(s =>
                s.IsRemote && string.Equals(s.ChannelId, entry.Channel.Id, StringComparison.OrdinalIgnoreCase));

            if (session is null)
            {
                continue;
            }

            // 预检（凭据 / 账户 / 远程桌面开关 / 共享目录）不通过就跳过这条通道 ——
            // 但计数必须减掉，否则整轮永远不会判定结束。
            if (!ValidateChannel(entry.Channel, entry.Tasks))
            {
                AppendLog($"[通道：{entry.Channel.DisplayName}] 预检未通过，该通道本轮不执行（原因见上方提示）。");
                session.MarkCompletionReported();
                FinishRunPart();
                continue;
            }

            jobs.Add(RunChannelAsync(session, entry.Tasks));
        }

        if (jobs.Count > 0)
        {
            await Task.WhenAll(jobs);
        }
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
        RequestRemoteStop(emergency: false);
    }

    public void EmergencyStop()
    {
        _runner?.RequestEmergencyStop();
        RequestRemoteStop(emergency: true);
    }

    /// <summary>
    /// 请求**全部通道**的目标会话停止正在跑的任务。
    ///
    /// 为什么需要它：远程任务跑在目标会话的代理进程里，本地 <c>_runner</c> 管不到它 ——
    /// 两个会话之间只有桥文件能通信，所以把"停"这个意图写进各通道自己的 stop.json，
    /// 由代理每秒检查一次。带 CommandId 做归属校验，避免把下一轮指令也一起停掉。
    /// </summary>
    private void RequestRemoteStop(bool emergency)
    {
        var sent = 0;

        foreach (var session in _sessions.ToList())
        {
            if (!session.IsRemote || !session.HasCommand)
            {
                continue;
            }

            if (!session.WriteStop(emergency, out var error))
            {
                AppendChannelLog(session, $"⚠ 停止请求写入桥目录失败：{error}（目标会话可能收不到停止指令）");
                continue;
            }

            session.MarkStopping(emergency);
            sent++;
        }

        if (sent == 0)
        {
            return;
        }

        AppendLog(emergency
            ? $"已向 {sent} 个通道发送紧急停止请求，等待代理响应。"
            : $"已向 {sent} 个通道发送停止请求，等待代理收尾。");

        MirrorActiveSession();
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
        LocalGroup?.UpdateLocal(statusText: status, phase: LocalPhaseFor(status));
        RaisePanelChanged();

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
        LocalGroup?.UpdateLocal(progress: progress.Current, total: progress.Total);
        RaisePanelChanged();
    });

    private void OnRunnerTaskStarted(object? sender, (string Name, int Index, int Total) info) => Post(() =>
    {
        CurrentTaskName = info.Name;
        ElapsedText = "00:00";
        ProgressText = $"{info.Index} / {info.Total}";
        LocalGroup?.UpdateLocal(
            currentTask: info.Name,
            progress: info.Index,
            total: info.Total,
            elapsedSeconds: 0);
        RaisePanelChanged();
    });

    private void OnRunnerElapsed(object? sender, int seconds) => Post(() =>
    {
        ElapsedText = FormatHelper.FormatSeconds(seconds);
        LocalGroup?.UpdateLocal(elapsedSeconds: seconds);
        RaisePanelChanged();
    });

    private void OnRunnerTaskLaunchSucceeded(object? sender, string taskName) => Post(() =>
    {
        if (MascotState == MascotStates.Error)
        {
            return;
        }

        StatusText = "运行中";
        LocalGroup?.UpdateLocal(statusText: "运行中", phase: "running");
        RaisePanelChanged();
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
        _runner = null;
        _runTask = null;
        _runStoppedOrError |= stoppedOrError;

        var stopped = StatusText.Contains("停止");
        string summary;

        if (stoppedOrError)
        {
            if (stopped && MascotState != MascotStates.Error)
            {
                StatusText = "已停止";
                SetMascotState(MascotStates.Rest);
                summary = "被手动停止";
            }
            else
            {
                StatusText = "已完成（有异常）";
                var count = Math.Max(1, _runTaskErrorCount);
                SetMascotState(MascotStates.Error, errorReason: $"任务结束，发生了 {count} 次任务异常。");
                summary = $"发生 {count} 次任务异常";
            }
        }
        else
        {
            StatusText = "已完成";
            summary = "全部任务结束";
        }

        // 与远程通道同构：只登记这块自己的结果，终态交给整轮汇总 ——
        // 多通道并行时"本地跑完了"不等于"整轮结束"，看板娘不能提前休息。
        // 主动停止不算异常（它不该把整轮判成"有异常"，只影响是否放行自动退出）。
        _runParts.Add(new RunPartResult("本地执行", false, stoppedOrError && !stopped, summary));

        // 本地执行组同步落终态：单块跑本地时，面板/概览就是靠它显示"已完成"
        LocalGroup?.UpdateLocal(
            phase: stopped ? "stopped" : stoppedOrError ? "error" : "done",
            statusText: StatusText);
        RaisePanelChanged();

        if (_pendingRuns > 1)
        {
            ShowTemporaryMascotMessage(
                stoppedOrError && !stopped ? "本地这块结束了，但有异常。" : "本地这块顺利完成啦～",
                12, 60);
        }

        // 本地这一块收尾：还有远程通道在跑就只减计数，
        // 整轮结束才落终态、刷新预检、放行"完成后自动退出"（在 FinishRunPart 里）。
        FinishRunPart();
    });

    /// <summary>
    /// 把 <c>ScriptRunner</c> 的中文状态映射成会话字段用的 phase。
    /// 只有 <c>running</c> 算"活动"（概览卡的高亮与"当前展示通道"的判定都用它），
    /// 其余都是终态。判定顺序与 <see cref="OnRunnerStatus"/> 里原有的异常识别保持一致。
    /// </summary>
    private static string LocalPhaseFor(string status)
    {
        if (status.Contains("停止") || status.Contains("暂停"))
        {
            return "stopped";
        }

        if (status.Contains("异常") || status.Contains("错误") || status.Contains("失败") || status.Contains("报错"))
        {
            return "error";
        }

        return status.Contains("完成") ? "done" : "running";
    }

    // ---------------- 日志 ----------------

    public void AppendLog(string message)
    {
        var line = _logger.Log(message);
        AppendLogLine(line);
    }

    /// <summary>
    /// 带通道前缀的日志：多通道并行混跑时，一眼看出这条是哪条通道的。
    /// 前缀与通道页的展示名一致（形如 <c>[通道：1 号机 · Player2]</c>）。
    /// </summary>
    public void AppendChannelLog(RdpChannelSession session, string message) =>
        AppendLog($"[通道：{session.DisplayName}] {message}");

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
            // M4：静态项由 "rdp" 改名 "channels"（管理/预检页）；"channel" 是每条通道页。
            // 旧键 "rdp" 留着兼容（外部/旧代码仍可能用它导航）。
            "channels" or "rdp" => "每条通道就是一个目标账户的专属跑道，在这里配好就能并行跑任务了。",
            "channel" => "这条通道的画面和进度都在这里，直接点画面就能操作远端。",
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

    private DispatcherQueueTimer? _rdpPollTimer;
    private bool _isRdpBusy;
    private string _rdpStatusText = "未连接";
    private string _rdpPhase = "idle";
    private string _rdpCurrentTask = "－";
    private int _rdpProgress;
    private int _rdpTotal;
    private int _rdpElapsedSeconds;
    private string _rdpHeartbeatText = "－";
    private bool _rdpIsStale;

    /// <summary>
    /// 本轮执行的全部通道会话（含「本地执行」组）。进度 / 指令归属 / 事件序号 / 完成收尾
    /// 都按通道独立一份 —— 原来散在这里的那套单通道字段（<c>_rdpCurrentCommandId</c>、
    /// <c>_lastRdpEventSeq</c>、<c>_rdpCompletionReported</c> …）已整体下沉进
    /// <see cref="RdpChannelSession"/>（Services/RdpChannelSession.cs）。
    ///
    /// 多通道并行时这些状态必须每通道一份：留在 VM 里就只能是全局单例，
    /// 两条通道会互相覆盖 commandId 与事件序号，完成判定也会串台。
    /// </summary>
    private readonly List<RdpChannelSession> _sessions = new();

    /// <summary>界面上正在展示的那个通道（进度卡片 / 事件列表跟着它走）。</summary>
    private RdpChannelSession? _activeSession;

    /// <summary>无活跃通道时的空事件列表（保证绑定属性不返回 null）。</summary>
    private readonly ObservableCollection<RdpTaskEvent> _emptyEvents = new();

    /// <summary>本轮还剩几块没跑完：本地执行算一块，每条远程通道各算一块（归零即整轮结束）。</summary>
    private int _pendingRuns;

    /// <summary>
    /// 本轮各块（本地执行 / 每条通道）的收尾结果，**整轮结束**时才拿来出汇总与定看板娘终态。
    ///
    /// 为什么要攒着：多通道并行时各块收尾时间不同。若每块收尾就直接改看板娘状态，
    /// A 通道跑完而 B 还在跑时看板娘会提前"休息" —— 与事实不符。取最差见 <see cref="RunSummary"/>。
    /// </summary>
    private readonly List<RunPartResult> _runParts = new();

    /// <summary>
    /// 本轮是否被停止或出现异常 —— 决定整轮结束时是否放行「完成后自动退出」。
    /// 多通道下"一块结束"不等于"整轮结束"，所以不能沿用本地 runner 的单次结果。
    /// </summary>
    private bool _runStoppedOrError;

    /// <summary>
    /// 本轮执行的墙钟起点 / 终点（<c>default</c> = 还没跑过）。
    /// 多块并行时"耗时"必须取整个轮次 —— 单块的耗时代表不了另外几条通道。
    /// </summary>
    private DateTimeOffset _runStartedAt;
    private DateTimeOffset? _runEndedAt;

    /// <summary>上一轮是否有块异常收尾（多块结束时状态面板要显示"已完成（有异常）"）。</summary>
    private bool _lastRunAbnormal;

    /// <summary>
    /// 设置页单通道入口用的伪通道 id。配置里的通道 id 是 8 位十六进制，
    /// 双下划线开头不会与它们相撞。
    /// </summary>
    private const string SettingsChannelId = "__settings__";

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

    // ---------------- 会话通道（多通道并行） ----------------

    /// <summary>任务页「执行通道」下拉的选项（首项固定是本地执行）。</summary>
    public ObservableCollection<ChannelChoice> ChannelChoices { get; } = new();

    /// <summary>
    /// 本轮会话集合发生变化 —— 主窗口据此重建左侧菜单里的通道项。
    ///
    /// 用事件而不是直接绑 <see cref="Sessions"/>：菜单项要"插到指定位置 + 带分组标题 + 结束后保留"，
    /// 由窗口统一重建比双向同步可靠得多（也避开 NavigationView 选中态与集合变更的竞态）。
    /// </summary>
    public event EventHandler? SessionsChanged;

    /// <summary>
    /// 刷新任务页下拉选项与任务列表上的通道名。
    /// 通道配置增删改之后要调一次。
    /// </summary>
    public void RefreshChannelChoices()
    {
        ChannelChoices.Clear();
        ChannelChoices.Add(new ChannelChoice { Id = string.Empty, Name = "本地执行（当前会话）" });

        foreach (var channel in Config.Rdp.Channels)
        {
            ChannelChoices.Add(new ChannelChoice { Id = channel.Id, Name = channel.DisplayName });
        }

        // 任务本身只存 channel_id，列表上要显示的名字得从配置反查（含"指向已删除通道"的情形）
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in Config.Rdp.Channels)
        {
            byId[channel.Id] = channel.DisplayName;
        }

        foreach (var task in Tasks)
        {
            var id = (task.ChannelId ?? string.Empty).Trim();
            task.ChannelDisplay = id.Length == 0
                ? "本地执行"
                : byId.TryGetValue(id, out var name) ? name : $"未知通道（{id}）";
        }

        OnPropertyChanged(nameof(ChannelChoices));
    }

    private void RaiseSessionsChanged()
    {
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(HasRemoteSession));
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 通道管理（ChannelsPage，M4 / 计划书 §8.3） ----------------

    /// <summary>
    /// 通道列表的界面镜像。
    ///
    /// 为什么不让界面直接绑 <c>Config.Rdp.Channels</c>：那边是 <see cref="List{T}"/>，
    /// 增删不会通知界面；这里包一层 <see cref="ObservableCollection{T}"/>，
    /// 由 <see cref="ReloadChannels"/> 单向同步 —— **配置始终是唯一事实来源**，镜像只读不反写。
    /// </summary>
    public ObservableCollection<RdpChannel> Channels { get; } = new();

    private RdpChannel? _selectedChannel;
    private string _channelReadinessText = string.Empty;
    private string _channelSessionSummary = string.Empty;
    private string _channelSameHostHint = string.Empty;
    private string _channelAgentStatusText = string.Empty;
    private string _channelCredentialText = string.Empty;

    private RelayCommand? _addChannelCommand;
    private RelayCommand? _duplicateChannelCommand;
    private RelayCommand? _removeChannelCommand;
    private RelayCommand? _refreshChannelCommand;
    private RelayCommand? _clearChannelCredentialCommand;
    private RelayCommand? _deployChannelAgentCommand;
    private RelayCommand? _openChannelBridgeFolderCommand;

    /// <summary>当前正在编辑的通道（通道编辑卡的绑定源）。为 null 时卡片整体不可用。</summary>
    public RdpChannel? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (!SetProperty(ref _selectedChannel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedChannel));
            OnPropertyChanged(nameof(ChannelResolutionKey));
            OnPropertyChanged(nameof(ChannelBridgeDir));
            RefreshChannelReadiness();
        }
    }

    public bool HasSelectedChannel => _selectedChannel is not null;

    /// <summary>该通道的预检结论（环境层面能不能连）。</summary>
    public string ChannelReadinessText
    {
        get => _channelReadinessText;
        private set => SetProperty(ref _channelReadinessText, value);
    }

    public string ChannelSessionSummary
    {
        get => _channelSessionSummary;
        private set => SetProperty(ref _channelSessionSummary, value);
    }

    /// <summary>
    /// 同主机多通道提示（空 = 没有冲突）。
    ///
    /// Windows 客户端版同时只允许一个交互式会话（计划书 §3.3 / D5）：
    /// 同一台机器上配多条通道，它们会互相接管同一块桌面 —— 后连的把先连的顶掉。
    /// 按 D5 的决定**只提示不限制**（不禁用、不排队），所以这里只是把事实说清楚。
    /// </summary>
    public string ChannelSameHostHint
    {
        get => _channelSameHostHint;
        private set
        {
            if (SetProperty(ref _channelSameHostHint, value))
            {
                OnPropertyChanged(nameof(HasSameHostHint));
            }
        }
    }

    /// <summary>有没有同主机冲突（XAML 里直接绑 InfoBar 的 IsOpen）。</summary>
    public bool HasSameHostHint => _channelSameHostHint.Length > 0;

    /// <summary>该通道的代理状态（「已部署/在线」两件事分开说，与全局页口径一致）。</summary>
    public string ChannelAgentStatusText
    {
        get => _channelAgentStatusText;
        private set => SetProperty(ref _channelAgentStatusText, value);
    }

    /// <summary>该通道的凭据是否已存（文案版，供列表与编辑卡显示）。</summary>
    public string ChannelCredentialText
    {
        get => _channelCredentialText;
        private set => SetProperty(ref _channelCredentialText, value);
    }

    /// <summary>该通道的分辨率档位（按通道存，不再共用一个全局档位）。</summary>
    public string ChannelResolutionKey
    {
        get => _selectedChannel is null
            ? RdpResolutions.ToValue(0, 0)
            : RdpResolutions.ToValue(_selectedChannel.DesktopWidth, _selectedChannel.DesktopHeight);
        set
        {
            if (_selectedChannel is null)
            {
                return;
            }

            if (string.Equals(ChannelResolutionKey, value, StringComparison.Ordinal))
            {
                return;
            }

            var (width, height) = RdpResolutions.Parse(value);
            _selectedChannel.DesktopWidth = width;
            _selectedChannel.DesktopHeight = height;
            OnPropertyChanged(nameof(ChannelResolutionKey));
            OnPropertyChanged(nameof(ChannelResolutionLabel));
            SaveConfig();
        }
    }

    public string ChannelResolutionLabel => _selectedChannel is null
        ? "（未选择通道）"
        : RdpResolutions.Label(_selectedChannel.DesktopWidth, _selectedChannel.DesktopHeight);

    /// <summary>该通道的指令桥目录（留空 bridge_path 时按账户名派生，与目标会话里的代理算出来的一致）。</summary>
    public string ChannelBridgeDir => _selectedChannel is null
        ? string.Empty
        : RdpChannelPaths.Resolve(_selectedChannel.BridgePath, _selectedChannel.User);

    /// <summary>
    /// 把配置里的通道同步进界面镜像，并尽量保住当前选中的那条。
    ///
    /// <paramref name="refreshReadiness"/> 默认 true；<c>Initialize()</c> 里传 false ——
    /// 预检要跑 tasklist / powershell / reg，启动路径上没必要为"用户还没打开的页面"付这笔钱，
    /// 页面 <c>OnLoaded</c> 会自己刷新一次。
    /// </summary>
    public void ReloadChannels(bool refreshReadiness = true)
    {
        var keepId = _selectedChannel?.Id;

        Channels.Clear();
        foreach (var channel in Config.Rdp.Channels)
        {
            Channels.Add(channel);
        }

        // 选中项按 id 复原：集合被清空重建后原引用已失效，直接用旧引用会让编辑卡显示空白
        _selectedChannel = Channels.FirstOrDefault(c => c.Id == keepId) ?? Channels.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(HasSelectedChannel));
        OnPropertyChanged(nameof(ChannelResolutionKey));
        OnPropertyChanged(nameof(ChannelResolutionLabel));
        OnPropertyChanged(nameof(ChannelBridgeDir));
        OnPropertyChanged(nameof(Channels));

        RefreshChannelChoices();

        if (refreshReadiness)
        {
            RefreshChannelReadiness();
        }
    }

    /// <summary>
    /// 选中通道的预检。与全局 <see cref="RefreshRdpReadiness"/> 的区别：参数全部来自该通道，
    /// 结果的文案分开存，避免两条通道互相覆盖。
    /// </summary>
    public void RefreshChannelReadiness()
    {
        var channel = _selectedChannel;
        if (channel is null)
        {
            ChannelReadinessText = "还没有通道 —— 点「新建通道」加一条。";
            ChannelSessionSummary = "－";
            ChannelAgentStatusText = "－";
            ChannelCredentialText = "－";
            ChannelSameHostHint = string.Empty;
            OnPropertyChanged(nameof(ChannelBridgeDir));
            return;
        }

        // 同主机多通道提示（D5：只提示不禁用）
        ChannelSameHostHint = DescribeSameHostConflict(channel);

        // 桥目录可能刚被改过，检查前确保它存在（目录按目标账户派生）
        RdpBridge.For(RdpChannelPaths.Resolve(channel.BridgePath, channel.User)).EnsureDirectory();

        var readiness = RdpSessionService.CheckReadiness(
            channel.Host, channel.User, channel.CredentialSaved, channel.BridgePath);

        var notes = new List<string>();
        if (readiness.EditionNote.Length > 0)
        {
            notes.Add(readiness.EditionNote);
        }

        notes.AddRange(readiness.Notes);

        if (readiness.IsLocal)
        {
            var session = RdpSessionService.FindSession(channel.User);
            ChannelSessionSummary = session is null
                ? "目标账户当前没有会话"
                : $"会话 {session.Id} · {session.UserName} · {session.StateText}";
        }
        else
        {
            ChannelSessionSummary = $"远程主机 {RdpTargets.Normalize(channel.Host)}：会话状态无法在本机枚举";
        }

        ChannelReadinessText = readiness.Problems.Count == 0
            ? (notes.Count == 0 ? "环境就绪，可以连接。" : $"环境就绪，可以连接。（{string.Join("；", notes)}）")
            : string.Join("；", readiness.Problems);

        ChannelAgentStatusText = DescribeAgentStatus(readiness);
        ChannelCredentialText = readiness.CredentialSaved ? "已保存" : "未保存";

        channel.CredentialSaved = readiness.CredentialSaved;
        OnPropertyChanged(nameof(ChannelResolutionKey));
        OnPropertyChanged(nameof(ChannelResolutionLabel));
        OnPropertyChanged(nameof(ChannelBridgeDir));
    }

    /// <summary>
    /// 同主机冲突提示：找出配置里与当前通道指向同一台主机的其它**启用**通道。
    ///
    /// 按计划书 §3.3 / D5：只提示、不限制（不禁止并行、也不排队）——
    /// 但要如实告诉用户「Windows 客户端版同时只允许一个交互式会话」，
    /// 所以这两条通道会互相接管同一块桌面，任务挤在一个会话里跑。
    /// 按 id 比对（不是引用）：配置对象在 ReloadChannels 里会整体重建。
    /// </summary>
    private string DescribeSameHostConflict(RdpChannel channel)
    {
        var host = RdpTargets.Normalize(channel.Host);
        if (host.Length == 0)
        {
            return string.Empty;
        }

        var sameHost = Config.Rdp.Channels
            .Where(c => !string.Equals(c.Id, channel.Id, StringComparison.OrdinalIgnoreCase)
                        && c.Enabled
                        && string.Equals(RdpTargets.Normalize(c.Host), host, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.DisplayName)
            .ToList();

        if (sameHost.Count == 0)
        {
            return string.Empty;
        }

        return $"通道「{string.Join("」「", sameHost)}」与当前通道指向同一台机器（{host}）。" +
               "Windows 客户端版同时只允许一个交互式会话 —— 后连的通道会接管先连的那块桌面，" +
               "两条通道的任务会挤在同一个会话里。想真正并行，请让它们连不同的机器。";
    }

    public ICommand AddChannelCommand => _addChannelCommand ??= new RelayCommand(AddChannel);

    public ICommand DuplicateChannelCommand => _duplicateChannelCommand ??= new RelayCommand(DuplicateChannel);

    /// <summary>删除通道。<c>CommandParameter</c> 是 <see cref="RdpChannel"/>，不传则删选中的那条。</summary>
    public ICommand RemoveChannelCommand => _removeChannelCommand ??= new RelayCommand(RemoveChannel);

    public ICommand RefreshChannelCommand => _refreshChannelCommand ??= new RelayCommand(RefreshChannelReadiness);

    public ICommand ClearChannelCredentialCommand =>
        _clearChannelCredentialCommand ??= new RelayCommand(ClearChannelCredential);

    public ICommand DeployChannelAgentCommand =>
        _deployChannelAgentCommand ??= new RelayCommand(DeployChannelAgent);

    public ICommand OpenChannelBridgeFolderCommand =>
        _openChannelBridgeFolderCommand ??= new RelayCommand(OpenChannelBridgeFolder);

    private void AddChannel()
    {
        var channel = new RdpChannel
        {
            Id = RdpChannel.NewId(),
            Name = $"通道 {Config.Rdp.Channels.Count + 1}",
            // 新通道继承全局的默认值（约定见计划书 §4.1：旧字段是"新建通道时的默认值"）
            Host = RdpSettings.TargetHost,
            BridgePath = RdpSettings.BridgePath,
            SessionFinish = RdpSettings.SessionFinish,
            DesktopWidth = RdpSettings.DesktopWidth,
            DesktopHeight = RdpSettings.DesktopHeight,
        };
        channel.Sanitize(Config.Rdp.Channels.Count + 1);

        Config.Rdp.Channels.Add(channel);
        SaveConfig();
        ReloadChannels();
        SelectedChannel = Channels.FirstOrDefault(c => c.Id == channel.Id);
        AppendLog($"[会话通道] 新建通道「{channel.DisplayName}」（{channel.Host} / 待填账户）");
    }

    private void DuplicateChannel(object? parameter)
    {
        var source = parameter as RdpChannel ?? _selectedChannel;
        if (source is null)
        {
            return;
        }

        var copy = source.Clone();
        copy.Id = RdpChannel.NewId();
        copy.Name = $"{source.DisplayName} 副本";
        // 副本默认清掉凭据标记：同名账户换个主机就用不上了，让用户自己重存一次更稳
        copy.CredentialSaved = false;

        Config.Rdp.Channels.Add(copy);
        SaveConfig();
        ReloadChannels();
        SelectedChannel = Channels.FirstOrDefault(c => c.Id == copy.Id);
        AppendLog($"[会话通道] 复制出通道「{copy.DisplayName}」，记得补账户与凭据。");
    }

    private void RemoveChannel(object? parameter)
    {
        var channel = parameter as RdpChannel ?? _selectedChannel;
        if (channel is null)
        {
            return;
        }

        if (IsRunning || IsRdpBusy)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "正在执行任务，先停止执行再改通道。");
            return;
        }

        var users = Tasks.Count(t => string.Equals((t.ChannelId ?? string.Empty).Trim(), channel.Id, StringComparison.OrdinalIgnoreCase));

        Config.Rdp.Channels.Remove(channel);
        SaveConfig();

        // 引用它的任务不会静默改跑本地：留空 channel_id 会变成"本地执行"，
        // 那是另一回事 —— 这里只把话讲清楚，让用户在任务页自己决定改派给谁。
        ReloadChannels();
        RefreshChannelChoices();

        var tail = users == 0
            ? string.Empty
            : $"，有 {users} 个任务正指向它（那些任务会显示「未知通道」，需要到任务页重新选）";
        AppendLog($"[会话通道] 已删除通道「{channel.DisplayName}」{tail}。");
    }

    /// <summary>保存选中通道的凭据到 Windows 凭据管理器（按 host|user 一条，通道之间互不覆盖）。</summary>
    public void SaveChannelCredential(string password)
    {
        var channel = _selectedChannel;
        if (channel is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(channel.User))
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请先填写该通道的目标账户名。");
            return;
        }

        if (password.Length == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请填写该账户的登录密码。");
            return;
        }

        if (!RdpSessionService.SaveCredential(channel.Host, channel.User, password, out var message))
        {
            _ = DialogHelper.ShowMessageAsync("保存失败", message);
            return;
        }

        if (RdpSessionService.TryLoadPassword(channel.Host, channel.User) is null)
        {
            AppendLog($"[会话通道] 警告：「{channel.DisplayName}」的凭据写入后无法读回，远程桌面可能无法自动登录。");
        }

        channel.CredentialSaved = true;

        // 正在编辑的就是"当前设置"那条时，同步回全局字段：单通道快入口仍然读 RdpSettings
        if (string.Equals(channel.Id, SettingsChannelId, StringComparison.Ordinal))
        {
            RdpSessionService.PendingPassword = password;
        }

        SaveConfig();
        AppendLog($"[会话通道] 已保存「{channel.DisplayName}」的凭据（{RdpTargets.Normalize(channel.Host)}|{channel.User}，配置文件不保存密码）。");
        RefreshChannelReadiness();
    }

    public void ClearChannelCredential()
    {
        var channel = _selectedChannel;
        if (channel is null)
        {
            return;
        }

        RdpSessionService.DeleteCredential(channel.Host, channel.User);
        channel.CredentialSaved = false;
        SaveConfig();
        AppendLog($"[会话通道] 已删除「{channel.DisplayName}」的凭据。");
        RefreshChannelReadiness();
    }

    /// <summary>
    /// 部署会话代理（公共启动目录快捷方式）。代理是**全机器共用一份**的，
    /// 所以部署动作本身与通道无关，但"目标账户"要取自当前通道 ——
    /// 部署前要读它那路桥判断代理是不是正在跑任务（跑着就不能动）。
    /// </summary>
    private void DeployChannelAgent()
    {
        var channel = _selectedChannel;

        IsRdpBusy = true;
        RdpStatusText = "正在部署会话代理（首次会复制程序文件，稍等）…";
        AppendLog("[会话通道] 正在部署会话代理：把程序复制到所有用户都能访问的公共目录…");

        var bridge = channel?.BridgePath ?? RdpSettings.BridgePath;
        var targetUser = channel?.User ?? RdpSettings.TargetUser;

        Task.Run(() =>
        {
            var ok = RdpSessionService.DeployAgent(out var message, bridge, targetUser);
            return (Ok: ok, Message: message);
        }).ContinueWith(task =>
        {
            var (ok, message) = task.Result;
            _dispatcher.TryEnqueue(() =>
            {
                if (ok)
                {
                    AppendLog($"[会话通道] 会话代理：{Flatten(message)}");
                    RdpStatusText = message.Contains("注销")
                        ? "会话代理已部署（目标账户需注销重连才生效）"
                        : "会话代理已部署";
                }
                else
                {
                    // 失败原因可能是文件被占用 / PowerShell 报错 / 真的缺管理员权限。
                    // 不替用户下"就是缺权限"的结论 —— message 里已经带了真实报错与判断依据。
                    AppendLog($"[会话通道] 会话代理部署失败：{Flatten(message)}");
                    RdpStatusText = "会话代理部署失败";
                    _ = DialogHelper.ShowMessageAsync("部署失败", message);
                }

                IsRdpBusy = false;
                RefreshChannelReadiness();
                RefreshRdpReadiness();
            });
        });
    }

    private void OpenChannelBridgeFolder()
    {
        var channel = _selectedChannel;
        var dir = channel is null
            ? RdpChannelPaths.Resolve(RdpSettings.BridgePath, RdpSettings.TargetUser)
            : RdpChannelPaths.Resolve(channel.BridgePath, channel.User);

        var bridge = RdpBridge.For(dir);
        bridge.EnsureDirectory();
        bridge.OpenBridgeFolder();
    }

    /// <summary>
    /// 轻量落盘：把编辑框里的改动写进 config.json 并刷新任务页下拉与列表副标题。
    ///
    /// 输入框失焦时会调它 —— 通道名/主机/账户这类文本没有"选中即保存"的时机，
    /// 只靠一个「保存」按钮的话，用户填完直接切页面就丢了。
    /// 刻意不刷新预检（那要跑 tasklist / powershell / reg，失焦太频繁了扛不住）。
    /// </summary>
    public void PersistChannelEdits()
    {
        if (_selectedChannel is not null)
        {
            _selectedChannel.Sanitize(Config.Rdp.Channels.IndexOf(_selectedChannel) + 1);
        }

        SaveConfig();
        RefreshChannelChoices();
        OnPropertyChanged(nameof(Channels));
    }

    /// <summary>完整保存：轻量落盘 + 重跑该通道预检 + 记一条日志（对应界面上的「保存通道设置」按钮）。</summary>
    public void SaveChannelEdits()
    {
        PersistChannelEdits();
        RefreshChannelReadiness();

        var name = _selectedChannel?.DisplayName ?? "（未选择）";
        AppendLog($"[会话通道] 通道配置已保存（当前：{name}）。");
    }

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

    /// <summary>
    /// 只为**某条通道**建立连接（不下发任何任务）—— 通道页上「连接到这条通道」按钮走这里。
    ///
    /// 内嵌模式：画面挂进该通道页的「内嵌画面」区；
    /// 独立窗口模式：画面在系统自带的 mstsc 窗口里，这里退化为常规连接入口。
    /// </summary>
    public void ConnectChannelSurface(string channelId)
    {
        var session = _sessions.FirstOrDefault(s =>
            s.IsRemote && string.Equals(s.ChannelId, channelId, StringComparison.OrdinalIgnoreCase));

        // 还没跑过任务时 _sessions 是空的 —— 回退到配置里找通道，
        // 这样通道页上点「连接到这条通道」在开跑之前也能用。
        var channel = session?.Channel
            ?? Config.Rdp.Channels.FirstOrDefault(c =>
                string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            AppendLog("找不到要连接的会话通道（可能已被删除）。");
            return;
        }

        if (!IsEmbeddedMode)
        {
            // 独立窗口模式的画面不在应用里，走常规连接入口即可
            ConnectSurface();
            return;
        }

        // M6：内嵌画面按通道分家 —— 每条通道各有各的 client，可以同时连、同时显示。
        if (HasSurface(channelId))
        {
            AppendSurfaceLog(channelId, "内嵌画面已连接，直接复用。");
            return;
        }

        if (TryStartEmbeddedConnect(
                channelId,
                channel.Host,
                channel.User,
                channel.DesktopWidth,
                channel.DesktopHeight,
                out var message))
        {
            AppendSurfaceLog(channelId, "正在建立内嵌画面连接…");
            return;
        }

        AppendSurfaceLog(channelId, $"内嵌连接失败：{message}");
        _ = DialogHelper.ShowMessageAsync("连接失败", message);
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
            // 内嵌模式：不管会话是否已存在都直接连 —— FreeRDP 重连会接回同一会话，
            // 画面嵌进雪乃酱窗口。mstsc 的「避免重复连接接管桌面」短路在这里没有意义：
            // 不连上就看不到画面。
            if (IsEmbeddedMode)
            {
                // 已有内嵌连接：直接复用（页面切换回来时点「连接预览」走到这里，不重复连）
                if (HasSurface(string.Empty))
                {
                    RdpStatusText = "内嵌画面已连接（复用现有连接）";
                    AppendLog("[内嵌] 已有内嵌连接，直接复用。");
                    RefreshRdpReadiness();
                    return;
                }

                RdpStatusText = "内嵌连接中…";
                var (embedWidth, embedHeight) = RdpResolutionSize();
                // 空 channelId = 设置页这条不属于任何会话通道的入口
                if (TryStartEmbeddedConnect(string.Empty, host, RdpSettings.TargetUser, embedWidth, embedHeight, out var embedMessage))
                {
                    AppendLog("内嵌模式下画面显示在下方「内嵌画面」卡片里，不需要打开独立远程桌面窗口。");
                    RefreshRdpReadiness();
                    return;
                }
                RdpStatusText = "连接失败";
                AppendLog($"[内嵌] {embedMessage}");
                _ = DialogHelper.ShowMessageAsync("连接失败", embedMessage);
                return;
            }

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
        // 内嵌模式：断开 = ycn_rdp_disconnect（目标会话保留，语义 = mstsc「断开连接」）
        if (IsEmbeddedMode)
        {
            if (!HasAnySurface)
            {
                _ = DialogHelper.ShowMessageAsync("提示", "当前没有内嵌连接。");
                return;
            }

            RdpStatusText = "正在断开内嵌连接…";

            // 逐条断开：先打上「主动断开」抑制位，免得自动重连又把连接接回来
            foreach (var key in _embeds.Where(kv => kv.Value.Client is not null).Select(kv => kv.Key).ToList())
            {
                GetOrCreateConnection(key).UserDisconnect = true;
                DisposeSurface(key);
            }

            RdpStatusText = "内嵌连接已断开（目标会话保留）";
            RefreshRdpReadiness();
            return;
        }

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

    // ---- M5：内嵌连接通道（client_mode = embedded，计划书 §5.6）----
    //
    // M6 起改成「每通道一份」：一个 RdpEmbeddedClient 同一时刻只能挂一个渲染视图，
    // 所以"多路画面同时可见"（网格 / 弹出窗口）的前提是每条通道各有各的连接实例。
    // Key = 通道 id；空串 "" = 设置页那个不属于任何通道的入口。

    private readonly Dictionary<string, EmbedConnection> _embeds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近建立 / 操作过的那条（供「当前画面归属」一类旧属性查询）。</summary>
    private string _embedActiveKey = string.Empty;

    /// <summary>
    /// 一条内嵌画面的全部运行时状态。
    /// 之所以要打包成类：M6 之前这些是 VM 上的一组单例字段，
    /// 多通道并行时会互相覆盖（重试计数、看门狗、主动断开标志都会串台）。
    /// </summary>
    private sealed class EmbedConnection
    {
        /// <summary>通道 id；空串 = 设置页入口。</summary>
        public required string Key { get; init; }

        public RdpEmbeddedClient? Client { get; set; }

        // 上次连接用的参数 —— 自动重连必须按原样重来：
        // 换了账户会登错人，换了分辨率会让会话尺寸跳变。
        public string LastHost { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }

        public int RetryCount { get; set; }          // 黑屏自愈重试次数
        public int AutoReconnects { get; set; }      // 意外掉线自动重连次数
        public bool UserDisconnect { get; set; }     // 用户/流程主动断开（抑制自动重连）
        public DispatcherQueueTimer? Watchdog { get; set; }

        /// <summary>
        /// 画面是否已被「弹出独立窗口」占用。
        /// 占用的通道，页面内格子只显示提示、不挂 client —— 两个渲染器挂同一 client 会打架。
        /// </summary>
        public bool PoppedOut { get; set; }

        // 事件处理器引用（闭包捕获本对象）——解绑时必须用同一批委托实例
        public EventHandler<(int Session, uint Width, uint Height)>? OnConnected;
        public EventHandler<RdpFrameEventArgs>? OnFrameArrived;
        public EventHandler<(int Session, int Reason, string Detail)>? OnDisconnected;
    }

    /// <summary>最近建立 / 操作的内嵌画面所属通道 id；当前无任何内嵌连接时为空串。</summary>
    public string ActiveEmbedChannelId => _embeds.Count == 0 ? string.Empty : _embedActiveKey;

    /// <summary>当前内嵌画面所属通道的名字（给提示文案与日志用）。</summary>
    public string ActiveEmbedChannelName => SurfaceDisplayName(_embedActiveKey);

    private string SurfaceDisplayName(string key)
    {
        if (key.Length == 0)
        {
            return "当前设置";
        }

        return _sessions.FirstOrDefault(s => s.IsRemote
            && string.Equals(s.ChannelId, key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? key;
    }

    /// <summary>取某条通道的内嵌连接实例（通道页画面挂载用）；无则 null。</summary>
    public RdpEmbeddedClient? GetSurfaceClient(string? channelId)
        => _embeds.TryGetValue(channelId ?? string.Empty, out var conn) ? conn.Client : null;

    /// <summary>这条通道当前有没有内嵌画面。</summary>
    public bool HasSurface(string? channelId) => GetSurfaceClient(channelId) is not null;

    /// <summary>当前有内嵌画面的全部通道 id（可能含设置页入口的空串）。</summary>
    public IReadOnlyList<string> SurfaceChannelIds =>
        _embeds.Where(kv => kv.Value.Client is not null).Select(kv => kv.Key).ToList();

    /// <summary>当前有没有任何内嵌画面。</summary>
    public bool HasAnySurface => _embeds.Values.Any(c => c.Client is not null);

    /// <summary>这条通道的画面是否已被「弹出独立窗口」占用。</summary>
    public bool IsSurfacePoppedOut(string? channelId)
        => _embeds.TryGetValue(channelId ?? string.Empty, out var conn) && conn.PoppedOut;

    /// <summary>标记 / 撤销「画面已弹出到独立窗口」。</summary>
    public void MarkSurfacePoppedOut(string? channelId, bool poppedOut)
    {
        var key = channelId ?? string.Empty;
        if (_embeds.TryGetValue(key, out var conn))
        {
            conn.PoppedOut = poppedOut;
        }
    }

    /// <summary>返回该通道上次连接用的分辨率（弹出窗口新建画面时沿用，避免画面变形）。</summary>
    public (int Width, int Height) SurfaceLastSize(string? channelId)
    {
        var key = channelId ?? string.Empty;
        return _embeds.TryGetValue(key, out var conn) ? (conn.Width, conn.Height) : (0, 0);
    }

    /// <summary>
    /// 多画面网格要展示的通道 id（有会话或有内嵌画面的远程通道；设置页入口不进网格）。
    /// 顺序：本轮执行涉及的通道在前，其次是有画面的 —— 保证网格顺序稳定、不随刷新跳位。
    /// </summary>
    public IReadOnlyList<string> SurfaceGridChannelIds
    {
        get
        {
            var ids = new List<string>();

            foreach (var s in _sessions.Where(s => s.IsRemote))
            {
                if (!ids.Contains(s.ChannelId, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(s.ChannelId);
                }
            }

            foreach (var key in SurfaceChannelIds)
            {
                if (key.Length == 0)
                {
                    continue; // 设置页入口没有对应的通道页 / 网格格位
                }

                if (!ids.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(key);
                }
            }

            return ids;
        }
    }

    /// <summary>通道 id → 显示名（网格标题 / 弹窗标题用）。</summary>
    public string ChannelDisplayName(string? channelId)
    {
        var key = channelId ?? string.Empty;
        if (key.Length == 0)
        {
            return "当前设置";
        }

        return Config.Rdp.Channels
            .FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? key;
    }

    /// <summary>该通道画面当前的状态摘要（网格角标用）。</summary>
    public string SurfaceStateText(string? channelId)
    {
        var key = channelId ?? string.Empty;

        if (GetSurfaceClient(key) is null)
        {
            return "未连接";
        }

        if (IsSurfacePoppedOut(key))
        {
            return "已在独立窗口显示";
        }

        var session = _sessions.FirstOrDefault(s => s.IsRemote
            && string.Equals(s.ChannelId, key, StringComparison.OrdinalIgnoreCase));

        if (session is null)
        {
            return "已连接";
        }

        return session.IsActive
            ? (string.IsNullOrEmpty(session.CurrentTask) ? "执行中" : $"执行中 · {session.CurrentTask}")
            : session.StatusText;
    }

    /// <summary>连接方式下拉框数据源。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ClientModeItems => ClientModes.Items;

    /// <summary>当前是否为内嵌连接模式（client_mode = embedded）。</summary>
    public bool IsEmbeddedMode => ClientModes.Normalize(RdpSettings.ClientMode) == ClientModes.Embedded;

    /// <summary>最近建立 / 操作的那条内嵌连接实例（null = 当前无任何内嵌画面）。</summary>
    public RdpEmbeddedClient? ActiveEmbedClient => GetSurfaceClient(_embedActiveKey);

    /// <summary>
    /// 内嵌连接集合发生变化（发起连接 / 释放）。可能在任意线程触发，订阅方自行调度到 UI 线程。
    /// </summary>
    public event Action? EmbedClientChanged;

    /// <summary>
    /// 发起内嵌连接。同步发起、异步回调；凭据取自 Windows 凭据管理器。
    /// <para>黑屏自愈（M5 实测）：重连**同分辨率**会话时服务器不重绘桌面（RestoreRect 被无视），
    /// 画面永远黑屏。对策：请求宽按重试次数 +2（保持偶数不被服务器取整），必然与会话当前
    /// 分辨率不同 → 强制 resize → 服务器全量重绘；连接 6 秒后画面仍黑则宽再 +2 重连（最多 3 次）。</para>
    /// </summary>
    /// <param name="channelId">通道 id（空串 = 设置页入口）——M6 起连接按通道分家。</param>
    /// <param name="host">目标主机。</param>
    /// <param name="targetUser">目标账户（多通道下每条通道各用各的账户与凭据）。</param>
    /// <param name="width">期望宽度，0 = 用默认。</param>
    /// <param name="height">期望高度，0 = 用默认。</param>
    private bool TryStartEmbeddedConnect(
        string channelId, string host, string? targetUser, int width, int height, out string message)
    {
        message = string.Empty;
        var key = channelId ?? string.Empty;
        var normalized = RdpTargets.Normalize(host);

        // 按「主机 + 账户」取凭据：多通道并行时，同主机不同账户各有各的密码
        var password = RdpCredentialStore.Read(normalized, targetUser);
        if (password is null)
        {
            message = "该主机还没有保存凭据，请先在「会话通道」页点「保存凭据」。";
            return false;
        }

        // 账户名允许 "DOMAIN\user" 写法
        var user = (targetUser ?? string.Empty).Trim();
        string? domain = null;
        var backslash = user.IndexOf('\\');
        if (backslash > 0)
        {
            domain = user[..backslash];
            user = user[(backslash + 1)..];
        }

        DisposeSurface(key); // 幂等清理本通道的旧实例（不重置黑屏重试计数）

        var conn = GetOrCreateConnection(key);
        conn.LastHost = normalized;
        conn.User = user;
        conn.Width = width;
        conn.Height = height;
        conn.UserDisconnect = false; // 主动连接：解除自动重连抑制

        // 事件处理器闭包捕获 conn 本身 —— 多通道并存时才知道回调属于哪条通道。
        // 引用存在 conn 上，释放时用同一批委托实例解绑。
        var client = new RdpEmbeddedClient();
        conn.OnConnected = (_, e) => OnEmbedConnected(conn, e);
        conn.OnFrameArrived = (_, e) => OnEmbedFrameArrived(conn, e);
        conn.OnDisconnected = (_, e) => OnEmbedDisconnected(conn, e);
        client.Connected += conn.OnConnected;
        client.FrameArrived += conn.OnFrameArrived;
        client.Disconnected += conn.OnDisconnected;
        conn.Client = client;
        _embedActiveKey = key;
        EmbedClientChanged?.Invoke();

        var (address, port) = RdpSessionService.SplitHostPort(normalized);
        var started = client.Connect(new RdpConnectInfo
        {
            Host = address,
            Port = (ushort)(port > 0 ? port : 3389),
            Username = user,
            Domain = domain,
            Password = password,
            DesktopWidth = (uint)((width > 0 ? width : 1280) + 2 * conn.RetryCount),
            DesktopHeight = (uint)(height > 0 ? height : 720),
            AllowSelfsigned = true,
            EnableAudio = RdpSettings.AudioEnabled,
            UseGfx = RdpSettings.GfxEnabled,
        });
        if (!started)
        {
            AppendSurfaceLog(key, "连接发起失败（详见上方错误）。");
            DisposeSurface(key);
            message = "内嵌连接发起失败，详见运行日志。";
            return false;
        }

        AppendSurfaceLog(key, conn.RetryCount > 0
            ? $"黑屏自愈重试 #{conn.RetryCount}：请求 {normalized} @ {(width > 0 ? width : 1280) + 2 * conn.RetryCount}×{(height > 0 ? height : 720)}（宽 +{2 * conn.RetryCount} 强制会话刷新）"
            : $"正在连接 {normalized}（client_mode=embedded，音频={(RdpSettings.AudioEnabled ? "开" : "关")}）…");
        StartEmbedWatchdog(conn);
        return true;
    }

    /// <summary>
    /// 按上次的参数重连内嵌画面（黑屏自愈重试与意外掉线自动重连共用）。
    /// 必须带上原来那组主机 / 账户 / 分辨率 —— 换成默认值会登错账户，或让会话尺寸跳变。
    /// </summary>
    private void ReconnectEmbedded(EmbedConnection conn)
    {
        _ = TryStartEmbeddedConnect(conn.Key, conn.LastHost, conn.User, conn.Width, conn.Height, out _);
    }

    /// <summary>内嵌画面的日志：通道内嵌带通道前缀，设置页入口则只有 [内嵌]。</summary>
    private void AppendSurfaceLog(string key, string message)
    {
        var session = _sessions.FirstOrDefault(s => s.IsRemote
            && string.Equals(s.ChannelId, key, StringComparison.OrdinalIgnoreCase));

        if (session is not null)
        {
            AppendChannelLog(session, $"[内嵌] {message}");
        }
        else if (key.Length > 0)
        {
            AppendLog($"[通道：{ChannelDisplayName(key)}] [内嵌] {message}");
        }
        else
        {
            AppendLog($"[内嵌] {message}");
        }
    }

    /// <summary>取（或新建）某条通道的内嵌连接记录。字典条目刻意不随断开删除 —— 重试计数要跨连接保持。</summary>
    private EmbedConnection GetOrCreateConnection(string key)
    {
        if (_embeds.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new EmbedConnection { Key = key };
        _embeds[key] = created;
        return created;
    }

    private void OnEmbedFrameArrived(EmbedConnection conn, RdpFrameEventArgs e)
    {
        // 第一帧到达 = 画面在流；停掉本通道的黑屏看门狗（原生线程 → 编组）
        if (conn.Watchdog is not null)
        {
            Post(() => StopEmbedWatchdog(conn));
        }
    }

    /// <summary>
    /// 黑屏看门狗：连接后每 6 秒采样一帧亮度。全黑（&lt;20）→ 宽 +2 自动重连（最多 3 次）；
    /// 有内容 → 复位。会话重连服务器不重绘时的唯一自愈手段（M5 实测）。
    /// </summary>
    private void StartEmbedWatchdog(EmbedConnection conn)
    {
        conn.Watchdog?.Stop();
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        var ticks = 0;
        timer.Tick += (_, _) =>
        {
            ticks++;
            var client = conn.Client;
            if (client is null)
            {
                timer.Stop();
                conn.Watchdog = null;
                return;
            }
            if (client.State != RdpClientState.Connected)
            {
                return; // 仍在连接中，下一拍再看
            }
            if (!client.TryCopyFrame(out var w, out var h, out var st, out var px))
            {
                if (ticks <= 10)
                {
                    return; // 还没有任何帧：下一拍再看（最多等 60 秒）
                }
                AppendSurfaceLog(conn.Key, "60 秒未收到任何画面帧；请断开后重连，或改用 mstsc 连接方式。");
                timer.Stop();
                conn.Watchdog = null;
                return;
            }

            long sum = 0;
            var samples = 0;
            for (var i = 0; i + 2 < px.Length; i += 4096 * 4)
            {
                sum += px[i] + px[i + 1] + px[i + 2];
                samples += 3;
            }
            var brightness = samples > 0 ? sum / samples : 0;
            if (brightness > 20)
            {
                // 画面有内容：自愈完成
                timer.Stop();
                conn.Watchdog = null;
                conn.RetryCount = 0;
                AppendSurfaceLog(conn.Key, $"画面正常（{w}×{h}）。点击画面可获得焦点并直接操作。");
                return;
            }

            if (conn.RetryCount >= 3)
            {
                AppendSurfaceLog(conn.Key, "多次重试仍黑屏；请点「断开画面」后重新连接，或在连接方式里改回 mstsc。");
                timer.Stop();
                conn.Watchdog = null;
                return;
            }

            conn.RetryCount++;
            AppendSurfaceLog(conn.Key, $"检测到静止黑屏（亮度 {brightness}），自动重连第 {conn.RetryCount}/3 次…");
            ReconnectEmbedded(conn);
        };
        timer.Start();
        conn.Watchdog = timer;
    }

    private void StopEmbedWatchdog(EmbedConnection conn)
    {
        conn.Watchdog?.Stop();
        conn.Watchdog = null;
        conn.RetryCount = 0;
    }

    private void OnEmbedConnected(EmbedConnection conn, (int Session, uint Width, uint Height) e)
    {
        // 原生事件循环线程回调：AppendLog 自带编组；UI 属性必须 Post 到 UI 线程
        // （直接设 RdpStatusText 会触发 x:Bind 跨线程 set_Text → RPC_E_WRONG_THREAD 崩溃）
        AppendSurfaceLog(conn.Key, $"连接建立：桌面 {e.Width}×{e.Height}，画面已嵌入窗口。");
        conn.AutoReconnects = 0; // 连接成功：清零自动重连计数
        Post(() =>
        {
            if (RdpStatusText.Contains("内嵌连接中", StringComparison.Ordinal) ||
                RdpStatusText.Contains("自动重连", StringComparison.Ordinal))
            {
                RdpStatusText = "内嵌画面已连接";
            }
        });
    }

    private void OnEmbedDisconnected(EmbedConnection conn, (int Session, int Reason, string Detail) e)
    {
        // 可能在原生线程触发：AppendLog 线程安全；状态更新走 Post
        AppendSurfaceLog(conn.Key, $"连接断开：{e.Detail}（目标会话保留，可重新连接）。");
        Post(() => RdpStatusText = "内嵌连接已断开（目标会话保留）");

        // M6 自动重连：仅服务器/网络侧意外断开（环回解锁/网络闪断）。
        // LOCAL = 本端主动（用户断开/看门狗自愈链/窗口关闭）不重连；
        // ERROR = 协议错误，重连多半还错，避免风暴不重连。
        if (e.Reason == 2 && !conn.UserDisconnect && conn.RetryCount == 0)
        {
            ScheduleEmbedAutoReconnect(conn);
        }
        DisposeSurface(conn.Key);
    }

    /// <summary>
    /// 意外掉线自动重连（M6）：指数退避 1/2/4/8/16 秒，最多 5 次。
    /// 覆盖 §5.5 环回解锁场景（锁屏结束后内嵌画面自动恢复）。
    /// </summary>
    private void ScheduleEmbedAutoReconnect(EmbedConnection conn)
    {
        if (conn.AutoReconnects >= 5)
        {
            AppendSurfaceLog(conn.Key, "自动重连已达上限（5 次）；请手动重连或改用 mstsc 连接方式。");
            return;
        }
        conn.AutoReconnects++;
        var delay = Math.Min(1 << (conn.AutoReconnects - 1), 16);
        AppendSurfaceLog(conn.Key, $"{delay} 秒后自动重连（第 {conn.AutoReconnects}/5 次）…");
        Post(() =>
        {
            var timer = _dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(delay);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // 等待期间已手动连接/断开则放弃
                if (conn.Client is not null || conn.UserDisconnect)
                {
                    return;
                }
                ReconnectEmbedded(conn);
            };
            timer.Start();
        });
    }

    /// <summary>
    /// 释放某条通道的内嵌连接实例（ycn_rdp_disconnect：目标会话保留）。幂等，可在任意线程调用。
    /// 注意：不重置黑屏重试计数——自动重连链路依赖它跨连接保持。
    /// 另外：字典条目刻意保留（重试计数与「已弹出窗口」标记要跨连接存活）。
    /// </summary>
    public void DisposeSurface(string? channelId)
    {
        var key = channelId ?? string.Empty;
        if (!_embeds.TryGetValue(key, out var conn) || conn.Client is null)
        {
            return;
        }

        var client = conn.Client;
        conn.Client = null;
        conn.PoppedOut = false;

        if (conn.OnConnected is not null)
        {
            client.Connected -= conn.OnConnected;
        }

        if (conn.OnFrameArrived is not null)
        {
            client.FrameArrived -= conn.OnFrameArrived;
        }

        if (conn.OnDisconnected is not null)
        {
            client.Disconnected -= conn.OnDisconnected;
        }

        try
        {
            client.Dispose();
        }
        catch
        {
            // 释放失败不阻断 UI（原生侧自有兜底）
        }

        conn.Watchdog?.Stop();
        conn.Watchdog = null;

        if (string.Equals(_embedActiveKey, key, StringComparison.OrdinalIgnoreCase) && !HasAnySurface)
        {
            _embedActiveKey = string.Empty;
        }

        AppendSurfaceLog(key, "内嵌连接已断开（目标会话保留中）。");
        EmbedClientChanged?.Invoke();
    }

    /// <summary>断开全部内嵌画面（窗口关闭 / 退出应用时用）。</summary>
    public void DisposeEmbedClient()
    {
        foreach (var key in _embeds.Keys.ToList())
        {
            DisposeSurface(key);
        }
    }

    /// <summary>当前展示通道的事件列表（跟着 <see cref="ActiveSession"/> 走）。</summary>
    public ObservableCollection<RdpTaskEvent> RdpEvents => _activeSession?.Events ?? _emptyEvents;

    /// <summary>本轮执行涉及的全部通道会话（「本地执行」组在最前）。</summary>
    public IReadOnlyList<RdpChannelSession> Sessions => _sessions;

    /// <summary>
    /// 主页「会话通道」概览的数据源。由主页按 1Hz 调 <see cref="RefreshChannelOverview"/> 重建 ——
    /// 通道会话的属性不是可观察的，重建集合是最省事又足够便宜的做法（最多 8 条通道）。
    /// </summary>
    public ObservableCollection<ChannelOverviewItem> ChannelOverview { get; } = new();

    /// <summary>主页上有没有可展示的通道（没跑过任务时为 false，整块卡片隐藏）。</summary>
    public bool HasChannelOverview => _sessions.Count > 0;

    /// <summary>把当前各通道会话的状态拍成快照，供主页概览显示。</summary>
    public void RefreshChannelOverview()
    {
        ChannelOverview.Clear();

        foreach (var session in _sessions)
        {
            ChannelOverview.Add(new ChannelOverviewItem(
                session.ChannelId,
                session.DisplayName,
                session.IsRemote
                    ? $"{session.TargetHost}  /  {session.TargetUser}"
                    : "主控端当前会话",
                session.StatusText,
                $"{session.Progress} / {session.Total}",
                session.HeartbeatText,
                session.IsStale,
                session.IsRemote,
                session.IsActive,
                session.HasCommand));
        }

        OnPropertyChanged(nameof(HasChannelOverview));
    }

    /// <summary>
    /// 当前展示的通道。M4 引入通道页面后由页面切换来设置；在此之前取第一条远程通道
    /// （没有就取本地组），保持"单通道观感"与改造前完全一致。
    /// </summary>
    public RdpChannelSession? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (ReferenceEquals(_activeSession, value))
            {
                return;
            }

            _activeSession = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RdpEvents));
            OnPropertyChanged(nameof(ActiveChannelName));
            MirrorActiveSession();
        }
    }

    /// <summary>页头 / 菜单上显示的通道名。</summary>
    public string ActiveChannelName => _activeSession?.DisplayName ?? "未连接";

    /// <summary>本轮是否存在远程通道（决定进度区是否有意义）。</summary>
    public bool HasRemoteSession => _sessions.Any(s => s.IsRemote);

    /// <summary>
    /// 把活跃通道的状态镜像到界面属性上。
    ///
    /// <c>RdpPhase</c> / <c>RdpProgress</c> / <c>RdpHeartbeatText</c> 这些原本就是 VM 自己的
    /// 字段、被 XAML 直接绑定；镜像之后 XAML 一行都不用改，单通道观感与改造前一致。
    /// M4 拆出各通道页面时再改成直接绑 Session。
    /// </summary>
    private void MirrorActiveSession()
    {
        var s = _activeSession;

        RdpPhase = s?.Phase ?? "idle";
        RdpProgress = s?.Progress ?? 0;
        RdpTotal = s?.Total ?? 0;
        RdpElapsedSeconds = s?.ElapsedSeconds ?? 0;
        RdpCurrentTask = s?.TaskDisplay ?? "－";
        RdpHeartbeatText = s?.HeartbeatText ?? "－";
        RdpIsStale = s?.IsStale ?? false;
        RdpStatusText = s?.StatusText ?? "未连接";

        // 面板与进度区是两套绑定，但数据同源 —— 任何一次镜像都要顺手通知面板
        RaisePanelChanged();
    }

    // ---------------- 状态面板（看板娘浮动卡 / 主页状态卡） ----------------

    /// <summary>
    /// 状态面板的状态文案。
    ///
    /// <para>**为什么不能直接绑 <see cref="StatusText"/>**：那套属性（`StatusText` /
    /// `CurrentTaskName` / `ElapsedText` / `ProgressText`）只由本地执行的 `ScriptRunner` 回调写入，
    /// 而远程通道的进度全落在 <see cref="RdpChannelSession"/> 里、从不回灌 ——
    /// 于是"跑远程任务时面板一直是空闲 / 00:00 / - / 0 / 0"（真机实测过）。</para>
    ///
    /// <para>这里统一按"本轮有几块"取值：单块就是那一块（观感与改造前一致），
    /// 多块给整轮聚合（一块的数字代表不了整轮）。</para>
    /// </summary>
    public string PanelStatusText
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return StatusText;   // 还没跑过任何一轮：沿用本地面板（空闲 / 已完成…）
            }

            if (_sessions.Count == 1)
            {
                return _sessions[0].StatusText;
            }

            if (!IsRunning)
            {
                return _lastRunAbnormal ? "已完成（有异常）" : "已完成";
            }

            return $"执行中（{_sessions.Count} 块并行）";
        }
    }

    /// <summary>面板上的"当前任务"：多块并行时跟着正在展示的那条通道走。</summary>
    public string PanelCurrentTask
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return CurrentTaskName;
            }

            return _sessions.Count == 1
                ? _sessions[0].TaskDisplay
                : _activeSession?.TaskDisplay ?? "－";
        }
    }

    /// <summary>面板上的"耗时"：多块并行时取整轮的墙钟时间。</summary>
    public string PanelElapsedText
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return ElapsedText;
            }

            if (_sessions.Count == 1)
            {
                return _sessions[0].ElapsedText;
            }

            if (_runStartedAt == default)
            {
                return "00:00";
            }

            var end = _runEndedAt ?? DateTimeOffset.Now;
            var seconds = (int)(end - _runStartedAt).TotalSeconds;
            return FormatHelper.FormatSeconds(Math.Max(0, seconds));
        }
    }

    /// <summary>面板上的"进度"：多块并行时给各块之和。</summary>
    public string PanelProgressText
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return ProgressText;
            }

            if (_sessions.Count == 1)
            {
                return $"{_sessions[0].Progress} / {_sessions[0].Total}";
            }

            var done = _sessions.Sum(s => s.Progress);
            var total = _sessions.Sum(s => s.Total);
            return $"合计 {done} / {total}";
        }
    }

    /// <summary>本地执行组（未分配通道的那批任务；本轮没有本地任务时为 null）。</summary>
    private RdpChannelSession? LocalGroup => _sessions.FirstOrDefault(s => !s.IsRemote);

    /// <summary>
    /// 面板四项都是计算属性，值来自各通道会话 —— 任何一个块的状态变了都要整体通知一次。
    /// （属性名要写全：计算属性没法用"某个字段变了"来推断。）
    /// </summary>
    private void RaisePanelChanged()
    {
        OnPropertyChanged(nameof(PanelStatusText));
        OnPropertyChanged(nameof(PanelCurrentTask));
        OnPropertyChanged(nameof(PanelElapsedText));
        OnPropertyChanged(nameof(PanelProgressText));
    }

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

    /// <summary>
    /// 当前要操作的指令桥。
    ///
    /// M1（桥实例化）阶段仍等价于"单通道"：按配置里的桥目录 + 目标账户解析出目录，
    /// 与目标会话里代理自己算出来的目录一致；M3 引入 RdpChannelSession 后，
    /// 这里会换成按通道取（<see cref="RdpBridge.ForChannel"/>）。
    /// </summary>
    private RdpBridge Bridge => RdpBridge.For(RdpChannelPaths.Resolve(RdpSettings.BridgePath, RdpSettings.TargetUser));

    public string RdpBridgePath => Bridge.BridgeDir;

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

    public ICommand OpenBridgeFolderCommand => _openBridgeFolderCommand ??= new RelayCommand(() => Bridge.OpenBridgeFolder());

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
        // 桥目录可能刚被改过，每次检查前确保它存在（目录本身按目标账户派生）
        Bridge.EnsureDirectory();

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

        // 存进去要能取回来才作数：内嵌客户端连接时是靠取回密码来登录的（按主机 + 账户取）
        if (RdpSessionService.TryLoadPassword(RdpSettings.TargetHost, RdpSettings.TargetUser) is null)
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
        RdpSessionService.DeleteCredential(RdpSettings.TargetHost, RdpSettings.TargetUser);
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
        // 目标账户要带上：部署前要判断它那个常驻代理是不是正在跑任务（跑着就不能动）
        var targetUser = RdpSettings.TargetUser;

        Task.Run(() =>
        {
            var ok = RdpSessionService.DeployAgent(out var message, bridge, targetUser);
            return (Ok: ok, Message: message);
        }).ContinueWith(task =>
        {
            var (ok, message) = task.Result;
            _dispatcher.TryEnqueue(() =>
            {
                if (ok)
                {
                    AppendLog($"会话代理：{Flatten(message)}");
                    RdpStatusText = message.Contains("注销")
                        ? "会话代理已部署（目标账户需注销重连才生效）"
                        : "会话代理已部署";
                }
                else
                {
                    // 失败原因可能是文件被占用 / PowerShell 报错 / 真的缺管理员权限。
                    // 不再替用户下"就是缺权限"的结论 —— message 里已经带了真实报错和判断依据。
                    AppendLog($"[远程桌面] 会话代理部署失败：{Flatten(message)}");
                    RdpStatusText = "会话代理部署失败";
                    _ = DialogHelper.ShowMessageAsync("部署失败", message);
                }

                IsRdpBusy = false;
                RefreshRdpReadiness();
            });
        });
    }

    /// <summary>把多行报文压成一行 —— 日志是按行写的，带换行的字符串会把日志切乱。</summary>
    private static string Flatten(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

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
    /// 「连接并执行」单通道入口（远程页的按钮走这里）：用设置页那组 target_host / target_user /
    /// bridge_path 合成一个临时通道，再走与多通道**完全相同**的路径 ——
    /// 免得单通道与多通道各有一套实现，日后改一处漏一处。
    /// </summary>
    public async Task ConnectAndRunAsync()
    {
        var channel = ChannelFromSettings();
        var tasks = Tasks.Where(t => t.Enabled).ToList();

        if (!ValidateChannel(channel, tasks))
        {
            return;
        }

        if (!IsRunning)
        {
            // 从设置页单独点「连接并执行」时也要把本轮状态归零（不然上一轮的异常标志会残留）
            _runStoppedOrError = false;
            _runTaskErrorCount = 0;
            _runParts.Clear();
        }

        var session = EnsureSession(channel);
        ActiveSession = session;

        // 单通道入口也计入本轮运行：这样"停止执行"可用，收尾也走同一套
        _pendingRuns = Math.Max(1, _pendingRuns);
        IsRunning = true;
        StartRdpPolling();

        await RunChannelAsync(session, tasks);
    }

    /// <summary>
    /// 把设置页那一组单通道配置（target_* / bridge_path / session_finish / 分辨率）合成一个通道。
    /// 用固定 id，便于日志与去重识别；参数已被 <see cref="RdpChannel"/> 的 setter 规整过。
    /// </summary>
    private RdpChannel ChannelFromSettings() => new()
    {
        Id = SettingsChannelId,
        Name = "当前设置",
        Host = RdpSettings.TargetHost,
        User = RdpSettings.TargetUser,
        BridgePath = RdpSettings.BridgePath,
        CredentialSaved = RdpSettings.CredentialSaved,
        SessionFinish = RdpSettings.SessionFinish,
        DesktopWidth = RdpSettings.DesktopWidth,
        DesktopHeight = RdpSettings.DesktopHeight,
    };

    /// <summary>按通道 id 取已有的会话（没有就新建一个并登记进本轮）。</summary>
    private RdpChannelSession EnsureSession(RdpChannel channel)
    {
        var existing = _sessions.FirstOrDefault(s =>
            s.IsRemote && string.Equals(s.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // 同一通道再次下发：刷新配置快照（用户可能刚改过账户/分辨率）
            return existing;
        }

        var session = new RdpChannelSession(channel, RdpBridge.ForChannel(channel));
        _sessions.Add(session);
        RaiseSessionsChanged();
        return session;
    }

    /// <summary>把某条通道的状态镜像到界面（仅当它正在被展示）。</summary>
    private void MirrorSessionIfActive(RdpChannelSession session)
    {
        if (ReferenceEquals(_activeSession, session))
        {
            MirrorActiveSession();
        }
    }

    /// <summary>
    /// 通道下发前的预检（原单通道版全部保留，只是配置来源从 RdpSettings 换成通道）。
    /// 返回 false 表示这条通道不该启动，原因已经通过对话框 / 日志说明过。
    /// </summary>
    private bool ValidateChannel(RdpChannel channel, IReadOnlyList<TaskConfig> tasks)
    {
        var tag = channel.DisplayName;
        var host = RdpTargets.Normalize(channel.Host);
        var user = channel.User;
        var isLocal = RdpTargets.IsLocal(host);

        if (tasks.Count == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", $"通道「{tag}」没有启用的任务。");
            return false;
        }

        if (!HasUsableTaskConfig())
        {
            _ = DialogHelper.ShowMessageAsync("提示", "还没有设置可执行的脚本配置。");
            return false;
        }

        var readiness = RdpSessionService.CheckReadiness(host, user, channel.CredentialSaved, channel.BridgePath);

        if (isLocal && !readiness.UserExists)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"本机不存在账户「{user}」。\n请先在 Windows 里创建该账户，再回到这里连接。");
            return false;
        }

        if (!readiness.CredentialSaved)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"通道「{tag}」还没有保存目标账户的凭据（目标主机：{host}）。\n请先填写账户名与密码并保存。");
            return false;
        }

        if (isLocal && !readiness.RemoteDesktopEnabled)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接", $"系统尚未开启远程桌面，通道「{tag}」无法连接。请先点击「开启远程桌面」。");
            return false;
        }

        if (isLocal && !readiness.LikelySupported)
        {
            _ = DialogHelper.ShowMessageAsync("当前系统不支持",
                readiness.EditionNote + "\n\nWindows 家庭版不能作为远程桌面服务端，无法登录其它账户。");
            return false;
        }

        if (!isLocal && channel.BridgePath.Trim().Length == 0)
        {
            _ = DialogHelper.ShowMessageAsync("还需要配置共享目录",
                $"通道「{tag}」的目标 {host} 是远程主机，对方读不到本机的指令文件。\n\n" +
                "请先在该通道的「指令桥目录」里填一个两台机器都能读写的共享位置（例如 \\\\192.168.1.20\\YukinoBridge），" +
                "并在对方机器上也部署会话代理、指向同一个目录。\n\n" +
                "如果只想切本机另一个账户，把远程主机改回 127.0.0.1 就行。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 在**一条通道**上执行一批任务：下发指令 → 建立/复用会话 → 等代理上线 →
    /// 之后交给 1Hz 轮询按通道收尾。
    ///
    /// 多通道并行时，每条通道各跑一份本方法（互不阻塞）；通道内部的任务仍按 order 串行
    /// —— 那是代理侧 <c>ScriptRunner</c> 的事，与主控端无关。
    /// </summary>
    private async Task RunChannelAsync(RdpChannelSession session, IReadOnlyList<TaskConfig> tasks)
    {
        var channel = session.Channel!;
        var bridge = session.Bridge;
        var tag = session.DisplayName;

        var host = RdpTargets.Normalize(channel.Host);
        var user = channel.User;
        var isLocal = RdpTargets.IsLocal(host);

        var command = new RdpCommand
        {
            Tasks = tasks.Select(t => (TaskConfig)t.Clone()).ToList(),
            ShutdownAfterDone = Config.ShutdownAfterDone,
            ShutdownDelaySeconds = Config.ShutdownDelaySeconds,
            EnableTimeoutScreenshot = Config.EnableTimeoutScreenshot,
            SessionFinish = channel.SessionFinish,
            NotifyOnTaskDone = RdpSettings.NotifyOnTaskDone,
            NotifyOnAllDone = RdpSettings.NotifyOnAllDone,
        };

        bridge.EnsureDirectory();
        if (!bridge.WriteCommand(command))
        {
            AppendLog($"[通道：{tag}] ✗ 指令下发失败：{bridge.LastError}");
            _ = DialogHelper.ShowMessageAsync("下发失败",
                $"通道「{tag}」无法写入共享指令文件：{bridge.CommandPath}\n\n" +
                "请以管理员身份运行雪乃酱，或把程序放在所有用户都有写权限的目录。");

            // 这条通道不再等结果：标记收尾，让整轮计数能正常归零
            session.MarkCompletionReported();
            FinishRunPart();
            return;
        }

        // 复位桥里的状态并记下本指令的归属与下发时刻 ——
        // 之后的每次轮询都用它校验 status.json 是不是本指令写的，并从此刻起算启动宽限期。
        bridge.ResetStatus(command.Id);
        session.Begin(command.Id, tasks.Count, DateTimeOffset.Now);
        session.SetStatusText("正在连接目标会话…");
        MirrorSessionIfActive(session);

        AppendLog($"[通道：{tag}] 已下发远程执行指令 {command.Id}，共 {tasks.Count} 个任务，目标：{host} / {user}。");

        // 目标账户已经登录着就不再新建连接（仅 mstsc 模式）。
        // Windows 客户端版同时只允许一个交互式会话，重复连接会把目标账户
        // 正在用的那个桌面接管过来（当前桌面则被踢到锁屏），
        // 而这对"把任务跑起来"毫无帮助 —— 代理直接在那个已有会话里跑就行。
        // 内嵌模式例外：画面要嵌进窗口就必须真正建立连接（重连会接回同一会话，无副作用）。
        RdpSessionInfo? reusableSession = null;
        var hasReusableSession = !IsEmbeddedMode
            && isLocal
            && RdpSessionService.TryGetReusableSession(user, out reusableSession);

        // M6：内嵌画面按通道分家 —— 每条通道各有各的 client，不再限制"只有第一条能内嵌"。
        // 已连上的通道直接复用，未连的才新建（原生侧上限 YCN_MAX_SESSIONS = 8，与并发上限一致）。
        var usesEmbedded = IsEmbeddedMode && !HasSurface(channel.Id);
        var reuseEmbedded = IsEmbeddedMode && HasSurface(channel.Id);

        if (reuseEmbedded)
        {
            AppendChannelLog(session, "内嵌画面已连接，直接复用（该通道的画面在它自己的通道页上）。");
        }
        else if (hasReusableSession)
        {
            AppendLog(
                $"[{tag}] 检测到账户「{user}」已经登录（会话 {reusableSession!.Id}，{reusableSession.StateText}），" +
                "直接复用它，本次不再新建远程桌面连接。");
        }
        else if (usesEmbedded)
        {
            // 内嵌模式：FreeRDP 连接，画面嵌入窗口；会话建立后代理照常接管（桥/心跳零改动）
            if (!TryStartEmbeddedConnect(channel.Id, host, user, channel.DesktopWidth, channel.DesktopHeight, out var embedMessage))
            {
                _ = DialogHelper.ShowMessageAsync("连接失败", embedMessage);
                session.SetStatusText("连接失败");
                MirrorSessionIfActive(session);
                session.MarkCompletionReported();
                FinishRunPart();
                return;
            }

            AppendChannelLog(session, "内嵌模式下任务画面显示在该通道页里；也可以随时点全屏接管键盘操作。");
            AppendChannelLog(session, "提示：任务跑在目标会话里，断开内嵌连接等于「断开连接」——会话与进程保留，任务继续跑。");
        }
        else
        {
            // 用系统自带的远程桌面（mstsc）打开独立窗口。
            // .rdp 文件名带通道 id —— 否则两条通道会互相覆盖同一份会话文件。
            var started = RdpSessionService.Connect(
                host, out var connectMessage, channel.DesktopWidth, channel.DesktopHeight, channel.Id);

            if (!started)
            {
                _ = DialogHelper.ShowMessageAsync("连接失败", connectMessage);
                session.SetStatusText("连接失败");
                MirrorSessionIfActive(session);
                session.MarkCompletionReported();
                FinishRunPart();
                return;
            }

            AppendLog($"[通道：{tag}] {connectMessage}");
            if (isLocal)
            {
                AppendLog($"[通道：{tag}] 提示：Windows 客户端版同时只允许一个交互式会话，连接后当前桌面会被锁定，任务在目标账户中执行。");
                AppendLog($"[通道：{tag}] 任务跑在目标会话里：关闭远程桌面窗口时请选「断开连接」（会话和进程会保留，任务继续跑），不要选「注销」—— 注销会销毁会话，代理与脚本会被系统一并终止。");
            }
            else
            {
                AppendLog($"[通道：{tag}] 提示：远程目标需要一个双方都能读写的共享目录来传递指令与进度，Agent 会在对方机器上自动接管任务。");
            }
        }

        IsRdpBusy = true;
        MirrorSessionIfActive(session);

        await WaitForChannelReadyAsync(session, isLocal, hasReusableSession, reusableSession);

        RefreshRdpReadiness();
    }

    /// <summary>
    /// 等这条通道"可执行"：本机目标先等会话建立，再等目标会话里的代理上线；
    /// 远程目标等不到（会话不在本机），交给 status.json 轮询反映真实进度。
    /// </summary>
    private async Task WaitForChannelReadyAsync(
        RdpChannelSession session, bool isLocal, bool hasReusableSession, RdpSessionInfo? reusableSession)
    {
        var tag = session.DisplayName;
        var user = session.TargetUser;
        var channel = session.Channel!;

        if (!isLocal)
        {
            IsRdpBusy = false;
            session.SetStatusText($"已连接 {channel.Host}，等待对方会话代理接管…");
            MirrorSessionIfActive(session);
            AppendLog($"[通道：{tag}] 远程目标无法在本机枚举会话，以下方回传的 status.json 为准；若长时间无进展，请检查对方是否已部署会话代理并指向同一共享目录。");
            SetMascotState(MascotStates.Work, force: true);
            ShowTemporaryMascotMessage("已经连上远程主机，等那边的我回传进度～", 10, 80);
            return;
        }

        var timeout = RdpSettings.ConnectTimeoutSeconds;

        // 本机目标：先等目标会话真的建立起来（WTS 能枚举到）
        var connected = await Task.Run(() => RdpSessionService.WaitForSession(
            user,
            timeout,
            CancellationToken.None));

        if (!connected)
        {
            IsRdpBusy = false;
            session.SetStatusText("等待目标会话超时");
            MirrorSessionIfActive(session);
            AppendLog($"[通道：{tag}] 等待目标会话超时（{timeout} 秒）。请确认凭据正确、目标账户允许远程登录，或手动在远程桌面窗口里完成登录。");
            SetMascotState(MascotStates.Error, errorReason: $"通道「{tag}」等待目标账户会话超时，请检查凭据与远程桌面权限。");
            return;
        }

        var created = reusableSession ?? RdpSessionService.FindSession(user);
        session.SetStatusText($"目标会话已就绪（会话 {created?.Id}），正在等待会话代理上线…");
        MirrorSessionIfActive(session);
        AppendLog(hasReusableSession
            ? $"[{tag}] 复用已有会话 {created?.Id}，无需等待登录。"
            : $"[{tag}] 目标账户会话已建立。");
        AppendLog($"[通道：{tag}] 会话代理会随目标账户登录自动启动并常驻等待指令，正在确认它是否在线…");

        // 【不再隔空拉起】代理随登录自启且常驻，主控端只负责确认它上线。
        // 复用已有会话这一路同样要确认 —— 账户本来就登录着，启动项不会重跑，代理很可能不在线。
        var online = await Task.Run(() => RdpSessionService.WaitForAgentOnline(
            user,
            channel.BridgePath,
            timeout,
            CancellationToken.None));

        IsRdpBusy = false;

        if (online)
        {
            session.SetStatusText("会话代理已上线，等待接管任务…");
            MirrorSessionIfActive(session);
            AppendLog($"[通道：{tag}] 已确认会话代理在线，任务将自动交接给它。");
            SetMascotState(MascotStates.Work, force: true);
            ShowTemporaryMascotMessage("代理已经在线待命，开始干活～", 10, 80);
            return;
        }

        session.SetStatusText("目标账户已登录，但会话代理没有上线");
        MirrorSessionIfActive(session);
        AppendLog($"[通道：{tag}] 等待会话代理上线超时（{timeout} 秒）：目标账户虽然在会话里，但代理没有随之启动。");
        AppendLog($"[通道：{tag}] 原因：代理只在目标账户「登录」那一刻由启动项拉起；账户本来就登录着时，启动项不会重跑，代理也就不在。");
        SetMascotState(MascotStates.Error, errorReason: $"通道「{tag}」的会话代理没有上线，请注销目标账户后重新连接一次。");
        _ = PromptLogoffAndReconnectAsync(user);
    }
    /// <summary>
    /// 代理没上线时的可操作引导：把「注销目标账户 → 重新连接」这条正解摆到用户面前。
    ///
    /// 为什么必须注销后重连：代理只在目标账户登录那一刻由公共启动目录的快捷方式拉起。
    /// 账户本来就登录着时，启动项不会重跑，任何"隔空拉起"的旁路都已经删除，
    /// 唯一能让代理起来的方式就是让账户重新登录一次。
    /// 注销直接复用现有的 WTS 注销能力（<see cref="RdpSessionService.LogoffSession"/>）。
    /// </summary>
    /// <param name="targetUser">出问题的通道的目标账户；不传则用设置页当前配置的账户。</param>
    private async Task PromptLogoffAndReconnectAsync(string? targetUser = null)
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

            var session = RdpSessionService.FindSession(targetUser ?? RdpSettings.TargetUser);
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

    /// <summary>
    /// 每秒遍历**全部通道**的桥。每条通道各自校验指令归属、各自判定完成 ——
    /// 这正是多通道并行与改造前"全局单例轮询"的根本差别。
    /// </summary>
    private void PollRdpStatus()
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        foreach (var session in _sessions.ToList())
        {
            if (!session.IsRemote || !session.HasCommand)
            {
                continue;
            }

            var status = session.Bridge.TryReadStatus();
            if (status is null)
            {
                continue;
            }

            var tick = session.Tick(status, DateTimeOffset.Now, RdpSettings.ConnectTimeoutSeconds);
            ApplyChannelTick(session, tick);
        }

        // 界面属性跟着"正在展示的通道"走
        if (_activeSession is not null)
        {
            MirrorActiveSession();
        }
    }

    /// <summary>
    /// 把一次轮询结果落成用户可见的动作（日志 / 通知 / 看板娘 / 收尾）。
    /// Session 只改状态、不产生副作用，副作用统一收在这里。
    /// </summary>
    private void ApplyChannelTick(RdpChannelSession session, RdpChannelTick tick)
    {
        var tag = session.DisplayName;

        // ① 归属不匹配：桥里还是上一轮指令的状态（或本指令尚未落地）→
        //    这是"等待代理接管"的正常过渡态，绝不报失联，也不采信它的进度与事件。
        if (tick.AwaitingAgent)
        {
            if (tick.AwaitingAgentFirstTime)
            {
                AppendLog($"[通道：{tag}] 等待目标会话中的代理接管（尚未收到本指令的心跳，属正常过渡）。");
            }

            if (tick.HeartbeatRecovered)
            {
                AppendLog($"[通道：{tag}] ✓ 会话代理心跳已恢复，任务继续运行。");
            }

            return;
        }

        // ② 事件
        foreach (var ev in tick.NewEvents)
        {
            AppendLog($"[通道：{tag}] {ev.Time} {ev.Headline}");

            // 目标会话里的 Toast 在当前桌面上是看不到的，主控端这一侧也要弹一次
            if (ev.Kind == RdpEventKinds.TaskDone && RdpSettings.NotifyOnTaskDone)
            {
                if (!NotificationService.ShowTaskDone(
                        ev.TaskName, ev.Index, ev.Total, ev.ElapsedSeconds, ev.IsAbnormal))
                {
                    ShowTemporaryMascotMessage($"远程任务完成：{ev.TaskName}", 8, 70);
                }
            }
        }

        // 心跳恢复要补一条日志（否则日志会永远停在"断开连接"，用户翻日志只看到误报）
        if (tick.HeartbeatRecovered)
        {
            AppendLog($"[通道：{tag}] ✓ 会话代理心跳已恢复，任务继续运行。");
        }

        // ③ 失联：只报一次，避免每秒刷屏
        if (tick.JustWentStale)
        {
            var summary =
                $"任务「{session.TaskDisplay}」停在 {session.Progress} / {session.Total}（已运行 {session.ElapsedText}）";

            session.MarkStale();

            AppendLog($"[通道：{tag}] ⚠ 已超过 {RdpHeartbeat.TimeoutSeconds} 秒没收到会话代理心跳。最后状态：{summary}。");
            AppendLog($"[通道：{tag}] 目标会话很可能已经断开或被注销 —— 会话里的代理与脚本进程会随之终止，所以进度不再更新。");
            AppendLog($"[通道：{tag}] 如果是你主动断开的远程桌面：请用「断开连接」而不是「注销目标账户」，任务才能在后台继续跑。");

            SetMascotState(MascotStates.Error, errorReason: $"通道「{tag}」的会话代理已失联，请检查目标会话是否还在。");

            if (RdpSettings.NotifyOnAllDone)
            {
                NotificationService.ShowSummary($"雪乃酱：通道「{tag}」代理失联", $"最后状态：{summary}");
            }
        }

        // ④ 完成（幂等：一轮指令只收尾一次，事件通道与终态相位可能同时命中）
        if (tick.CompletionSignaled)
        {
            ReportSessionCompletion(session, tick);
        }
    }

    /// <summary>
    /// 一条通道的远程执行收尾 —— "跑完了"这件事的全部用户感知都在这。
    ///
    /// 幂等：一整轮指令只收尾一次（完成信号可能来自 Finished 事件，也可能来自终态相位，
    /// 两者可能同时命中，必须保证「看板娘 / 汇总通知 / 结束日志 / 刷统计」只做一次）。
    /// 多通道下每条通道各自收尾，收尾完一条才减计数，全部减完才算整轮结束。
    /// </summary>
    /// <param name="session">收尾的通道。</param>
    /// <param name="tick">完成那一刻的轮询结果（用于取结束文案与异常标记）。</param>
    private void ReportSessionCompletion(RdpChannelSession session, RdpChannelTick tick)
    {
        if (session.CompletionReported)
        {
            return;
        }

        session.MarkCompletionReported();

        var status = tick.Status;
        var tag = session.DisplayName;

        // 汇总正文优先取 Finished 事件的 Message。不要用 status.StatusText ——
        // 常驻代理 1Hz 的 idle 心跳会把它覆盖成「代理在线，等待指令。」，
        // 日志里已实证这个竞态真的命中过（汇总通知正文变成了一句废话）。
        var summary = status?.Events.LastOrDefault(e => e.Kind == RdpEventKinds.Finished)?.Message;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = string.IsNullOrWhiteSpace(status?.StatusText) ? "全部任务结束" : status!.StatusText;
        }

        // 收尾方式按通道自己的配置（多通道下每条通道可以不一样）
        var finishMode = session.Channel?.SessionFinish ?? RdpSettings.SessionFinish;
        var abnormal = tick.Abnormal;

        // 只登记这块**自己的**结果，暂不定终态 ——
        // 还有别的块在跑时看板娘必须继续"工作"，终态由 FinalizeRunSummary 取最差统一决定。
        _runParts.Add(new RunPartResult($"通道「{tag}」", true, abnormal, summary));

        if (abnormal)
        {
            // 异常要立刻可见（整轮可能还要跑很久才结束）。Error 本身是粘性状态，
            // 后续各块的正常结束不会把它顶掉 —— 这正好是"取最差"的天然实现。
            SetMascotState(MascotStates.Error, errorReason: $"通道「{tag}」远程会话执行出现异常，请查看运行日志。");
        }

        // 还有别的块没结束时，给这条通道一个单独的收尾反馈；整轮结束的汇总另行发布
        if (_pendingRuns > 1)
        {
            ShowTemporaryMascotMessage(
                abnormal ? $"通道「{tag}」结束了，但有异常。" : $"通道「{tag}」的远程任务全部结束啦～",
                10, 60);
        }

        if (RdpSettings.NotifyOnAllDone)
        {
            var posted = NotificationService.ShowSummary($"雪乃酱：通道「{tag}」任务结束", summary);

            // 本地多用户场景下主控端会话大概率处于锁定状态（mstsc 接管了控制台），
            // 锁定会话不弹 Toast 横幅，通知只会静默进操作中心。
            // 只要无法确认横幅已展示（发送失败或窗口非激活），就落一份暂存，
            // 等窗口重新激活时由 MainWindow 补发。
            if (!posted || !MainWindow.IsWindowActive)
            {
                PendingNotificationStore.Save($"雪乃酱：通道「{tag}」任务结束", summary);
            }
        }

        AppendChannelLog(session, $"远程执行结束：{summary}（会话处理方式：{SessionFinishModes.Label(finishMode)}）。");

        FinishRunPart();
    }

    /// <summary>
    /// 一条通道收尾后的交接：还有别的通道在跑就把界面切到那条，全跑完才停轮询、落终态。
    /// </summary>
    private void FinishChannel(RdpChannelSession session)
    {
        if (!ReferenceEquals(_activeSession, session) || _pendingRuns <= 0)
        {
            return;
        }

        // 正在展示的这条已结束 → 切到还没结束的那条，用户不至于盯着一个静止的卡片
        var next = _sessions.FirstOrDefault(s => s.IsRemote && s.HasCommand && s != session);
        if (next is not null)
        {
            ActiveSession = next;
        }
    }

    /// <summary>
    /// 本轮执行的一块（本地执行算一块，每条远程通道各算一块）跑完。
    /// 归零才停轮询、刷终态、放行自动退出 —— 多通道并行时"一部分结束"不等于"整轮结束"。
    /// </summary>
    private void FinishRunPart()
    {
        _pendingRuns = Math.Max(0, _pendingRuns - 1);

        if (_sessions.Count > 0 && _pendingRuns > 0)
        {
            // 还有通道/本地任务在跑：只把已结束的那条切走，别停轮询
            var finished = _sessions.FirstOrDefault(s => s.IsRemote && !s.HasCommand && s.CompletionReported);
            if (finished is not null)
            {
                FinishChannel(finished);
            }

            return;
        }

        StopRdpPolling();
        IsRdpBusy = false;
        IsRunning = false;
        _runEndedAt = DateTimeOffset.Now;
        RefreshStats();
        RefreshRdpReadiness();

        // 整轮结束：清掉活跃指针（菜单项与事件列表留给用户看，见计划书 D3）
        RaiseRunFinished();

        // 定终态 + 出汇总（看板娘取最差、一条汇总通知、逐块汇总日志）
        FinalizeRunSummary();

        RaisePanelChanged();

        if (Config.AutoExitAfterDone && !_runStoppedOrError)
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
    }

    /// <summary>整轮执行结束（本地 + 全部通道）时通知界面刷新。</summary>
    private void RaiseRunFinished()
    {
        OnPropertyChanged(nameof(HasRemoteSession));
        RefreshRdpReadiness();
    }

    /// <summary>
    /// 整轮结束定终态：看板娘**取最差**（任一异常 → Error，否则 Rest）+ 一条汇总通知 + 逐块汇总日志。
    ///
    /// 为什么不在各块收尾时直接定：多通道并行时各块完成时间不同，
    /// A 跑完就给看板娘"休息"会让用户以为整轮结束（而 B 其实还在跑）。
    /// 所以各块只往 <see cref="_runParts"/> 里登记结果，真正的终态在这里一次性决定。
    ///
    /// 单块（只有本地、或只有一条通道）时不发汇总 —— 它和上面按块的收尾反馈完全重复。
    /// </summary>
    private void FinalizeRunSummary()
    {
        var parts = _runParts.ToList();
        _runParts.Clear();

        if (parts.Count == 0)
        {
            _lastRunAbnormal = false;
            return;
        }

        // 多块时状态面板要显示"已完成（有异常）"—— 这里定下来，面板是计算属性、不自己判
        _lastRunAbnormal = RunSummary.HasAnyAbnormal(parts);

        if (parts.Count > 1)
        {
            AppendLog($"[汇总] {RunSummary.ComposeHeadline(parts)}");
            foreach (var line in RunSummary.ComposeLines(parts))
            {
                AppendLog("[汇总] " + line);
            }

            if (RdpSettings.NotifyOnAllDone)
            {
                var body = RunSummary.ComposeHeadline(parts);
                var posted = NotificationService.ShowSummary(RunSummary.Title, body);
                if (!posted || !MainWindow.IsWindowActive)
                {
                    PendingNotificationStore.Save(RunSummary.Title, body);
                }
            }
        }

        if (RunSummary.HasAnyAbnormal(parts))
        {
            SetMascotState(
                MascotStates.Error,
                errorReason: $"本轮执行有 {RunSummary.AbnormalCount(parts)} 块出现异常，详情见运行日志。");
        }
        else
        {
            // 不 force：中途若有 Error（比如日志里出现过错误行）应当保持粘着，
            // 让用户看得到 —— 这与"取最差"是同一个取向。
            SetMascotState(MascotStates.Rest);
        }

        ShowTemporaryMascotMessage(RunSummary.ComposeMascotText(parts), 12, 60);
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
