// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using YukinoChan.Helpers;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// 一次轮询给通道带来的变化。Session 本身只改状态、不产生副作用，
/// 日志 / 通知 / 看板娘 / 收尾这些动作由主控端（MainViewModel）按本结果执行 ——
/// 这样整条判定链能脱离 WinUI 单测（见 _smoke/RdpChannelCheck.cs）。
/// </summary>
public sealed class RdpChannelTick
{
    /// <summary>本次读到的状态快照（收尾文案要用它取 Finished 事件的 Message）。</summary>
    public RdpStatus? Status { get; set; }

    /// <summary>本拍新出现的事件（按序号去重后）。</summary>
    public List<RdpTaskEvent> NewEvents { get; } = new();

    /// <summary>本拍收到了本指令的 Finished 事件。</summary>
    public bool SawFinished { get; set; }

    /// <summary>Finished 事件带异常标记（"有异常"只能靠它区分，Phase 是恒等的）。</summary>
    public bool FinishedAbnormal { get; set; }

    /// <summary>桥里的状态不属于本指令 —— 「等待代理接管」的过渡态，绝不报失联。</summary>
    public bool AwaitingAgent { get; set; }

    /// <summary>本拍是第一次进入「等待接管」（用于只记一条中性日志）。</summary>
    public bool AwaitingAgentFirstTime { get; set; }

    /// <summary>本拍心跳从失联中恢复（用于补一条「已恢复」日志）。</summary>
    public bool HeartbeatRecovered { get; set; }

    /// <summary>本拍刚判定为失联（只报一次，避免每秒刷屏）。</summary>
    public bool JustWentStale { get; set; }

    /// <summary>
    /// 完成信号：两条路任一即可 ——
    ///   ① 收到本指令的 Finished 事件（持久化在 Events 里、跨 idle 心跳保留，最可靠）；
    ///   ② 恰好轮询到终态相位（注销收尾时代理被连带杀掉、done 留盘，这条会命中）。
    /// 都不依赖"恰好轮询到终态相位"这个时序前提。
    ///
    /// <para>**归属不匹配时一律不算完成** —— 桥里躺着的可能是上一轮指令写的 done，
    /// 若直接采信它的相位，新指令会在下发瞬间被判"已完成"（收尾、停轮询、刷统计全被误触发）。</para>
    /// </summary>
    public bool CompletionSignaled =>
        !AwaitingAgent && (SawFinished || Status?.Phase is "done" or "stopped" or "error");

    /// <summary>本次是否异常收尾（同样只在归属匹配时才作数）。</summary>
    public bool Abnormal => !AwaitingAgent && (FinishedAbnormal || Status?.Phase == "error");

}

/// <summary>
/// 一个「执行通道」的运行时状态 —— 也就是原本散在 MainViewModel 里的那一整套单通道字段
/// （进度 / 相位 / 心跳 / 事件 / 指令归属 / 收尾幂等标志）。
///
/// 为什么要有它：多通道并行意味着这些状态必须**每通道一份**。留在 VM 里就只能是全局单例，
/// 两条通道会互相覆盖彼此的 commandId 与事件序号，完成判定也会串台。
///
/// 纯逻辑（只依赖 BCL + Models + RdpBridge），不碰 WinUI —— 内嵌画面（RdpEmbeddedClient）
/// 仍由 VM 按通道持有，那个依赖原生 DLL，不能进冒烟工程。
/// </summary>
public sealed class RdpChannelSession
{
    /// <summary>界面事件列表的保留条数（与改造前一致）。</summary>
    public const int MaxEventLines = 200;

    private readonly RdpBridge _bridge;
    private long _lastEventSeq;
    private bool _staleReported;
    private bool _awaitingLogged;
    private bool _completionReported;

    /// <param name="channel">
    /// 通道配置快照；本地执行（未分配通道）传 null —— 那种情况下本对象只用于承载
    /// 界面状态，不与任何桥交互。
    /// </param>
    /// <param name="bridge">该通道的指令桥。</param>
    public RdpChannelSession(RdpChannel? channel, RdpBridge bridge)
    {
        Channel = channel;
        _bridge = bridge;
    }

    // ---------------- 身份 ----------------

    public RdpChannel? Channel { get; }

    public string ChannelId => Channel?.Id ?? string.Empty;

    /// <summary>菜单 / 日志里用的名字。</summary>
    public string DisplayName => Channel?.DisplayName ?? "本地执行";

    public string TargetHost => Channel?.Host ?? string.Empty;

    public string TargetUser => Channel?.User ?? string.Empty;

    public RdpBridge Bridge => _bridge;

    /// <summary>是否是与远程会话打交道（本地执行组为 false）。</summary>
    public bool IsRemote => Channel is not null;

    // ---------------- 运行时状态 ----------------

    public ObservableCollection<RdpTaskEvent> Events { get; } = new();

    /// <summary>当前下发指令的 Id —— 用于校验桥里的 status.json 是不是本指令写的。</summary>
    public string? CommandId { get; private set; }

    /// <summary>下发当前指令的时刻 —— 启动宽限期从这里起算。</summary>
    public DateTimeOffset IssuedAt { get; private set; }

    public int Total { get; private set; }

    public string Phase { get; private set; } = "idle";

    public string StatusText { get; private set; } = "未连接";

    public string CurrentTask { get; private set; } = "－";

    public int Progress { get; private set; }

    public int ElapsedSeconds { get; private set; }

    /// <summary>心跳描述，例如「3 秒前」「已失联 8 分 12 秒」。</summary>
    public string HeartbeatText { get; private set; } = "－";

    /// <summary>代理是否已停止回传心跳（会话断开 / 被注销时的典型表现）。</summary>
    public bool IsStale { get; private set; }

    /// <summary>最后一次有效心跳的时间点（失联告警里报"最后心跳"用）。</summary>
    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public bool IsActive => Phase is "running";

    public bool HasCommand => !string.IsNullOrEmpty(CommandId);

    /// <summary>"跑完了"这件事是否已经收尾（保证一轮指令只收尾一次）。</summary>
    public bool CompletionReported => _completionReported;

    public string ElapsedText => FormatHelper.FormatSeconds(ElapsedSeconds);

    public string TaskDisplay => string.IsNullOrEmpty(CurrentTask) ? "－" : CurrentTask;

    // ---------------- 生命周期 ----------------

    /// <summary>
    /// 下发指令时复位全部状态并记下归属与下发时刻。
    /// 事件序号归零 + 桥里的 status 已被 <see cref="RdpBridge.ResetStatus"/> 换成全新一份，
    /// 所以上一轮的 Finished 不会给本轮制造假完成。
    /// </summary>
    public void Begin(string commandId, int total, DateTimeOffset now)
    {
        CommandId = commandId;
        IssuedAt = now;
        Total = total;
        Progress = 0;
        ElapsedSeconds = 0;
        CurrentTask = "－";
        Phase = "idle";
        StatusText = "正在连接目标会话…";
        HeartbeatText = "－";
        IsStale = false;
        LastHeartbeatAt = null;
        Events.Clear();

        _lastEventSeq = 0;
        _staleReported = false;
        _awaitingLogged = false;
        _completionReported = false;
    }

    /// <summary>
    /// 只复位界面状态、不记归属（用于本地执行组 —— 它没有桥，也不需要完成判定）。
    /// </summary>
    public void BeginLocal(int total)
    {
        Total = total;
        Progress = 0;
        ElapsedSeconds = 0;
        CurrentTask = "－";
        Phase = "idle";
        StatusText = "未连接";
        HeartbeatText = "－";
        IsStale = false;
        Events.Clear();
        _lastEventSeq = 0;
        _staleReported = false;
        _awaitingLogged = false;
        _completionReported = false;
        CommandId = null;
    }

    /// <summary>
    /// 本地执行组的进度入口。
    ///
    /// 本地没有桥，进度来自主控端 <c>ScriptRunner</c> 的回调。之所以要把它记进这同一套字段，
    /// 是为了让**状态面板 / 主页通道概览 / 整轮汇总**只有一个数据源 ——
    /// 否则本地这块在面板上永远是初始值（"跑起来了但面板没反应"，已实际发生过）。
    /// 只传需要改的项，null 表示保持原值。
    /// </summary>
    public void UpdateLocal(
        string? phase = null,
        string? statusText = null,
        int? progress = null,
        int? total = null,
        string? currentTask = null,
        int? elapsedSeconds = null)
    {
        if (phase is not null)
        {
            Phase = phase;
        }

        if (statusText is not null)
        {
            StatusText = statusText;
        }

        if (progress is not null)
        {
            Progress = progress.Value;
        }

        if (total is not null)
        {
            Total = total.Value;
        }

        if (currentTask is not null)
        {
            CurrentTask = currentTask;
        }

        if (elapsedSeconds is not null)
        {
            ElapsedSeconds = elapsedSeconds.Value;
        }
    }

    /// <summary>
    /// 收尾完成：置幂等标志并清掉归属标记，
    /// 之后点「停止执行」不会再往桥里写一条无主的 stop.json。
    /// </summary>
    public void MarkCompletionReported()
    {
        _completionReported = true;
        CommandId = null;
    }

    // ---------------- 轮询 ----------------

    /// <summary>
    /// 应用一次 status.json 快照。返回本拍的变化，由调用方转成日志 / 通知 / 收尾。
    ///
    /// 判定顺序不可换（三段式，理由见 <see cref="RdpHeartbeat.Evaluate"/>）：
    ///   ① 归属校验 → 不是本指令的状态一律不采信（也不报失联）；
    ///   ② 归属一致才应用进度与事件；
    ///   ③ 最后才按时间戳判失联。
    /// </summary>
    /// <param name="connectTimeoutSeconds">连接超时（秒），用于折算启动宽限期。</param>
    public RdpChannelTick Tick(RdpStatus status, DateTimeOffset now, int connectTimeoutSeconds)
    {
        var tick = new RdpChannelTick { Status = status };

        var grace = RdpHeartbeat.ResolveGraceSeconds(connectTimeoutSeconds);
        var beat = RdpHeartbeat.Evaluate(
            status.UpdatedAt,
            status.Phase,
            now,
            CommandId,
            status.CommandId,
            IssuedAt,
            grace);

        // ① 归属不匹配（含本指令尚未在桥上落地）：过渡态，只给中性提示。
        //    绝不动 Events / Progress —— 否则上一轮指令残留的事件会被当成这一轮的进度收进来。
        if (beat.AwaitingAgent)
        {
            tick.AwaitingAgent = true;
            HeartbeatText = beat.Text;
            IsStale = false;

            if (!_awaitingLogged)
            {
                _awaitingLogged = true;
                tick.AwaitingAgentFirstTime = true;
            }

            if (_staleReported)
            {
                _staleReported = false;
                tick.HeartbeatRecovered = true;
            }

            return tick;
        }

        _awaitingLogged = false;

        // ② 归属一致 → 应用进度
        Phase = status.Phase;
        Progress = status.Progress;
        Total = status.Total;
        ElapsedSeconds = status.ElapsedSeconds;
        CurrentTask = string.IsNullOrEmpty(status.CurrentTask) ? "－" : status.CurrentTask;
        StatusText = string.IsNullOrEmpty(status.StatusText) ? status.Phase : status.StatusText;

        foreach (var ev in status.Events)
        {
            if (ev.Seq <= _lastEventSeq)
            {
                continue;
            }

            _lastEventSeq = ev.Seq;
            Events.Add(ev);
            while (Events.Count > MaxEventLines)
            {
                Events.RemoveAt(0);
            }

            tick.NewEvents.Add(ev);

            // 只【记录】、不在这里收尾 —— 收尾统一由调用方在循环外做一次，
            // 否则中途停轮询后同一次调用还会继续走到后面的判定，容易重复触发。
            if (ev.Kind == RdpEventKinds.Finished)
            {
                tick.SawFinished = true;
                tick.FinishedAbnormal |= ev.IsAbnormal;
            }
        }

        // ③ 心跳健康度与「失联 / 恢复」的边沿
        if (!beat.Parsed)
        {
            HeartbeatText = "未知";
            return tick;
        }

        HeartbeatText = beat.Text;

        if (!beat.IsStale)
        {
            IsStale = false;
            LastHeartbeatAt = beat.UpdatedAt;

            if (_staleReported)
            {
                _staleReported = false;
                tick.HeartbeatRecovered = true;
            }

            return tick;
        }

        IsStale = true;
        if (!_staleReported)
        {
            _staleReported = true;
            tick.JustWentStale = true;
        }

        return tick;
    }

    /// <summary>连接阶段直接设定状态文案（进度区显示）。</summary>
    public void SetStatusText(string text) => StatusText = text;

    /// <summary>
    /// 标记失联：状态文案切成告警（进度刻意保留，让用户看到停在哪一步）。
    /// </summary>
    public void MarkStale()
    {
        var last = LastHeartbeatAt ?? IssuedAt;
        StatusText = $"⚠ 代理已失联（最后心跳 {last:HH:mm:ss}）";
    }

    // ---------------- 停止 ----------------

    /// <summary>
    /// 把「停」这个意图写进本通道的 stop.json，由代理每秒检查一次。
    /// 远程任务跑在目标会话的代理进程里，本地 ScriptRunner 管不到它 —— 桥是唯一通道。
    /// </summary>
    public bool WriteStop(bool emergency, out string error)
    {
        error = string.Empty;
        var commandId = CommandId;
        if (string.IsNullOrEmpty(commandId))
        {
            return false;
        }

        if (!_bridge.WriteStop(commandId, emergency))
        {
            error = _bridge.LastError;
            return false;
        }

        return true;
    }

    /// <summary>把当前状态同步成"已停止"（停止请求已下发、等代理收尾时用）。</summary>
    public void MarkStopping(bool emergency)
    {
        StatusText = emergency ? "已请求紧急停止…" : "已请求停止…";
    }
}
