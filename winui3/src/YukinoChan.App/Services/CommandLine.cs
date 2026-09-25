// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace YukinoChan.Services;

/// <summary>Windows 风格命令行拆分（等价于 Python shlex.split(text, posix=False)）。</summary>
public static class CommandLine
{
    public static List<string> Split(string commandText)
    {
        var text = (commandText ?? string.Empty).Trim();
        var result = new List<string>();
        if (text.Length == 0)
        {
            return result;
        }

        var ptr = NativeMethods.CommandLineToArgvW(text, out var count);
        if (ptr == IntPtr.Zero)
        {
            // 兜底：按空格粗暴拆分
            foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(part.Trim('"'));
            }

            return result;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size));
                result.Add((item ?? string.Empty).Trim('"'));
            }
        }
        finally
        {
            NativeMethods.LocalFree(ptr);
        }

        return result;
    }

    /// <summary>判断参数栏写的是“完整命令”还是单纯参数。</summary>
    public static bool LooksLikeFullCommand(string commandText)
    {
        var text = (commandText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var parts = Split(text);
        if (parts.Count == 0)
        {
            return false;
        }

        var first = parts[0].Trim('"');
        var lower = first.ToLowerInvariant();
        return lower.StartsWith(".\\", StringComparison.Ordinal)
               || lower.StartsWith("./", StringComparison.Ordinal)
               || lower.EndsWith(".exe", StringComparison.Ordinal)
               || lower.EndsWith(".bat", StringComparison.Ordinal)
               || lower.EndsWith(".cmd", StringComparison.Ordinal)
               || lower.EndsWith(".py", StringComparison.Ordinal)
               || Path.IsPathRooted(first);
    }

    /// <summary>把完整命令文本解析为 (fileName, arguments)。</summary>
    public static (string FileName, string Arguments) BuildFullCommand(string commandText, string workingDirectory)
    {
        var parts = Split(commandText);
        if (parts.Count == 0)
        {
            throw new ArgumentException("完整命令为空", nameof(commandText));
        }

        var exePath = parts[0].Trim('"');
        if (!Path.IsPathRooted(exePath))
        {
            exePath = Path.GetFullPath(Path.Combine(workingDirectory, exePath));
        }

        var rest = parts.Count > 1 ? parts.GetRange(1, parts.Count - 1) : new List<string>();
        var suffix = Path.GetExtension(exePath).ToLowerInvariant();

        if (suffix is ".bat" or ".cmd")
        {
            return ("cmd.exe", QuoteJoin(new[] { "/d", "/c", exePath }, rest));
        }

        if (suffix == ".py")
        {
            return (ResolvePython(), QuoteJoin(new[] { exePath }, rest));
        }

        return (exePath, QuoteJoin(Array.Empty<string>(), rest));
    }

    /// <summary>按任务配置与脚本后缀构造启动命令。</summary>
    public static (string FileName, string Arguments) BuildTaskCommand(string scriptPath, bool useArgs, string args)
    {
        var suffix = Path.GetExtension(scriptPath).ToLowerInvariant();
        var argText = useArgs ? (args ?? string.Empty).Trim() : string.Empty;
        var workingDirectory = Path.GetDirectoryName(scriptPath) ?? AppPaths.BaseDir;

        if (argText.Length > 0 && LooksLikeFullCommand(argText))
        {
            return BuildFullCommand(argText, workingDirectory);
        }

        var extra = argText.Length > 0 ? Split(argText) : new List<string>();

        switch (suffix)
        {
            case ".bat":
            case ".cmd":
                return ("cmd.exe", QuoteJoin(new[] { "/d", "/c", scriptPath }, extra));
            case ".py":
                return (ResolvePython(), QuoteJoin(new[] { scriptPath }, extra));
            default:
                return (scriptPath, QuoteJoin(Array.Empty<string>(), extra));
        }
    }

    public static string ResolvePython()
    {
        var configured = Environment.GetEnvironmentVariable("AUTO_DAILY_PYTHON");
        return string.IsNullOrWhiteSpace(configured) ? "python" : configured;
    }

    private static string QuoteJoin(IReadOnlyList<string> head, IReadOnlyList<string> tail)
    {
        var parts = new List<string>();
        foreach (var item in head)
        {
            parts.Add(item);
        }

        foreach (var item in tail)
        {
            parts.Add(item);
        }

        // 使用 Windows 引号规则拼装，保证带空格的路径不会被拆开
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            AppendArgument(builder, parts[i]);
        }

        return builder.ToString();
    }

    private static void AppendArgument(System.Text.StringBuilder builder, string argument)
    {
        if (argument.Length == 0)
        {
            builder.Append("\"\"");
            return;
        }

        var needsQuote = argument.Contains(' ') || argument.Contains('\t') || argument.Contains('"');
        if (!needsQuote)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        builder.Append('"');
    }
}
