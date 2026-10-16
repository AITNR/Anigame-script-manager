// -*- coding: utf-8 -*-
// 外置 UI 看门狗核心：进程外探测主控端是否整窗假死。
//
// 为什么必须放在进程外（2026-10-16 三次漏报后的结论）：
//   进程内看门狗（Services/UiHangWatchdog.cs）用自己的后台线程 + SendMessageTimeout 探测，
//   逻辑本身是对的，但真实冻结场景是 68 个线程一起卡住、连低优先级后台线程都得不到调度，
//   于是看门狗自己也跑不起来 → 什么都没记下。实测三次（10-12 / 10-15 / 10-16）全部漏报。
//
//   放到独立进程后，主控端怎么卡都影响不到本进程，探测一定还能跑。
//
// 探测判据（与进程内版一致，踩过三次坑后固化，详见 RdpView 侧文档 §10）：
//   rc != 0                      → 响应了，活着
//   rc == 0 且 IsWindow(hwnd)    → 窗口还在却不响应 = 假死
//   rc == 0 且 !IsWindow(hwnd)   → 窗口没了 = 正在关，不报
//   ⚠️ 不要看 GetLastError（卡死时会出现 1460 / 0 / 1400，毫无规律）
//   ⚠️ 不要看 IsHungAppWindow（卡在 WrLpcReply 时它返回 false）
//
// 发现假死后做三件事：
//   ① 写日志（主控端 logs/watchdog.log，与 crash.log 分开，便于区分）
//   ② 抓目标进程全部线程状态快照（NtQuerySystemInformation，一次系统调用）
//   ③ 连续假死超过阈值 → 触发一次崩溃转储（createdump / MiniDumpWriteDump），
//      让「假死」也能留下可分析的栈，而不只是文字描述
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YukinoChan.Watchdog;

/// <summary>目标进程的一次线程状态快照（一行一个线程）。</summary>
public sealed class ThreadSnapshot
{
    public uint Tid;
    public uint ContextSwitches;
    public long KernelMs;
    public long UserMs;
    public uint State;
    public uint WaitReason;
}

/// <summary>
/// 对指定窗口句柄做无响应探测，并能在需要时抓取目标进程的线程快照。
/// 静态工具类：不持有状态，便于测试与复用。
/// </summary>
public static class HangProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass, IntPtr buffer, int bufferLength, out int returnLength);

    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int SystemProcessInformation = 5;

    /// <summary>NtQuerySystemInformation 进程块内线程数组的起始偏移（x64）。</summary>
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
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended", "UserRequest",
        "WrExecutive", "WrFreePage", "WrPageIn", "WrPoolAllocation", "WrDelayExecution", "WrSuspended",
        "WrUserRequest", "WrEventPair", "WrQueue", "WrLpcReceive", "WrLpcReply", "WrVirtualMemory",
        "WrPageOut", "WrRendezvous", "WrKeyedEvent", "WrTerminated", "WrProcessInSwap",
        "WrCpuRateControl", "WrCalloutStack", "WrKernel", "WrResource", "WrPushLock", "WrMutex",
        "WrQuantumEnd", "WrDispatchInt", "WrPreempted", "WrYieldExecution", "WrFastMutex",
        "WrGuardedMutex", "WrRundown", "WrAlertByThreadId", "WrDeferredPreempt", "WrPhysicalFault",
    };

    /// <summary>探测结果。</summary>
    public enum ProbeResult
    {
        /// <summary>窗口正常响应。</summary>
        Alive,

        /// <summary>窗口存在但无响应 —— 假死。</summary>
        Hung,

        /// <summary>窗口已不存在（应用正在关）。</summary>
        WindowGone,
    }

    /// <summary>对句柄做一次探测。</summary>
    public static ProbeResult Probe(IntPtr hwnd, int timeoutMs)
    {
        IntPtr result;
        var rc = SendMessageTimeout(
            hwnd, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, (uint)timeoutMs, out result);

        if (rc != 0)
        {
            return ProbeResult.Alive;
        }

        return IsWindow(hwnd) ? ProbeResult.Hung : ProbeResult.WindowGone;
    }

    /// <summary>
    /// 抓取指定进程的全部线程状态。失败返回 null（调用方自己决定怎么记）。
    /// </summary>
    public static List<ThreadSnapshot>? CaptureThreads(int pid)
    {
        var buffer = IntPtr.Zero;
        try
        {
            var length = 1 << 22;
            int status;
            while (true)
            {
                buffer = Marshal.AllocHGlobal(length);
                status = NtQuerySystemInformation(SystemProcessInformation, buffer, length, out _);
                if (status == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    length = Math.Min(length * 2, 1 << 27);
                    if (length >= (1 << 27))
                    {
                        return null;
                    }

                    continue;
                }

                break;
            }

            if (status != 0)
            {
                return null;
            }

            var cursor = buffer.ToInt64();
            while (true)
            {
                var next = Marshal.ReadInt32((IntPtr)cursor, 0);
                var threadCount = Marshal.ReadInt32((IntPtr)cursor, 4);
                var processId = Marshal.ReadIntPtr((IntPtr)cursor, 80).ToInt64();

                if (processId == pid)
                {
                    var list = new List<ThreadSnapshot>(threadCount);
                    var threadsBase = cursor + ThreadArrayOffset;

                    for (var i = 0; i < threadCount; i++)
                    {
                        var entry = threadsBase + (long)i * ThreadEntrySize;
                        list.Add(new ThreadSnapshot
                        {
                            KernelMs = Marshal.ReadInt64((IntPtr)entry, 0) / 10000,
                            UserMs = Marshal.ReadInt64((IntPtr)entry, 8) / 10000,
                            Tid = (uint)Marshal.ReadInt32((IntPtr)entry, 48),
                            ContextSwitches = (uint)Marshal.ReadInt32((IntPtr)entry, 64),
                            State = (uint)Marshal.ReadInt32((IntPtr)entry, 68),
                            WaitReason = (uint)Marshal.ReadInt32((IntPtr)entry, 72),
                        });
                    }

                    return list;
                }

                if (next == 0)
                {
                    break;
                }

                cursor += next;
            }

            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>把线程快照格式化成可读文本（按 ctxSw 降序，最活跃的排前面）。</summary>
    public static string FormatThreads(List<ThreadSnapshot> threads, int maxRows = 128)
    {
        var ordered = new List<ThreadSnapshot>(threads);
        ordered.Sort(static (a, b) => b.ContextSwitches.CompareTo(a.ContextSwitches));

        var builder = new StringBuilder();
        builder.Append("  进程 ").Append(threads.Count).AppendLine(" 个线程：");

        var limit = Math.Min(ordered.Count, maxRows);
        for (var i = 0; i < limit; i++)
        {
            var t = ordered[i];
            var state = t.State < ThreadStates.Length
                ? ThreadStates[t.State]
                : "st" + t.State.ToString(CultureInfo.InvariantCulture);
            var reason = t.WaitReason < WaitReasons.Length
                ? WaitReasons[t.WaitReason]
                : "wr" + t.WaitReason.ToString(CultureInfo.InvariantCulture);

            builder.Append("    tid=").Append(t.Tid.ToString(CultureInfo.InvariantCulture))
                .Append("  ").Append(state.PadRight(12))
                .Append(' ').Append(reason.PadRight(20))
                .Append(" ctxSw=").Append(t.ContextSwitches.ToString(CultureInfo.InvariantCulture))
                .Append(" cpuK=").Append(t.KernelMs.ToString(CultureInfo.InvariantCulture)).Append("ms")
                .Append(" cpuU=").Append(t.UserMs.ToString(CultureInfo.InvariantCulture)).Append("ms")
                .AppendLine();
        }

        if (limit < ordered.Count)
        {
            builder.Append("    …另有 ").Append(ordered.Count - limit).AppendLine(" 个线程未列出");
        }

        return builder.ToString().TrimEnd();
    }
}