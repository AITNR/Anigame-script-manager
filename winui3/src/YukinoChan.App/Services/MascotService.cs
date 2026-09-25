// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// 看板娘素材与文案。
/// 状态：idle（待机）/ work（执行中）/ rest（暂停或结束）/ error（异常）。
/// 素材目录沿用 Python 版约定：assets/mascot/{state}/*.png
/// </summary>
public static class MascotService
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    private static readonly Dictionary<string, string[]> StateAliases = new()
    {
        [MascotStates.Idle] = new[] { "idle" },
        [MascotStates.Rest] = new[] { "rest", "pause", "paused", "done", "finish", "finished" },
        [MascotStates.Error] = new[] { "error", "err", "exception", "warning" },
        [MascotStates.Work] = new[] { "work", "working", "run", "running", "gaming" },
    };

    private static readonly Random Random = new();

    public static List<string> ImageCandidates(string state)
    {
        var result = new List<string>();
        var mascotDir = AppPaths.MascotDir;
        if (!Directory.Exists(mascotDir))
        {
            return result;
        }

        var aliases = StateAliases.TryGetValue(state, out var list) ? list : new[] { state };

        foreach (var alias in aliases)
        {
            var folder = Path.Combine(mascotDir, alias);
            if (Directory.Exists(folder))
            {
                foreach (var extension in Extensions)
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(folder, "*" + extension))
                        {
                            AddUnique(result, file);
                        }
                    }
                    catch
                    {
                        // 忽略目录读取异常
                    }
                }
            }

            foreach (var extension in Extensions)
            {
                try
                {
                    foreach (var file in Directory.GetFiles(mascotDir, alias + "_*" + extension))
                    {
                        AddUnique(result, file);
                    }

                    foreach (var file in Directory.GetFiles(mascotDir, alias + "*" + extension))
                    {
                        AddUnique(result, file);
                    }
                }
                catch
                {
                    // 忽略
                }

                var single = Path.Combine(mascotDir, alias + extension);
                if (File.Exists(single))
                {
                    AddUnique(result, single);
                }
            }
        }

        return result;
    }

    public static string? PickImage(string state, bool randomPick = true)
    {
        var candidates = ImageCandidates(state);
        if (candidates.Count == 0 && state != MascotStates.Idle)
        {
            candidates = ImageCandidates(MascotStates.Idle);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return randomPick
            ? candidates[Random.Next(candidates.Count)]
            : candidates[0];
    }

    public static string DefaultText(string state)
    {
        switch (state)
        {
            case MascotStates.Work:
            {
                var pool = new[]
                {
                    "正在执行中，我会帮你看着的。",
                    "任务已经跑起来啦～先别急着关窗口哦。",
                    "目前正在运行中，有问题我会提醒你的。",
                    "雪乃酱正在确认运行状态。",
                };
                return pool[Random.Next(pool.Length)];
            }

            case MascotStates.Rest:
                return "现在先休息一下，需要时再继续吧。";

            case MascotStates.Error:
                return "这次运行好像遇到问题了，我先帮你标出来。";

            default:
                return "雪乃酱待命中～";
        }
    }

    public static int PriorityFor(string state) => state switch
    {
        MascotStates.Error => 100,
        MascotStates.Work => 70,
        MascotStates.Rest => 50,
        _ => 20,
    };

    /// <summary>统一兜底气泡文案：最多三行、最多 72 字，避免气泡被撑破。</summary>
    public static string NormalizeBubbleText(string text, IReadOnlyList<string>? guideTexts = null)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return "雪乃酱待命中～";
        }

        if (raw.Contains("还未设置配置文件")
            || raw.Contains("请在任务执行中设置配置")
            || raw.Contains("左上角文件导入配置"))
        {
            return guideTexts is { Count: > 0 } ? guideTexts[0] : "还没有配置任务哦～";
        }

        var lines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        if (lines.Count > 3)
        {
            raw = string.Join("\n", lines.GetRange(0, 3));
        }
        else if (lines.Count > 0)
        {
            raw = string.Join("\n", lines);
        }

        if (raw.Length > 72)
        {
            raw = raw.Substring(0, 69).TrimEnd('，', '。', ',', '.', ' ') + "……";
        }

        return raw;
    }

    /// <summary>把日志/异常内容转换成更像桌面助手的气泡文案。</summary>
    public static string FriendlyErrorText(string message)
    {
        var raw = (message ?? string.Empty).Trim();
        var clean = raw;
        var marker = raw.IndexOf("] ", StringComparison.Ordinal);
        if (marker >= 0)
        {
            clean = raw.Substring(marker + 2);
        }

        var taskPart = "当前任务";
        var start = clean.IndexOf("任务「", StringComparison.Ordinal);
        if (start >= 0)
        {
            var rest = clean.Substring(start + 3);
            var end = rest.IndexOf('」');
            if (end >= 0)
            {
                taskPart = "任务「" + rest.Substring(0, end) + "」";
            }
        }

        if (ContainsAny(clean, "启动失败", "构建启动命令失败", "脚本不存在", "脚本路径为空", "路径不是文件", "WinError 740", "请求的操作需要提升"))
        {
            return $"{taskPart}没有顺利启动。雪乃酱先帮你标出来，详细原因可以看日志。";
        }

        if (ContainsAny(clean, "已超时", "超时", "强制结束"))
        {
            return $"{taskPart}运行太久了，已经按设置处理。雪乃酱会先保持异常提示。";
        }

        if (ContainsAny(clean, "疑似过早退出", "提前退出", "过早退出"))
        {
            return $"{taskPart}很快就退出了，可能没有正常跑完。详细情况在日志里。";
        }

        if (ContainsAny(clean, "未检测到", "判定为异常"))
        {
            return $"{taskPart}没有检测到预期结果，雪乃酱先记为异常。";
        }

        return $"{taskPart}遇到异常了。雪乃酱先提示你，详细信息可以打开日志查看。";
    }

    public static bool IsErrorLogLine(string line) =>
        ContainsAny(line,
            "启动失败", "构建启动命令失败", "脚本不存在", "脚本路径为空",
            "路径不是文件", "请求的操作需要提升", "WinError 740",
            "执行器发生异常", "保存异常报告失败",
            "强制结束失败", "kill 失败", "终止失败",
            "紧急停止失败", "关闭失败")
        || ContainsAny(line, "已超时", "超时：正在强制结束");

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddUnique(List<string> target, string value)
    {
        foreach (var item in target)
        {
            if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        target.Add(value);
    }
}
