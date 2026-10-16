// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using YukinoChan.Helpers;

namespace YukinoChan.Services;

/// <summary>
/// 按程序启动批次写日志：logs/2026-05-26.log、logs/2026-05-26(2).log。
/// </summary>
public sealed class FileLogger
{
    private readonly object _lock = new();

    public FileLogger(string logDir)
    {
        LogDir = logDir;
        Directory.CreateDirectory(LogDir);
        PruneOldSessions();
        SessionLabel = AllocateSessionLabel();
        LogPath = Path.Combine(LogDir, SessionLabel + ".log");
    }

    public string LogDir { get; }

    public string LogPath { get; }

    public string SessionLabel { get; }

    public string MakeLine(string message) => $"[{FormatHelper.NowTimeText()}] {message}";

    /// <summary>写入一行并返回格式化后的内容。</summary>
    public string Log(string message)
    {
        var line = MakeLine(message);
        WriteLine(line);
        return line;
    }

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogPath, line.TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 日志写入失败不应影响主流程
            }
        }
    }

    /// <summary>构造 session 标记，用于日志总结器隔离不同脚本的日志段。</summary>
    public static string BuildSessionMarker(string taskName, string markerType) =>
        $"===== SESSION {markerType.Trim().ToUpperInvariant()} : {taskName} =====";

    /// <summary>会话日志文件名：<c>2026-10-09.log</c> 与同日的 <c>2026-10-09(2).log</c>。</summary>
    private static readonly Regex SessionLogName = new(@"^\d{4}-\d{2}-\d{2}(\(\d+\))?\.log$", RegexOptions.IgnoreCase);

    /// <summary>保留最近多少份会话日志。日志的价值全在"最近几次"，更早的留着只是占盘。</summary>
    private const int MaxSessionLogs = 20;

    /// <summary>
    /// 删掉超出份数的<b>会话日志</b>，避免 logs/ 只增不减。
    ///
    /// 三条纪律（碰坏任意一条都可能删掉不该删的东西）：
    ///   ① <b>只认会话日志的命名</b>（<c>YYYY-MM-DD.log</c> / <c>YYYY-MM-DD(N).log</c>）。
    ///      <c>crash.log</c> 不在这个模式里 —— 它由 <c>CrashLog</c> 自己按 4MB 轮转，
    ///      <c>last_run.state</c>、<c>agent_boot.log</c> 同理，这里一律不碰。
    ///   ② <b>按最后写入时间排序</b>，不是按文件名：同一天多次启动会生成 (2)(3)(4)…
    ///      文件名字典序与真实新旧不总是一致。
    ///   ③ <b>只在启动时做一次</b>，且全程 try/catch —— 清理失败绝不能影响记日志本身；
    ///      日志目录在 OneDrive 下，被同步占用而删不掉是常态，静默跳过即可。
    /// </summary>
    private void PruneOldSessions()
    {
        try
        {
            var dir = new DirectoryInfo(LogDir);
            if (!dir.Exists)
            {
                return;
            }

            var sessions = new List<FileInfo>();
            foreach (var file in dir.EnumerateFiles("*.log"))
            {
                if (SessionLogName.IsMatch(file.Name))
                {
                    sessions.Add(file);
                }
            }

            if (sessions.Count <= MaxSessionLogs)
            {
                return;
            }

            var stale = sessions
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxSessionLogs)
                .ToList();

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // 单个文件删不掉（被同步/占用）不影响其余清理
                }
            }
        }
        catch
        {
            // 清理失败绝不能影响记日志
        }
    }

    /// <summary>构造 session 标记，用于日志总结器隔离不同脚本的日志段。</summary>
    private string AllocateSessionLabel()
    {
        var baseName = FormatHelper.TodayText();
        var first = Path.Combine(LogDir, baseName + ".log");
        if (!File.Exists(first))
        {
            return baseName;
        }

        var index = 2;
        while (true)
        {
            var candidate = $"{baseName}({index})";
            if (!File.Exists(Path.Combine(LogDir, candidate + ".log")))
            {
                return candidate;
            }

            index++;
        }
    }
}
