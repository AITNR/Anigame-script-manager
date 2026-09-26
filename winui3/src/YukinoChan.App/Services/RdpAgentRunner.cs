// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YukinoChan.Helpers;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// RDP 目标会话里的执行代理。
///
/// 为什么需要它：两个 Windows 用户会话之间无法直接通信，主控端也不能跨会话启动进程。
/// 因此目标账户登录后，由共享启动目录里的快捷方式以 --rdp-agent 拉起雪乃酱，
/// 读取主控端下发的 command.json 执行任务队列，再把进度与事件写回 status.json，
/// 主控端轮询 status.json 即可看到实时进度并弹通知。
///
/// Agent 全程隐藏窗口运行，任务结束后按指令处理会话（保持 / 断开 / 注销）。
/// </summary>
public sealed class RdpAgentRunner
{
    /// <summary>Agent 模式的命令行开关（实现在 <see cref="RdpTargets"/>，便于脱离 WinUI 单独测试）。</summary>
    public const string AgentArgument = RdpTargets.AgentArgument;

    /// <summary>
    /// 运行期回传"脉搏"事件的间隔（秒）。
    /// 主控端靠它确认任务确实在推进；太密会挤掉真正重要的事件，太疏又看不出动静。
    /// </summary>
    private const int PulseIntervalSeconds = 60;

    /// <summary>指定指令桥目录的命令行前缀。</summary>
    public const string BridgeArgumentPrefix = RdpTargets.BridgeArgumentPrefix;

    private readonly RdpCommand _command;
    private readonly FileLogger _logger;
    private readonly RuntimeStatsManager _stats;

    /// <summary>本代理读写哪座桥 —— Agent 启动时由 --bridge:（或按账户名派生）确定，见 App.OnLaunched。</summary>
    private readonly RdpBridge _bridge;

    /// <summary>停止请求只处理一次（击键连点 / 多拍重复读到同一个请求时不要反复下发）。</summary>
    private bool _stopHandled;

    public RdpAgentRunner(RdpCommand command)
    {
        _command = command;
        _logger = new FileLogger(AppPaths.LogDir);
        _stats = new RuntimeStatsManager(AppPaths.StatsDir);
        _bridge = RdpBridge.Agent;
    }

    /// <summary>命令行里是否带了 --rdp-agent。</summary>
    public static bool IsAgentInvocation(IReadOnlyList<string> args) => RdpTargets.IsAgentInvocation(args);

    /// <summary>从命令行里取出 --bridge: 后面的桥目录（没有则为 null）。</summary>
    public static string? ExtractBridgePath(IReadOnlyList<string> args) => RdpTargets.ExtractBridgePath(args);

    /// <summary>生成 Agent 快捷方式的命令行参数（带桥目录时一并带上）。</summary>
    public static string BuildAgentArguments(string? bridgePath) => RdpTargets.BuildAgentArguments(bridgePath);

    public event EventHandler<string>? Logged;

    public async Task RunAsync(CancellationToken token)
    {
        Log($"RDP 会话代理已启动：目标账户 {Environment.UserName}，指令 {_command.Id}。");

        var tasks = new List<TaskConfig>();
        for (var i = 0; i < _command.Tasks.Count; i++)
        {
            var task = (TaskConfig)_command.Tasks[i].Clone();
            task.Sanitize(i + 1);
            tasks.Add(task);
        }

        var enabled = tasks.Where(t => t.Enabled).ToList();
        Update(status =>
        {
            status.CommandId = _command.Id;
            status.AgentUser = Environment.UserName;
            status.Phase = "running";
            status.Total = enabled.Count;
            status.Progress = 0;
            status.StatusText = "已接管指令，准备执行";
            return status;
        });

        if (enabled.Count == 0)
        {
            var message = "指令里没有启用的任务，代理结束。";
            Log(message);
            Append(RdpEventKinds.Finished, message);
            FinishSession("done", "没有可执行的任务");
            return;
        }

        var runner = new ScriptRunner(
            enabled,
            _command.ShutdownAfterDone,
            _command.ShutdownDelaySeconds,
            _logger,
            _stats,
            _command.EnableTimeoutScreenshot);

        // 上次回传脉搏时的秒数（任务切换时会归零）
        var lastPulseSecond = 0;

        runner.Logged += (_, line) =>
        {
            Log(line);
            // 只有关键节点进事件流，避免把上千行日志塞进 status.json
            if (line.Contains("已超时") || line.Contains("启动失败") || line.Contains("异常"))
            {
                Append(RdpEventKinds.Log, line);
            }
        };

        runner.StatusChanged += (_, text) => Update(status =>
        {
            status.StatusText = text;
            return status;
        });

        runner.ProgressChanged += (_, info) => Update(status =>
        {
            status.Progress = info.Current;
            status.Total = info.Total;
            return status;
        });

        runner.ElapsedChanged += (_, seconds) =>
        {
            // 每秒一拍：顺手看一眼主控端有没有请求停止（桥文件是两个会话之间唯一的通信手段）
            CheckStopRequest(runner);

            Update(status =>
            {
                // 任务切换时 ScriptRunner 会把计时归零（RaiseElapsed(0)），脉搏也要跟着重置，
                // 否则新任务的脉搏要等很久才会出现。
                if (seconds < lastPulseSecond)
                {
                    lastPulseSecond = 0;
                }

                status.ElapsedSeconds = seconds;

                // 【脉搏】任务运行期间每 60 秒回一条事件。
                // 没有它的话，主控端在任务执行期间只能看到一个静止的"运行中" ——
                // ScriptRunner 在任务跑的过程中只有日志、没有结构性事件，
                // 事件列表会一直空着，用户根本判断不出任务是还在跑还是已经卡死。
                if (seconds - lastPulseSecond >= PulseIntervalSeconds)
                {
                    lastPulseSecond = seconds;
                    var name = string.IsNullOrEmpty(status.CurrentTask) ? "（未知任务）" : status.CurrentTask;
                    status.Events.Add(RdpBridge.MakeEvent(
                        status,
                        RdpEventKinds.Log,
                        $"仍在运行，已耗时 {FormatHelper.FormatSeconds(seconds)}",
                        name,
                        status.Progress,
                        status.Total,
                        seconds));
                }

                return status;
            });
        };

        runner.TaskStarted += (_, info) => Update(status =>
        {
            status.CurrentTask = info.Name;
            status.Progress = info.Index;
            status.Total = info.Total;
            status.ElapsedSeconds = 0;
            return status;
        });

        runner.TaskCompleted += (_, info) =>
        {
            // 每个任务完成都回传一条事件，并在本会话弹一次系统通知
            Update(status =>
            {
                status.Events.Add(RdpBridge.MakeEvent(
                    status,
                    RdpEventKinds.TaskDone,
                    $"{info.Status}（{FormatHelper.FormatSeconds(info.ElapsedSeconds)}）",
                    info.Name,
                    info.Index,
                    info.Total,
                    info.ElapsedSeconds,
                    info.IsAbnormal));
                return status;
            });

            if (_command.NotifyOnTaskDone)
            {
                if (!NotificationService.ShowTaskDone(
                        info.Name, info.Index, info.Total, info.ElapsedSeconds, info.IsAbnormal))
                {
                    Log($"任务「{info.Name}」的系统通知投递失败，已降级为仅记录日志（{NotificationService.LastError}）。");
                }
            }
        };

        runner.ShutdownPrompt += (_, delay) =>
        {
            // Agent 没有交互界面，直接按配置排定关机（仍带倒计时，切过去可取消）
            Log($"已启用自动关机，代理将在 {delay} 秒后关机（可在目标会话执行 shutdown /a 取消）。");
            ShutdownService.Schedule(delay);
        };

        var hadError = false;
        runner.Finished += (_, abnormal) => hadError = abnormal;

        try
        {
            await runner.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = $"代理执行异常：{ex.Message}";
            Log(message);
            Append(RdpEventKinds.Finished, message, abnormal: true);
            FinishSession("error", message);
            return;
        }

        var summary = hadError ? "已完成（有异常）" : "全部任务已完成";
        // 异常与否写进事件：常驻代理会把 Phase 从 done 迅速改成 idle，主控端来不及看相位，
        // 只能靠这条 Finished 事件的 abnormal 标记来区分"正常结束"与"有异常结束"。
        Append(RdpEventKinds.Finished, summary, abnormal: hadError);

        if (_command.NotifyOnAllDone)
        {
            NotificationService.ShowSummary("雪乃酱：远程任务结束", summary);
        }

        FinishSession(hadError ? "done" : "done", summary);
    }

    private void FinishSession(string phase, string summary)
    {
        Update(status =>
        {
            status.Phase = phase;
            status.StatusText = summary;
            status.CurrentTask = string.Empty;
            return status;
        });

        var mode = SessionFinishModes.Normalize(_command.SessionFinish);
        var session = RdpSessionService.FindSession(Environment.UserName);

        switch (mode)
        {
            case SessionFinishModes.Disconnect:
                if (session is not null)
                {
                    RdpSessionService.DisconnectSession(session.Id, out var message);
                    Log($"会话收尾：{message}");
                }
                else
                {
                    Log("会话收尾：找不到当前会话，跳过断开。");
                }

                break;

            case SessionFinishModes.Logoff:
                if (session is not null)
                {
                    RdpSessionService.LogoffSession(session.Id, out var message);
                    Log($"会话收尾：{message}");
                }
                else
                {
                    Log("会话收尾：找不到当前会话，跳过注销。");
                }

                break;

            default:
                Log("会话收尾：保持连接，方便随时切回来查看结果。");
                break;
        }
    }

    // ---------------- 状态与日志 ----------------

    private void Update(Func<RdpStatus, RdpStatus> mutator)
    {
        _bridge.UpdateStatus(status =>
        {
            status.CommandId = _command.Id;
            status.AgentUser = Environment.UserName;
            return mutator(status);
        });
    }

    private void Append(string kind, string message, bool abnormal = false) =>
        _bridge.UpdateStatus(status =>
        {
            status.CommandId = _command.Id;
            status.AgentUser = Environment.UserName;
            status.Events.Add(RdpBridge.MakeEvent(status, kind, message, abnormal: abnormal));
            return status;
        });

    private void Log(string line)
    {
        try
        {
            _logger.Log(line);
        }
        catch
        {
            // 目标账户可能没有日志目录写权限，忽略
        }

        Logged?.Invoke(this, line);
    }

    /// <summary>
    /// 响应主控端的停止请求（stop.json）。
    ///
    /// 为什么必须由 Agent 主动查：任务跑在目标会话里，主控端点了「停止执行」之后
    /// 没有任何办法直接去停那个进程 —— 两条会话之间只有桥文件能通信。
    /// 这里挂在每秒的计时回调上，代价可以忽略。
    /// </summary>
    private void CheckStopRequest(ScriptRunner runner)
    {
        if (!_bridge.IsStopRequested(_command.Id, out var emergency))
        {
            return;
        }

        // 先把请求文件清掉：ScriptRunner 停止是异步收尾，还会再跑一会儿，
        // 不清掉的话后面每一拍都会重复触发。
        _bridge.ClearStop();

        if (_stopHandled)
        {
            return;
        }

        _stopHandled = true;

        var text = emergency ? "收到紧急停止请求，立即结束任务。" : "收到停止请求，正在收尾。";
        Log(text);
        Append(RdpEventKinds.Log, text);

        if (emergency)
        {
            runner.RequestEmergencyStop();
        }
        else
        {
            runner.RequestStop();
        }
    }
}
