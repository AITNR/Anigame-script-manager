// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>统计来源的种类。</summary>
public enum StatsSourceKind
{
    /// <summary>该通道桥目录里的 stats.json：代理跑完一轮后发布的快照（首选，远程主机也读得到）。</summary>
    Bridge,

    /// <summary>本机代理副本目录下按账户分家的 runtime_stats\&lt;账户&gt;（老代理没发布快照时的兜底）。</summary>
    AgentPayload,
}

/// <summary>一个统计来源：界面上的标签 + 去哪个目录读。<see cref="Path"/> 对 Bridge 是桥目录、对 AgentPayload 是账户统计目录。</summary>
public sealed record StatsSource(string Label, StatsSourceKind Kind, string Path);

/// <summary>
/// 主控端能看到哪些耗时统计。
///
/// 为什么需要它：任务大多数并不是跑在主控端，而是交给目标会话里的代理执行（见 RdpChannelPlanner）。
/// 代理把统计写在**它自己那份副本**的 <c>runtime_stats\&lt;账户&gt;</c> 下（AppPaths.AgentMode 按账户分家），
/// 主控端那份 <c>runtime_stats</c> 永远是空的 —— 这就是"耗时统计页一直显示空的"的根因。
/// 于是分两路取数：
///   ① 桥目录里的 stats.json：Agent 每跑完一轮把合并后的历史发布过来（跨主机也读得到，首选）；
///   ② 本机代理副本的 <c>runtime_stats\&lt;账户&gt;</c>：老代理还没有发布逻辑时的兜底，
///      只在"目标是本机"时成立（远程主机的 ProgramData 主控端根本读不到）。
///
/// 同一通道两路都取到数据时**只认桥里那份**：快照本来就是从副本目录那份算出来的，
/// 两份一起合并会把次数与总耗时翻倍（见 <see cref="Collect"/>）。
/// </summary>
public static class StatsSourcePlanner
{
    /// <summary>主控端自己跑的那批任务的标签。</summary>
    public const string LocalLabel = "本机";

    /// <summary>代理副本里统计目录的名字（与 AppPaths 保持一致）。</summary>
    public const string AgentStatsFolderName = "runtime_stats";

    /// <summary>历史统计文件名（与 RuntimeStatsManager 保持一致）。</summary>
    public const string HistoryFileName = "runtime_history.json";

    /// <summary>
    /// 列出本轮界面该去哪些地方取统计。返回顺序 = 同标签下的优先级顺序（桥在前、副本在后）。
    /// </summary>
    /// <param name="rdp">RDP 配置（通道表 + 旧单通道字段）。</param>
    /// <param name="payloadSlotDirs">本机代理副本槽目录（见 RdpSessionService.PayloadSlotDirs），没有就不做兜底。</param>
    public static IReadOnlyList<StatsSource> Plan(RdpConfig? rdp, IEnumerable<string>? payloadSlotDirs)
    {
        var sources = new List<StatsSource>();
        if (rdp is null)
        {
            return sources;
        }

        // RDP 总开关关掉时所有任务都在本地跑（与 RdpChannelPlanner 一致），此时不该再去翻通道的旧统计
        if (!rdp.Enabled)
        {
            return sources;
        }

        var slots = (payloadSlotDirs ?? Array.Empty<string>())
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => dir.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var channels = (rdp.Channels ?? new List<RdpChannel>())
            .Where(c => c is not null && c.Enabled && c.IsConfigured)
            .ToList();

        if (channels.Count == 0)
        {
            // 没有可用的通道 → 退回旧单通道那一套（target_host / target_user / bridge_path）
            AddChannelSources(
                sources,
                string.IsNullOrWhiteSpace(rdp.TargetUser) ? "远程会话" : rdp.TargetUser.Trim(),
                rdp.TargetHost,
                rdp.TargetUser,
                RdpChannelPaths.Resolve(rdp.BridgePath, rdp.TargetUser),
                slots);
            return sources;
        }

        foreach (var channel in channels)
        {
            AddChannelSources(
                sources,
                channel.DisplayName,
                channel.Host,
                channel.User,
                RdpChannelPaths.BridgeDirFor(channel),
                slots);
        }

        return sources;
    }

    /// <summary>
    /// 按来源取数并合到「每个标签一份」的结果里。同标签下取第一个有数据的来源
    /// （桥快照优先，副本目录只在桥里什么都没有时才顶上）。
    /// </summary>
    public static List<(string Label, Dictionary<string, RuntimeHistoryItem> History)> Collect(
        IEnumerable<StatsSource>? sources)
    {
        var result = new List<(string Label, Dictionary<string, RuntimeHistoryItem> History)>();

        foreach (var group in (sources ?? Array.Empty<StatsSource>()).GroupBy(s => s.Label))
        {
            foreach (var source in group)
            {
                var history = Load(source);
                if (history.Count == 0)
                {
                    continue;
                }

                result.Add((group.Key, history));
                break;
            }
        }

        return result;
    }

    /// <summary>读一个来源的历史统计。读不到就给空字典（缺目录 / 缺文件 / 坏 JSON 都算"没数据"）。</summary>
    public static Dictionary<string, RuntimeHistoryItem> Load(StatsSource source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Path))
        {
            return new Dictionary<string, RuntimeHistoryItem>();
        }

        try
        {
            return source.Kind switch
            {
                StatsSourceKind.Bridge => RdpBridge.For(source.Path).TryReadStatsSnapshot()
                                          ?? new Dictionary<string, RuntimeHistoryItem>(),
                _ => RuntimeStatsManager.ReadHistoryFile(Path.Combine(source.Path, HistoryFileName)),
            };
        }
        catch
        {
            return new Dictionary<string, RuntimeHistoryItem>();
        }
    }

    private static void AddChannelSources(
        List<StatsSource> sources,
        string label,
        string? host,
        string? user,
        string bridgeDir,
        IReadOnlyList<string> payloadSlotDirs)
    {
        sources.Add(new StatsSource(label, StatsSourceKind.Bridge, bridgeDir));

        if (payloadSlotDirs.Count == 0 || !RdpTargets.IsLocal(host))
        {
            return;
        }

        foreach (var slot in payloadSlotDirs)
        {
            foreach (var dir in EnumerateAccountStatsDirs(slot, user))
            {
                sources.Add(new StatsSource(label, StatsSourceKind.AgentPayload, dir));
            }
        }
    }

    /// <summary>
    /// 在副本槽的 runtime_stats 下找出属于这个账户的目录。
    ///
    /// 为什么要按名字反查而不是直接拼路径：代理侧分家的目录名用的是
    /// <c>Environment.UserName</c> 原样（AppPaths.AccountTag，只把非法字符换成下划线），
    /// 而通道这里只有配置里填的用户名。两者在大写、空格这类差异上对不齐，
    /// 用同一套 <see cref="RdpChannelPaths.Slug"/> 归一化后再比才稳。
    /// </summary>
    private static IEnumerable<string> EnumerateAccountStatsDirs(string payloadSlot, string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            yield break;
        }

        var root = Path.Combine(payloadSlot, AgentStatsFolderName);
        var wanted = RdpChannelPaths.Slug(user);

        string[] dirs;
        try
        {
            if (!Directory.Exists(root))
            {
                yield break;
            }

            dirs = Directory.GetDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            string name;
            try
            {
                name = Path.GetFileName(dir);
            }
            catch
            {
                continue;
            }

            if (string.Equals(RdpChannelPaths.Slug(name), wanted, StringComparison.OrdinalIgnoreCase))
            {
                yield return dir;
            }
        }
    }
}
