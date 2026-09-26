// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;

namespace YukinoChan.Models;

/// <summary>
/// 本轮一块执行（主控端本地组 / 一条远程通道）的收尾结果。
///
/// 多通道并行时"某一块结束"不等于"整轮结束"，所以各块收尾时**只记录结果**，
/// 等全部结束再一次性定终态 —— 见 <see cref="RunSummary"/>。
/// </summary>
/// <param name="Label">展示名：本地组是「本地执行」，通道是「通道『某台机』」。</param>
/// <param name="IsRemote">true = 远程通道；false = 主控端本地执行组。</param>
/// <param name="Abnormal">该块是否出现过异常。</param>
/// <param name="Summary">一行结束说明（远程优先取代理的 Finished 事件正文）。</param>
public sealed record RunPartResult(string Label, bool IsRemote, bool Abnormal, string Summary);

/// <summary>
/// 整轮执行的汇总（纯函数，可脱离 WinUI 单测，见 <c>_smoke/RdpChannelCheck.cs</c>）。
///
/// 存在的理由：多通道并行时，每条通道跑完都会触发一次收尾。
/// 如果每块收尾时都直接给看板娘定终态，那么 A 通道跑完而 B 还在跑时，
/// 看板娘会提前变成"休息"—— 状态与事实不符（用户会以为全都完了）。
/// 所以规则是：**各块只记录结果，整轮结束才由这里"取最差"定终态**。
/// </summary>
public static class RunSummary
{
    /// <summary>整轮汇总通知的标题。</summary>
    public const string Title = "雪乃酱：本轮执行全部结束";

    /// <summary>有没有任何一块出错（决定看板娘是 Error 还是 Rest）。</summary>
    public static bool HasAnyAbnormal(IReadOnlyList<RunPartResult>? parts) =>
        parts is not null && parts.Any(p => p.Abnormal);

    /// <summary>成功的块数。</summary>
    public static int SuccessCount(IReadOnlyList<RunPartResult>? parts) =>
        parts?.Count(p => !p.Abnormal) ?? 0;

    /// <summary>异常的块数。</summary>
    public static int AbnormalCount(IReadOnlyList<RunPartResult>? parts) =>
        parts?.Count(p => p.Abnormal) ?? 0;

    /// <summary>
    /// 汇总的**一行式**正文：`共 3 块（本地 1 / 通道 2）：成功 2、异常 1。`
    /// 通知正文压成一行 —— Toast 正文多了会被截断，锁屏补发时更明显。
    /// </summary>
    public static string ComposeHeadline(IReadOnlyList<RunPartResult>? parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return "没有执行任何任务。";
        }

        var remote = parts.Count(p => p.IsRemote);
        var local = parts.Count - remote;
        var scope = local > 0 && remote > 0
            ? $"本地 {local} / 通道 {remote}"
            : remote > 0 ? $"通道 {remote}" : $"本地 {local}";

        var text = $"共 {parts.Count} 块（{scope}）：成功 {SuccessCount(parts)}";
        var bad = AbnormalCount(parts);
        if (bad > 0)
        {
            text += $"、异常 {bad}";
        }

        return text + "。";
    }

    /// <summary>
    /// 汇总的多行正文：一行一块，**异常块排前面**（先看要紧的）。日志用。
    /// </summary>
    public static IReadOnlyList<string> ComposeLines(IReadOnlyList<RunPartResult>? parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return new[] { "没有执行任何任务。" };
        }

        return parts
            .OrderByDescending(p => p.Abnormal)
            .Select(p => $"· {p.Label}：{(p.Abnormal ? "异常" : "成功")} —— {p.Summary}")
            .ToList();
    }

    /// <summary>看板娘气泡文案（整轮结束那一刻）。</summary>
    public static string ComposeMascotText(IReadOnlyList<RunPartResult>? parts)
    {
        var bad = AbnormalCount(parts);
        return bad > 0
            ? $"本轮全部结束，有 {bad} 块出现了异常，记得看一眼运行日志。"
            : "这轮全部跑完啦～所有任务都顺利收工。";
    }
}
