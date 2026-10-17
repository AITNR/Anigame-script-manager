// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// 外部脚本实时日志的跟踪器 —— 只读、尽力而为、**一行不丢**。
///
/// 为什么要它：BetterGI / March7thAssistant / MaaEnd 跑起来之后，
/// 雪乃酱只能看到"进程在 / 进程没了"，中间几十分钟完全黑盒。它们自己都写日志文件，
/// 跟住文件尾部就能拿到进度。
///
/// 三条关键设计：
///   ① 用 FileShare.ReadWrite 打开 —— 脚本进程不独占句柄（实测三个脚本均可共享打开）。
///   ② 每次 Poll 都重新选文件：按天滚动的软件跨天换文件也能自动跟上。
///   ③ 文件被清空重写时**重新打开**流 —— 仅回退位置无效：
///      StreamReader 内部会记住已到 EOF，DiscardBufferedData() 清不掉，
///      ReadLine() 仍返回 null。必须 Close() 后重开。
///
/// 任何一步失败都只返回空列表，**绝不让跟踪问题影响任务执行**。
/// </summary>
public sealed class ExternalLogTail : IDisposable
{
    private readonly string _rootPath;
    private readonly string _pattern;
    private readonly Encoding _encoding;
    private readonly bool _followTail;

    private FileStream? _stream;
    private StreamReader? _reader;
    private string? _currentFile;
    private long _position;
    private long _currentWriteTimeUtc;
    private long _currentCreationTimeUtc;
    private bool _disposed;

    /// <param name="path">日志文件路径，或日志所在目录。支持环境变量。</param>
    /// <param name="pattern">路径是目录时的文件名匹配模式。</param>
    /// <param name="encodingName">编码取值，见 ExternalLogEncodings。</param>
    /// <param name="followTail">是否从当前末尾开始跟。</param>
    public ExternalLogTail(string path, string? pattern, string? encodingName, bool followTail)
    {
        _rootPath = (path ?? string.Empty).Trim();
        _pattern = ExternalLogDefaults.NormalizePattern(pattern);
        _encoding = ResolveEncoding(ExternalLogEncodings.Normalize(encodingName));
        _followTail = followTail;
    }

    /// <summary>当前正在跟的文件（还没选中则为 null）。</summary>
    public string? CurrentFile => _currentFile;

    /// <summary>读出新增的完整行（**全部返回，不抽样**）。出错返回空列表。</summary>
    public IReadOnlyList<ExternalLogLine> Poll(IReadOnlyList<string>? keywords)
    {
        if (_disposed || _rootPath.Length == 0)
        {
            return Array.Empty<ExternalLogLine>();
        }

        try
        {
            var target = SelectTargetFile();
            if (target is null)
            {
                return Array.Empty<ExternalLogLine>();
            }

            if (!EnsureOpen(target))
            {
                return Array.Empty<ExternalLogLine>();
            }

            return ReadNewLines(keywords);
        }
        catch
        {
            return Array.Empty<ExternalLogLine>();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    // ---------------- 文件选择 ----------------

    /// <summary>
    /// 选出要跟的那一份文件：
    ///   路径是文件 -> 直接跟它；
    ///   路径是目录 -> 按模式匹配，取最近修改的那一份。
    /// 每次 Poll 都重算，跨天滚动的软件换文件也能自动跟上。
    /// </summary>
    private string? SelectTargetFile()
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(_rootPath);
            if (File.Exists(expanded))
            {
                return expanded;
            }

            if (!Directory.Exists(expanded))
            {
                return null;
            }

            var candidates = Directory.EnumerateFiles(expanded, _pattern).ToList();
            if (candidates.Count == 0)
        {
                return null;
        }

            return candidates
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .First();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>保证 target 已打开；换文件了就关旧的重新开。</summary>
    private bool EnsureOpen(string target)
    {
        if (_stream is not null && _reader is not null &&
            string.Equals(_currentFile, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Close();

        try
        {
            _stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _reader = new StreamReader(_stream, _encoding, detectEncodingFromByteOrderMarks: true);
            _currentFile = target;
            _position = _followTail ? _stream.Length : 0;
            _stream.Position = _position;
            _currentWriteTimeUtc = File.GetLastWriteTimeUtc(target).ToFileTimeUtc();
            _currentCreationTimeUtc = File.GetCreationTimeUtc(target).ToFileTimeUtc();
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    // ---------------- 读取 ----------------

    private IReadOnlyList<ExternalLogLine> ReadNewLines(IReadOnlyList<string>? keywords)
    {
        var results = new List<ExternalLogLine>();
        if (_stream is null || _reader is null)
        {
            return results;
        }

        // 文件被清空重写 -> 回到开头重读。
        //
        // 三个判据任一成立即视为“被重写”：
        //   ① 长度变短       —— 清空后从头写（最常见）
        //   ② 写入时间倒退   —— 被更旧内容覆盖（如备份还原）
        //   ③ 创建时间变化   —— 文件被删除重建
        //
        // 为什么要有 ③：File.WriteAllText 是“截断 + 写入”，若重写后字节数与重写前
        // 恰好相同，则长度不变短、写入时间只前进，①② 都察觉不到 —— ③ 补上这个缺口。
        if (_currentFile is { } tracked)
        {
            var writeTime = SafeWriteTimeUtc(tracked);
            var creationTime = SafeCreationTimeUtc(tracked);

            var rewritten = _stream.Length < _position
                            || writeTime < _currentWriteTimeUtc
                            || creationTime != _currentCreationTimeUtc;

            if (rewritten)
            {
                // 必须重新打开流与读取器：只回退 _stream.Position 无效 ——
                // StreamReader 内部会记住“已到 EOF”，DiscardBufferedData() 清不掉，
                // 之后 ReadLine() 一律返回 null（实测确认，见 docs/ 计划书备注）。
                var path = tracked;
                Close();

                try
                {
                    _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    _reader = new StreamReader(_stream, _encoding, detectEncodingFromByteOrderMarks: true);
                    _currentFile = path;
                    _position = 0;
                    _stream.Position = 0;
                }
                catch
                {
                    Close();
                    return results;
                }
            }

            _currentWriteTimeUtc = writeTime;
            _currentCreationTimeUtc = creationTime;
        }

        while (_reader.ReadLine() is { } raw)
        {
            var line = ExternalLogFilter.Parse(raw, keywords);
            if (line.Text.Length > 0)
            {
                results.Add(line);
            }
        }

        _position = _stream?.Length ?? _position;
        return results;
    }
    private void Close()
    {
        try
        {
            _reader?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _reader = null;
        _stream = null;
        _currentFile = null;
        _position = 0;
        _currentWriteTimeUtc = 0;
        _currentCreationTimeUtc = 0;
    }

    private static long SafeWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).ToFileTimeUtc(); } catch { return 0; }
    }

    private static long SafeCreationTimeUtc(string path)
    {
        try { return File.GetCreationTimeUtc(path).ToFileTimeUtc(); } catch { return 0; }
    }

    // ---------------- 编码 ----------------

    private static Encoding ResolveEncoding(string mode) => mode switch
    {
        ExternalLogEncodings.Utf8 => new UTF8Encoding(false),
        ExternalLogEncodings.Gbk => Encoding.GetEncoding("GBK"),
        _ => Encoding.Default,
    };
}
