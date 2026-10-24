// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Yomori.Models;

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
/// <param name="Reasons">
/// 该块异常的**原因明细**（每个出问题的任务一行，例如「任务「日常」脚本不存在，已跳过」）。
/// 以前只留 <paramref name="Summary"/>，而远程 Summary 恒为「已完成（有异常）」——
/// 界面上等于只说了一句"有异常"、不说为什么。
/// </param>
/// <param name="Stopped">该块是被用户手动停止打断的（不算异常，但终态要说"已停止"）。</param>
public sealed record RunPartResult(
    string Label,
    bool IsRemote,
    bool Abnormal,
    string Summary,
    IReadOnlyList<string>? Reasons = null,
    bool Stopped = false);

/// <summary>
/// 整轮执行的汇总（纯函数，可脱离 WinUI 单测，见 <c>_smoke/RdpChannelCheck.cs</c>）。
///
/// 存在的理由：多通道并行时，每条通道跑完都会触发一次收尾。
/// 如果每块收尾时都直接落终态，那么 A 通道跑完而 B 还在跑时，
/// 汇总会提前变成"休息"—— 状态与事实不符（用户会以为全都完了）。
/// 所以规则是：**各块只记录结果，整轮结束才由这里"取最差"定终态**。
/// </summary>
public static class RunSummary
{
    /// <summary>整轮汇总通知的标题。</summary>
    public const string Title = "Yomori：本轮执行全部结束";

    /// <summary>有没有任何一块出错（决定整轮汇总是否记为异常）。</summary>
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

    /// <summary>
    /// 异常原因明细的最大条数 —— 界面与日志共用。超出的截断，
    /// 免得一个跑了几百个任务的通道把状态栏顶成一堵墙（完整原因永远在运行日志里）。
    /// </summary>
    public const int MaxReasons = 12;

    /// <summary>
    /// 整轮的异常原因明细，一行一条，**带块标签**（`通道「3 号机」：任务「日常」脚本不存在，已跳过`）。
    ///
    /// <para>存在的理由：状态面板此前只会显示「已完成（有异常）」，用户看不出是哪条通道、
    /// 哪个任务、为什么 —— 界面上等于只说了"出了问题"这一件事。
    /// 这里把各块收集到的原因拍平，状态栏直接显示第一条，日志与悬浮提示显示全部。</para>
    ///
    /// <para>只取 <see cref="RunPartResult.Abnormal"/> 为真的块：被手动停止的块不该进这里
    /// （那是用户自己的操作，不是故障）。</para>
    /// </summary>
    public static IReadOnlyList<string> ComposeReasons(IReadOnlyList<RunPartResult>? parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var part in parts.Where(p => p.Abnormal))
        {
            if (part.Reasons is { Count: > 0 })
            {
                foreach (var reason in part.Reasons)
                {
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        continue;
                    }

                    var line = $"{part.Label}：{reason.Trim()}";
                    if (seen.Add(line))
                    {
                        lines.Add(line);
                    }

                    if (lines.Count >= MaxReasons)
                    {
                        return lines;
                    }
                }

                continue;
            }

            // 该块异常但一条原因都没收集到：别编，也别把「已完成（有异常）」当原因再念一遍。
            var fallback = DescribeMissingReason(part);
            if (seen.Add(fallback))
            {
                lines.Add(fallback);
            }

            if (lines.Count >= MaxReasons)
            {
                return lines;
            }
        }

        return lines;
    }

    /// <summary>异常块没有明细时的兜底说明 —— 必须指出"去哪儿看"，别让用户对着界面发呆。</summary>
    private static string DescribeMissingReason(RunPartResult part)
    {
        var summary = string.IsNullOrWhiteSpace(part.Summary) ? string.Empty : part.Summary.Trim();

        // Summary 里的收尾语（「已完成（有异常）」/「全部任务结束」）本身没有信息量，
        // 当成原因重复一遍只会让用户以为是另一条线索。
        var informative = summary.Length > 0
            && !summary.Contains("有异常", StringComparison.Ordinal);

        return informative
            ? $"{part.Label}：{summary}"
            : $"{part.Label}：没有上报具体原因（详见「运行日志」里该通道的输出）";
    }
}
