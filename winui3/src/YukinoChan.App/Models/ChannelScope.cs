// -*- coding: utf-8 -*-
using YukinoChan.Helpers;

namespace YukinoChan.Models;

/// <summary>
/// 任务执行页左侧「执行通道」列表的一项 —— **编排任务的组织单位**
/// （计划书 <c>docs/tasks-by-channel-plan.md</c> §3.1）。
///
/// 三种来源：
///   · <see cref="IsLocal"/>      → 本地执行（<c>Id = ""</c>），恒为第一项，永远可用；
///   · 普通通道                    → 对应 <c>Config.Rdp.Channels</c> 里的一条；
///   · <see cref="IsMissing"/>     → 任务指向了已被删除的通道（孤儿），单独成组以便改派，
///                                  不然那些任务会变成"看不见、但执行时被静默跳过"的死角。
///
/// 只做展示与计数，**不是数据源**：真正的任务仍然只有 <c>MainViewModel.Tasks</c> 一份。
/// </summary>
public sealed class ChannelScope : ObservableObject
{
    private int _taskCount;

    /// <summary>空串表示本地执行（与 <see cref="TaskConfig.ChannelId"/> 同一套语义）。</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>名称下面那行小字：目标主机 / 账户 / 凭据 / 停用。</summary>
    public string Subtitle { get; init; } = string.Empty;

    public bool IsLocal { get; init; }

    /// <summary>指向已删除的通道。</summary>
    public bool IsMissing { get; init; }

    /// <summary>能不能往里排任务（通道停用 / 未配账户 / RDP 总开关关掉都为 false）。</summary>
    public bool IsUsable { get; init; } = true;

    /// <summary>不可用原因（停用 / 未配账户 / 总开关关闭 / 未知通道），可用时为空串。</summary>
    public string HintText { get; init; } = string.Empty;

    public int TaskCount
    {
        get => _taskCount;
        set
        {
            if (SetProperty(ref _taskCount, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string CountText => _taskCount == 0 ? "无任务" : $"{_taskCount} 个任务";

    public double RowOpacity => IsUsable ? 1.0 : 0.5;

    public bool ShowHint => HintText.Length > 0;

    /// <summary>
    /// 能否「添加」任务：孤儿组不行（新建任务不该再制造孤儿）。
    /// **停用 / 未配账户的通道照样能排任务** —— 那些任务只是本轮不执行，
    /// 禁止编辑反而会让用户没法提前排好再启用。
    /// </summary>
    public bool CanAddTask => !IsMissing;
}
