// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Xaml;
using YukinoChan.Services;

namespace YukinoChan;

/// <summary>
/// 自定义入口点（配 DISABLE_XAML_GENERATED_MAIN 使用）。
///
/// 为什么要自己写 Main：
///   WinUI 3 的自动生成 Main 只做「启动 XAML 运行时」一件事，
///   一旦 XAML 初始化失败（例如运行在非交互式窗口站、缺少桌面会话），
///   进程就直接退出，App 的异常处理根本没机会执行，连日志都留不下。
///   实测表现就是「代理被拉起后进程秒退、什么线索都没有」。
///
///   自己接管 Main 之后，可以在 XAML 之前先把「谁在启动、在什么环境启动」落到日志，
///   这样即使后面崩了，也能从日志里看出原因。
/// </summary>
public static class Program
{
    /// <summary>agent 模式下把启动上下文写进该文件，便于排查「进程秒退」。</summary>
    private const string AgentBootLogName = "agent_boot.log";

    /// <summary>M4 自检通道：--embed-auto <host> <user> <password> 启动时自动连接内嵌预览。
    /// 状态全走 VM.AppendLog（日志文件），供外部无人值守验收。</summary>
    /// <summary>M5/M6 自检：--embed-vm host user password 走正式 VM 连接路径（client_mode=embedded）。</summary>
    public static string[]? EmbedVmArgs { get; private set; }
    public static bool EmbedNoKeys { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(
        IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetStartupInfoW(out StartupInfo lpStartupInfo);

    [STAThread]
    private static void Main(string[] args)
    {
        var isAgent = IsAgentInvocation(args);

        if (isAgent)
        {
            WriteAgentBootLog(args);
        }

        // M5/M6 自检：--embed-vm host user password [--embed-nokeys]
        for (var i = 0; i < args.Length - 3; i++)
        {
            if (args[i] == "--embed-vm")
            {
                EmbedVmArgs = new[] { args[i + 1], args[i + 2], args[i + 3] };
                EmbedNoKeys = Array.Exists(args, a => a == "--embed-nokeys");
                break;
            }
        }

        try
        {
            // 走 WinUI 的标准启动流程。unpackaged 模式下用 Application.Start，
            // 由它创建 App 实例并驱动消息循环。
            Application.Start(_ => new App());
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);

            if (isAgent)
            {
                AppendAgentBootLog("XAML 启动失败：" + ex);
            }

            // agent 模式下不要静默退出，留个非零退出码方便从外部观察启动失败
            Environment.Exit(1);
        }
    }

    private static bool IsAgentInvocation(string[] args)
    {
        foreach (var argument in args)
        {
            if (string.Equals(argument, "--rdp-agent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把启动环境写下来：账户、会话、窗口站、桌面 —— 这几项能直接判定
    /// 「是不是跑在非交互式窗口站里」，也就是 WinUI 启动失败的典型原因。
    /// </summary>
    private static void WriteAgentBootLog(string[] args)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 70));
            builder.AppendLine("时间      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("账户      : " + Environment.UserName);
            builder.AppendLine("进程ID    : " + Environment.ProcessId);
            builder.AppendLine("BaseDir   : " + AppContext.BaseDirectory);
            builder.AppendLine("命令行    : " + string.Join(' ', args));
            builder.AppendLine("桌面      : " + DescribeDesktop());
            builder.AppendLine("窗口站    : " + DescribeWindowStation());

            var path = Path.Combine(AppContext.BaseDirectory, AgentBootLogName);
            File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 写日志失败不应影响启动
        }
    }

    private static void AppendAgentBootLog(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, AgentBootLogName);
            File.AppendAllText(path, message + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // 同上
        }
    }

    private static string DescribeWindowStation()
    {
        try
        {
            var handle = GetProcessWindowStation();
            if (handle == IntPtr.Zero)
            {
                return "(拿不到窗口站句柄)";
            }

            var buffer = new StringBuilder(256);
            // UOI_NAME = 2
            return GetUserObjectInformationW(handle, 2, buffer, buffer.Capacity * 2, out _)
                ? buffer.ToString()
                : "(读取窗口站名失败)";
        }
        catch
        {
            return "(查询窗口站异常)";
        }
    }

    private static string DescribeDesktop()
    {
        try
        {
            if (GetStartupInfoW(out var info) && info.lpDesktop != IntPtr.Zero)
            {
                var desktop = Marshal.PtrToStringUni(info.lpDesktop);
                return string.IsNullOrEmpty(desktop) ? "(空)" : desktop;
            }

            return "(lpDesktop 为空 —— 通常意味着非交互式启动)";
        }
        catch
        {
            return "(查询桌面异常)";
        }
    }
}
