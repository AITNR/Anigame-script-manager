// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YukinoChan.Services;

/// <summary>
/// 一次"结束进程"尝试的结果。
///
/// 为什么要它：原来 <c>taskkill</c> 是"发了就算完"，输出与退出码全丢，
/// 日志里只留下"正在结束 X"。于是**没人能判断到底关掉没有** ——
/// 用户对着还在跑的游戏，只能猜是没杀掉还是画面没刷新（真机踩过）。
/// </summary>
public sealed class ProcessTerminateResult
{
    public ProcessTerminateResult(bool ok, int exitCode, string detail)
    {
        Ok = ok;
        ExitCode = exitCode;
        Detail = detail ?? string.Empty;
    }

    /// <summary>是否确认结束（或本来就不存在）。</summary>
    public bool Ok { get; }

    /// <summary>taskkill 的退出码；托管 API 或跳过时为 -1。</summary>
    public int ExitCode { get; }

    /// <summary>失败原因 / 补充说明（taskkill 的 stderr 或托管 API 的异常消息）。</summary>
    public string Detail { get; }

    public static ProcessTerminateResult Skip(string reason) => new(true, -1, reason);

    /// <summary>日志用的一行摘要。</summary>
    public string Summary => Ok
        ? (Detail.Length > 0 ? "已结束（" + Detail + "）" : "已结束")
        : (Detail.Length > 0 ? $"未能结束：{Detail}" : "未能结束");

    /// <summary>失败时给用户的下一步建议 —— 绝大多数情况是权限/自我保护。</summary>
    public string Hint => Ok
        ? string.Empty
        : "目标进程可能带反作弊保护、或属于更高权限的运行环境，普通权限结束不了。";
}

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

    /// <summary>
    /// 关键词 → 用于匹配**窗口标题 / 镜像名**的键（小写、已剥扩展名）。
    ///
    /// 为什么必须剥：镜像名（<c>ProcessName</c>）本来就不带 <c>.exe</c>，
    /// 窗口标题是「原神」「星穹铁道」这种人类可读文本也不会带 ——
    /// 而游戏本体的真实镜像名往往是英文（原神 = <c>YuanShen</c>、崩铁 = <c>StarRail</c>），
    /// 按名字 <c>taskkill /IM</c> 注定失败，**只剩按窗口标题兜底这一条路**能把游戏清掉。
    /// 拿带扩展名的原始关键词去比标题（「原神.exe」vs「原神」）会让这条路整个失效。
    /// </summary>
    public static string TitleMatchKey(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var name = Path.GetFileNameWithoutExtension(text).Trim();
        return (name.Length == 0 ? text : name).ToLowerInvariant();
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

    /// <summary>按镜像名结束进程树：taskkill /IM /T /F。返回结果用于日志回执。</summary>
    public static ProcessTerminateResult TerminateProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return ProcessTerminateResult.Skip("进程名为空");
        }

        var result = RunHidden("taskkill", $"/IM \"{processName}\" /T /F", 10000);
        return new ProcessTerminateResult(result.Ok, result.ExitCode, result.Detail);
    }

    /// <summary>按 PID 结束进程树：taskkill /PID /T /F。返回结果用于日志回执。</summary>
    public static ProcessTerminateResult TerminatePidTree(int pid)
    {
        if (pid <= 0)
        {
            return ProcessTerminateResult.Skip("PID 无效");
        }

        var result = RunHidden("taskkill", $"/PID {pid} /T /F", 10000);
        return new ProcessTerminateResult(result.Ok, result.ExitCode, result.Detail);
    }

    /// <summary>
    /// 结束进程树的**带降级**版本：taskkill 不成就换托管 API。
    ///
    /// 为什么需要降级：taskkill /F 对**带内核级自我保护的游戏**（原神 / 星穹铁道的反作弊驱动）
    /// 会直接拒绝访问，而 taskkill 的失败是静默的 —— 日志里只留下"正在结束"，
    /// 用户对着还在跑的游戏根本判断不出到底关了没有（真机踩过）。
    /// 托管 API 失败时会抛出真实原因（拒绝访问等），能明确写进日志。
    /// </summary>
    public static ProcessTerminateResult TerminateProcessTree(int pid)
    {
        if (pid <= 0)
        {
            return ProcessTerminateResult.Skip("PID 无效");
        }

        var viaTaskkill = TerminatePidTree(pid);
        if (viaTaskkill.Ok)
        {
            return viaTaskkill;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
            return new ProcessTerminateResult(true, -1, "taskkill 被拒，改用托管 API 结束成功");
        }
        catch (ArgumentException)
        {
            // 查不到这个 PID = 它已经退出了，按成功处理
            return new ProcessTerminateResult(true, -1, "进程已不存在");
        }
        catch (Exception ex)
        {
            var detail = viaTaskkill.Detail.Length > 0 ? $"{viaTaskkill.Detail}；" : string.Empty;
            return new ProcessTerminateResult(false, -1, $"{detail}托管 API 也失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 给指定进程的所有可见顶层窗口发 WM_CLOSE —— 走游戏自己的"正常退出"流程。
    ///
    /// 为什么要这条：对带反作弊保护的游戏，强制结束进程会被驱动拦下；
    /// 但"请求窗口关闭"是应用层消息，走的是游戏自己的退出逻辑，反作弊不拦。
    /// 代价是有些游戏会弹一个"确认退出"对话框，需要用户或后续按键确认。
    /// </summary>
    public static int CloseMainWindowsByPid(int pid)
    {
        if (pid <= 0)
        {
            return 0;
        }

        var closed = 0;
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != (uint)pid || !NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                if (NativeMethods.PostMessageW(hwnd, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero))
                {
                    closed++;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败不影响主流程
        }

        return closed;
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

    /// <summary>
    /// 跑一个隐藏外部命令并**把结果带回来**。
    ///
    /// 为什么必须带回来：以前这里把 taskkill 的输出与退出码全丢掉、异常也吞掉，
    /// 于是"结束失败"和"结束成功"在日志里长得一模一样。
    /// 输出用异步读，避免子进程写满管道缓冲导致 WaitForExit 卡住（经典的死锁写法）。
    /// </summary>
    private static (bool Ok, int ExitCode, string Detail) RunHidden(string fileName, string arguments, int timeoutMs)
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

            // taskkill 在中文系统上按 ANSI/OEM 输出，用 UTF-8 读会乱码
            var encoding = ConsoleTextEncoding();
            startInfo.StandardOutputEncoding = encoding;
            startInfo.StandardErrorEncoding = encoding;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, -1, $"无法启动 {fileName}");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 忽略：超时进程杀不掉也不该影响调用方
                }

                return (false, -1, $"{fileName} 超时（>{timeoutMs}ms）");
            }

            var text = ((stderr.Result ?? string.Empty) + (stdout.Result ?? string.Empty)).Trim();
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 300)
            {
                text = text[..300];
            }

            return (process.ExitCode == 0, process.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.Message);
        }
    }

    /// <summary>取控制台命令的文本编码（中文系统 = CP936）；取不到就退回 UTF-8。</summary>
    private static Encoding ConsoleTextEncoding()
    {
        try
        {
            return Encoding.GetEncoding(936);
        }
        catch
        {
            // 未注册 CodePagesEncodingProvider 时 GetEncoding(936) 会抛，退回默认
            return Encoding.UTF8;
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
