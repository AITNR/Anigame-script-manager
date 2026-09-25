// -*- coding: utf-8 -*-
using System.Collections.Generic;

namespace YukinoChan.Models;

/// <summary>
/// 任务的监控目标描述。
///
/// V31.17.1 优先级：
/// 1. 游戏窗口关键词（window_keywords）
/// 2. 目标进程关键词（process_keywords）
/// 3. 旧 wait_mode + wait_process_name 兼容兜底
/// 4. direct_process：只看雪乃酱直接启动的进程
/// </summary>
public sealed class MonitorSpec
{
    public string Kind { get; init; } = MonitorKinds.Direct;

    public string Label { get; init; } = "直接启动进程";

    public List<string> Keywords { get; init; } = new();

    public string Source { get; init; } = "direct_process";

    public string Describe()
    {
        var keywords = new List<string>();
        foreach (var keyword in Keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keywords.Add(keyword.Trim());
            }
        }

        return keywords.Count > 0 ? $"{Label}：{string.Join(", ", keywords)}" : Label;
    }
}
