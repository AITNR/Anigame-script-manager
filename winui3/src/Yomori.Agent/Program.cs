// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;

using Yomori.Services;

namespace Yomori;

/// <summary>
/// 会话代理的进程入口（<c>Yomori.Agent.exe</c>）。
///
/// <para>【为什么要独立一个 exe，而不是给主控端加个 <c>--rdp-agent</c> 参数】</para>
///
/// 那段历史值得留在这里：代理本来确实是主控端同一个 exe 的一部分，靠参数分流。
/// 代价是**部署时只能把整个程序目录复制一份**到 <c>%ProgramData%\Yomori\agent</c>
/// （代理是常驻进程，公共目录里必须有它自己的完整依赖，不能指向主控账户目录 ——
/// 目标账户读不到）。于是每个目标账户白占 228MB，其中 WinUI 界面栈、WindowsAppSDK、
/// ONNX/DirectML、安装器残留加起来约 128MB，代理一次都用不到。
///
/// 独立工程之后：
/// <list type="bullet">
///   <item>产物只有「.NET 运行时 + 桥协议 + 任务执行」这一条链，没有界面栈；</item>
///   <item>manifest 天生就是 <c>asInvoker</c>（不再需要部署时用 mt.exe 去改写副本清单，
///         那段「改不成就中止部署」的脆弱逻辑对代理失效）；</item>
///   <item>主控端与代理之间**仍走同一份源码**（本工程链接 Yomori.App 的
///         Models/Services/Helpers），不存在两套桥协议各自演化的风险。</item>
/// </list>
///
/// <para>本地主控端目录里没有 <c>agent\</c> 子目录时（开发期直接 dotnet build），
/// 部署会回退到旧行为：复制主控端整个目录、按 <c>--rdp-agent</c> 拉起 Yomori.exe。
/// 也就是说这个 exe 是「更省的路径」，不是「唯一路径」。</para>
/// </summary>
internal static class Program
{
    /// <summary>启动上下文日志：进程秒退时唯一能看到的线索。</summary>
    private const string BootLogName = "agent_boot.log";

    private static int Main(string[] args)
    {
        try
        {
            // .NET 8 默认不带 GBK(936)：代理要读 netsh / cmdkey / 旧控制台的输出，
            // 没有这个 provider 会拿到一堆问号，进而把「连不上」误判成「密码错」。
            // 主控端那边是靠 ExternalLogTail 的静态构造顺带注册的，代理不能指望那一步，
            // 这里显式注册一次（重复注册是幂等的）。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // AgentHost.Run 内部会置 AppPaths.AgentMode = true（日志与统计按账户分家），
            // 然后阻塞在常驻循环上：正常情况永不返回，返回即进程结束。
            return AgentHost.Run(args);
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            WriteBootLog("代理宿主启动失败：" + ex);
            return 1;
        }
    }

    private static void WriteBootLog(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, BootLogName);
            File.AppendAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // 写日志失败不应引发二次崩溃：代理此刻已经在异常路径上
        }
    }
}
