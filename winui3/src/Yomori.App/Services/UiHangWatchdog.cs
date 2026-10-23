// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Yomori.Services;

/// <summary>
/// UI 线程无响应看门狗。<b>跑在自己的后台线程上，不依赖 DispatcherQueue / 消息泵。</b>
///
/// 为什么必须独立于消息泵：本应用原有的全部定时器（心跳轮询、黑屏看门狗、1Hz 刷新、
/// 呈现节拍器）都是 <c>DispatcherQueue.CreateTimer()</c>，即"全部排队等 UI 线程执行"。
/// UI 线程一旦卡死，这些机制会一起静默失效 —— 于是卡死表现为：
///   界面僵住、进程活着、<b>没有任何日志、crash.log 干净、不会自动恢复</b>。
/// 用户侧看到的就是"突然卡死且查不出原因"。
///
/// 本类只用 Win32 <c>SendMessageTimeout</c> 探测：该调用由调用方线程自己完成等待，
/// 与目标进程的消息泵无关，因此目标卡死时它一定超时（而非跟着一起挂住）。
///
/// 探测到卡死后落三样东西（都在 <c>CrashLog</c> 里，不新建日志文件）：
///   ① 卡死事实：开始时刻、持续时长、连续超时次数；
///   ② 本进程全部线程的快照（tid / 状态 / 等待原因 / 上下文切换 / CPU）——
///      这是判断"卡在哪个等待"的第一手依据，取它只需一次 NtQuerySystemInformation；
///   ③ 恢复时刻（若后来自己好了）。
///
/// 设计约束（改前必读）：
///   - 本类<b>永不</b>触碰任何 XAML / DispatcherQueue / WinRT 对象，只写文件 + 发系统通知；
///   - 所有异常都在内部吞掉：看门狗自己绝不能成为启动失败或再次卡死的原因；
///   - 只对<b>主控端</b>启用（代理模式没有 UI 线程，见 <c>AppPaths.AgentMode</c>）。
/// </summary>
public static class UiHangWatchdog
{
    // ---------------- P/Invoke ----------------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass, IntPtr buffer, int bufferLength, out int returnLength);

    /// <summary>
    /// 窗口句柄是否还指向一个真实存在的窗口。
    /// 🔑 这是「应用是否正在关」的<b>唯一可靠判据</b>（2026-10-15 实测）：
    /// 窗口活着但 UI 线程卡死时它返回 true；窗口销毁后返回 false。
    /// 对比 <c>GetLastError</c> —— 后者在 SMTO 失败时可能是 1460 / 0 / 1400，毫无规律。
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// 窗口是否已被 Windows 判定为「未响应」。
    /// ⚠️ <b>不用于卡死判据</b>：实测 UI 线程硬卡在 WrLpcReply 时它返回 false
    /// （那不是 DWM 能判定的 hung），会把真卡死放过去。只作诊断输出里的参考。
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    private const uint WmNull = 0x0000;
    /// <summary>目标无响应时立即返回，而不是排队等它 —— 少了它，探测本身会被卡死拖住。</summary>
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoNormal = 0x0000;

    /// <summary>SystemProcessInformation。</summary>
    private const int SystemProcessInformation = 5;

    // ---------------- 线程结构（x64） ----------------

    /// <summary>NtQuerySystemInformation 的进程块：线程数组起始偏移。</summary>
    private const int ThreadArrayOffset = 256;

    /// <summary>SYSTEM_THREAD_INFORMATION 在 x64 上的大小。</summary>
    private const int ThreadEntrySize = 80;

    private static readonly string[] ThreadStates =
    {
        "Initialized", "Ready", "Running", "Standby", "Terminated",
        "Waiting", "Transition", "DeferredReady", "GateWaitObsolete", "WaitingForProcessInSwap",
    };

    private static readonly string[] WaitReasons =
    {
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended",
        "UserRequest", "WrExecutive", "WrFreePage", "WrPageIn", "WrPoolAllocation",
        "WrDelayExecution", "WrSuspended", "WrUserRequest", "WrEventPair", "WrQueue",
        "WrLpcReceive", "WrLpcReply", "WrVirtualMemory", "WrPageOut", "WrRendezvous",
        "WrKeyedEvent", "WrTerminated", "WrProcessInSwap", "WrCpuRateControl",
        "WrCalloutStack", "WrKernel", "WrResource", "WrPushLock", "WrMutex", "WrQuantumEnd",
        "WrDispatchInt", "WrPreempted", "WrYieldExecution", "WrFastMutex", "WrGuardedMutex",
        "WrRundown", "WrAlertByThreadId", "WrDeferredPreempt", "WrPhysicalFault",
    };

    // ---------------- 参数 ----------------

    /// <summary>探测间隔。取值要明显大于一次正常 UI 卡顿（几十毫秒），避免误报。</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    /// <summary>单次探测的超时。UI 线程健康时 WM_NULL 是微秒级的。</summary>
    private const int ProbeTimeoutMs = 3000;

    /// <summary>连续多少次探测失败才判定为"真的卡死"（避免一次抖动就报）。</summary>
    private const int FailuresBeforeReport = 2;

    /// <summary>卡死期间重复落盘的间隔（已经报过一次就别每秒刷屏）。</summary>
    private static readonly TimeSpan RereportInterval = TimeSpan.FromSeconds(60);

    /// <summary>单次最多打印多少个线程，避免把日志撑爆。</summary>
    private const int MaxThreadsInReport = 128;

    private static readonly object Gate = new();

    private static Thread? _thread;
    private static volatile bool _stopping;
    private static IntPtr _hwnd;

    /// <summary>卡死是否已经报告过（用于"只报一次 + 周期性补报"）。</summary>
    private static bool _reported;

    /// <summary>本次卡死的起始时刻（本地时间）。</summary>
    private static DateTime _hangStartedAt;

    /// <summary>连续探测失败计数。</summary>
    private static int _consecutiveFailures;

    /// <summary>上一次补报时刻。</summary>
    private static DateTime _lastReportAt;

    private static bool _installed;

    /// <summary>看门狗是否已安装。</summary>
    public static bool Installed => _installed;

    /// <summary>
    /// 装上并启动后台探测线程。幂等；窗口句柄为 0 时自动退化为"无窗口可探测"（不启动）。
    /// </summary>
    /// <param name="hwnd">主窗口 HWND（用 <c>Helpers.WindowHelper.GetHwnd</c> 取）。</param>
    public static void Install(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;

            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            _hwnd = hwnd;
            _stopping = false;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "yomori-ui-hang-watchdog",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }
    }

    /// <summary>停止探测（进程退出时调用；不 join —— 后台线程自己会随进程结束）。</summary>
    public static void Stop()
    {
        _stopping = true;
    }

    // ---------------- 主循环 ----------------

    private static void Loop()
    {
        while (!_stopping)
        {
            try
            {
                Thread.Sleep(ProbeInterval);
            }
            catch
            {
                // 被唤醒打断就继续下一轮
            }

            if (_stopping)
            {
                break;
            }

            bool alive;
            try
            {
                alive = Probe();
            }
            catch
            {
                // 探测本身出错（极端情况）：当作"活着"，绝不让看门狗误报
                alive = true;
            }

            if (alive)
            {
                HandleAlive();
            }
            else
            {
                HandleHung();
            }
        }
    }

    /// <summary>
    /// 发一条 WM_NULL 看 UI 线程理不理。
    /// 返回 true = 正常；false = 无响应（卡死）。
    ///
    /// 🔑 <b>判据只有两个：rc + IsWindow</b>（2026-10-15 第三次修，有三场景实测支撑）。
    ///
    /// 前两次都因为把不可靠的信号当判据而漏报：
    ///   ① 只看 <c>GetLastError</c>：同一卡死窗口，第一次 err=1460，之后 err=0
    ///      （被其它线程的成功调用清掉）→ 后续全判成「活着」。
    ///   ② 加 <c>IsHungAppWindow</c> 兜底：UI 线程硬卡在 WrLpcReply 时它返回 false
    ///      （那不是 DWM 能判定的 hung），兜底也放过。
    ///   ③ 用 <c>err == ERROR_INVALID_WINDOW_HANDLE</c> 排除关闭：实测卡死过程中
    ///      err 也会变成 1400，又把真卡死放过一次。
    ///
    /// 三场景实测（WinForms，STA 线程自旋占住）：
    /// <code>
    ///   场景                     rc   err        IsWindow
    ///   A 窗口活着、UI 卡死       0    1460 或 0  True
    ///   B 窗口已销毁             0    1400       False
    ///   C 健康                   1    0          True
    /// </code>
    /// 结论：<c>rc</c> 区分「响应了没」，<c>IsWindow</c> 区分「窗口还在没」。
    /// err 与 IsHungAppWindow 只作诊断参考，不作判据。
    /// </summary>
    private static bool Probe()
    {
        IntPtr result;
        var rc = SendMessageTimeout(
            _hwnd, WmNull, IntPtr.Zero, IntPtr.Zero,
            SmtoAbortIfHung | SmtoNormal, ProbeTimeoutMs, out result);

        // 响应了 = 活着
        if (rc != 0)
        {
            return true;
        }

        // 没响应：窗口还在就是卡死；窗口没了说明应用正在关，不报
        return !IsWindow(_hwnd);
    }

    private static void HandleAlive()
    {
        if (!_reported)
        {
            _consecutiveFailures = 0;
            return;
        }

        // 之前报过卡死、现在恢复了 —— 补一条"已恢复"，否则日志里只有开始没有结束。
        var since = DateTime.Now - _hangStartedAt;
        SafeNote(
            "UI 线程无响应已恢复（本次持续 " + FormatDuration(since) + "）。");
        Reset();
    }

    private static void HandleHung()
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

            // 附带两个「不可靠但有用」的参考信号：GetLastError 与 IsHungAppWindow。
            // 它们不作判据（见 Probe 注释里的三次漏报历史），但写进日志能省掉下次反查。
            var lastErr = 0;
            try { lastErr = Marshal.GetLastWin32Error(); } catch { }
            var hungFlag = false;
            try { hungFlag = IsHungAppWindow(_hwnd); } catch { }

            SafeNote(
                "UI 线程无响应（卡死）：探测连续 " + failures + " 次超时"
                + "（每次 " + ProbeTimeoutMs + "ms）。"
                + " 这是<b>假死</b>，不是崩溃：进程仍活着但消息泵停摆，"
                + "轮询 / 看门狗 / 呈现节拍器全在 UI 线程上，因此会一起静默失效。"
                + " [参考] GetLastError=" + lastErr
                + "（1460=ERROR_TIMEOUT；0 表示被其它线程的成功调用清掉，不可作判据）"
                + "，IsHungAppWindow=" + (hungFlag ? "true" : "false")
                + "（卡在 WrLpcReply 时也是 false，不可作判据）。"
                + " 下面是本进程全部线程状态快照（取这一刻，用于定位卡在哪个等待）。");

            SafeNote(DumpThreads());

            return;
        }

        // 周期性补报：长时间卡死时给一份更新的线程快照
        if (now - _lastReportAt >= RereportInterval)
        {
            _lastReportAt = now;
            SafeNote(
                "UI 线程仍无响应（已持续 " + FormatDuration(now - _hangStartedAt) + "），"
                + "追加一份线程快照。");
            SafeNote(DumpThreads());
        }
    }

    private static void Reset()
    {
        _reported = false;
        _consecutiveFailures = 0;
        _lastReportAt = default;
    }

    // ---------------- 线程快照 ----------------

    /// <summary>
    /// 抓本进程全部线程的状态并格式化成可读文本。
    ///
    /// 用 <c>NtQuerySystemInformation(SystemProcessInformation)</c> 而不是
    /// <c>GetThreadWaitChain</c>：后者需要跨进程权限，主控端是 requireAdministrator
    /// 而看门狗线程虽是管理员，但在某些环境下依然取不到链；前者只需一次系统调用，
    /// 且"状态 + 等待原因"已经足以区分"卡在同步等待"与"在跑"。
    /// </summary>
    private static string DumpThreads()
    {
        try
        {
            return DumpThreadsCore();
        }
        catch (Exception ex)
        {
            return "（线程快照失败：" + ex.GetType().Name + " " + ex.Message + "）";
        }
    }

    private static string DumpThreadsCore()
    {
        var self = Environment.ProcessId;

        var buffer = IntPtr.Zero;
        try
        {
            // 从小开始按需翻倍：一次调用拿全部进程，再挑出自己那一段。
            var length = 1 << 22;
            while (true)
            {
                buffer = Marshal.AllocHGlobal(length);
                var status = NtQuerySystemInformation(
                    SystemProcessInformation, buffer, length, out var needed);

                if (status == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    length = Math.Min(length * 2, 1 << 27);
                    if (length >= (1 << 27))
                    {
                        return "（线程快照失败：缓冲区超过 128MB 仍未足够）";
                    }

                    continue;
                }

                if (status != 0)
                {
                    return "（线程快照失败：NtQuerySystemInformation = 0x"
                        + status.ToString("X8", CultureInfo.InvariantCulture) + "）";
                }

                break;
            }

            var rows = new List<string>();
            var cursor = buffer.ToInt64();

            while (true)
            {
                var next = Marshal.ReadInt32((IntPtr)cursor, 0);
                var threadCount = Marshal.ReadInt32((IntPtr)cursor, 4);
                var pid = Marshal.ReadIntPtr((IntPtr)cursor, 80).ToInt64();

                if (pid == self)
                {
                    var builder = new StringBuilder();
                    builder.Append("  进程 ").Append(self)
                        .Append(" 共 ").Append(threadCount).AppendLine(" 个线程：");

                    var limit = Math.Min(threadCount, MaxThreadsInReport);
                    var threadsBase = cursor + ThreadArrayOffset;

                    for (var i = 0; i < limit; i++)
                    {
                        var entry = threadsBase + (long)i * ThreadEntrySize;

                        var kernelMs = Marshal.ReadInt64((IntPtr)entry, 0) / 10000;
                        var userMs = Marshal.ReadInt64((IntPtr)entry, 8) / 10000;
                        var waitMs = (uint)Marshal.ReadInt32((IntPtr)entry, 24);
                        var tid = (uint)Marshal.ReadInt32((IntPtr)entry, 48);
                        var priority = Marshal.ReadInt32((IntPtr)entry, 56);
                        var switches = (uint)Marshal.ReadInt32((IntPtr)entry, 64);
                        var state = (uint)Marshal.ReadInt32((IntPtr)entry, 68);
                        var reason = (uint)Marshal.ReadInt32((IntPtr)entry, 72);

                        var stateText = state < ThreadStates.Length
                            ? ThreadStates[state]
                            : "st" + state.ToString(CultureInfo.InvariantCulture);
                        var reasonText = reason < WaitReasons.Length
                            ? WaitReasons[reason]
                            : "wr" + reason.ToString(CultureInfo.InvariantCulture);

                        builder.Append("    tid=").Append(tid.ToString(CultureInfo.InvariantCulture))
                            .Append("  ").Append(stateText.PadRight(12))
                            .Append(' ').Append(reasonText.PadRight(20))
                            .Append(" pri=").Append(priority.ToString(CultureInfo.InvariantCulture))
                            .Append(" ctxSw=").Append(switches.ToString(CultureInfo.InvariantCulture))
                            .Append(" cpuK=").Append(kernelMs.ToString(CultureInfo.InvariantCulture)).Append("ms")
                            .Append(" cpuU=").Append(userMs.ToString(CultureInfo.InvariantCulture)).Append("ms")
                            .Append(" wait=").Append(waitMs.ToString(CultureInfo.InvariantCulture)).Append("ms")
                            .AppendLine();
                    }

                    if (limit < threadCount)
                    {
                        builder.Append("    …另有 ").Append(threadCount - limit).AppendLine(" 个线程未列出");
                    }

                    return builder.ToString().TrimEnd();
                }

                if (next == 0)
                {
                    break;
                }

                cursor += next;
            }

            return "（线程快照失败：在本进程列表里找不到 pid=" + self + "）";
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // ---------------- 输出 ----------------

    /// <summary>
    /// 写一条 crash.log 备注。失败静默 —— 看门狗绝不能因为写不了日志而抛异常。
    /// </summary>
    private static void SafeNote(string message)
    {
        try
        {
            Services.CrashLog.WriteNote(message);
        }
        catch
        {
        }
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:00}:{2:00}",
                (int)span.TotalHours, span.Minutes, span.Seconds);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}分{1:00}秒",
            span.Minutes, span.Seconds);
    }
}
