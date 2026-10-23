// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 外部日志行的解析 —— **全部是纯函数**，不碰文件、不碰 WinUI，
/// 这样整条逻辑能脱离界面直接跑断言。
///
/// 级别（<see cref="Classify"/>）只用于**标注**（界面上区分普通行 / 告警 / 错误），
/// 不作为丢弃依据 —— 日志一律**全部保留**。
/// </summary>
public static class ExternalLogFilter
{
    /// <summary>ANSI 转义序列（颜色码 / 光标控制），日志里常见但显示出来是一坨乱码。</summary>
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[a-zA-Z]", RegexOptions.Compiled);

    /// <summary>连续空白（不含换行）压缩成单个空格。</summary>
    private static readonly Regex WhitespaceRun = new(@"[\t ]{2,}", RegexOptions.Compiled);

    /// <summary>控制字符（除制表符外）一律去掉。</summary>
    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

    private static readonly string[] ErrorTokens =
    {
        "ERROR", "ERR", "FATAL", "CRITICAL", "PANIC", "EXCEPTION", "UNHANDLED",
        "失败", "错误", "异常", "崩溃", "致命",
    };

    private static readonly string[] WarnTokens =
    {
        "WARN", "WARNING", "WRN", "警告", "重试", "超时",
    };

    private static readonly string[] InfoTokens =
    {
        "INFO", "INF", "INFORMATION", "信息", "提示",
    };

    private static readonly string[] DebugTokens =
    {
        "DEBUG", "DBG", "调试",
    };

    private static readonly string[] TraceTokens =
    {
        "TRACE", "TRC", "VERBOSE",
    };

    /// <summary>
    /// 级别字段的提取器 —— 只在**真正的级别字段**里认级别。
    ///
    /// 为什么必须先剥离：三种日志的时间戳含 - : , 等字符，
    /// 直接全行搜 "error" 会把正文里的普通词误判成错误
    /// （比如「正在处理 error 模块」被标成 [错误]，界面全是红的）。
    ///
    /// 三种写法：
    ///   [INF] / [ERROR]       —— BetterGI、MaaEnd
    ///   | INFO |              —— March7thAssistant
    ///   时间戳后紧跟大写级别    —— MaaEnd 的 "2026-10-16 12:26:08 INFO"
    /// </summary>
    private static readonly Regex LevelField = new(
        @"^\s*(?:\[?\d{4}-\d{2}-\d{2}\]?[ T]\[?\d{2}:\d{2}:\d{2}(?:[.,]\d{1,6})?\]?\s*)?" +
        @"(?:\[(?<b>[A-Z]{3,11})\]|\|\s*(?<v>[A-Z]{3,11})\s*\||(?<t>\b[A-Z]{3,11}\b))",
        RegexOptions.Compiled);

    /// <summary>
    /// 把原始行规整成能安全落盘 / 展示的形态。
    ///
    /// 顺序不能换：先去转义 -> 再去控制字符 -> 再压空白 -> 最后截断。
    /// 若先压空白，夹在中间的控制字符会挡住压缩，留下大段空白。
    /// </summary>
    public static string NormalizeLine(string? raw)
    {
        var text = (raw ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = AnsiEscape.Replace(text, string.Empty);
        text = ControlChars.Replace(text, string.Empty);
        text = WhitespaceRun.Replace(text, " ");
        text = text.Trim();

        return text.Length <= ExternalLogDefaults.MaxLineLength
            ? text
            : text.Substring(0, ExternalLogDefaults.MaxLineLength) + "\u2026";
    }

    /// <summary>
    /// 识别行级别，仅用于界面标注。
    ///
    /// 只在**级别字段**里比对，正文里的 "error" / "超时" 不会被误判。
    /// </summary>
    public static ExternalLogLevel Classify(string? raw)
    {
        var text = raw ?? string.Empty;
        if (text.Length == 0)
        {
            return ExternalLogLevel.Unknown;
        }

        // 三种格式的时间戳 + 级别都落在前 60 字符内，再往后是正文
        var head = text.Length > 60 ? text.Substring(0, 60) : text;

        // ① [BRACKET] 写法（BetterGI / MaaEnd）
        var bracket = Regex.Match(head, @"\[(?<v>[A-Z]{3,11})\]");
        if (bracket.Success)
        {
            return MatchLevel(bracket.Groups["v"].Value);
        }

        // ② | PIPE | 写法（March7thAssistant）
        var pipe = Regex.Match(head, @"\|\s*(?<v>[A-Za-z]{3,11})\s*\|");
        if (pipe.Success)
        {
            return MatchLevel(pipe.Groups["v"].Value);
        }

        // ③ 时间戳后紧跟独立大写词（MaaEnd 的另一种写法）
        var bare = Regex.Match(
            head,
            @"^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\s+(?<v>[A-Z]{3,11})\b");
        if (bare.Success)
        {
            return MatchLevel(bare.Groups["v"].Value);
        }

        return ExternalLogLevel.Unknown;
    }

    /// <summary>是否命中用户配置的关键字（全行不区分大小写匹配）。</summary>
    public static bool MatchesKeyword(string? raw, IReadOnlyList<string>? keywords)
    {
        var text = raw ?? string.Empty;
        if (text.Length == 0 || keywords is null || keywords.Count == 0)
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            var needle = (keyword ?? string.Empty).Trim();
            if (needle.Length == 0)
        {
            continue;
        }

            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把原始行解析成结构化的一行（规整 + 级别 + 关键字）。</summary>
    public static ExternalLogLine Parse(string? raw, IReadOnlyList<string>? keywords)
        => Parse(raw, keywords, wasTruncated: false);

    /// <summary>
    /// 把已在原始字节层被截断的行标出，保留行首以维持级别识别。
    /// </summary>
    public static ExternalLogLine Parse(string? raw, IReadOnlyList<string>? keywords, bool wasTruncated)
    {
        var text = NormalizeLine(raw);
        if (wasTruncated && text.Length > 0)
        {
            const string suffix = " [日志行原始字节超限，已截断]";
            var keep = Math.Max(0, ExternalLogDefaults.MaxLineLength - suffix.Length);
            text = text.Length <= keep ? text + suffix : text.Substring(0, keep) + suffix;
        }

        return new ExternalLogLine
        {
            Text = text,
            Level = Classify(text),
            KeywordHit = MatchesKeyword(text, keywords),
        };
    }

    private static ExternalLogLevel MatchLevel(string field)
    {
        if (ContainsAny(field, ErrorTokens))
        {
            return ExternalLogLevel.Error;
        }

        if (ContainsAny(field, WarnTokens))
        {
            return ExternalLogLevel.Warn;
        }

        if (ContainsAny(field, InfoTokens))
        {
            return ExternalLogLevel.Info;
        }

        if (ContainsAny(field, DebugTokens))
        {
            return ExternalLogLevel.Debug;
        }

        if (ContainsAny(field, TraceTokens))
        {
            return ExternalLogLevel.Trace;
        }

        return ExternalLogLevel.Unknown;
    }

    private static bool ContainsAny(string text, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
