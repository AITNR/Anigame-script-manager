// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace YukinoChan.Models;

/// <summary>
/// 「按执行通道编排任务」的纯函数集合（计划书 <c>docs/tasks-by-channel-plan.md</c> §3）。
///
/// 为什么单独抽一层：**任务仍然只有一份扁平表**（<c>config.tasks[]</c> + <c>channel_id</c>），
/// "通道 → 任务"只是它的一种读法。放到不碰界面、不碰文件的静态类里，
/// 既能让 <c>MainViewModel</c> 只做薄壳，也能在 <c>_smoke</c> 里脱离 WinUI 直接测。
///
/// ⚠️ 与执行顺序的关系（§3.2 等价性）：<see cref="RdpChannelPlanner.Plan"/> 是
/// 「全局 order 升序 → 稳定分桶 → 按通道配置顺序启动」。本类只做两件事：
///   1. 通道内上移/下移**只在同通道内**交换 order；
///   2. 重编 order 时保持（本地 → 通道配置序 → 孤儿）的分组次序与组内相对次序。
/// 于是每条通道拿到的任务集合与顺序与改造前**逐一相同**，执行链路一行都不用改。
/// </summary>
public static class TaskScopePlanner
{
    /// <summary>本地执行（与 <see cref="TaskConfig.ChannelId"/> 空串同一语义）。</summary>
    // 注意：string.Empty 是 static readonly，不能赋给 const —— 这里必须写字面量
    public const string LocalScopeId = "";

    /// <summary>删除通道时「保持未知」的哨兵值 —— 只在对话框里传递，不落盘。</summary>
    public const string KeepOrphan = "__keep__";

    public static string NormalizeId(string? id) => (id ?? string.Empty).Trim();

    private static bool SameId(string? a, string? b) =>
        string.Equals(NormalizeId(a), NormalizeId(b), StringComparison.OrdinalIgnoreCase);

    // ---------------- 分组读取 ----------------

    /// <summary>某条通道下的任务，按 <see cref="TaskConfig.Order"/> 升序（与执行顺序一致）。</summary>
    public static List<TaskConfig> TasksForScope(IEnumerable<TaskConfig>? tasks, string? scopeId)
    {
        return (tasks ?? Enumerable.Empty<TaskConfig>())
            .Where(t => t is not null && SameId(t.ChannelId, scopeId))
            .OrderBy(t => t.Order)
            .ToList();
    }

    public static int CountForScope(IEnumerable<TaskConfig>? tasks, string? scopeId) =>
        TasksForScope(tasks, scopeId).Count;

    /// <summary>
    /// 孤儿 id 列表：任务指向的通道已经不在配置里了。
    /// 按 id 排序 —— 分组顺序必须稳定，否则界面上的行会来回跳。
    /// </summary>
    public static List<string> OrphanIds(IEnumerable<TaskConfig>? tasks, IReadOnlyList<RdpChannel>? channels)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels ?? Array.Empty<RdpChannel>())
        {
            if (!string.IsNullOrWhiteSpace(channel.Id))
            {
                known.Add(channel.Id.Trim());
            }
        }

        return (tasks ?? Enumerable.Empty<TaskConfig>())
            .Where(t => t is not null)
            .Select(t => NormalizeId(t.ChannelId))
            .Where(id => id.Length > 0 && !known.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 左侧「执行通道」列表：本地执行（恒第一项）→ 各通道（配置顺序）→ 未知通道（孤儿）。
    /// 顺带把每个 scope 的任务数算好。
    /// </summary>
    public static List<ChannelScope> BuildScopes(
        IEnumerable<TaskConfig>? tasks,
        IReadOnlyList<RdpChannel>? channels,
        bool rdpEnabled)
    {
        var list = (tasks ?? Enumerable.Empty<TaskConfig>()).Where(t => t is not null).ToList();
        var channelList = channels ?? Array.Empty<RdpChannel>();
        var scopes = new List<ChannelScope>();

        scopes.Add(new ChannelScope
        {
            Id = LocalScopeId,
            Name = "本地执行（当前会话）",
            Subtitle = "主控端当前账户",
            IsLocal = true,
            IsUsable = true,
            TaskCount = CountForScope(list, LocalScopeId),
        });

        foreach (var channel in channelList)
        {
            if (string.IsNullOrWhiteSpace(channel.Id))
            {
                continue;
            }

            var usable = rdpEnabled && channel.Enabled && channel.IsConfigured;
            var hint =
                !rdpEnabled ? "总开关已关闭 —— 开始执行时这些任务会按本地跑"
                : !channel.Enabled ? "通道已停用 —— 本轮不会下发"
                : !channel.IsConfigured ? "通道未配置目标账户 —— 本轮不会下发"
                : string.Empty;

            scopes.Add(new ChannelScope
            {
                Id = channel.Id,
                Name = channel.DisplayName,
                Subtitle = channel.ListSubtitle,
                IsUsable = usable,
                HintText = hint,
                TaskCount = CountForScope(list, channel.Id),
            });
        }

        foreach (var id in OrphanIds(list, channelList))
        {
            scopes.Add(new ChannelScope
            {
                Id = id,
                Name = $"未知通道（{id}）",
                Subtitle = "这条通道已经被删除了",
                IsMissing = true,
                IsUsable = false,
                HintText = "里面的任务不会被下发 —— 请移到别的通道或本地执行",
                TaskCount = CountForScope(list, id),
            });
        }

        return scopes;
    }

    /// <summary>写通道内显示序号（1..N，运行期属性，不落盘）。</summary>
    public static void RenumberScope(IReadOnlyList<TaskConfig> scopeTasks)
    {
        for (var i = 0; i < scopeTasks.Count; i++)
        {
            scopeTasks[i].ScopeOrder = i + 1;
        }
    }

    // ---------------- 编排操作 ----------------

    /// <summary>
    /// 把 order 重编成 1..N 连续唯一：先按（本地 → 通道配置序 → 孤儿）分组，组内按现有 order 升序。
    /// **只改 order 的值，不改集合的物理顺序** —— 执行侧只看 order。
    /// </summary>
    public static void NormalizeOrdersByChannel(IList<TaskConfig> tasks, IReadOnlyList<RdpChannel>? channels)
    {
        if (tasks is null)
        {
            return;
        }

        var rank = BuildRankMap(channels, tasks);

        var ordered = tasks
            .Where(t => t is not null)
            .OrderBy(t => RankOf(t.ChannelId, rank))
            .ThenBy(t => t.Order)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i + 1;
        }
    }

    /// <summary>
    /// 通道内上移/下移（delta = -1 / +1）：**只与同通道内相邻的一项交换 order**。
    /// 别的通道的顺序不受影响 —— 这正是"执行顺序等价"的关键。
    /// </summary>
    public static bool TryMoveInScope(
        IList<TaskConfig> tasks,
        TaskConfig? task,
        int delta,
        IReadOnlyList<RdpChannel>? channels)
    {
        if (tasks is null || task is null || delta == 0)
        {
            return false;
        }

        var channelList = channels ?? Array.Empty<RdpChannel>();

        // 先重编一次：万一 order 有重复（旧配置 / 手工改过），交换两个相同值等于没动
        NormalizeOrdersByChannel(tasks, channelList);

        var scope = TasksForScope(tasks, task.ChannelId);
        var index = scope.IndexOf(task);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= scope.Count)
        {
            return false;
        }

        (scope[index].Order, scope[target].Order) = (scope[target].Order, scope[index].Order);
        NormalizeOrdersByChannel(tasks, channelList);
        return true;
    }

    /// <summary>改任务归属：换到目标通道并落到它的末尾，然后统一重编 order。</summary>
    public static void MoveToScope(
        IList<TaskConfig> tasks,
        TaskConfig? task,
        string? targetScopeId,
        IReadOnlyList<RdpChannel>? channels)
    {
        if (tasks is null || task is null)
        {
            return;
        }

        var id = NormalizeId(targetScopeId);
        if (SameId(task.ChannelId, id))
        {
            return;
        }

        task.ChannelId = id;

        var max = tasks.Where(t => t is not null).Select(t => t.Order).DefaultIfEmpty(0).Max();
        task.Order = max + 1;

        NormalizeOrdersByChannel(tasks, channels ?? Array.Empty<RdpChannel>());
    }

    // ---------------- 并发组（只在通道内生效） ----------------

    /// <summary>
    /// 跨执行单元的并发组名：同一个组名出现在 ≥2 个通道（本地组也算一个）。
    /// <see cref="Services.ScriptRunner"/> 只在**本批次任务列表**里扫描相邻同组任务，
    /// 所以跨通道同名组根本不会并在一起 —— 这里只用来给一行日志提示，不阻断。
    /// </summary>
    public static List<string> CrossChannelGroups(IEnumerable<TaskConfig>? tasks)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks ?? Enumerable.Empty<TaskConfig>())
        {
            if (task is null || !task.Enabled)
            {
                continue;
            }

            var group = (task.ConcurrentGroup ?? string.Empty).Trim();
            if (group.Length == 0)
            {
                continue;
            }

            if (!owners.TryGetValue(group, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                owners[group] = set;
            }

            set.Add(NormalizeId(task.ChannelId));
        }

        return owners
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => kv.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- 内部：分组次序 ----------------

    private static Dictionary<string, int> BuildRankMap(
        IReadOnlyList<RdpChannel>? channels,
        IEnumerable<TaskConfig> tasks)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [LocalScopeId] = 0,
        };

        var channelList = channels ?? Array.Empty<RdpChannel>();
        for (var i = 0; i < channelList.Count; i++)
        {
            var id = NormalizeId(channelList[i].Id);
            if (id.Length > 0)
            {
                rank[id] = i + 1;
            }
        }

        var orphanBase = channelList.Count + 1;
        var orphans = OrphanIds(tasks, channelList);
        for (var i = 0; i < orphans.Count; i++)
        {
            rank[orphans[i]] = orphanBase + i;
        }

        return rank;
    }

    private static int RankOf(string? channelId, Dictionary<string, int> rank) =>
        rank.TryGetValue(NormalizeId(channelId), out var value) ? value : int.MaxValue;
}
