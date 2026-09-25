// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YukinoChan.Services;

/// <summary>
/// 进程 / 窗口探测与清理。
/// 对应 Python 版 ScriptRunnerWorker 中的 tasklist / EnumWindows / taskkill 逻辑。
/// </summary>
public static class ProcessHelper
{
    /// <summary>把用户填写的进程名转成 taskkill /IM 可尝试的名称。</summary>
    public static List<string> ProcessNameCandidates(string? raw)
    {
        var result = new List<string>();
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return result;
        }

        var name = Path.GetFileName(text);
        AddUnique(result, name);
        if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            AddUnique(result, name + ".exe");
        }

        return result;
    }

    /// <summary>
    /// 按 Windows 镜像名检测进程是否在运行。
    /// 优先用托管 API（快、不受系统语言 / 代码页影响），失败再回落到 tasklist（与 Python 版一致）。
    /// 名称可以不带 .exe，也可以写成路径，内部会展开候选名。
    /// </summary>
    public static bool IsProcessNameRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (var candidate in ProcessNameCandidates(processName))
        {
            if (IsExactImageRunning(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>单个镜像名（可带 .exe）的检测：托管 API 优先，tasklist 兜底。</summary>
    private static bool IsExactImageRunning(string imageName)
    {
        var bare = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? imageName[..^4]
            : imageName;
        if (bare.Length == 0)
        {
            return false;
        }

        try
        {
            if (Process.GetProcessesByName(bare).Length > 0)
            {
                return true;
            }
        }
        catch
        {
            // 某些受保护进程枚举会抛异常，落到 tasklist 兜底
        }

        return TasklistHasImage(imageName);
    }

    /// <summary>tasklist /FI 兜底检测（对应 Python 版实现）。</summary>
    private static bool TasklistHasImage(string imageName)
    {
        try
        {
            var startInfo = new ProcessStartInfo("tasklist")
            {
                Arguments = $"/FI \"IMAGENAME eq {imageName}\" /NH",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.StandardOutputEncoding = Encoding.Default;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(true);
            }

            var lower = output.ToLowerInvariant();
            if (lower.Contains("没有运行的任务") || lower.Contains("no tasks"))
            {
                return false;
            }

            // tasklist 命中时会回显镜像名（形如 smoke.exe  1234 Console ...）
            var bare = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? imageName[..^4]
                : imageName;
            return lower.Contains(bare.ToLowerInvariant());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>兜底：按进程名 / 主窗口标题包含关键词检测。</summary>
    public static bool IsProcessKeywordPresent(string keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (IsProcessNameRunning(text))
        {
            return true;
        }

        try
        {
            var lower = text.ToLowerInvariant();
            var currentPid = Environment.ProcessId;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentPid)
                    {
                        continue;
                    }

                    var name = process.ProcessName;
                    if (name.ToLowerInvariant().Contains(lower))
                    {
                        return true;
                    }

                    var title = process.MainWindowTitle;
                    if (!string.IsNullOrEmpty(title) && title.ToLowerInvariant().Contains(lower))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 进程已退出或拒绝访问，跳过
                }
            }
        }
        catch
        {
            // 忽略
        }

        return false;
    }

    /// <summary>按命令行关键词检测（PowerShell / CIM，沿用 Python 版实现）。</summary>
    public static bool IsCommandlineKeywordRunning(string keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var safe = text.Replace("'", "''");
        var ps = "Get-CimInstance Win32_Process | "
                 + $"Where-Object {{$_.CommandLine -like '*{safe}*'}} | "
                 + "Select-Object -First 1 -ExpandProperty ProcessId";

        try
        {
            var startInfo = new ProcessStartInfo("powershell")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command {ps}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                process.Kill(true);
            }

            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否存在包含指定关键词的可见窗口。</summary>
    public static bool IsWindowTitlePresent(string keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        foreach (var title in VisibleWindowTitles())
        {
            if (title.ToLowerInvariant().Contains(lower))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>按窗口标题关键词收集窗口所属 PID。</summary>
    public static List<int> WindowPidsByKeyword(string keyword)
    {
        var result = new List<int>();
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return result;
        }

        var lower = text.ToLowerInvariant();
        var pids = new List<int>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (title is null || !title.ToLowerInvariant().Contains(lower))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0 && !pids.Contains((int)pid))
            {
                pids.Add((int)pid);
            }

            return true;
        }, IntPtr.Zero);

        return pids;
    }

    /// <summary>枚举所有可见窗口标题。</summary>
    public static List<string> VisibleWindowTitles()
    {
        var titles = new List<string>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (title is not null)
            {
                titles.Add(title);
            }

            return true;
        }, IntPtr.Zero);

        return titles;
    }

    /// <summary>按镜像名结束进程树：taskkill /IM /T /F。</summary>
    public static void TerminateProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        RunHidden("taskkill", $"/IM \"{processName}\" /T /F", 10000);
    }

    /// <summary>按 PID 结束进程树：taskkill /PID /T /F。</summary>
    public static void TerminatePidTree(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        RunHidden("taskkill", $"/PID {pid} /T /F", 10000);
    }

    /// <summary>向当前前台窗口发送一次 Enter（仅用于用户明确确认过的启动确认框）。</summary>
    public static void PressEnter()
    {
        NativeMethods.keybd_event(NativeMethods.VkReturn, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VkReturn, 0, NativeMethods.KeyEventFKeyUp, UIntPtr.Zero);
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowTextW(hwnd, builder, builder.Capacity);
        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static void RunHidden(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(true);
            }
        }
        catch
        {
            // taskkill 对不存在的进程会返回非零退出码，忽略即可
        }
    }

    private static void AddUnique(List<string> target, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        foreach (var item in target)
        {
            if (string.Equals(item, text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        target.Add(text);
    }
}
