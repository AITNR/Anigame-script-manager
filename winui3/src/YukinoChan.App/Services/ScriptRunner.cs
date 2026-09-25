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
    private readonly List<AbnormalEvent> _abnormalEvents = new();

    private volatile bool _stopRequested;
    private volatile bool _pauseRequested;

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

    /// <summary>停止执行：只收尾当前正在运行的任务。</summary>
    public void RequestStop()
    {
        _stopRequested = true;
        _pauseRequested = false;
        Log("收到用户停止执行请求：正在按当前任务的启动脚本进程 → 目标进程 → 游戏窗口/扩展进程顺序收尾。");

        List<IMonitoredProcess> processes;
        List<TaskConfig> tasks;
        lock (_sync)
        {
            processes = new List<IMonitoredProcess>(_currentProcesses);
            tasks = DistinctTasks(_tasksByPid.Values);
        }

        CleanupProcessesForTasks(tasks, processes, "停止执行", includeWindowKeywords: true);
    }

    /// <summary>紧急停止：停止队列，并按所有任务配置执行全局三层清理。</summary>
    public void RequestEmergencyStop()
    {
        _stopRequested = true;
        _pauseRequested = false;
        Log("收到紧急停止请求：正在扫描所有任务配置，并按启动脚本进程 → 目标进程 → 游戏窗口/扩展进程顺序强制收尾。");

        List<IMonitoredProcess> processes;
        lock (_sync)
        {
            processes = new List<IMonitoredProcess>(_currentProcesses);
        }

        CleanupProcessesForTasks(new List<TaskConfig>(_tasks), processes, "紧急停止", includeWindowKeywords: true);
        Log("紧急停止处理已执行。");
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

            lock (_sync)
            {
                _currentProcesses.Clear();
            }

            if (stopped)
            {
                RaiseStatus("已停止");
                Log("任务队列已停止。");
                SaveAbnormalReport();
                RaiseFinished(true);
                return;
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

            var paths = _stats.SaveSession();
            if (paths.Count > 0)
            {
                Log($"本次运行耗时统计已保存：{paths[0]}");
            }

            RaiseFinished(HadTaskError);
        }
        catch (Exception ex)
        {
            RaiseStatus("异常");
            Log($"执行器发生异常：{ex.Message}");
            MarkTaskError($"执行器发生异常：{ex.Message}");
            SaveAbnormalReport();
            var paths = _stats.SaveSession();
            if (paths.Count > 0)
            {
                Log($"异常前耗时统计已保存：{paths[0]}");
            }

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
                CleanupProcessesForTasks(new List<TaskConfig> { task }, new List<IMonitoredProcess> { process }, "停止执行", includeWindowKeywords: true);
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
        foreach (var process in directProcesses ?? new List<IMonitoredProcess>())
        {
            if (process is not null && process.Poll() is null)
            {
                Log($"正在终止脚本进程 PID：{process.Pid}");
                process.Terminate();
            }
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
    }

    private void TerminateProcessesByWindowKeyword(string keyword, string scopeLabel, string taskName)
    {
        var text = keyword.Trim();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var pids = ProcessHelper.WindowPidsByKeyword(text);
            if (pids.Count == 0)
            {
                Log($"{scopeLabel}：任务「{taskName}」未发现游戏窗口关键词「{text}」对应的残留窗口。");
                return;
            }

            Log($"{scopeLabel}：任务「{taskName}」发现游戏窗口关键词「{text}」对应 PID：{string.Join(", ", pids)}");
            foreach (var pid in pids)
            {
                ProcessHelper.TerminatePidTree(pid);
            }
        }
        catch (Exception ex)
        {
            Log($"{scopeLabel}：按游戏窗口关键词「{text}」清理失败：{ex.Message}");
        }
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
                ProcessHelper.TerminateProcessName(candidate);
                Log($"已尝试结束进程名：{candidate}");
            }
        }

        try
        {
            var lower = text.ToLowerInvariant();
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
                        ProcessHelper.TerminatePidTree(proc.Id);
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
    /// 1. 启动进程（脚本 exe + launcher_process）
    /// 2. 目标进程（wait_process_name + main_process + process_keywords）
    /// 3. 游戏/扩展进程（game_process + window_keywords 中的 .exe）
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
