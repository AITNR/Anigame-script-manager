// -*- coding: utf-8 -*-
namespace YukinoChan.Models;

/// <summary>
/// 主页「会话通道」概览里的一行。
///
/// 为什么要有这个**快照**类型：<c>RdpChannelSession</c> 的状态字段是普通属性
/// （没有实现 INotifyPropertyChanged —— 那样每条通道都得挂一整套通知，不值当），
/// 所以 DataTemplate 里的 <c>x:Bind</c> 不会自己更新。主页按 1Hz 重建这个集合，
/// 界面自然跟着走。
/// </summary>
/// <param name="ChannelId">通道 id（本地组为空串）。</param>
/// <param name="Name">展示名。</param>
/// <param name="TargetText">目标描述（主机 / 账户，或"主控端当前会话"）。</param>
/// <param name="StatusText">当前状态文案。</param>
/// <param name="ProgressText">进度文案，形如 <c>2 / 5</c>。</param>
/// <param name="HeartbeatText">心跳文案。</param>
/// <param name="IsStale">是否已判定失联。</param>
/// <param name="IsRemote">是否远程通道（本地执行组为 false）。</param>
/// <param name="IsActive">是否正在跑（相位 running）。</param>
/// <param name="HasCommand">本轮是否已经下发过指令。</param>
public sealed record ChannelOverviewItem(
    string ChannelId,
    string Name,
    string TargetText,
    string StatusText,
    string ProgressText,
    string HeartbeatText,
    bool IsStale,
    bool IsRemote,
    bool IsActive,
    bool HasCommand)
{
    /// <summary>点击该行要跳转的导航 tag（本地执行组没有独立页面，返回空串）。</summary>
    public string NavTag => IsRemote && ChannelId.Length > 0 ? "ch:" + ChannelId : string.Empty;

    /// <summary>行首状态符号：失联 → 警告，运行中 → 实心点，已下发未跑 → 空心点，其它 → 中点。</summary>
    public string StateGlyph => IsStale ? "⚠" : IsActive ? "●" : HasCommand ? "○" : "·";
}
