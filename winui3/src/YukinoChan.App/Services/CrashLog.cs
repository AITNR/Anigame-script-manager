// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace YukinoChan.Services;

/// <summary>
/// 崩溃现场留痕。三条路径互相独立、互不依赖：
/// <list type="number">
/// <item><b>托管异常</b>（UI 线程由 <c>App.UnhandledException</c> 转来，后台线程 / 线程池 /
/// 非 UI 线程由 <see cref="AppDomain"/> 转来）→ 写完整 <c>Exception.ToString()</c> 栈。</item>
/// <item><b>漏掉的 async 异常</b>（<c>UnobservedTaskException</c>）→ 写栈并 <c>SetObserved()</c>。
/// 默认行为是在终结器线程上重抛 → 又一次进程终止，而那次终止不经过
/// <c>AppDomain.UnhandledException</c>，等于完全不留痕。</item>
/// <item><b>原生层崩溃</b>（FreeRDP / D3D 里的 access violation）→ 走
/// <c>SetUnhandledExceptionFilter</c>。这条最关键也最难：进程此刻已经半死，
/// 托管堆可能分配不出来、任何锁都可能已被 AV 打断而永远等不到。</item>
/// </list>
///
/// <para><b>原生路径的铁律：零堆分配、零加锁。</b>
/// <c>File.AppendAllText</c> 会分配内存并抢 <c>_writeLock</c>。若 AV 恰好发生在别的线程
/// 持有该锁时，我们就把"记录崩溃"变成了"永久挂起" —— 进程还在、界面还在、却再也不会有
/// 任何后续动作也不退出，比不记录更糟。所以这条路径只允许两次 syscall：
/// 取系统时间、写文件。模块名靠启动时拍好的非托管快照二分查表（纯内存比对），
/// 异常信息靠 <c>Marshal.ReadInt32</c> 逐字段读 —— 全程不碰任何托管对象。</para>
///
/// <para>判据：崩溃路径上出现的每一个 <c>new</c>、每一次 <c>ToString()</c>、
/// 每一次 LINQ，都是"这一行可能把唯一的证据变成沉默"的地方。</para>
///
/// <para><b>落盘位置</b>：<see cref="AppPaths.LogDir"/>/crash.log。代理模式下
/// <see cref="AppPaths.LogDir"/> 已按账户分家（logs/&lt;账户&gt;/），
/// 多个目标账户各写各的，CREATOR OWNER 机制下互不干扰。</para>
/// </summary>
public static class CrashLog
{
    // ───────────────────────────── 常量 / 状态 ─────────────────────────────

    private const string CrashFileName = "crash.log";

    private const string ExitMarkerFileName = "last_run.state";

    private const string CleanExitToken = "clean-exit";

    /// <summary>
    /// 交接标记：这一轮不是"上一轮的结局"，只是把活交给别的进程（如提权重启）。
    /// 少了它，每次提权启动都会被 ProcessExit 写成"正常退出"，
    /// 真正跑的那一轮崩没崩就被彻底掩盖了。
    /// </summary>
    private const string HandoffToken = "handoff";

    private static bool _handoff;

    private const int CrashBufferSize = 4096;

    /// <summary>轮转阈值：4MB。崩溃日志的价值全在"最近几次"，无限增长只会把盘吃满。</summary>
    private const long MaxCrashLogBytes = 4L * 1024 * 1024;

    private static readonly object _writeLock = new();

    private static bool _installed;

    /// <summary>
    /// 崩溃路径专用的文件句柄，装的时候就打开并长期持有。
    /// 崩溃那一刻再开文件是一次分配 + 一次可能失败的创建调用，赌不起。
    /// 以 FILE_APPEND_DATA 打开 → 内核维护写指针，崩溃路径不必调 SetFilePointer。
    /// </summary>
    private static IntPtr _crashFile = IntPtr.Zero;

    /// <summary>崩溃路径的固定缓冲（非托管），不参与 GC。</summary>
    private static IntPtr _crashBuffer = IntPtr.Zero;

    /// <summary>
    /// 退出标记：下次启动据此判断"上次是崩的还是自己关的"。
    /// 跟着 <see cref="AppPaths.LogDir"/> 走而不是用相对路径 —— 相对路径取决于当前工作目录，
    /// 而快捷方式启动 / 提权重启 / 代理自启三种入口的 CWD 各不相同，同一个进程重启两次
    /// 可能读到两个不同的标记文件，判据就废了。
    /// </summary>
    private static string _exitMarker = string.Empty;

    public static string CrashLogPath => Path.Combine(AppPaths.LogDir, CrashFileName);

    public static string ExitMarkerPath => _exitMarker;

    // ───────────────────────────── 安装 ─────────────────────────────

    /// <summary>
    /// 装上全部崩溃路径。<b>必须在任何业务代码跑之前调用</b>（<c>Program.Main</c> 的第一件事）——
    /// 晚装就意味着早于它的那段代码崩了照样没人管。
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        // 标记路径必须在 AppPaths.AgentMode 定下来之后算：代理模式与主控端的 LogDir 不同，
        // 而 Install 是全进程最早跑的那段代码，那之前没人设过 AgentMode。
        _exitMarker = Path.Combine(AppPaths.LogDir, ExitMarkerFileName);

        // 每一段独立 try：日志设施本身绝不能成为启动失败的原因。
        try
        {
            PrepareCrashFile();
        }
        catch
        {
        }

        try
        {
            SnapshotModules();
        }
        catch
        {
        }

        // ① 托管异常。后台线程 / 线程池 / 非 UI 线程抛的都会走这里；
        //    UI 线程那条由 App.UnhandledException 单独接（它能拿到 WinUI 的上下文）。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteManaged("托管异常（" + (e.IsTerminating ? "即将终止进程" : "未终止") + "）",
                e.ExceptionObject as Exception);

        // ② 漏掉的 async 异常。记完必须 SetObserved，否则这条记录本身会把进程带走。
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteManaged("未观察的 Task 异常（async 漏接）", e.Exception);
            e.SetObserved();
        };

        // ③ 原生崩溃。链式调用 CoreCLR 自己的 filter：只旁路记录，不改变崩溃后的行为
        //    （弹 WER、写事件日志、终止都照旧）。
        //
        //    返回值是函数指针而不是委托，要自己转一次。转换放在这里（进程启动期）而不是
        //    崩溃路径上 —— Marshal.GetDelegateForFunctionPointer 会分配，
        //    正好是这条路径承诺不做的操作。
        try
        {
            var previous = SetUnhandledExceptionFilter(CrashFilter);
            if (previous != IntPtr.Zero)
            {
                _previousFilter = Marshal.GetDelegateForFunctionPointer<UnhandledExceptionFilter>(previous);
            }
        }
        catch
        {
        }

        // ④ 正常退出打标记。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => MarkCleanExit();

        WriteSessionHeader();
    }

    // ───────────────────────────── 托管路径（可以随便分配） ─────────────────────────────

    /// <summary>写一条带完整异常栈的记录。</summary>
    public static void WriteManaged(string title, Exception? exc)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 78))
                   .AppendLine(Stamp() + "  " + title)
                   .AppendLine("  Runtime   : " + Environment.Version)
                   .AppendLine("  PID       : " + Environment.ProcessId)
                   .AppendLine("  账户      : " + Environment.UserName)
                   .AppendLine("  线程      : " + CurrentManagedThreadId())
                   .AppendLine("  模式      : " + (AppPaths.AgentMode ? "会话代理" : "主控端"))
                   .AppendLine("  BaseDir   : " + AppPaths.BaseDir);

            if (exc is AccessViolationException av)
            {
                // 访问违规是本项目最需要盯的那一类：FreeRDP / D3D 里崩就是这个。
                //
                // ❗ 它<b>通常不走 SetUnhandledExceptionFilter</b>。实测（_probe/crashprobe，
                // 2026-10-06）：在 P/Invoke 里让原生函数写只读页制造真 AV，进程确实崩了
                // （退出码 139），但 UEF 一次都没被调用 —— CLR 跨过 P/Invoke 边界把 AV
                // 消费掉、 转成托管的 AccessViolationException 再 FailFast，
                // 全程不经过 UEF。打开 EnableConsumingManagedExceptionsFromNativeHosts
                // 开关也不改变这一点。
                //
                // 所以对这类异常要显式标出来：它是原生层的锅，栈顶那个 P/Invoke 帧
                // 就是崩进去的原生调用，不是托管代码写错了。
                builder.AppendLine("  ---- 原生层访问违规（AV） ----")
                       .AppendLine("  判定      : 由原生层冒出来的，不是托管代码的逻辑错误")
                   .AppendLine("  注意      : 这类崩溃不会经过 SetUnhandledExceptionFilter，"
                               + "UEF 里那套零分配记录是多余的")
                   .AppendLine("  栈顶帧    : 崩进去的原生调用（P/Invoke 目标）")
                   .AppendLine("  待查      : 该原生 DLL 内部；对照 crash.log 上方 session "
                               + "段看崩前在做什么");
            }

            builder.AppendLine(exc is not null
                ? "  ---- 异常 ----"
                : "  ---- 无 Exception 对象（抛出的是非 Exception 实例） ----");

            if (exc is not null)
            {
                builder.AppendLine(Indent(exc.ToString()));
            }

            AppendTail(builder, 20);
            WriteRaw(builder.ToString());
        }
        catch
        {
        }
    }

    /// <summary>写一条纯文字记录。用于"发现异常状态"这类诊断标记。</summary>
    public static void WriteNote(string message)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(new string('-', 78))
                   .AppendLine(Stamp() + "  " + message)
                   .AppendLine("  PID " + Environment.ProcessId + " / 线程 " + CurrentManagedThreadId());
            AppendTail(builder, 10);
            WriteRaw(builder.ToString());
        }
        catch
        {
        }
    }

    private static void WriteSessionHeader()
    {
        try
        {
            var crashed = !IsLastRunClean();
            WriteRaw(
                new string('=', 78) + Environment.NewLine
                + Stamp() + "  进程启动" + Environment.NewLine
                + "  PID       : " + Environment.ProcessId + Environment.NewLine
                + "  账户      : " + Environment.UserName + Environment.NewLine
                + "  模式      : " + (AppPaths.AgentMode ? "会话代理" : "主控端") + Environment.NewLine
                + "  BaseDir   : " + AppPaths.BaseDir + Environment.NewLine
                + "  上次退出  : " + (crashed ? "未正常结束（崩溃或被强杀）—— 往上翻本文件第一条记录" : "正常")
                + Environment.NewLine);
        }
        catch
        {
        }
    }

    // ───────────────────────────── 原生崩溃路径（零分配 / 零加锁） ─────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int UnhandledExceptionFilter(IntPtr exceptionPointers);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetUnhandledExceptionFilter(UnhandledExceptionFilter callback);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(IntPtr moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimeAsFileTime(out long fileTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(IntPtr file);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr file, IntPtr buffer, uint count, out uint written, IntPtr overlapped);

    /// <summary>CoreCLR 自己装的那个 filter。转回去，崩溃行为保持原样。</summary>
    private static UnhandledExceptionFilter? _previousFilter;

    /// <summary>必须被静态字段引用住，否则回调时可能已被 GC 回收 → 跳进野指针。</summary>
    private static readonly UnhandledExceptionFilter CrashFilter = OnNativeCrash;

    private static int OnNativeCrash(IntPtr exceptionPointers)
    {
        try
        {
            if (_crashFile != IntPtr.Zero && _crashBuffer != IntPtr.Zero)
            {
                var length = BuildNativeRecord(exceptionPointers);
                if (length > 0)
                {
                    WriteFile(_crashFile, _crashBuffer, (uint)length, out _, IntPtr.Zero);
                    FlushFileBuffers(_crashFile);
                }
            }
        }
        catch
        {
            // 这一层不允许有任何漏出来的东西
        }

        try
        {
            // UEF 的返回值语义是"是否已处理"，我们不替 CoreCLR 做决定。
            return _previousFilter is not null ? _previousFilter(exceptionPointers) : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// 1601-01-01（FILETIME 纪元）到 1970-01-01（Unix 纪元）的毫秒差。
    ///
    /// <b>这个数字写错不会崩，只会安静地给出错误年份</b> —— 2026-10-06 实际写成
    /// 116444736000000000（多了三个数量级），时间戳就成了 1970-01-29 47382803。
    /// 冒烟靠"与 DateTime.UtcNow 相差 5 秒内"这条行为断言抓到的，形状断言抓不到。
    /// </summary>
    private const long UnixEpochMilliseconds = 11644473600000L;

    /// <summary>
    /// 在非托管缓冲里拼一条崩溃记录。全程只有栈上 Span 与 <c>Marshal.Read/WriteInt32</c>：
    /// 没有 <c>new</c>、没有 <c>ToString()</c>、没有 LINQ、没有锁。
    /// </summary>
    private static int BuildNativeRecord(IntPtr exceptionPointers)
    {
        var pos = 0;

        pos = PutAscii(pos, "==================== NATIVE CRASH ====================\r\n");

        if (GetSystemTimeAsFileTime(out var fileTime))
        {
            pos = PutUtcStamp(pos, (fileTime / 10000L) - UnixEpochMilliseconds);
        }

        pos = PutAscii(pos, "  type        : native unhandled exception (access violation / stack overflow)\r\n");

        // EXCEPTION_POINTERS 第一个字段就是 EXCEPTION_RECORD*，两个字段手动读，不走泛型版
        // Marshal.PtrToStructure（那会走类型初始化与可能的装箱）。
        var address = IntPtr.Zero;
        var code = 0u;
        if (exceptionPointers != IntPtr.Zero)
        {
            var record = Marshal.ReadIntPtr(exceptionPointers, 0);
            if (record != IntPtr.Zero)
            {
                code = unchecked((uint)Marshal.ReadInt32(record, 0));
                address = Marshal.ReadIntPtr(record, 8);
            }
        }

        pos = PutHex(pos, "  exception   : 0x", code);
        pos = PutHex(pos, "  fault addr  : 0x", (ulong)address.ToInt64());
        pos = PutAscii(pos, "  module      : ");
        pos = PutModuleName(pos, address);
        pos = PutAscii(pos, "\r\n  thread id   : ");
        pos = PutNumber(pos, GetCurrentThreadId());
        pos = PutAscii(pos, "\r\n  process id  : ");
        pos = PutNumber(pos, GetCurrentProcessId());

        // 主模块基址：故障地址减它 = 模块内偏移，正好对上事件日志 1000 的错误偏移。
        pos = PutHex(pos, "\r\n  image base  : 0x", (ulong)GetModuleHandleW(IntPtr.Zero).ToInt64());
        pos = PutAscii(pos, "\r\n  note        : no managed exception object or stack for this one."
                            + " See the session section above in this file for what was running.\r\n");

        return pos;
    }

    // ── 非托管缓冲写入原语 ──

    private static int PutAscii(int pos, string value)
    {
        for (var i = 0; i < value.Length && pos < CrashBufferSize - 4; i++)
        {
            var ch = value[i];
            // 调用点全是 ASCII 字面量（加载时已驻留），走到非 ASCII 只可能是意外 → 写 '?'。
            Marshal.WriteByte(_crashBuffer, pos++, ch < 128 ? (byte)ch : (byte)'?');
        }

        return pos;
    }

    private static int PutNumber(int pos, uint value)
    {
        Span<byte> digits = stackalloc byte[10];
        var n = 0;
        do
        {
            digits[n++] = (byte)('0' + (value % 10));
            value /= 10;
        }
        while (value > 0 && n < digits.Length);

        for (var i = n - 1; i >= 0 && pos < CrashBufferSize - 4; i--)
        {
            Marshal.WriteByte(_crashBuffer, pos++, digits[i]);
        }

        return pos;
    }

    private static int PutHexValue(int pos, ulong value)
    {
        const string hex = "0123456789ABCDEF";

        // 必须是可写 span：先填后读的临时数组。写成 ReadOnlySpan 会在编译期报
        // CS8331（不能给只读变量赋值），而那条错误在 XAML 工程里会连带一串 WMC 报错，
        // 很容易被误判成 XAML 出了问题（见 MEMORY「XAML 纪律」条）。
        Span<byte> digits = stackalloc byte[16];
        var n = 0;
        var v = value;
        do
        {
            digits[n++] = (byte)hex[(int)(v & 0xF)];
            v >>= 4;
        }
        while (v > 0 && n < digits.Length);

        for (var i = n - 1; i >= 0 && pos < CrashBufferSize - 4; i--)
        {
            Marshal.WriteByte(_crashBuffer, pos++, digits[i]);
        }

        return pos;
    }

    private static int PutHex(int pos, string label, ulong value)
    {
        pos = PutAscii(pos, label);
        return PutHexValue(pos, value);
    }

    /// <summary>
    /// 把时间戳写进缓冲。<b>不调用 <c>DateTime.ToString</c></c>：那会分配字符串。
    /// 纯整数历法运算，缓冲区也是栈上的。
    /// </summary>
    private static int PutUtcStamp(int pos, long unixMs)
    {
        var days = unixMs / 86400000L;
        var rem = (int)(unixMs % 86400000L);
        if (rem < 0)
        {
            rem += 86400000;
            days -= 1;
        }

        var year = 1970;
        while (true)
        {
            var yearDays = IsLeapYear(year) ? 366 : 365;
            if (days < yearDays)
            {
                break;
            }

            days -= yearDays;
            year += 1;
        }

        var month = 1;
        for (var i = 0; i < 12; i++)
        {
            var len = MonthLength(year, i + 1);
            if (days < len)
            {
                month = i + 1;
                break;
            }

            days -= len;
            month = i + 1;
        }

        pos = PutAscii(pos, "  time (UTC)  : ");
        pos = PutPadded(pos, (uint)year, 4);
        pos = PutAscii(pos, '-');
        pos = PutPadded(pos, (uint)month, 2);
        pos = PutAscii(pos, '-');
        pos = PutPadded(pos, (uint)days + 1, 2);
        pos = PutAscii(pos, ' ');
        pos = PutPadded(pos, (uint)(rem / 3600000), 2);
        pos = PutAscii(pos, ':');
        pos = PutPadded(pos, (uint)(rem / 60000 % 60), 2);
        pos = PutAscii(pos, ':');
        pos = PutPadded(pos, (uint)(rem / 1000 % 60), 2);
        return PutAscii(pos, " UTC\r\n");
    }

    private static int PutAscii(int pos, char value)
    {
        if (pos < CrashBufferSize - 4)
        {
            Marshal.WriteByte(_crashBuffer, pos++, value < 128 ? (byte)value : (byte)'?');
        }

        return pos;
    }

    private static int PutPadded(int pos, uint value, int width)
    {
        Span<byte> digits = stackalloc byte[12];
        var n = 0;
        var v = value;
        do
        {
            digits[n++] = (byte)('0' + (v % 10));
            v /= 10;
        }
        while (v > 0 && n < digits.Length);

        for (var i = n; i < width && pos < CrashBufferSize - 4; i++)
        {
            Marshal.WriteByte(_crashBuffer, pos++, (byte)'0');
        }

        for (var i = n - 1; i >= 0 && pos < CrashBufferSize - 4; i--)
        {
            Marshal.WriteByte(_crashBuffer, pos++, digits[i]);
        }

        return pos;
    }

    private static int MonthLength(int year, int month)
    {
        switch (month)
        {
            case 1: case 3: case 5: case 7: case 8: case 10: case 12: return 31;
            case 4: case 6: case 9: case 11: return 30;
            case 2: return IsLeapYear(year) ? 29 : 28;
            default: return 30;
        }
    }

    private static bool IsLeapYear(int year) =>
        (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    // ───────────────────────────── 模块快照（启动时拍，崩溃时查） ─────────────────────────────

    /// <summary>
    /// <c>MODULEINFO</c>（psapi）的真实布局。
    ///
    /// ❗ <b>SizeOfImage 必须是 <see cref="IntPtr"/>（8 字节），不能写 uint。</b>
    /// MSDN 上把这一字段标成了 <c>PVOID</c>，看起来像笔误，其实是真实 ABI：
    /// 写成 <c>uint</c> 会让 <c>SizeOfImage</c> 只读到低 32 位，
    /// 后面的 <c>ModuleHandle</c> / <c>Flags</c> 全部错位一格。
    /// 症状很隐蔽：模块区间变成"从基址到几百 TB"，二分查找怎么写都命中不了正确的行 ——
    /// 2026-10-06 首次跑行为验证时，快照首行是
    /// <c>0000000000008000|000000000A3492B0|psapi.dll</c>，174TB 的 psapi.dll。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ModuleInfo
    {
        public IntPtr BaseOfImage;
        public IntPtr SizeOfImage;   // 见上方注释：PVOID 是真实 ABI，不是笔误
        public IntPtr EntryPoint;
        public IntPtr ModuleHandle;
        public uint Flags;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModules(
        IntPtr process, IntPtr[] modules, uint size, out uint needed);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(
        IntPtr process, IntPtr module, out ModuleInfo info, uint size);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleBaseNameW(
        IntPtr process, IntPtr module, StringBuilder name, uint size);

    /// <summary>行格式：<c>基址16位HEX | 结束地址16位HEX | 模块名\n</c>，按基址升序。</summary>
    private const int ModuleLineStartWidth = 16;
    private const int ModuleNameOffset = 34;

    private static IntPtr _moduleSnapshot = IntPtr.Zero;

    private static int _moduleSnapshotLength;

    private static void SnapshotModules()
    {
        const int maxModules = 512;

        var modules = new IntPtr[maxModules];
        if (!EnumProcessModules(
            GetCurrentProcess(), modules, (uint)(modules.Length * IntPtr.Size), out var needed))
        {
            return;
        }

        var count = (int)Math.Min(needed / (uint)IntPtr.Size, (uint)maxModules);
        if (count <= 0)
        {
            return;
        }

        _moduleSnapshot = Marshal.AllocHGlobal(count * 128 + 64);
        _moduleSnapshotLength = 0;
        _moduleLineCount = 0;

        var list = new List<(ulong Start, ulong End, string Name)>(count);
        var self = GetCurrentProcess();
        var buffer = new StringBuilder(64);
        var infoSize = (uint)Marshal.SizeOf<ModuleInfo>();

        for (var i = 0; i < count; i++)
        {
            if (!GetModuleInformation(self, modules[i], out var info, infoSize))
            {
                continue;
            }

            buffer.Clear();
            if (GetModuleBaseNameW(self, modules[i], buffer, (uint)buffer.Capacity) == 0)
            {
                continue;
            }

            var start = (ulong)info.BaseOfImage.ToInt64();
            var size = (ulong)info.SizeOfImage.ToInt64();

            // 大小为 0 的行会让区间退化成空集，二分时永远命中不了。
            // 宁可丢掉这一行（那个模块查不到名字）也不要放进来搅乱排序。
            if (size == 0)
            {
                continue;
            }

            list.Add((start, start + size, buffer.ToString()));
        }

        list.Sort((a, b) => a.Start.CompareTo(b.Start));

        foreach (var (start, end, name) in list)
        {
            var line = start.ToString("X16") + "|" + end.ToString("X16") + "|" + name;
            var bytes = Encoding.ASCII.GetBytes(line);
            Marshal.Copy(bytes, 0, _moduleSnapshot + _moduleSnapshotLength, bytes.Length);
            Marshal.WriteByte(_moduleSnapshot, _moduleSnapshotLength + bytes.Length, (byte)'\n');
            _moduleSnapshotLength += bytes.Length + 1;
            _moduleLineCount++;
        }
    }

    /// <summary>
    /// 二分查模块，把"<c>名字+0x偏移</c>"直接写进崩溃缓冲。
    /// <b>绝不返回字符串</b> —— 返回就要分配，而这条路径的全部意义就是不分配。
    ///
    /// ⚠️ 二分必须在<b>行号</b>上做，不能在字节偏移上做。
    /// 早先的写法把 <c>high</c> 改成 <c>lineStart - 1</c>（字节偏移），
    /// 于是 <c>high</c> 落到上一行的 <c>\n</c> 之后 —— 区间与行边界不同步，
    /// 查主模块基址这种"最该命中"的地址反而报"不属于任何已知模块"。
    /// 行为断言（拿 <c>GetModuleHandleW(NULL)</c> 当故障地址）当场抓到了这个。
    /// </summary>
    private static int PutModuleName(int pos, IntPtr address)
    {
        if (_moduleSnapshot == IntPtr.Zero || address == IntPtr.Zero)
        {
            return PutAscii(pos, "(no module snapshot)");
        }

        var target = (ulong)address.ToInt64();
        var lineCount = CountSnapshotLines();
        if (lineCount <= 0)
        {
            return PutAscii(pos, "(module snapshot empty)");
        }

        var low = 0;
        var high = lineCount - 1;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var lineStart = LineStart(mid);
            if (lineStart < 0)
            {
                return PutAscii(pos, "(snapshot corrupt)");
            }

            var startValue = ReadHex(lineStart, ModuleLineStartWidth);
            var endValue = ReadHex(lineStart + ModuleLineStartWidth + 1, ModuleLineStartWidth);

            if (startValue == 0)
            {
                return PutAscii(pos, "(snapshot corrupt)");
            }

            if (target < startValue)
            {
                high = mid - 1;   // 目标在更前面的模块里
                continue;
            }

            if (target >= endValue)
            {
                low = mid + 1;    // 目标在更后面的模块里
                continue;
            }

            // 命中。
            var nameStart = lineStart + ModuleNameOffset;
            var length = 0;
            while (nameStart + length < _moduleSnapshotLength
                && Marshal.ReadByte(_moduleSnapshot, nameStart + length) != (byte)'\n')
            {
                length++;
            }

            if (length == 0)
            {
                return PutAscii(pos, "(unnamed module)");
            }

            for (var i = 0; i < length && pos < CrashBufferSize - 4; i++)
            {
                var b = Marshal.ReadByte(_moduleSnapshot, nameStart + i);
                Marshal.WriteByte(_crashBuffer, pos++, b < 128 ? b : (byte)'?');
            }

            pos = PutAscii(pos, "+0x");
            return PutHexValue(pos, target - startValue);
        }

        return PutAscii(pos, "(address not in any known module)");
    }

    /// <summary>快照里有多少行。Install 时顺手记下来，崩溃路径就不必再数一遍。</summary>
    private static int _moduleLineCount;

    private static int CountSnapshotLines() => _moduleLineCount;

    /// <summary>取第 <paramref name="lineIndex"/> 行（0 起）的起始字节偏移。</summary>
    private static int LineStart(int lineIndex)
    {
        var offset = 0;
        for (var i = 0; i < lineIndex; i++)
        {
            while (offset < _moduleSnapshotLength
                && Marshal.ReadByte(_moduleSnapshot, offset) != (byte)'\n')
            {
                offset++;
            }

            if (offset >= _moduleSnapshotLength)
            {
                return -1;
            }

            offset++;   // 跳过 \n
        }

        return offset;
    }

    private static ulong ReadHex(int offset, int length)
    {
        ulong value = 0;
        for (var i = 0; i < length; i++)
        {
            var c = (char)Marshal.ReadByte(_moduleSnapshot, offset + i);
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => 0
            };

            value = (value << 4) | (uint)digit;
        }

        return value;
    }

    /// <summary>
    /// 声明"本进程已经把活交给别的进程，正常退出不算这一轮的真实结局"。
    /// 典型场景：主控端提权重启 —— 拉起带 <c>--elevated</c> 的新实例后自己 return。
    /// </summary>
    public static void MarkHandoff() => _handoff = true;

    // ───────────────────────────── 退出标记 ─────────────────────────────

    private static bool IsLastRunClean()
    {
        try
        {
            if (!File.Exists(_exitMarker))
            {
                return false;
            }

            // 上一轮是"交接"时不能算正常也不能算异常 —— 真正跑的那一轮自己会写标记。
            return File.ReadAllText(_exitMarker, Encoding.UTF8)
                .TrimStart().StartsWith(CleanExitToken, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void MarkCleanExit()
    {
        if (_handoff)
        {
            // 交接轮次：留一个既非 clean 也非"崩溃"的标记，让下一轮如实报"未知"。
            TryWriteMarker(HandoffToken + Environment.NewLine + Stamp());
            return;
        }

        TryWriteMarker(CleanExitToken + Environment.NewLine + Stamp());
    }

    private static void TryWriteMarker(string content)
    {
        try
        {
            File.WriteAllText(_exitMarker, content, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    // ───────────────────────────── 文件准备 / 写盘 ─────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    private const uint FileAppendData = 0x00000004;
    private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x00000080;

    private static void PrepareCrashFile()
    {
        var path = CrashLogPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        RotateIfTooLarge(path);
        _crashBuffer = Marshal.AllocHGlobal(CrashBufferSize);

        _crashFile = CreateFileW(
            path,
            FileAppendData,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenAlways,
            FileAttributeNormal,
            IntPtr.Zero);
    }

    private static void RotateIfTooLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxCrashLogBytes)
            {
                return;
            }

            var archive = path + ".1";
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }

            File.Move(path, archive);
        }
        catch
        {
        }
    }

    private static void WriteRaw(string text)
    {
        lock (_writeLock)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            if (_crashFile != IntPtr.Zero)
            {
                // 与崩溃路径共用同一个句柄：两个写入口不各持一份文件位置。
                var unmanaged = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
                try
                {
                    Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                    WriteFile(_crashFile, unmanaged, (uint)bytes.Length, out _, IntPtr.Zero);
                    FlushFileBuffers(_crashFile);
                }
                finally
                {
                    Marshal.FreeHGlobal(unmanaged);
                }

                return;
            }

            // 句柄没开成（权限 / 占用）→ 退回按路径写，至少内容还在。
            var path = CrashLogPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// 附上业务日志末尾若干行。崩溃现场最值钱的往往不是栈，而是"崩之前最后在干什么"——
    /// 这一段是那句经验判断的自动化版本。
    /// </summary>
    private static void AppendTail(StringBuilder builder, int lines)
    {
        try
        {
            var dir = AppPaths.LogDir;
            if (!Directory.Exists(dir))
            {
                return;
            }

            var latest = Directory.GetFiles(dir, "*.log")
                .Where(p => !Path.GetFileName(p).StartsWith(CrashFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null)
            {
                return;
            }

            var all = File.ReadAllLines(latest);
            builder.AppendLine("  ---- 业务日志末尾（" + Path.GetFileName(latest) + "） ----");
            foreach (var line in all.Skip(Math.Max(0, all.Length - lines)))
            {
                builder.Append("    ").AppendLine(line.TrimEnd());
            }
        }
        catch
        {
        }
    }

    // ───────────────────────────── 冒烟测试钩子（internal，不参与生产流程） ─────────────────────────────

    /// <summary>
    /// 造一条假的崩溃记录并返回渲染结果，**只供冒烟验证原生路径的正确性**。
    ///
    /// 为什么要专门开这个口子：原生路径的正确性（模块查找、十六进制、历法换算）
    /// 靠源码形状断言验不出来 —— 那只能证明"代码长什么样"，证明不了"算出来对不对"。
    /// 而真正触发一次 access violation 来验证，等于把测试进程自己搞崩。
    /// 所以这里让冒烟能直接调渲染逻辑、对着已知输入核对输出。
    ///
    /// 内部依然走 <see cref="BuildNativeRecord"/> 那条零分配路径，不是另写一份。
    /// </summary>
    internal static string RenderNativeRecordForTest(uint exceptionCode, ulong faultAddress)
    {
        PrepareForTest();

        // 造一个 EXCEPTION_RECORD：+0 = ExceptionCode，+8 = ExceptionAddress。
        var record = Marshal.AllocHGlobal(64);
        var pointers = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteInt32(record, 0, unchecked((int)exceptionCode));
            Marshal.WriteIntPtr(record, 8, (IntPtr)(long)faultAddress);
            Marshal.WriteIntPtr(pointers, 0, record);

            var length = BuildNativeRecord(pointers);
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new byte[length];
            Marshal.Copy(_crashBuffer, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(record);
            Marshal.FreeHGlobal(pointers);
        }
    }

    /// <summary>只初始化缓冲与模块快照，不装任何处理器（供冒烟反复调用）。</summary>
    internal static void PrepareForTest()
    {
        if (_crashBuffer == IntPtr.Zero)
        {
            _crashBuffer = Marshal.AllocHGlobal(CrashBufferSize);
        }

        if (_moduleSnapshot == IntPtr.Zero)
        {
            SnapshotModules();
        }
    }

    /// <summary>快照诊断（供冒烟排查用）：行数、字节数、首行原文。</summary>
    internal static string DescribeModuleSnapshot()
    {
        if (_moduleSnapshot == IntPtr.Zero)
        {
            return "(快照为空)";
        }

        var firstLineEnd = 0;
        while (firstLineEnd < _moduleSnapshotLength
            && Marshal.ReadByte(_moduleSnapshot, firstLineEnd) != (byte)'\n')
        {
            firstLineEnd++;
        }

        var first = new byte[firstLineEnd];
        Marshal.Copy(_moduleSnapshot, first, 0, firstLineEnd);
        return $"行数={_moduleLineCount} 字节={_moduleSnapshotLength} 首行={Encoding.ASCII.GetString(first)}";
    }

    // ───────────────────────────── 小工具 ─────────────────────────────

    private static string Indent(string text) => "    " + text.Replace("\n", "\n    ");

    private static string Stamp() => "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "]";

    private static int CurrentManagedThreadId()
    {
        try
        {
            return Environment.CurrentManagedThreadId;
        }
        catch
        {
            return -1;
        }
    }
}
