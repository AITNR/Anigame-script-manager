// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// 构造时建立文件 baseline；每个文件保留独立字节位置与未完成行。
/// 所有读取与释放串行化，日志文件可以在脚本启动后才出现。
/// </summary>
public sealed class ExternalLogTail : IDisposable
{
    private sealed class Cursor
    {
        public long Offset;
        public long Creation;
        public byte[] Anchor = Array.Empty<byte>();
        public readonly List<byte> Pending = new();
        public Encoding? BomEncoding;
        public bool HeaderChecked;
    }

    private readonly object _sync = new();
    private readonly string _rootPath;
    private readonly string _pattern;
    private readonly string _encodingMode;
    private readonly Dictionary<string, Cursor> _files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private string? _currentFile;
    private string _lastError = string.Empty;
    private bool _disposed;

    static ExternalLogTail() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public ExternalLogTail(string path, string? pattern, string? encodingName, bool followTail,
        string? baseDirectory = null)
    {
        var expanded = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim());
        _rootPath = expanded.Length == 0 ? string.Empty : Path.GetFullPath(expanded,
            Path.GetFullPath(baseDirectory ?? Environment.CurrentDirectory));
        _pattern = ExternalLogDefaults.NormalizePattern(pattern);
        _encodingMode = ExternalLogEncodings.Normalize(encodingName);
        try
        {
            foreach (var file in Candidates())
            {
                using var stream = Open(file);
                var cursor = new Cursor { Creation = File.GetCreationTimeUtc(file).Ticks };
                cursor.Offset = followTail ? stream.Length : 0;
                cursor.Anchor = ReadAnchor(stream, cursor.Offset);
                DetectExistingBom(stream, cursor);
                _files[file] = cursor;
            }
        }
        catch (Exception ex) { SetError(ex); }
    }

    public string? CurrentFile { get { lock (_sync) { return _currentFile; } } }
    /// <summary>最近一次诊断；成功读取后清空。由 worker 在变化/恢复时报告。</summary>
    public string LastError { get { lock (_sync) { return _lastError; } } }

    public IReadOnlyList<ExternalLogLine> Poll(IReadOnlyList<string>? keywords) => Read(keywords, false);

    /// <summary>停止时读取最后新增字节并发送最后一条未换行文本；应在 Dispose 前调用。</summary>
    public IReadOnlyList<ExternalLogLine> Drain(IReadOnlyList<string>? keywords) => Read(keywords, true);

    private IReadOnlyList<ExternalLogLine> Read(IReadOnlyList<string>? keywords, bool final)
    {
        lock (_sync)
        {
            var result = new List<ExternalLogLine>();
            if (_disposed) { return result; }
            try
            {
                var candidates = Candidates().OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                if (candidates.Count == 0)
                {
                    _lastError = $"等待日志文件：{_rootPath}（匹配 {_pattern}）";
                }
                else
                {
                    _currentFile = candidates[0];
                    // 先抽取旧目标末尾，轮转后新文件第一行也从头读取。
                    foreach (var file in _files.Keys.Concat(new[] { _currentFile })
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                    {
                        if (File.Exists(file)) { ReadFile(file, result, keywords, final); }
                    }
                    _lastError = string.Empty;
                }
                if (final)
                {
                    foreach (var cursor in _files.Values) { ExtractLines(cursor, result, keywords, true); }
                }
            }
            catch (Exception ex) { SetError(ex); }
            return result;
        }
    }

    private IEnumerable<string> Candidates()
    {
        if (_rootPath.Length == 0) { throw new IOException("日志路径为空"); }
        // GetAttributes 保留权限错误；File.Exists/Directory.Exists 会把它们掩盖为不存在。
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_rootPath); }
        catch (FileNotFoundException) { return Array.Empty<string>(); }
        catch (DirectoryNotFoundException) { throw new DirectoryNotFoundException($"日志目录不存在：{_rootPath}"); }
        return (attributes & FileAttributes.Directory) != 0
            ? Directory.GetFiles(_rootPath, _pattern)
            : new[] { _rootPath };
    }

    private void ReadFile(string path, List<ExternalLogLine> result, IReadOnlyList<string>? keywords, bool final)
    {
        using var stream = Open(path);
        var creation = File.GetCreationTimeUtc(path).Ticks;
        if (!_files.TryGetValue(path, out var cursor))
        {
            cursor = new Cursor { Creation = creation };
            _files[path] = cursor;
        }
        else if (creation != cursor.Creation || stream.Length < cursor.Offset ||
                 !ReadAnchor(stream, cursor.Offset).SequenceEqual(cursor.Anchor))
        {
            // baseline 之后的截断/重建也必须从零开始，不能 Math.Min(baseline, length)。
            cursor = new Cursor { Creation = creation };
            _files[path] = cursor;
        }

        stream.Position = cursor.Offset;
        var end = stream.Length; // 本拍快照，持续写入不应阻塞取消。
        var buffer = new byte[4096];
        while (stream.Position < end)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - stream.Position));
            if (read == 0) { break; }
            cursor.Offset += read;
            for (var i = 0; i < read; i++) { cursor.Pending.Add(buffer[i]); }
            ExtractLines(cursor, result, keywords, false);
        }
        cursor.Anchor = ReadAnchor(stream, cursor.Offset);
        if (final) { ExtractLines(cursor, result, keywords, true); }
    }

    private void ExtractLines(Cursor cursor, List<ExternalLogLine> result,
        IReadOnlyList<string>? keywords, bool final)
    {
        if (!cursor.HeaderChecked)
        {
            // 跨 Poll 的 BOM 和多字节字符暂存原始字节，完整行到达后才解码。
            if (!final && cursor.Pending.Count < 4 && !cursor.Pending.Contains((byte)'\n')) { return; }
            var bytes = cursor.Pending.ToArray();
            var bomLength = FindBom(bytes, out var encoding);
            cursor.BomEncoding = encoding;
            cursor.HeaderChecked = true;
            if (bomLength > 0) { cursor.Pending.RemoveRange(0, bomLength); }
        }
        var width = cursor.BomEncoding?.CodePage is 1200 or 1201 ? 2 :
            cursor.BomEncoding?.CodePage is 12000 or 12001 ? 4 : 1;
        var bigEndian = cursor.BomEncoding?.CodePage is 1201 or 12001;
        var start = 0;
        for (var i = 0; i + width <= cursor.Pending.Count; i += width)
        {
            var lf = true;
            for (var b = 0; b < width; b++)
            {
                var expected = b == (bigEndian ? width - 1 : 0) ? 10 : 0;
                if (cursor.Pending[i + b] != expected) { lf = false; break; }
            }
            if (!lf) { continue; }
            AddLine(cursor, cursor.Pending.GetRange(start, i - start).ToArray(), result, keywords);
            start = i + width;
        }
        if (start > 0) { cursor.Pending.RemoveRange(0, start); }
        if (final && cursor.Pending.Count > 0)
        {
            AddLine(cursor, cursor.Pending.ToArray(), result, keywords);
            cursor.Pending.Clear();
        }
    }

    private void AddLine(Cursor cursor, byte[] bytes, List<ExternalLogLine> result, IReadOnlyList<string>? keywords)
    {
        string text;
        if (cursor.BomEncoding is not null) { text = cursor.BomEncoding.GetString(bytes); }
        else if (_encodingMode == ExternalLogEncodings.Gbk) { text = Encoding.GetEncoding(936).GetString(bytes); }
        else if (_encodingMode == ExternalLogEncodings.Utf8) { text = Encoding.UTF8.GetString(bytes); }
        else
        {
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding(936).GetString(bytes); }
        }
        var line = ExternalLogFilter.Parse(text.TrimEnd('\r'), keywords);
        if (line.Text.Length > 0) { result.Add(line); }
    }

    private static void DetectExistingBom(FileStream stream, Cursor cursor)
    {
        var prefix = new byte[(int)Math.Min(4, stream.Length)];
        stream.Position = 0;
        var read = stream.Read(prefix, 0, prefix.Length);
        if (read != prefix.Length) { Array.Resize(ref prefix, read); }
        FindBom(prefix, out var encoding);
        cursor.BomEncoding = encoding;
        cursor.HeaderChecked = cursor.Offset > 0;
    }

    private static int FindBom(byte[] b, out Encoding? encoding)
    {
        encoding = null;
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
        { encoding = new UTF32Encoding(false, false); return 4; }
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
        { encoding = new UTF32Encoding(true, false); return 4; }
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        { encoding = Encoding.UTF8; return 3; }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        { encoding = Encoding.Unicode; return 2; }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        { encoding = Encoding.BigEndianUnicode; return 2; }
        return 0;
    }

    private static byte[] ReadAnchor(FileStream stream, long offset)
    {
        if (offset > stream.Length) { return Array.Empty<byte>(); }
        var bytes = new byte[(int)Math.Min(64, offset)];
        stream.Position = offset - bytes.Length;
        var read = stream.Read(bytes, 0, bytes.Length);
        if (read != bytes.Length) { Array.Resize(ref bytes, read); }
        return bytes;
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private void SetError(Exception ex) => _lastError =
        $"日志读取失败：{_rootPath}：{ex.Message.Replace("异常", "错误", StringComparison.Ordinal)}";

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _files.Clear(); // FileStream 只在每拍读取期间持有，均由 using 释放。
        }
    }
}
