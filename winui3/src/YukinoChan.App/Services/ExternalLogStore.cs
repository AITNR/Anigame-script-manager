// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>一份外部日志文件的摘要（设置页「占用」列表用）。</summary>
public sealed class ExternalLogFileInfo
{
    public string FilePath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    /// <summary>通道标识（本地执行组为 <c>local</c>）。</summary>
    public string ChannelTag { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public DateTime LastWriteTime { get; init; }

    /// <summary>给人看的体积文本，例如「312 KB」。</summary>
    public string SizeText => ExternalLogStore.FormatBytes(Bytes);
}

/// <summary>外部日志目录的占用概览。</summary>
public sealed class ExternalLogUsage
{
    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public string TotalText => ExternalLogStore.FormatBytes(TotalBytes);

    public List<ExternalLogFileInfo> Files { get; init; } = new();
}

/// <summary>
/// 外部脚本日志的**独立**落盘 —— 与雪乃酱自己的会话日志（<c>logs/YYYY-MM-DD.log</c>）完全分开。
///
/// <para>目录结构：<c>external_logs/&lt;通道标识&gt;/&lt;通道标识&gt;-YYYY-MM-DD.log</c>
/// —— 也就是「每个通道每天一个文件」。</para>
///
/// <para>为什么独立：会话日志是"雪乃酱做了什么"，外部日志是"脚本说了什么"，
/// 两者节奏差几个数量级（BetterGI 一天几千行）。混在一起会把调度信息淹掉。</para>
///
/// <para>为什么按通道分目录：多通道并行时不同通道可能在跑**同一个**脚本
/// （比如两条通道都跑 BetterGI），共用一个文件就会互相穿插、看不出哪行属于哪条通道。</para>
/// </summary>
public static class ExternalLogStore
{
    /// <summary>外部日志根目录名（程序根目录下）。</summary>
    public const string RootDirName = "external_logs";

    /// <summary>本地执行组的通道标识。</summary>
    public const string LocalChannelTag = "local";

    /// <summary>外部日志根目录：<c>&lt;BaseDir&gt;\external_logs</c>。</summary>
    public static string RootDir => Path.Combine(AppPaths.BaseDir, RootDirName);

    /// <summary>某通道的目录：<c>&lt;RootDir&gt;\&lt;通道标识&gt;</c>。</summary>
    public static string ChannelDir(string channelTag) =>
        Path.Combine(RootDir, SanitizeTag(channelTag));

    /// <summary>
    /// 某通道今天的文件：<c>&lt;通道目录&gt;\&lt;通道标识&gt;-YYYY-MM-DD.log</c>。
    /// 一天一份，跨天自动换文件。
    /// </summary>
    public static string TodayFilePath(string channelTag, DateTime now)
    {
        var tag = SanitizeTag(channelTag);
        return Path.Combine(ChannelDir(tag), tag + "-" + now.ToString("yyyy-MM-dd") + ".log");
    }

    /// <summary>
    /// 追加一行。UTF-8 无 BOM；文件不存在就建；**失败一律吞掉** ——
    /// 日志落盘绝不能影响任务执行。
    /// </summary>
    public static void Append(string filePath, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(filePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // 目录不可写 / 文件被占用 / 盘满 —— 都静默，主流程不受影响
        }
    }

    /// <summary>
    /// 统计外部日志的占用情况。
    ///
    /// 只认本功能自己的命名（<c>&lt;tag&gt;-YYYY-MM-DD.log</c>），
    /// 跟 <c>FileLogger.PruneOldSessions</c> 同样的三条纪律：只认命名、按写入时间排序、全程 try/catch。
    /// 乱删别的日志文件比不清理严重得多。
    /// </summary>
    public static ExternalLogUsage GetUsage()
    {
        var result = new List<ExternalLogFileInfo>();

        try
        {
            var root = new DirectoryInfo(RootDir);
            if (root.Exists)
            {
                foreach (var dir in root.EnumerateDirectories())
                {
                    foreach (var file in dir.EnumerateFiles("*.log"))
                    {
                        result.Add(new ExternalLogFileInfo
                        {
                            FilePath = file.FullName,
                            FileName = file.Name,
                            ChannelTag = dir.Name,
                            Bytes = file.Length,
                            LastWriteTime = file.LastWriteTime,
                        });
                    }
                }
            }
        }
        catch
        {
            // 读不到就当 0 条，界面显示"暂无"即可
        }

        result = result.OrderByDescending(f => f.LastWriteTime).ToList();

        return new ExternalLogUsage
        {
            FileCount = result.Count,
            TotalBytes = result.Sum(f => f.Bytes),
            Files = result,
        };
    }

    /// <summary>
    /// 删掉超出份数的日志文件。
    ///
    /// <para>**按通道各自计数**：保留 30 份 = 每条通道各留最近 30 天，
    /// 不是全部通道加起来 30 份 —— 否则通道一多，老通道的记录会先被挤掉。</para>
    ///
    /// <para>返回（删掉的文件数，释放的字节数）。失败不抛异常。</para>
    /// </summary>
    public static (int Deleted, long FreedBytes) Prune(int keepFiles)
    {
        var keep = Math.Max(1, keepFiles);
        var deleted = 0;
        var freed = 0L;

        try
        {
            var root = new DirectoryInfo(RootDir);
            if (!root.Exists)
            {
                return (0, 0);
            }

            foreach (var dir in root.EnumerateDirectories())
            {
                var sessions = new List<FileInfo>();
                foreach (var file in dir.EnumerateFiles("*.log"))
                {
                    sessions.Add(file);
                }

                if (sessions.Count <= keep)
                {
                    continue;
                }

                // 按最后写入时间排序，而不是按文件名：字典序与真实新旧不总是一致
                var stale = sessions
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(keep)
                    .ToList();

                foreach (var file in stale)
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deleted++;
                        freed += size;
                    }
                    catch
                    {
                        // 单个文件删不掉（被同步 / 占用）不影响其余清理
                    }
                }
            }
        }
        catch
        {
            // 整轮清理失败也不影响主流程
        }

        return (deleted, freed);
    }

    /// <summary>清空某个通道（或全部通道）的外部日志。返回（删掉文件数，释放字节数）。</summary>
    /// <param name="channelTag">传 null / 空 = 清空全部通道。</param>
    public static (int Deleted, long FreedBytes) Clear(string? channelTag)
    {
        var deleted = 0;
        var freed = 0L;

        try
        {
            var root = new DirectoryInfo(RootDir);
            if (!root.Exists)
            {
                return (0, 0);
            }

            var dirs = string.IsNullOrWhiteSpace(channelTag)
                ? root.EnumerateDirectories().ToList()
                : root.EnumerateDirectories().Where(d =>
                    string.Equals(d.Name, SanitizeTag(channelTag), StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var dir in dirs)
            {
                foreach (var file in dir.EnumerateFiles("*.log").ToList())
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deleted++;
                        freed += size;
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            }
        }
        catch
        {
            // 忽略
        }

        return (deleted, freed);
    }

    /// <summary>
    /// 把通道标识规整成合法的目录名片段。
    ///
    /// 通道 id 可能是 8 位十六进制（<c>5c24fe85</c>）、也可能是 <c>default</c>／中文名，
    /// 这里统一：去路径分隔符、折叠非法字符、截到 32 字符、空则回落 <c>default</c>。
    /// 目标只有一个 —— 绝不能让配置里的字符串拼出 <c>..\..</c> 这种越界路径。
    /// </summary>
    public static string SanitizeTag(string? channelTag)
    {
        var text = (channelTag ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return LocalChannelTag;
        }

        var builder = new StringBuilder(text.Length);
        var lastSeparator = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                builder.Append(ch);
                lastSeparator = false;
            }
            else if (!lastSeparator)
            {
                builder.Append('_');
                lastSeparator = true;
            }
        }

        var tag = builder.ToString().Trim('_', '.', ' ');
        if (tag.Length > 32)
        {
            tag = tag.Substring(0, 32).Trim('_', '.', ' ');
        }

        return tag.Length == 0 ? LocalChannelTag : tag;
    }

    /// <summary>字节数 → 给人看的体积文本。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{(long)size} {units[unit]}"
            : $"{size:0.0} {units[unit]}";
    }
}
