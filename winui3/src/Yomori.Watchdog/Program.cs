// -*- coding: utf-8 -*-
// 外置 UI 看门狗入口。
//
// 用法：
//   Yomori.Watchdog.exe                常驻模式：监视主控端，假死就记录 + 抓 dump
//   Yomori.Watchdog.exe --once         单次模式：探测一次并打印结果（自检用）
//   Yomori.Watchdog.exe --pid <pid>    只监视指定 PID（调试用）
//
// 为什么不用进程内看门狗：见 HangProbe.cs 头部注释。三次漏报的结论是
// 「进程内线程监视进程内卡死不可靠」，必须独立进程。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Yomori.Watchdog;

public static class Program
{
    // ---------------- P/Invoke：找主控端窗口 ----------------

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>脱离控制台。常驻模式下调用，避免开机自启时弹出黑窗。</summary>
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    // ---------------- P/Invoke：抓崩溃转储 ----------------

    /// <summary>MiniDumpWithFullMemory：线程栈 + 模块 + 句把都在，dotnet-dump 能分析栈。</summary>
    private const uint MiniDumpWithFullMemory = 0x00000002;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ —— MiniDumpWriteDump 需要这两个。</summary>
    private const uint ProcessAccessForDump = 0x0410;

    // ---------------- 参数 ----------------

    /// <summary>探测间隔。要明显大于一次正常 UI 卡顿（几十毫秒），避免误报。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);

    /// <summary>单次探测超时。UI 健康时 WM_NULL 是微秒级的。</summary>
    private const int ProbeTimeoutMs = 3000;

    /// <summary>连续多少次假死才判定为真（防抖）。</summary>
    private const int FailuresBeforeReport = 2;

    /// <summary>已判定假死后，重复记录的间隔。</summary>
    private static readonly TimeSpan RereportInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 假死持续多久才抓转储。设成 90 秒：短于它可能只是瞬时卡顿，
    /// 而抓 dump 会让目标进程暂停更久，不能一发现就抓。
    /// </summary>
    private static readonly TimeSpan DumpAfter = TimeSpan.FromSeconds(90);

    /// <summary>两次抓 dump 的最小间隔，避免反复抓把磁盘写满。</summary>
    private static readonly TimeSpan DumpCooldown = TimeSpan.FromMinutes(10);

    /// <summary>主控端进程名（不含扩展名）。</summary>
    private const string TargetProcessName = "Yomori";

    /// <summary>本看门狗的数据目录（与 agent 平级）。
    ///
    /// 🔑 <b>不能写到 exe 同级的 logs\</b>（2026-10-16 实测踩过）：
    /// 安装目录 <c>C:\Program Files\Yomori</c> 只给 Administrators 写权限，
    /// 而本看门狗 manifest 是 asInvoker（标准用户）—— 写进去会静默失败，
    /// 日志一行都留不下。（主控端能写是因为它自己就是管理员进程。）
    /// ProgramData 已验证标准用户可写，且 agent 的日志也在这里。
    /// </summary>
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Yomori", "watchdog");

    /// <summary>本看门狗自己的日志文件名。</summary>
    private const string WatchdogLogName = "watchdog.log";

    /// <summary>崩溃转储输出目录名（在 DataDir 下）。</summary>
    private const string DumpDirName = "crashdumps";

    /// <summary>
    /// 日志文件大小上限（5 MB）。超过就截断保留末尾部分。
    /// 看门狗是常驻进程（开机至关机），不加上限的话会一直长；
    /// 而且真正有用的总是最近那几次卡死记录。
    /// </summary>
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();

    private static StreamWriter? _log;

    /// <summary>当前日志文件路径；与 _log 配套，用于检测目标变化时重开。</summary>
    private static string? _logPath;

    /// <summary>当前被监视的 PID（日志与 dump 目录要按它定位）。</summary>
    private static int? _currentPid;

    private static int _consecutiveFailures;
    private static bool _reported;
    private static DateTime _hangStartedAt;
    private static DateTime _lastReportAt;
    private static DateTime _lastDumpAt = DateTime.MinValue;
    private static bool _dumpTaken;

    /// <summary>常驻模式是否已脱离控制台（只做一次）。</summary>
    private static bool _consoleDetached;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        var forcedPid = ReadIntArg(args, "--pid");

        try
        {
            return once ? RunOnce(forcedPid) : RunLoop(forcedPid);
        }
        catch (Exception ex)
        {
            // 看门狗自己绝不能无声无息地死掉
            Console.Error.WriteLine("看门狗异常退出：" + ex);
            return 1;
        }
        finally
        {
            lock (Gate)
            {
                _log?.Dispose();
                _log = null;
                _logPath = null;
            }
        }
    }

    // ---------------- 单次模式（自检） ----------------

    private static int RunOnce(int? forcedPid)
    {
        var target = FindTarget(forcedPid);
        if (target is null)
        {
            Console.WriteLine("未找到主控端进程（" + TargetProcessName + ".exe，可见窗口）。");
            return 2;
        }

        Console.WriteLine("目标 PID=" + target.Value.Pid + " HWND=" + target.Value.Hwnd.ToInt64());

        var result = HangProbe.Probe(target.Value.Hwnd, ProbeTimeoutMs);
        Console.WriteLine("探测结果 = " + result);

        if (result == HangProbe.ProbeResult.Hung)
        {
            var threads = HangProbe.CaptureThreads(target.Value.Pid);
            Console.WriteLine(threads is null
                ? "（线程快照失败）"
                : HangProbe.FormatThreads(threads));
        }

        return result == HangProbe.ProbeResult.Hung ? 1 : 0;
    }

    // ---------------- 常驻模式 ----------------

    private static int RunLoop(int? forcedPid)
    {
        // 🔑 脱离控制台（2026-10-16 实测踩过）：本工程 OutputType=Exe，
        // 进程一启动就会附加一个控制台窗口（实测到 ConsoleWindowClass 类的顶层窗口）。
        // 看门狗是开机自启、常驻后台的，不该弹黑窗扰人。
        // 不改成 WinExe 是为了保留 --once / --pid 的控制台输出（人工自检需要）。
        if (!_consoleDetached)
        {
            try { FreeConsole(); } catch { }
            _consoleDetached = true;
        }

        Log("外置 UI 看门狗启动（pid=" + Environment.ProcessId
            + "，间隔 " + ProbeInterval.TotalSeconds + "s，超时 " + ProbeTimeoutMs + "ms"
            + "，连续 " + FailuresBeforeReport + " 次判定，假死 " + DumpAfter.TotalSeconds
            + "s 后抓 dump）");

        while (true)
        {
            try
            {
                Thread.Sleep(ProbeInterval);
            }
            catch
            {
                // 被唤醒打断就继续下一轮
            }

            (int Pid, IntPtr Hwnd)? target;
            try
            {
                target = FindTarget(forcedPid);
            }
            catch (Exception ex)
            {
                Log("查找目标进程失败：" + ex.Message);
                continue;
            }

            if (target is null)
            {
                // 主控端没开：复位状态，等它下次启动
                Reset();
                continue;
            }

            HangProbe.ProbeResult result;
            try
            {
                result = HangProbe.Probe(target.Value.Hwnd, ProbeTimeoutMs);
            }
            catch (Exception ex)
            {
                // 探测本身出错（句柄失效等）：当作「窗口没了」，不误报
                Log("探测异常（当作窗口已关闭）：" + ex.Message);
                result = HangProbe.ProbeResult.WindowGone;
            }

            _currentPid = target.Value.Pid;

            switch (result)
            {
                case HangProbe.ProbeResult.Alive:
                    HandleAlive(target.Value);
                    break;

                case HangProbe.ProbeResult.Hung:
                    HandleHung(target.Value);
                    break;

                default:
                    Reset();
                    break;
            }
        }
    }

    private static void HandleAlive((int Pid, IntPtr Hwnd) target)
    {
        if (!_reported)
        {
            _consecutiveFailures = 0;
            return;
        }

        var since = DateTime.Now - _hangStartedAt;
        Log("UI 线程无响应已恢复（本次持续 " + FormatDuration(since)
            + "，pid=" + target.Pid + "）");
        Reset();
    }

    private static void HandleHung((int Pid, IntPtr Hwnd) target)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures < FailuresBeforeReport)
        {
            return;
        }

        var now = DateTime.Now;

        if (!_reported)
        {
            _reported = true;
            _hangStartedAt = now;
            _lastReportAt = now;

            Log("UI 线程无响应（假死）：连续 " + failures + " 次探测失败"
                + "（每次 " + ProbeTimeoutMs + "ms），pid=" + target.Pid
                + "。这是<b>假死</b>，不是崩溃：进程仍活着但消息泵停摆。"
                + " 进程内看门狗在这种场景下会一起被冻住，所以由本进程（外部）记录。");

            WriteThreadSnapshot(target.Pid);
        }
        else if (now - _lastReportAt >= RereportInterval)
        {
            _lastReportAt = now;
            Log("UI 线程仍无响应（已持续 " + FormatDuration(now - _hangStartedAt)
                + "，pid=" + target.Pid + "），追加线程快照。");
            WriteThreadSnapshot(target.Pid);
        }

        // 持续假死足够久 → 抓一次转储，留下可分析的栈
        if (!_dumpTaken && now - _hangStartedAt >= DumpAfter
            && now - _lastDumpAt >= DumpCooldown)
        {
            _dumpTaken = true;
            _lastDumpAt = now;
            TryCaptureDump(target.Pid);
        }
    }

    private static void WriteThreadSnapshot(int pid)
    {
        try
        {
            var threads = HangProbe.CaptureThreads(pid);
            Log(threads is null
                ? "  线程快照失败（NtQuerySystemInformation 未返回该进程）。"
                : HangProbe.FormatThreads(threads));
        }
        catch (Exception ex)
        {
            Log("  线程快照异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 用 <c>MiniDumpWriteDump</c> 抓一份全量转储。
    ///
    /// 🔑 不用 createdump（2026-10-16 实测失败）：新版 .NET 的 createdump 只能抓自己，
    /// 传 pid 直接报 <c>[createdump] The pid argument is no longer supported</c>。
    /// MiniDumpWriteDump 是 Win32 API，能抓任意进程。
    ///
    /// 抓出来的 dump 没有托管堆，但<b>线程栈、模块、句把都在</b>，足以判断
    /// 「UI 线程卡在哪个调用」—— 那正是本看门狗要的证据。若还需托管堆
    /// （如看对象分配），用提权的 dotnet-dump collect 补抓（它走诊断管道，需要权限）。
    /// </summary>
    private static void TryCaptureDump(int pid)
    {
        try
        {
            var dumpDir = ResolveDumpDir(pid);
            if (dumpDir is null)
            {
                Log("  抓 dump 失败：找不到被监视进程的安装目录。");
                return;
            }

            Directory.CreateDirectory(dumpDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var dumpPath = Path.Combine(dumpDir, "hang-" + stamp + "-" + pid + ".dmp");

            var handle = OpenProcess(ProcessAccessForDump, inheritHandle: false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                var openErr = Marshal.GetLastWin32Error();
                Log("  抓 dump 失败：OpenProcess 被拒（err=" + openErr + "）。"
                    + "主控端是管理员进程；要么把本看门狗也跑成管理员，"
                    + "要么手动用提权的 dotnet-dump collect 补抓。");
                return;
            }

            try
            {
                using var file = new FileStream(
                    dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read);

                var ok = MiniDumpWriteDump(
                    handle,
                    (uint)pid,
                    file.SafeFileHandle.DangerousGetHandle(),
                    MiniDumpWithFullMemory,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                file.Flush();

                if (!ok)
                {
                    var dumpErr = Marshal.GetLastWin32Error();
                    Log("  抓 dump 失败：MiniDumpWriteDump 返回 false（err=" + dumpErr + "）。");
                    TryDelete(dumpPath);
                    return;
                }

                var size = new FileInfo(dumpPath).Length;
                Log("  已抓 dump（线程栈级）：" + dumpPath + "（" + (size / 1024 / 1024) + " MB）。"
                    + "若需完整托管堆，请再用提权的 dotnet-dump collect 补抓。");
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception ex)
        {
            Log("  抓 dump 异常：" + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就算了，磁盘上多一个空文件不影响判断
        }
    }

    // ---------------- 目标查找 ----------------

    /// <summary>
    /// 找主控端进程的主窗口。要求：进程名匹配 + 顶层窗口 + 可见。
    /// 这样能天然排掉 --rdp-agent（无窗口）和本看门狗自己。
    /// </summary>
    private static (int Pid, IntPtr Hwnd)? FindTarget(int? forcedPid)
    {
        if (forcedPid is int pid)
        {
            var hwnd = MainWindowOf(pid);
            return hwnd == IntPtr.Zero ? null : (pid, hwnd);
        }

        var found = new List<(int Pid, IntPtr Hwnd)>();

        bool Callback(IntPtr hWnd, IntPtr _)
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid == 0 || windowPid == Environment.ProcessId)
            {
                return true;
            }

            string name;
            try
            {
                name = Process.GetProcessById((int)windowPid).ProcessName;
            }
            catch
            {
                return true; // 进程已退出
            }

            if (string.Equals(name, TargetProcessName, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(((int)windowPid, hWnd));
            }

            return true;
        }

        EnumWindows(Callback, IntPtr.Zero);

        if (found.Count == 0)
        {
            return null;
        }

        // 多个主窗口时取 PID 最小的那个，保证行为确定
        found.Sort(static (a, b) => a.Pid.CompareTo(b.Pid));
        return found[0];
    }

    private static IntPtr MainWindowOf(int pid)
    {
        IntPtr result = IntPtr.Zero;

        bool Callback(IntPtr hWnd, IntPtr _)
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid == pid)
            {
                result = hWnd;
                return false;
            }

            return true;
        }

        EnumWindows(Callback, IntPtr.Zero);
        return result;
    }

    // ---------------- 路径解析 ----------------

    private static string ResolveDumpDir(int? pid)
    {
        // 参数保留为了调用侧语义清晰（“这是哪个目标的转储”），
        // 但目录不再跟随目标变化 —— ProgramData 是唯一可靠可写位。
        _ = pid;
        return Path.Combine(DataDir, DumpDirName);
    }

    private static string ResolveLogPath(int? pid)
    {
        _ = pid;
        return Path.Combine(DataDir, WatchdogLogName);
    }

    // ---------------- 日志 ----------------

    private static void Log(string message)
    {
        var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            + "] " + message;

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var path = ResolveLogPath(_currentPid);

                // 超过上限就截断保留末尾：看门狗是开机至关机的常驻进程，
                // 不编辑会一直长；有用的只有最近那几次卡死记录。
                TruncateIfTooLarge(path);

                if (_log is null || !string.Equals(_logPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Dispose();
                    _logPath = path;
                    _log = new StreamWriter(new FileStream(
                        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        AutoFlush = true,
                    };
                }

                _log.WriteLine(line);
            }
            catch
            {
                // 日志写不进不影响探测
            }

            // 常驻模式已 FreeConsole，这里写不出去（也不会报错）；
            // --once / --pid 还有控制台，保留输出供人工自检。
            if (!_consoleDetached)
            {
                try
                {
                    Console.WriteLine(line);
                }
                catch
                {
                    // 无控制台时忽略
                }
            }
        }
    }

    /// <summary>日志超过上限时截断，保留末尾。</summary>
    private static void TruncateIfTooLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxLogBytes)
            {
                return;
            }

            // 当前句柄可能还开着：先关再截断，由调用方重开
            _log?.Dispose();
            _log = null;
            _logPath = null;

            var keep = MaxLogBytes / 2;
            var bytes = File.ReadAllBytes(path);
            var tail = bytes[(bytes.Length - (int)keep)..];
            File.WriteAllText(path,
                "[日志超过 " + (MaxLogBytes / 1024 / 1024) + " MB，已截断保留末尾]" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.AppendAllText(path, Encoding.UTF8.GetString(tail));
        }
        catch
        {
            // 截断失败不影响主流程（下次再试）
        }
    }

    private static void Reset()
    {
        _reported = false;
        _consecutiveFailures = 0;
        _dumpTaken = false;
        _lastReportAt = default;
        _currentPid = null;
    }

    private static int? ReadIntArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
                (int)span.TotalHours, span.Minutes, span.Seconds);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}分{1:00}秒", span.Minutes, span.Seconds);
    }
}
