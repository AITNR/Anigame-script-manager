// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;
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
