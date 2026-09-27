// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YukinoChan.Helpers;
using YukinoChan.Models;

namespace YukinoChan.Services;

public sealed class AbnormalEvent
{
    public string Time { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public string LogFile { get; set; } = string.Empty;
    public string LogSession { get; set; } = string.Empty;
}

/// <summary>单个任务完成的快照，用于通知与 RDP 跨会话状态回传。</summary>
public sealed class TaskCompletedInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>1-based 序号；并发组内为组内推算序号。未知时为 0。</summary>
    public int Index { get; set; }

    public int Total { get; set; }

    public int ElapsedSeconds { get; set; }

    /// <summary>人类可读的结束原因，例如「正常结束，退出码：0」。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>是否为异常结束（启动失败 / 超时强退 / 路径无效等）。</summary>
    public bool IsAbnormal { get; set; }

    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.Now;

    public string Headline =>
        Total > 0 && Index > 0 ? $"({Index}/{Total}) {Name}" : Name;
}

public sealed class AbnormalReport
{
    public string Version { get; set; } = "v2.0 WinUI";
    public string CreatedAt { get; set; } = string.Empty;
    public string LogFile { get; set; } = string.Empty;
    public string LogSession { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<AbnormalEvent> Events { get; set; } = new();
}

/// <summary>
/// 任务队列执行器。完整移植 Python 版 ScriptRunnerWorker 的调度语义：
/// 顺序执行 / 并发组 / 暂停 / 停止 / 紧急停止 / 三层清理 / 超时兜底 / 过早退出识别。
/// </summary>
public sealed class ScriptRunner
{
    private const double MonitorAbsentGraceSeconds = 15.0;
    private const double MonitorDetectDeadlineSeconds = 180.0;
    private const double LaunchConfirmSeconds = 2.0;

    /// <summary>
    /// 直接进程秒退后确认"是否由新进程接力"的最长等待（秒）。
    /// 提权重启要过 UAC、新实例要初始化，给 10 秒足够；太长会把真崩溃的判定拖慢。
    /// </summary>
    private const double LaunchRelayGraceSeconds = 10.0;
    private const int ErrorLockBaseSeconds = 15;

    private readonly List<TaskConfig> _tasks;
    private readonly bool _shutdownAfterDone;
    private readonly int _shutdownDelaySeconds;
    private readonly bool _enableTimeoutScreenshot;
    private readonly FileLogger _logger;
    private readonly RuntimeStatsManager _stats;

    private readonly object _sync = new();
    private readonly List<IMonitoredProcess> _currentProcesses = new();
    private readonly Dictionary<int, List<string>> _processNamesByPid = new();
    private readonly Dictionary<int, TaskConfig> _tasksByPid = new();

    /// <summary>
    /// 本轮通过窗口标题监控**实际命中**的进程镜像名（如 <c>YuanShen</c> / <c>StarRail</c>）。
    /// 配置里写的常是中文窗口标题，靠它杀不掉也查不出，见 <see cref="LearnMonitorImageNames"/>。
    /// </summary>
    private readonly HashSet<string> _learnedImageNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本轮**脚本进程派生出来的后代**（PID + 镜像名）。
    /// 停止时必须连同它们一起清，理由见 <see cref="LearnScriptDescendants"/>。
    /// 按 PID 记而不是按名字记 —— 名字容易撞车（脚本常会派生 <c>conhost.exe</c> 这类公共进程），
    /// 按名字清会误伤系统里别的同名进程。
    /// </summary>
    private readonly List<ProcessSnapshotEntry> _scriptDescendants = new();

    private readonly List<AbnormalEvent> _abnormalEvents = new();

    private volatile bool _stopRequested;
    private volatile bool _pauseRequested;
    private volatile bool _emergencyRequested;

    public ScriptRunner(
        IEnumerable<TaskConfig> tasks,
        bool shutdownAfterDone,
        int shutdownDelaySeconds,
        FileLogger logger,
        RuntimeStatsManager stats,
        bool enableTimeoutScreenshot)
    {
        _tasks = new List<TaskConfig>(tasks);
        _shutdownAfterDone = shutdownAfterDone;
        _shutdownDelaySeconds = Math.Max(0, shutdownDelaySeconds);
        _enableTimeoutScreenshot = enableTimeoutScreenshot;
        _logger = logger;
        _stats = stats;
        AbnormalReportPath = Path.Combine(logger.LogDir, "last_abnormal_report.json");
    }

    public string AbnormalReportPath { get; }

    public bool HadTaskError { get; private set; }

    public int TaskErrorCount { get; private set; }

    public IReadOnlyList<AbnormalEvent> AbnormalEvents => _abnormalEvents;

    public event EventHandler<string>? Logged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<(int Current, int Total)>? ProgressChanged;
    public event EventHandler<(string Name, int Index, int Total)>? TaskStarted;
    public event EventHandler<TaskCompletedInfo>? TaskCompleted;
    public event EventHandler<int>? ElapsedChanged;
    public event EventHandler<string>? TaskLaunchSucceeded;
    public event EventHandler<string>? TaskError;
    public event EventHandler<int>? ShutdownPrompt;
    public event EventHandler<bool>? Finished;

    // ---------------- 对外控制 ----------------

    public void RequestPause()
    {
        _pauseRequested = true;
        RaiseStatus("已暂停");
        Log("收到暂停请求：任务队列会暂停计时和后续检查，已启动的外部脚本不会被强制暂停。");
    }

    public void RequestResume()
    {
        if (!_pauseRequested)
        {
            return;
        }

        _pauseRequested = false;
        RaiseStatus("运行中");
        Log("收到继续请求：任务队列恢复运行。");
    }

    /// <summary>
    /// 停止执行：只收尾当前正在运行的任务。
    ///
    /// 【为什么不再在这里做整轮清理】本方法是被"每秒计时回调"/停止监听线程**同步**调用的，
    /// 而按关键词清理要枚举全部进程、逐个 taskkill，实测一轮要十几秒。
    /// 在这十几秒里工作线程被完全占住：进度停摆、心跳停摆、状态不再回传 ——
    /// 主控端看到的就是"点了停止，界面卡住没反应"（真机日志实证过：停止到收尾隔了 14 秒）。
    /// 现在这里只做两件轻量且立刻见效的事：置标志位 + 终止已登记的直接脚本进程；
    /// 按关键词的统一三层清理交给主循环下一拍（250ms 内必到），那里本来就有完整的收尾分支。
    /// </summary>
    public void RequestStop()
    {
        _stopRequested = true;
        _pauseRequested = false;
        Log("收到用户停止执行请求：正在按当前任务的启动脚本进程 → 目标进程 → 游戏窗口/扩展进程顺序收尾。");
        TerminateTrackedProcesses("停止执行");
    }

    /// <summary>
    /// 紧急停止：停掉队列，并把**全部任务配置**（不只当前任务）的进程一起收尾。
    ///
    /// 与 <see cref="RequestStop"/> 的唯一差别是清理范围：
    ///   常规停止 = 当前任务（+ 本轮监控学到的真实镜像名）；
    ///   紧急停止 = 全部任务配置 —— 因此连"启动后不等待"那类已经脱离本轮的进程也能扫到。
    /// 两者都不在这里做重活（理由同 RequestStop）。
    /// </summary>
    public void RequestEmergencyStop()
    {
        _stopRequested = true;
        _pauseRequested = false;
        _emergencyRequested = true;
        Log("收到紧急停止请求：本轮立即停止，并按全部任务配置（不只当前任务）清理进程。");
        TerminateTrackedProcesses("紧急停止");
    }

    /// <summary>
    /// 立刻结束当前登记在册的脚本进程（轻量：只 Kill，不枚举、不扫关键词）。
    /// 关键词那层留给主循环，避免把调用方（计时回调 / 停止监听线程）堵住。
    /// </summary>
    private void TerminateTrackedProcesses(string scopeLabel)
    {
        List<IMonitoredProcess> processes;
        lock (_sync)
        {
            processes = new List<IMonitoredProcess>(_currentProcesses);
        }

        // 在这里就把"脚本进程派生了谁"记下来 —— 这是停止链路上**最早**的一刻，
        // 晚一点父进程可能已经死了，只剩孤儿记录（2026-09-27 漏网事故的直接原因）。
        // 开销只是一次进程快照（毫秒级），不影响本方法"轻量"的约束。
        LearnScriptDescendants(processes);

        foreach (var process in processes)
        {
            if (process.Poll() is null)
            {
                Log($"{scopeLabel}：正在终止脚本进程 PID：{process.Pid}");
                process.Terminate();
            }
        }
    }

    public Task RunAsync() => Task.Run(RunCore);

    // ---------------- 主循环 ----------------

    private void RunCore()
    {
        var stopped = false;
        try
        {
            RaiseStatus("运行中");

            var enabledTasks = new List<TaskConfig>();
            foreach (var task in _tasks)
            {
                if (task.Enabled)
                {
                    enabledTasks.Add(task);
                }
            }

            enabledTasks.Sort((a, b) => a.Order.CompareTo(b.Order));

            var total = enabledTasks.Count;
            if (total == 0)
            {
                Log("没有启用的任务，执行结束。");
                RaiseStatus("已完成");
                RaiseProgress(0, 0);
                RaiseFinished(false);
                return;
            }

            Log($"运行日志文件：{Path.GetFileName(_logger.LogPath)}");
            Log($"开始执行任务队列，共 {total} 个启用任务。");
            RaiseProgress(0, total);

            var index = 0;
            while (index < total)
            {
                WaitIfPaused();
                if (_stopRequested)
                {
                    stopped = true;
                    Log("检测到停止请求，后续任务不再执行。");
                    break;
                }

                var task = enabledTasks[index];
                var groupName = task.ConcurrentGroup.Trim();

                if (groupName.Length > 0)
                {
                    var groupTasks = new List<TaskConfig> { task };
                    var j = index + 1;
                    while (j < total && enabledTasks[j].ConcurrentGroup.Trim() == groupName)
                    {
                        groupTasks.Add(enabledTasks[j]);
                        j++;
                    }

                    RaiseProgress(index + 1, total);
                    RaiseStatus("并发运行中");
                    RaiseElapsed(0);
                    var groupOutcome = RunConcurrentGroup(groupName, groupTasks, index + 1, total);
                    index = j;

                    if (groupOutcome != RunOutcome.Continue)
                    {
                        stopped = true;
                        break;
                    }
                }
                else
                {
                    RaiseProgress(index + 1, total);
                    RaiseTaskStarted(task.DisplayName, index + 1, total);
                    RaiseStatus("运行中");
                    RaiseElapsed(0);
                    var outcome = RunOneTask(task, index + 1, total);
                    index++;

                    if (outcome != RunOutcome.Continue)
                    {
                        stopped = true;
                        break;
                    }
                }
            }

            if (stopped)
            {
                // 统一收尾（必须在清空登记表**之前**）：正常路径上 RunOneTask 已经就地清过了，
                // 但「启动确认 / 接力确认被停止打断」走的是另一条分支（只放弃等待、不做清理），
                // 常规停止在那条路上就没人杀目标进程 —— 表现成"点了停止，游戏还在跑"。
                CleanupForStop();

                RaiseStatus("已停止");
                Log("任务队列已停止。");
                SaveAbnormalReport();
                RaiseFinished(true);
                return;
            }

            lock (_sync)
            {
                _currentProcesses.Clear();
            }

            if (HadTaskError)
            {
                RaiseStatus("已完成（有异常）");
                RaiseProgress(total, total);
                Log("任务队列执行结束，但存在启动失败、过早退出、超时强退等异常。");
                Log("异常已写入上次运行报告；下次启动会主动汇报。");
            }
            else if (_abnormalEvents.Count > 0)
            {
                RaiseStatus("已完成（有提醒）");
                RaiseProgress(total, total);
                Log("任务队列执行结束，存在配置或监控目标提醒，但未判定为严重异常。");
                Log("提醒已写入上次运行报告；下次启动会主动汇报。");
            }
            else
            {
                RaiseStatus("已完成");
                RaiseProgress(total, total);
                Log("所有任务执行完成。");
            }

            SaveAbnormalReport();

            if (_shutdownAfterDone)
            {
                var delay = Math.Max(1, _shutdownDelaySeconds == 0 ? 60 : _shutdownDelaySeconds);
                Log($"已启用自动关机，将交由主界面弹出 {delay} 秒倒计时确认窗口。");
                ShutdownPrompt?.Invoke(this, delay);
            }
            else
            {
                Log("未启用自动关机。");
            }

            SaveStatsQuietly("本次运行");

            RaiseFinished(HadTaskError);
        }
        catch (Exception ex)
        {
            RaiseStatus("异常");
            Log($"执行器发生异常：{ex.Message}");
            MarkTaskError($"执行器发生异常：{ex.Message}");
            SaveAbnormalReport();
            SaveStatsQuietly("异常前");

            RaiseFinished(true);
        }
    }

    private RunOutcome RunConcurrentGroup(string groupName, List<TaskConfig> groupTasks, int startIndex, int total)
    {
        var policy = ConcurrentPolicies.Normalize(groupTasks[0].ConcurrentPolicy);
        Log($"开始并发组「{groupName}」，共 {groupTasks.Count} 个任务，策略：{ConcurrentPolicies.Label(policy)}。");
        RaiseTaskStarted($"并发组：{groupName}", startIndex, total);

        var results = new Dictionary<int, RunOutcome>();
        var resultsLock = new object();
        var doneEvents = new List<ManualResetEventSlim>();
        var threads = new List<Thread>();

        for (var i = 0; i < groupTasks.Count; i++)
        {
            doneEvents.Add(new ManualResetEventSlim(false));
        }

        for (var i = 0; i < groupTasks.Count; i++)
        {
            var capturedIndex = i;
            var capturedTask = groupTasks[i];
            var thread = new Thread(() =>
            {
                var outcome = RunOneTask(capturedTask, startIndex + capturedIndex, total);
                lock (resultsLock)
                {
                    results[capturedIndex] = outcome;
                }

                doneEvents[capturedIndex].Set();
            })
            {
                IsBackground = true,
                Name = $"concurrent-{groupName}-{i}",
            };

            threads.Add(thread);
            thread.Start();
            Thread.Sleep(200);
        }

        if (policy == ConcurrentPolicies.WaitFirst)
        {
            Log($"并发组「{groupName}」采用只等待组首任务策略；组首任务完成后会继续后续队列，其它同组任务可能仍在运行。");
            while (!doneEvents[0].IsSet)
            {
                WaitIfPaused();
                if (_stopRequested)
                {
                    return RunOutcome.StopRequested;
                }

                Thread.Sleep(250);
            }

            Log($"并发组「{groupName}」组首任务已完成，继续后续队列。");
            return results.TryGetValue(0, out var first) ? first : RunOutcome.Continue;
        }

        while (true)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                return RunOutcome.StopRequested;
            }

            var allDone = true;
            foreach (var done in doneEvents)
            {
                if (!done.IsSet)
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone)
            {
                break;
            }

            Thread.Sleep(250);
        }

        foreach (var outcome in results.Values)
        {
            if (outcome != RunOutcome.Continue)
            {
                return RunOutcome.StopAll;
            }
        }

        Log($"并发组「{groupName}」全部任务已完成。");
        return RunOutcome.Continue;
    }

    private RunOutcome RunOneTask(TaskConfig task, int index, int total)
    {
        var name = task.DisplayName;
        var scriptText = task.ScriptPath.Trim();
        var timeoutSeconds = task.TimeoutSeconds;

        Log($"开始执行任务：{name}");
        Log(FileLogger.BuildSessionMarker(name, "START"));
        Log($"脚本路径：{scriptText}");
        if (task.UseArgs && task.Args.Trim().Length > 0)
        {
            Log($"启动参数：{task.Args.Trim()}");
        }

        var monitorSpec = BuildMonitorSpec(task);
        if (task.WaitMode == WaitModes.FireAndContinue)
        {
            Log("等待模式：启动后不等待；此任务启动成功后会立即进入后续任务。");
        }
        else
        {
            Log($"监控模式：{monitorSpec.Describe()}。监控目标出现后开始等待，连续消失 {MonitorAbsentGraceSeconds:0} 秒后继续下一个任务；超时仅作为兜底。");
        }

        if (task.ConcurrentGroup.Trim().Length > 0)
        {
            Log($"并发组：{task.ConcurrentGroup.Trim()}，组策略：{ConcurrentPolicies.Label(task.ConcurrentPolicy)}");
        }

        if (scriptText.Length == 0)
        {
            var message = $"任务「{name}」脚本路径为空，已跳过。";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "脚本路径为空，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        var scriptPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(scriptText));
        if (!File.Exists(scriptPath))
        {
            var message = $"任务「{name}」脚本不存在，已跳过：{scriptPath}";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "脚本不存在，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        string fileName;
        string arguments;
        try
        {
            (fileName, arguments) = CommandLine.BuildTaskCommand(scriptPath, task.UseArgs, task.Args);
        }
        catch (Exception ex)
        {
            var message = $"任务「{name}」构建启动命令失败，已跳过：{ex.Message}";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "构建启动命令失败，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        var workingDirectory = Path.GetDirectoryName(scriptPath) ?? AppPaths.BaseDir;

        IMonitoredProcess process;
        try
        {
            process = ProcessLauncher.Start(fileName, arguments, workingDirectory);
        }
        catch (Exception ex)
        {
            var message = $"任务「{name}」启动失败，已跳过：{ex.Message}";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "启动失败，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        var processNames = ProcessNamesForTask(task, scriptPath);
        TrackProcess(process, processNames, task);

        Log($"任务「{name}」已启动，PID：{process.Pid}");
        if (processNames.Count > 0)
        {
            Log($"已登记可强制结束的同名进程：{string.Join(", ", processNames)}");
        }

        var stopwatch = Stopwatch.StartNew();
        if (!ConfirmLaunchSuccess(task, process, name, stopwatch, monitorSpec))
        {
            // 停止请求把启动确认 / 接力确认打断了 —— 进程很可能还活着（是我们主动放弃等待，
            // 不是它退出了），所以既不解除登记、也不记"启动失败或提前退出"这条异常：
            // 登记信息要留给 RunCore 的统一收尾去清（提权接力起来的新实例也在关键词范围内）。
            if (_stopRequested)
            {
                Log($"任务「{name}」在启动确认阶段被停止请求打断，交给统一收尾清理。");
                return RunOutcome.StopRequested;
            }

            UntrackProcess(process);
            var failedElapsed = (int)stopwatch.Elapsed.TotalSeconds;
            _stats.AddRecord(name, failedElapsed, "启动失败或提前退出", task.WaitMode);
            RaiseTaskCompleted(name, index, total, failedElapsed, "启动失败或提前退出", abnormal: true);
            return RunOutcome.Continue;
        }

        TaskLaunchSucceeded?.Invoke(this, name);

        if (task.WaitMode == WaitModes.FireAndContinue)
        {
            Log($"任务「{name}」已按“启动后不等待”处理，继续后续任务。");
            UntrackProcess(process);
            _stats.AddRecord(name, 0, "启动后不等待", task.WaitMode);
            RaiseTaskCompleted(name, index, total, 0, "已启动（启动后不等待）", abnormal: false);
            return RunOutcome.Continue;
        }

        if (task.ConfirmEnterDelaySeconds > 0)
        {
            SchedulePressEnter(task.ConfirmEnterDelaySeconds, name);
        }

        var lastEmitSecond = -1;
        var earlyExitThreshold = timeoutSeconds > 0 ? timeoutSeconds / 6.0 : 0;

        var monitorSeen = false;
        var monitorWaitLogged = false;
        double? monitorAbsentSince = null;

        RunOutcome FinishWithLog(string status, bool checkEarlyExit = true, bool abnormal = false)
        {
            var elapsedDone = (int)stopwatch.Elapsed.TotalSeconds;
            RaiseElapsed(elapsedDone);
            Log(FileLogger.BuildSessionMarker(name, "END"));
            Log($"任务「{name}」{status}，耗时：{FormatHelper.FormatSeconds(elapsedDone)}。");

            if (checkEarlyExit && earlyExitThreshold > 0 && elapsedDone < earlyExitThreshold)
            {
                var message =
                    $"任务「{name}」疑似过早退出：实际耗时 {FormatHelper.FormatSeconds(elapsedDone)}，" +
                    $"低于预估/最大时间 1/6（{FormatHelper.FormatSeconds((long)earlyExitThreshold)}）。";
                Log(message);
                MarkTaskError(message, name);
                abnormal = true;
            }

            UntrackProcess(process);
            _stats.AddRecord(name, elapsedDone, status, task.WaitMode);
            RaiseTaskCompleted(name, index, total, elapsedDone, status, abnormal);
            return RunOutcome.Continue;
        }

        while (true)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                Log($"任务「{name}」执行中收到停止请求，正在按统一清理顺序收尾当前任务。");

                // 常规停止只收尾当前任务；紧急停止按全部任务配置清理。
                var scope = _emergencyRequested
                    ? new List<TaskConfig>(_tasks)
                    : new List<TaskConfig> { task };

                CleanupProcessesForTasks(scope, new List<IMonitoredProcess> { process }, "停止执行", includeWindowKeywords: true);
                UntrackProcess(process);
                return RunOutcome.StopRequested;
            }

            var exitCode = process.Poll();
            var now = stopwatch.Elapsed.TotalSeconds;
            var monitorKind = monitorSpec.Kind;

            if (monitorKind is MonitorKinds.Window or MonitorKinds.Process or MonitorKinds.Cmdline)
            {
                var (present, matchedKeyword) = IsMonitorPresent(monitorSpec);
                var label = monitorSpec.Label;

                if (present)
                {
                    monitorAbsentSince = null;
                    if (!monitorSeen)
                    {
                        monitorSeen = true;
                        Log($"检测到{label}「{matchedKeyword}」，现在开始等待它消失。");

                        // 监控命中 = 目标真的起来了，此刻它的**真实镜像名**是可查的。
                        // 记下来，停止时按它清理和复查，不再依赖窗口标题（见 LearnMonitorImageNames）。
                        LearnMonitorImageNames(monitorSpec);
                    }
                }
                else if (monitorSeen)
                {
                    if (monitorAbsentSince is null)
                    {
                        monitorAbsentSince = now;
                        Log($"{label}暂时消失，开始 {(int)MonitorAbsentGraceSeconds} 秒确认，避免更新/闪退造成误判。");
                    }
                    else if (now - monitorAbsentSince.Value >= MonitorAbsentGraceSeconds)
                    {
                        return FinishWithLog($"{label}已连续消失 {(int)MonitorAbsentGraceSeconds} 秒");
                    }
                }
                else if (exitCode is not null)
                {
                    if (!monitorWaitLogged)
                    {
                        monitorWaitLogged = true;
                        Log($"直接启动进程已结束，仍在等待{label}出现：{string.Join(", ", monitorSpec.Keywords)}。");
                    }

                    if (now >= MonitorDetectDeadlineSeconds)
                    {
                        var keywords = string.Join(", ", monitorSpec.Keywords);
                        var message = $"直接进程已结束，180 秒内未检测到{label}「{keywords}」，按提醒处理：可能是配置不匹配";
                        Log(message);
                        MarkTaskWarning(message, name);
                        return FinishWithLog(message, checkEarlyExit: false);
                    }
                }
            }
            else if (exitCode is not null)
            {
                return FinishWithLog($"正常结束，退出码：{exitCode}");
            }

            var elapsed = (int)now;
            if (elapsed != lastEmitSecond)
            {
                lastEmitSecond = elapsed;
                RaiseElapsed(elapsed);
            }

            if (timeoutSeconds > 0 && elapsed >= timeoutSeconds)
            {
                RaiseStatus("超时处理中");
                var timeoutMessage =
                    $"任务「{name}」已超时，最大运行时间：{task.TimeoutMinutes} 分钟，" +
                    $"超时处理方式：{TimeoutActions.Label(task.TimeoutAction)}。";
                Log(timeoutMessage);

                var screenshotNames = new List<string>();
                if (_enableTimeoutScreenshot)
                {
                    var before = CaptureTimeoutScreenshot(name, "before_kill");
                    if (before is not null)
                    {
                        screenshotNames.Add(Path.GetFileName(before));
                    }
                }

                var timeoutOutcome = HandleTimeout(task, process, name);

                if (_enableTimeoutScreenshot)
                {
                    var after = CaptureTimeoutScreenshot(name, "after_kill");
                    if (after is not null)
                    {
                        screenshotNames.Add(Path.GetFileName(after));
                    }
                }

                if (screenshotNames.Count > 0)
                {
                    timeoutMessage = $"{timeoutMessage} 已保存现场截图：{string.Join(", ", screenshotNames)}";
                }

                MarkTaskError(timeoutMessage, name);
                _stats.AddRecord(name, elapsed, "超时处理", task.WaitMode);
                RaiseTaskCompleted(name, index, total, elapsed, $"超时处理：{TimeoutActions.Label(task.TimeoutAction)}", abnormal: true);
                return timeoutOutcome;
            }

            Thread.Sleep(250);
        }
    }

    private bool ConfirmLaunchSuccess(
        TaskConfig task,
        IMonitoredProcess process,
        string name,
        Stopwatch stopwatch,
        MonitorSpec monitorSpec)
    {
        while (stopwatch.Elapsed.TotalSeconds < LaunchConfirmSeconds)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                return false;
            }

            var exitCode = process.Poll();
            if (exitCode is not null)
            {
                var (present, matchedKeyword) = IsMonitorPresent(monitorSpec);
                if (present)
                {
                    Log($"任务「{name}」直接进程已退出，但已检测到{monitorSpec.Label}「{matchedKeyword}」，判定启动成功。");
                    return true;
                }

                // 直接进程秒退 ≠ 启动失败：提权重启（BetterGI 未以管理员运行时会 runas 重启自己并
                // 用退出码 553 退出）、单实例转发都会让原进程立刻退出、由新进程接手。
                // 给它一个短窗口确认是否真的被"接力"起来了。
                if (TryConfirmRelayLaunch(task, name, stopwatch, monitorSpec))
                {
                    return true;
                }

                var message = $"工作出现了问题！！！任务「{name}」运行出现问题或者提前退出了，退出码：{exitCode}。";
                Log(message);
                MarkTaskError(message, name);
                return false;
            }

            Thread.Sleep(200);
        }

        Log($"任务「{name}」启动确认通过：进程稳定运行超过 {(int)LaunchConfirmSeconds} 秒。");
        return true;
    }

    /// <summary>
    /// 接力判定：直接进程已退出、监控目标还没出现时，等一小会儿看真正的程序有没有被"接力"起来。
    ///
    /// 典型场景：BetterGI 检测到自己不是管理员就用 runas 提权重启，原进程带着退出码 553 秒退，
    /// 提权后的新实例才是我要等的那个（它起来后才会拉起原神窗口，监控目标短期内本就不该出现）。
    /// 单实例程序同理：新实例把参数转给已在运行的实例后就退出。
    ///
    /// 命中条件（任一）：① 监控目标出现；② 与脚本/任务登记同名的进程又在跑。
    /// 命中即判启动成功 —— 后续由主循环继续按监控目标等待完成。
    /// 纯"等待直接进程"模式没有接力可言（退出就是结束），直接返回 false。
    /// </summary>
    private bool TryConfirmRelayLaunch(
        TaskConfig task,
        string name,
        Stopwatch stopwatch,
        MonitorSpec monitorSpec)
    {
        if (monitorSpec.Kind == MonitorKinds.Direct)
        {
            return false;
        }

        var relayNames = ProcessNamesForTask(task, task.ScriptPath);
        if (relayNames.Count == 0)
        {
            var fallback = Path.GetFileNameWithoutExtension(task.ScriptPath.Trim());
            if (fallback.Length > 0)
            {
                relayNames.Add(fallback);
            }
        }

        while (stopwatch.Elapsed.TotalSeconds < LaunchRelayGraceSeconds)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                return false;
            }

            var (present, matchedKeyword) = IsMonitorPresent(monitorSpec);
            if (present)
            {
                Log($"任务「{name}」直接进程已退出，但已检测到{monitorSpec.Label}「{matchedKeyword}」，判定为提权/单实例接力成功。");
                return true;
            }

            foreach (var candidate in relayNames)
            {
                if (ProcessHelper.IsProcessNameRunning(candidate))
                {
                    Log($"任务「{name}」直接进程已退出，但检测到同名进程「{candidate}」在运行，判定为提权/单实例接力成功。");
                    return true;
                }
            }

            Thread.Sleep(300);
        }

        return false;
    }

    private RunOutcome HandleTimeout(TaskConfig task, IMonitoredProcess process, string name)
    {
        switch (task.TimeoutAction)
        {
            case TimeoutActions.SkipAndContinue:
                Log($"任务「{name}」超时：按照配置不结束当前脚本，直接继续下一个任务。注意：这可能导致多个脚本同时运行。");
                UntrackProcess(process);
                RaiseStatus("运行中");
                return RunOutcome.Continue;

            case TimeoutActions.StopAll:
                Log($"任务「{name}」超时：正在停止全部任务，并按统一清理顺序收尾当前任务。");
                CleanupProcessesForTasks(new List<TaskConfig> { task }, new List<IMonitoredProcess> { process }, "超时处理", includeWindowKeywords: true);
                UntrackProcess(process);
                return RunOutcome.StopAll;

            default:
                Log($"任务「{name}」超时：正在按启动脚本进程 → 目标进程 → 游戏窗口/扩展进程顺序强制收尾，然后继续下一个任务。");
                CleanupProcessesForTasks(new List<TaskConfig> { task }, new List<IMonitoredProcess> { process }, "超时处理", includeWindowKeywords: true);
                UntrackProcess(process);
                RaiseStatus("运行中");
                return RunOutcome.Continue;
        }
    }

    // ---------------- 进程清理 ----------------

    private void CleanupProcessesForTasks(
        List<TaskConfig> tasks,
        List<IMonitoredProcess>? directProcesses,
        string scopeLabel,
        bool includeWindowKeywords)
    {
        // ⚠️ 必须在任何结束动作**之前**学：父子关系一动手就变了（根一死只剩孤儿记录）
        LearnScriptDescendants(directProcesses);

        foreach (var process in directProcesses ?? new List<IMonitoredProcess>())
        {
            if (process is not null && process.Poll() is null)
            {
                Log($"{scopeLabel}：正在终止脚本进程 PID：{process.Pid}");
                process.Terminate();
            }
        }

        // 紧接着收脚本进程派生的后代 —— 它才是"真正干活的引擎"。
        // 留到后面收等于白给了它一个把游戏重新拉起来的时间窗（真机事故就是这么来的）。
        TerminateScriptDescendants(scopeLabel);

        // 第 0 层：本轮监控实际命中的进程。
        // 配置里写的往往是中文窗口标题（「原神」「星穹铁道」），而真实镜像名是英文
        // （YuanShen / StarRail）—— 按名字 taskkill 打不中中文，只能靠窗口标题那一路兜底；
        // 这里用监控时学到的真实镜像名直接杀，不依赖窗口是否还在。
        List<string> learned;
        lock (_sync)
        {
            learned = new List<string>(_learnedImageNames);
        }

        if (learned.Count > 0)
        {
            Log($"{scopeLabel}：正在清理本轮监控到的实际进程：{string.Join(", ", learned)}");
            foreach (var name in learned)
            {
                var result = ProcessHelper.TerminateProcessName(name);
                Log($"{scopeLabel}：结束进程名「{name}」：{result.Summary}");
                if (!result.Ok)
                {
                    Log($"{scopeLabel}：↳ {result.Hint}");
                }
            }

            Thread.Sleep(350);
        }

        var killedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var scriptPath = task.ScriptPath.Trim().Length > 0
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(task.ScriptPath.Trim()))
                : null;

            var taskName = task.DisplayName;

            foreach (var (stage, rawNames) in BuildCleanupPlan(task, scriptPath))
            {
                if (rawNames.Count == 0)
                {
                    continue;
                }

                var stageNames = new List<string>();
                foreach (var rawName in rawNames)
                {
                    foreach (var candidate in ProcessHelper.ProcessNameCandidates(rawName))
                    {
                        AddUnique(stageNames, candidate);
                    }
                }

                if (stageNames.Count == 0)
                {
                    continue;
                }

                Log($"{scopeLabel}：任务「{taskName}」正在清理{stage}：{string.Join(", ", stageNames)}");

                foreach (var rawName in rawNames)
                {
                    var key = rawName.Trim();
                    if (key.Length == 0 || !killedKeys.Add(key))
                    {
                        continue;
                    }

                    TerminateProcessKeyword(key);
                }

                Thread.Sleep(350);
            }

            if (includeWindowKeywords)
            {
                foreach (var keyword in KeywordHelper.Normalize(task.WindowKeywords))
                {
                    TerminateProcessesByWindowKeyword(keyword, scopeLabel, taskName);
                }
            }
        }

        ReportCleanupResult(tasks, includeWindowKeywords, scopeLabel);
    }

    /// <summary>
    /// 记下"脚本进程派生了哪些子进程"。
    ///
    /// 为什么必须学：像 March7th 这类工具，配置里指向的 `March7th Launcher.exe` **只是个壳**，
    /// 真正干活的是它派生的 `March7th Assistant.exe`。停止时若只按配置里写的名字清理，
    /// 子进程会完全漏网 —— 它随后会自己把游戏重新拉起来。2026-09-27 真机实测：
    /// 22:42:01 打完「✓ 都已结束」，22:42:57 游戏就被它重启，一直跑到 22:49 才收尾，
    /// 用户看到的就是"明明停止了，过一会又自己跑起来"。
    ///
    /// 调用时机：必须在**任何结束动作之前**。根进程一死，进程表里就只剩孤儿记录，
    /// 那时再想按 PPID 补课已经晚了 —— 这也是当初漏网的直接原因。
    ///
    /// 记 PID 而不是名字：脚本常会派生 <c>conhost.exe</c> 这类公共进程，
    /// 按名字清会误伤系统里别的同名实例，按 PID 才精准。
    /// </summary>
    private void LearnScriptDescendants(List<IMonitoredProcess>? directProcesses)
    {
        var rootPids = new List<int>();

        lock (_sync)
        {
            foreach (var pid in _tasksByPid.Keys)
            {
                if (!rootPids.Contains(pid))
                {
                    rootPids.Add(pid);
                }
            }
        }

        foreach (var process in directProcesses ?? new List<IMonitoredProcess>())
        {
            if (process is not null && process.Pid > 0 && !rootPids.Contains(process.Pid))
            {
                rootPids.Add(process.Pid);
            }
        }

        if (rootPids.Count == 0)
        {
            return;
        }

        var snapshot = ProcessHelper.SnapshotProcesses();
        if (snapshot.Count == 0)
        {
            Log("停止清理：读不到系统进程表，无法登记脚本进程的后代（只能退回按名字清理，可能漏掉子进程）。");
            return;
        }

        foreach (var pid in rootPids)
        {
            foreach (var child in ProcessHelper.DescendantsOf(pid, snapshot))
            {
                var known = false;
                lock (_sync)
                {
                    foreach (var item in _scriptDescendants)
                    {
                        if (item.Pid == child.Pid)
                        {
                            known = true;
                            break;
                        }
                    }

                    if (!known)
                    {
                        _scriptDescendants.Add(child);
                    }
                }

                if (known)
                {
                    continue;
                }

                Log($"已记下脚本进程（PID {pid}）派生的子进程：{child.Name}（PID {child.Pid}）—— 停止时按它清理与复查。");
            }
        }
    }

    /// <summary>
    /// 结束本轮登记过的脚本后代（按 PID，**先深后浅**）。
    ///
    /// 每个都必须给回执：这次事故里最难发现的一环正是"上一步报成功、子进程其实还在"，
    /// 所以这里不用任何"没抛异常就算过"的写法，结论一律来自进程表的复查。
    /// </summary>
    private void TerminateScriptDescendants(string scopeLabel)
    {
        List<ProcessSnapshotEntry> descendants;
        lock (_sync)
        {
            descendants = new List<ProcessSnapshotEntry>(_scriptDescendants);
        }

        if (descendants.Count == 0)
        {
            return;
        }

        // 倒着收 = 从最深的叶子往根：先断掉"会自己重启游戏"的引擎，壳留到最后。
        for (var i = descendants.Count - 1; i >= 0; i--)
        {
            var item = descendants[i];
            if (!ProcessHelper.IsSameProcessAlive(item.Pid, item.Name))
            {
                continue;
            }

            Log($"{scopeLabel}：正在清理脚本进程派生的子进程：{item.Name}（PID {item.Pid}）");
            var result = ProcessHelper.TerminateProcessTree(item.Pid);
            Log($"{scopeLabel}：结束「{item.Name}」（PID {item.Pid}）：{result.Summary}");

            if (result.Ok)
            {
                continue;
            }

            var closed = ProcessHelper.CloseMainWindowsByPid(item.Pid);
            if (closed > 0)
            {
                Log($"{scopeLabel}：强制结束被拒，已向「{item.Name}」的 {closed} 个窗口发送关闭请求（走它自己的退出流程）。");
            }
        }
    }

    /// <summary>
    /// 记下"本轮监控命中的那个窗口属于哪个进程"。
    ///
    /// 为什么必须学：任务配置里写的往往是中文窗口标题（「原神」「星穹铁道」），
    /// 而游戏真实镜像名是英文（<c>YuanShen</c> / <c>StarRail</c>）——
    /// 只用配置里的关键词，会同时踩两个坑：
    ///   ① 杀不掉：<c>taskkill /IM "原神"</c> 打不中英文镜像名；
    ///   ② 复查不出：按中文名字查进程永远是"不在"，明明还在跑也会报"已结束"。
    /// 监控命中的那一刻真实镜像名是可查的，记下来就能绕开窗口标题这条脆弱链路。
    /// </summary>
    private void LearnMonitorImageNames(MonitorSpec monitorSpec)
    {
        if (monitorSpec.Kind != MonitorKinds.Window)
        {
            // 进程 / 命令行模式下配置里写的就是真实镜像名，没什么可学的
            return;
        }

        foreach (var keyword in monitorSpec.Keywords)
        {
            foreach (var pid in ProcessHelper.WindowPidsByKeyword(keyword))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var imageName = process.ProcessName;
                    if (imageName.Length == 0)
                    {
                        continue;
                    }

                    lock (_sync)
                    {
                        if (!_learnedImageNames.Add(imageName))
                        {
                            continue;
                        }
                    }

                    Log($"已记下监控命中的真实进程名：{imageName}（PID {pid}）—— 停止时按它清理与复查，不再依赖窗口标题。");
                }
                catch
                {
                    // 进程已退出或拒绝访问，跳过
                }
            }
        }
    }

    /// <summary>
    /// 清理后复查：把"到底关掉没有"明确写进日志。
    ///
    /// 为什么必要：<c>taskkill</c> 对被内核级反作弊保护的游戏会失败，而失败与成功在旧日志里
    /// 长得一模一样（都只有"正在结束"）。用户只能对着还在跑的游戏猜是没杀掉还是画面没刷新。
    /// 这里用同一套候选名 + 监控学到的真实镜像名 + 窗口关键词再查一遍：
    /// 干净就明确确认，有残留就点名并给出下一步。
    /// </summary>
    private void ReportCleanupResult(List<TaskConfig> tasks, bool includeWindowKeywords, string scopeLabel)
    {
        var leftovers = new List<string>();

        List<string> learned;
        lock (_sync)
        {
            learned = new List<string>(_learnedImageNames);
        }

        foreach (var name in learned)
        {
            if (ProcessHelper.IsProcessNameRunning(name))
            {
                AddUnique(leftovers, name + "（监控命中的实际进程）");
            }
        }

        // 脚本进程派生的后代：按 PID 复查（按名字查会撞上同名的公共进程，如 conhost.exe）
        List<ProcessSnapshotEntry> descendants;
        lock (_sync)
        {
            descendants = new List<ProcessSnapshotEntry>(_scriptDescendants);
        }

        var descendantLeftovers = new List<string>();
        foreach (var item in descendants)
        {
            if (!ProcessHelper.IsSameProcessAlive(item.Pid, item.Name))
            {
                continue;
            }

            AddUnique(descendantLeftovers, item.ToString());
            AddUnique(leftovers, $"{item}（脚本进程派生的子进程）");
        }

        foreach (var task in tasks)
        {
            var scriptPath = task.ScriptPath.Trim().Length > 0
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(task.ScriptPath.Trim()))
                : null;

            foreach (var (stage, rawNames) in BuildCleanupPlan(task, scriptPath))
            {
                foreach (var rawName in rawNames)
                {
                    foreach (var candidate in ProcessHelper.ProcessNameCandidates(rawName))
                    {
                        if (ProcessHelper.IsProcessNameRunning(candidate))
                        {
                            AddUnique(leftovers, $"{candidate}（{stage}）");
                        }
                    }
                }
            }

            if (!includeWindowKeywords)
            {
                continue;
            }

            foreach (var keyword in KeywordHelper.Normalize(task.WindowKeywords))
            {
                var pids = ProcessHelper.WindowPidsByKeyword(keyword);
                if (pids.Count == 0)
                {
                    var stripped = ProcessHelper.TitleMatchKey(keyword);
                    if (stripped.Length > 0
                        && !string.Equals(stripped, keyword.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        pids = ProcessHelper.WindowPidsByKeyword(stripped);
                    }
                }

                if (pids.Count > 0)
                {
                    AddUnique(leftovers, $"窗口「{keyword}」仍在（PID {string.Join("/", pids)}）");
                }
            }
        }

        if (leftovers.Count == 0)
        {
            Log($"{scopeLabel}：✓ 已复查确认，任务相关的进程都已结束。");
            return;
        }

        Log($"{scopeLabel}：⚠ 复查发现 {leftovers.Count} 项仍在运行：{string.Join("、", leftovers)}");
        Log($"{scopeLabel}：⚠ 它们多半带反作弊 / 驱动级保护，普通权限结束不了。"
            + "改成在目标会话里以管理员身份运行代理、或手动结束；"
            + "若日志里一直没有「按目标进程关键词兜底结束」这类记录，把真实镜像名（如 YuanShen、StarRail）补进任务的「进程关键词」。");

        // 单独点名"漏网的是脚本自己的子进程"：这是最容易被误判成"已经停止"的一种，
        // 而且它会自己把游戏重新拉回来（2026-09-27 真机事故）。
        if (descendantLeftovers.Count > 0)
        {
            Log($"{scopeLabel}：⚠ 其中「启动脚本派生的子进程」没结束掉：{string.Join("、", descendantLeftovers)}"
                + " —— 它是真正干活的引擎，很可能会自己把游戏重新拉起来，建议在目标会话里手动结束它再确认。");
        }
    }

    private void TerminateProcessesByWindowKeyword(string keyword, string scopeLabel, string taskName)
    {
        var text = keyword.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 无条件先记一条 —— 这一层跑没跑到必须能一眼看出来。
        // （真机上出现过"配置里有窗口关键词、日志里却一条相关记录都没有"的情况，
        //   当时无法判断是没执行、还是执行了但两条分支都没命中。）
        Log($"{scopeLabel}：任务「{taskName}」按游戏窗口关键词「{text}」查找残留进程…");

        try
        {
            // 窗口标题里不会出现 .exe：关键词写成「原神.exe」时按原样比标题必然落空，
            // 而这条路正是清掉游戏本体（YuanShen / StarRail）的主要手段。剥掉扩展名再比。
            var pids = ProcessHelper.WindowPidsByKeyword(text);
            if (pids.Count == 0)
            {
                var stripped = ProcessHelper.TitleMatchKey(text);
                if (stripped.Length > 0 && !string.Equals(stripped, text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    pids = ProcessHelper.WindowPidsByKeyword(stripped);
                }
            }

            if (pids.Count == 0)
            {
                Log($"{scopeLabel}：任务「{taskName}」未发现游戏窗口关键词「{text}」对应的残留窗口。");
                return;
            }

            Log($"{scopeLabel}：任务「{taskName}」发现游戏窗口关键词「{text}」对应 PID：{string.Join(", ", pids)}");
            foreach (var pid in pids)
            {
                KillWithFallback(pid, $"窗口「{text}」");
            }
        }
        catch (Exception ex)
        {
            Log($"{scopeLabel}：按游戏窗口关键词「{text}」清理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 结束一个进程树并把结果写进日志；强杀被拒时退一步"请它自己退出"。
    ///
    /// 为什么要降级：原神 / 星穹铁道这类游戏带内核级反作弊驱动，普通权限的
    /// <c>taskkill /F</c> 会直接被拒 —— 而失败是**静默**的，旧日志里只留下"正在结束"，
    /// 用户看着还在跑的游戏根本判断不出到底关了没有（真机踩过）。
    /// 强杀被拒后给窗口发 WM_CLOSE：那是应用层消息，反作弊不拦，走游戏自己的退出流程
    /// （有些游戏会弹一个确认退出框，需要在目标会话里点一下）。
    /// </summary>
    private void KillWithFallback(int pid, string label)
    {
        var result = ProcessHelper.TerminateProcessTree(pid);
        Log($"  ↳ {label}：{result.Summary}");

        if (result.Ok)
        {
            return;
        }

        var closed = ProcessHelper.CloseMainWindowsByPid(pid);
        if (closed > 0)
        {
            Log($"  ↳ 强制结束被拒，已向 {label} 的 {closed} 个窗口发送关闭请求（走游戏自己的退出流程，可能需要在目标会话里确认）。");
            return;
        }

        Log($"  ↳ 强制结束被拒，且没有可发送关闭请求的窗口。{result.Hint}");
    }

    private void TerminateProcessKeyword(string keyword)
    {
        var text = keyword.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ProcessHelper.ProcessNameCandidates(text))
        {
            if (tried.Add(candidate))
            {
                var byName = ProcessHelper.TerminateProcessName(candidate);
                Log($"结束进程名「{candidate}」：{byName.Summary}");
            }
        }

        try
        {
            // 关键词要剥掉扩展名才能匹配标题与镜像名（理由见 ProcessHelper.TitleMatchKey）。
            // 这一路兜底是把游戏本体（YuanShen / StarRail）清掉的**唯一**手段 ——
            // 按名字 taskkill 用的是中文关键词，注定打不中英文镜像名。
            var lower = ProcessHelper.TitleMatchKey(text);
            if (lower.Length == 0)
            {
                return;
            }

            var currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == currentPid)
                    {
                        continue;
                    }

                    var procName = proc.ProcessName;
                    var title = proc.MainWindowTitle;
                    if (procName.ToLowerInvariant().Contains(lower)
                        || (!string.IsNullOrEmpty(title) && title.ToLowerInvariant().Contains(lower)))
                    {
                        Log($"按目标进程关键词兜底结束：{text} -> PID {proc.Id} ({procName})");
                        KillWithFallback(proc.Id, procName);
                    }
                }
                catch
                {
                    // 进程已退出或拒绝访问
                }
            }
        }
        catch (Exception ex)
        {
            Log($"按目标进程关键词「{text}」兜底清理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 统一清理顺序：
    /// 1. 启动进程（脚本 exe + 任务名 + launcher_process）
    /// 2. 目标进程（wait_process_name + main_process + process_keywords）
    /// 3. 游戏/扩展进程（game_process + window_keywords 中的 .exe）
    ///
    /// 清理与复查**共用这一份计划** —— 只清不查会漏，只查不清会误报，
    /// 两边用的名字必须来自同一个函数（2026-09-27 假 ✓ 事故的教训）。
    /// </summary>
    private static List<(string Stage, List<string> Names)> BuildCleanupPlan(TaskConfig task, string? scriptPath)
    {
        var launcherNames = new List<string>();
        var mainNames = new List<string>();
        var gameNames = new List<string>();

        if (scriptPath is not null && Path.GetExtension(scriptPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            AddUnique(launcherNames, Path.GetFileName(scriptPath));
        }

        // 任务名本身也纳入「启动脚本进程」。
        // 为什么必须加：漏网事故里活下来、并把游戏重新拉起来的那个进程
        // （`March7th Assistant.exe`），名字与**任务名**一致，却既不是 script_path 的文件名、
        // 也不在 wait_process_name 里 —— 于是清理和复查两边都看不见它，复查才会打出假的 ✓。
        if (ProcessHelper.LooksLikeImageName(task.Name))
        {
            AddUnique(launcherNames, task.Name.Trim());
        }

        AddUnique(launcherNames, task.LauncherProcess);

        AddUnique(mainNames, task.WaitProcessName);
        AddUnique(mainNames, task.MainProcess);
        foreach (var item in KeywordHelper.Normalize(task.ProcessKeywords))
        {
            AddUnique(mainNames, item);
        }

        AddUnique(gameNames, task.GameProcess);
        foreach (var item in KeywordHelper.Normalize(task.WindowKeywords))
        {
            if (item.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(gameNames, item);
            }
        }

        var plan = new List<(string, List<string>)>();
        if (launcherNames.Count > 0)
        {
            plan.Add(("启动脚本进程", launcherNames));
        }

        if (mainNames.Count > 0)
        {
            plan.Add(("目标进程", mainNames));
        }

        if (gameNames.Count > 0)
        {
            plan.Add(("游戏/扩展进程", gameNames));
        }

        return plan;
    }

    private static List<string> ProcessNamesForTask(TaskConfig task, string? scriptPath)
    {
        var names = new List<string>();
        foreach (var (_, items) in BuildCleanupPlan(task, scriptPath))
        {
            foreach (var item in items)
            {
                foreach (var candidate in ProcessHelper.ProcessNameCandidates(item))
                {
                    AddUnique(names, candidate);
                }
            }
        }

        return names;
    }

    // ---------------- 监控目标 ----------------

    public static MonitorSpec BuildMonitorSpec(TaskConfig task)
    {
        var windowKeywords = KeywordHelper.Normalize(task.WindowKeywords);
        var processKeywords = KeywordHelper.Normalize(task.ProcessKeywords);
        var legacyKeyword = (task.WaitProcessName ?? string.Empty).Trim();

        if (windowKeywords.Count > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Window,
                Label = "游戏窗口关键词",
                Keywords = windowKeywords,
                Source = "window_keywords",
            };
        }

        if (processKeywords.Count > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Process,
                Label = "目标进程关键词",
                Keywords = processKeywords,
                Source = "process_keywords",
            };
        }

        if (task.WaitMode == WaitModes.WindowTitle && legacyKeyword.Length > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Window,
                Label = "窗口标题关键词",
                Keywords = new List<string> { legacyKeyword },
                Source = "legacy_wait_mode",
            };
        }

        if (task.WaitMode == WaitModes.CmdlineKeyword && legacyKeyword.Length > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Cmdline,
                Label = "命令行关键词",
                Keywords = new List<string> { legacyKeyword },
                Source = "legacy_wait_mode",
            };
        }

        if (task.WaitMode == WaitModes.ProcessName && legacyKeyword.Length > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Process,
                Label = "目标进程关键词",
                Keywords = new List<string> { legacyKeyword },
                Source = "legacy_wait_mode",
            };
        }

        return new MonitorSpec
        {
            Kind = MonitorKinds.Direct,
            Label = "直接启动进程",
            Keywords = new List<string>(),
            Source = "direct_process",
        };
    }

    private static (bool Present, string Keyword) IsMonitorPresent(MonitorSpec spec)
    {
        foreach (var keyword in spec.Keywords)
        {
            var text = keyword.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var hit = spec.Kind switch
            {
                MonitorKinds.Window => ProcessHelper.IsWindowTitlePresent(text),
                MonitorKinds.Process => ProcessHelper.IsProcessKeywordPresent(text),
                MonitorKinds.Cmdline => ProcessHelper.IsCommandlineKeywordRunning(text),
                _ => false,
            };

            if (hit)
            {
                return (true, text);
            }
        }

        return (false, string.Empty);
    }

    // ---------------- 辅助 ----------------

    private void WaitIfPaused()
    {
        while (_pauseRequested && !_stopRequested)
        {
            Thread.Sleep(250);
        }
    }

    private void TrackProcess(IMonitoredProcess process, List<string> processNames, TaskConfig task)
    {
        lock (_sync)
        {
            _currentProcesses.Add(process);
            if (processNames.Count > 0)
            {
                _processNamesByPid[process.Pid] = new List<string>(processNames);
            }

            _tasksByPid[process.Pid] = task;
        }
    }

    private void UntrackProcess(IMonitoredProcess process)
    {
        lock (_sync)
        {
            _currentProcesses.Remove(process);
            _processNamesByPid.Remove(process.Pid);
            _tasksByPid.Remove(process.Pid);
        }
    }

    /// <summary>
    /// 停止时的统一收尾兜底 —— 保证"停止请求到达的那一刻，无论在哪个阶段，目标进程都会被清掉"。
    ///
    /// 为什么必须有它：把整轮关键词清理从 <see cref="RequestStop"/> 里挪走之后（那里同步跑十几秒
    /// 会把工作线程堵死，进度与心跳全停），清理就只剩 RunOneTask 主循环里那一条路径。
    /// 而「启动确认 / 接力确认阶段被停止打断」走的是另一条分支 —— 那里只放弃等待、不做清理，
    /// 于是目标进程（包括提权 / 单实例接力起来的新实例）会活下来。
    ///
    /// 范围按**已登记过的任务**定（<c>_tasksByPid</c>）：只覆盖真的启动过的那批，
    /// 免得顺手把用户另外开着、只是关键词撞上的程序也杀掉。紧急停止才扩大到全部任务配置。
    ///
    /// 幂等：正常路径上 RunOneTask 已经清过、并把进程解除登记了，这里会拿到空集合直接返回，
    /// 不会白跑一遍十几秒的清理。
    /// </summary>
    private void CleanupForStop()
    {
        List<IMonitoredProcess> processes;
        List<TaskConfig> tasks;
        lock (_sync)
        {
            processes = new List<IMonitoredProcess>(_currentProcesses);
            tasks = _emergencyRequested
                ? new List<TaskConfig>(_tasks)
                : DistinctTasks(_tasksByPid.Values);
        }

        if (processes.Count == 0 && tasks.Count == 0)
        {
            return;
        }

        var label = _emergencyRequested ? "紧急停止" : "停止执行";
        Log($"{label}：统一收尾 —— 复查 {tasks.Count} 个任务配置、{processes.Count} 个登记进程。");
        CleanupProcessesForTasks(tasks, processes, label, includeWindowKeywords: true);

        if (_emergencyRequested)
        {
            Log("紧急停止处理已执行。");
        }
    }

    private static List<TaskConfig> DistinctTasks(IEnumerable<TaskConfig> source)
    {
        var result = new List<TaskConfig>();
        var seen = new HashSet<TaskConfig>();
        foreach (var task in source)
        {
            if (seen.Add(task))
            {
                result.Add(task);
            }
        }

        return result;
    }

    private void SchedulePressEnter(int delaySeconds, string taskName)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(Math.Max(0, delaySeconds) * 1000);
            if (_stopRequested)
            {
                return;
            }

            try
            {
                ProcessHelper.PressEnter();
                Log($"任务「{taskName}」已按配置发送一次 Enter 确认键。");
            }
            catch (Exception ex)
            {
                Log($"任务「{taskName}」发送 Enter 失败：{ex.Message}");
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
    }

    private string? CaptureTimeoutScreenshot(string taskName, string stage)
    {
        var directory = Path.Combine(_logger.LogDir, "screenshots");
        var safeName = Regex.Replace(taskName, @"[^0-9A-Za-z_\-一-鿿]+", "_").Trim('_');
        if (safeName.Length == 0)
        {
            safeName = "task";
        }

        var safeStage = Regex.Replace(stage, @"[^0-9A-Za-z_\-]+", "_").Trim('_');
        var fileName =
            $"{_logger.SessionLabel}_{DateTime.Now:HHmmss}_{safeName}_timeout_{safeStage}.png";

        var path = ScreenshotService.CaptureFullScreen(directory, fileName);
        if (path is null)
        {
            Log("超时截图失败：未能完成全屏抓图。");
            return null;
        }

        Log($"超时现场截图已保存：{path}");
        return path;
    }

    private void MarkTaskError(string message, string taskName = "")
    {
        RecordTaskIssue(message, taskName, severity: "error", notifyError: true);
    }

    private void MarkTaskWarning(string message, string taskName = "")
    {
        RecordTaskIssue(message, taskName, severity: "warning", notifyError: false);
    }

    private void RecordTaskIssue(string message, string taskName, string severity, bool notifyError)
    {
        if (severity == "error")
        {
            HadTaskError = true;
            TaskErrorCount++;
        }

        _abnormalEvents.Add(new AbnormalEvent
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TaskName = taskName.Length > 0 ? taskName : GuessTaskNameFromMessage(message),
            Message = message,
            Severity = severity,
            LogFile = Path.GetFileName(_logger.LogPath),
            LogSession = _logger.SessionLabel,
        });

        if (notifyError)
        {
            TaskError?.Invoke(this, message);
        }
    }

    private static string GuessTaskNameFromMessage(string message)
    {
        const string marker = "任务「";
        var index = message.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return string.Empty;
        }

        var rest = message.Substring(index + marker.Length);
        var end = rest.IndexOf('」');
        return end < 0 ? string.Empty : rest.Substring(0, end);
    }

    private void SaveAbnormalReport()
    {
        try
        {
            var directory = Path.GetDirectoryName(AbnormalReportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (_abnormalEvents.Count == 0)
            {
                if (File.Exists(AbnormalReportPath))
                {
                    File.Delete(AbnormalReportPath);
                }

                return;
            }

            var errorCount = 0;
            var warningCount = 0;
            foreach (var item in _abnormalEvents)
            {
                if (item.Severity == "warning")
                {
                    warningCount++;
                }
                else
                {
                    errorCount++;
                }
            }

            var report = new AbnormalReport
            {
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                LogFile = Path.GetFileName(_logger.LogPath),
                LogSession = _logger.SessionLabel,
                Summary = $"上次运行发现 {errorCount} 个异常项，{warningCount} 个提醒项。",
                Events = new List<AbnormalEvent>(_abnormalEvents),
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            File.WriteAllText(AbnormalReportPath, JsonSerializer.Serialize(report, options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log($"保存异常报告失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 保存本次运行的耗时统计。
    ///
    /// 统计只是"锦上添花"：写失败（多账户共用副本时目录权限不够、文件名撞车都可能）
    /// 绝不该把整轮任务判成异常 —— 所以这里吞掉异常，只留一条日志。
    /// </summary>
    private void SaveStatsQuietly(string label)
    {
        try
        {
            var paths = _stats.SaveSession();
            if (paths.Count > 0)
            {
                Log($"{label}耗时统计已保存：{paths[0]}");
            }
        }
        catch (Exception ex)
        {
            Log($"{label}耗时统计保存失败（不影响任务结果）：{ex.Message}");
        }
    }

    private void Log(string message)
    {
        var line = _logger.Log(message);
        Logged?.Invoke(this, line);
    }

    private void RaiseStatus(string status) => StatusChanged?.Invoke(this, status);

    private void RaiseProgress(int current, int total) => ProgressChanged?.Invoke(this, (current, total));

    private void RaiseElapsed(int seconds) => ElapsedChanged?.Invoke(this, seconds);

    private void RaiseTaskStarted(string name, int index, int total) => TaskStarted?.Invoke(this, (name, index, total));

    // 注意：并发组下本回调会在工作线程上触发，订阅方需自行切回 UI 线程。
    private void RaiseTaskCompleted(string name, int index, int total, int elapsedSeconds, string status, bool abnormal) =>
        TaskCompleted?.Invoke(this, new TaskCompletedInfo
        {
            Name = name,
            Index = index,
            Total = total,
            ElapsedSeconds = elapsedSeconds,
            Status = status,
            IsAbnormal = abnormal,
            FinishedAt = DateTimeOffset.Now,
        });

    private void RaiseFinished(bool stoppedOrError) => Finished?.Invoke(this, stoppedOrError);

    private static void AddUnique(List<string> target, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        foreach (var item in target)
        {
            if (string.Equals(item, text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        target.Add(text);
    }

    private enum RunOutcome
    {
        Continue,
        StopRequested,
        StopAll,
    }
}
