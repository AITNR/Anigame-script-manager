// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace YukinoChan.Services;

/// <summary>
/// RDP 会话代理的宿主：<c>--rdp-agent</c> 模式下的进程入口。
///
/// 【为什么不走 WinUI 启动】
/// 原实现在 agent 模式下也走 <c>Application.Start</c>：加载 App.xaml 的主题资源与
/// XBF、造一个 XAML <c>Window</c> 再立刻 <c>AppWindow.Hide()</c>，
/// 全部目的只为换一个"消息循环 + 进程别退出"。
/// 但代理的执行链（<see cref="RdpAgentRunner"/> / <see cref="ScriptRunner"/> /
/// <see cref="RdpBridge"/> / 截图 / 关机）**不引用任何 XAML 类型** ——
/// 全项目引用 XAML 的只有 <c>RdpD3DRenderer</c>、<c>MainViewModel</c>、
/// <c>DialogHelper</c>、<c>WindowHelper</c>，一个都不在这条链上。
/// 而 WinUI 3 空载启动的常驻内存是几十 MB 量级（实测每个空闲代理 Private ≈ 105MB /
/// WS ≈ 155MB，2026-10-05），代理又是「每个目标账户一个、全天常驻」——
/// 三个账户就是三份白付的钱。
///
/// 所以这里用 <see cref="DispatcherQueueController"/> 在当前线程建一个消息泵：
/// 同样能维持进程、能派发系统通知，但不碰 XAML 运行时、不加载主题资源。
/// 语义与原来的隐藏窗口完全一致：进程不主动退出，只有桥目录彻底不可用才退出。
/// </summary>
public static class AgentHost
{
    /// <summary>Agent 登录后等待桌面环境就绪的时间（秒）。</summary>
    private const int AgentStartupDelaySeconds = 5;

    /// <summary>
    /// 常驻等待指令时的轮询间隔（秒）。
    ///
    /// 既是"查一次有没有新指令"的节奏，也是"写一次心跳"的节奏 ——
    /// 主控端 RdpHeartbeat.TimeoutSeconds = 20 秒，1 秒一次留足余量，不会误报失联。
    /// </summary>
    private const int AgentPollIntervalSeconds = 1;

    /// <summary>
    /// 连续写心跳失败多少次就判定桥目录彻底不可用（次）。
    /// 只有到这一步才退出 —— "没有指令"绝不该是退出的理由。
    /// </summary>
    private const int AgentBridgeFailureLimit = 30;

    /// <summary>
    /// 启动代理进程。<b>阻塞</b>到进程退出为止，调用方（<c>Program.Main</c>）拿到控制权时进程已结束。
    ///
    /// 绝不返回：正常情况下靠 <see cref="ExitAgent"/> 直接结束进程；
    /// 桥目录彻底不可用时同样 <c>Environment.Exit</c>。保留 <c>int</c> 返回值只为让
    /// 未来想改成"跑完返回码再退出"时不必改签名。
    /// </summary>
    public static int Run(IReadOnlyList<string> args)
    {
        // 多账户共用同一份代理副本，日志/统计必须按账户分家（见 AppPaths.AgentMode 说明）。
        // 必须在任何 FileLogger / RuntimeStatsManager 构造之前置位。
        AppPaths.AgentMode = true;

        // 确定代理该读写哪座桥：远程场景由 --bridge: 指定，
        // 本机多账户场景则按"自己登录的是哪个账户"自动派生（与主控端算出的目录一致）。
        RdpBridge.ConfigureAgent(RdpAgentRunner.ExtractBridgePath(args));

        var controller = TryCreateDispatcher();
        if (controller is not null)
        {
            NotificationService.Initialize(controller.DispatcherQueue);
        }

        // 代理循环跑在工作线程：它全程 await（Task.Delay / 文件 IO / 进程管理），
        // 从不碰 UI 线程，也就不需要消息泵与它同线程。
        var loop = Task.Run(RunAgentLoopAsync);

        if (controller is null)
        {
            // 拿不到消息泵（通知降级为仅记录日志）时，主线程同步等循环结束。
            // 循环是 while(true)，正常不会返回；返回了说明桥目录废了。
            WaitLoop(loop);
            return 0;
        }

        // 阻塞在这里 —— 这就是原来那个隐藏 XAML 窗口的全部作用：维持进程存活。
        //
        // ⚠️ WinAppSDK 的 DispatcherQueue **没有**阻塞式 Run()（那是 UWP CoreDispatcher 的 API，
        // 名字像但不是同一个类型，别再当成有）。Controller 只管建队列和 ShutdownQueue，
        // 要"跑到没人投消息为止"必须自己拉 Win32 消息循环 —— 这正是 CoreDispatcher 当年的做法。
        // 队列本身是靠线程消息队列驱动的，所以这个循环同时也在泵 controller 那个队列。
        var exitCode = 0;
        try
        {
            exitCode = PumpMessages();
        }
        finally
        {
            SafeShutdown(controller);
        }

        return exitCode;
    }

    /// <summary>
    /// Win32 消息循环：取一条消息就派发一条，直到收到 <c>WM_QUIT</c>。
    ///
    /// 为什么必须是它：进程要活得有消息循环，WinAppSDK 又没提供现成的阻塞泵。
    /// 有了它就不必再造一个 XAML 窗口（那要加载整套 XAML 运行时 + 主题资源 + XBF，
    /// 空载就是几十 MB，而代理是每个目标账户一份、全天常驻）。
    ///
    /// 收尾：<see cref="RdpAgentRunner"/> 那边结束会话若走"注销"会由系统发 WM_QUIT；
    /// 桥目录彻底不可用时 <see cref="ExitAgent"/> 直接 <c>Environment.Exit</c>，也到不了这里。
    /// 所以正常情况下这个循环是"永久阻塞"的 —— 与原实现行为一致。
    /// </summary>
    private static int PumpMessages()
    {
        while (true)
        {
            // PeekMessage 而非 GetMessage：队列空时立刻返回 false，
            // 于是我们每轮还能顺带看一眼 loop 有没有自己结束（理论上不会，
            // 但真到了那一步也不该死等）。
            if (!NativeMethods.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PmRemove))
            {
                if (_loopEnded)
                {
                    return 0;
                }

                Thread.Sleep(50);
                continue;
            }

            if (msg.message == NativeMethods.WmQuit)
            {
                return unchecked((int)msg.wParam.ToInt64());
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    /// <summary>代理循环是否已经自己结束（正常只在桥目录彻底不可用时）。</summary>
    private static volatile bool _loopEnded;

    /// <summary>
    /// 常驻主循环：等指令 → 执行 → 回到等待，只有在桥目录彻底不可用时才退出。
    ///
    /// 关键约束：
    ///   1. "没有指令"进入等待，不再退出（旧实现读不到就 ExitAgent，正是事故根因）。
    ///   2. 等待期间持续写心跳（UpdatedAt 每秒刷新，Phase=idle），主控端据此判定"代理在线"。
    ///   3. 写心跳只在【等待】分支执行；任务执行期间本方法正 await 在 RunAsync 上，
    ///      不会插手，从而不会覆盖 RdpAgentRunner 正在写的任务状态。
    /// </summary>
    private static async Task RunAgentLoopAsync()
    {
        try
        {
            // 刚登录时桌面环境可能还没就绪，等一下再开工，避免脚本启动失败
            await Task.Delay(TimeSpan.FromSeconds(AgentStartupDelaySeconds)).ConfigureAwait(false);

            await WaitForCommandsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            ExitAgent();
        }
        finally
        {
            // 通知消息泵可以收尾了（正常走不到这里 —— 循环是 while(true)，
            // 只有桥目录彻底不可用时才会真的返回）。
            _loopEnded = true;
        }
    }

    private static async Task WaitForCommandsAsync()
    {
        var bridgeFailures = 0;
        var bridge = RdpBridge.Agent;

        while (true)
        {
            if (bridge.TryReadCommand(out var command) && command is not null)
            {
                bridgeFailures = 0;

                // 立刻删掉指令，防止代理被再次拉起时重复执行同一批任务
                bridge.ConsumeCommand();

                // 注意：这里【不挂】Completed → ExitAgent。
                // 常驻代理执行完必须回到等待状态继续待命，而不是退出。
                var runner = new RdpAgentRunner(command);
                await runner.RunAsync(CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            // 没有指令：清掉桥里可能残留的停止请求。
            //
            // 为什么会残留：停止请求只有在执行期间才会被检查，而"点停止 → 清理跑十几秒 →
            // 收尾"这段时间里如果主控端又补了一份（或代理当时正卡在清理里没读），
            // 那份 stop.json 就会一直躺到下一轮。靠 CommandId 校验虽然不会误停新指令，
            // 但排障时看到一份没人消费的 stop.json 极其误导。
            // 这里能安全删除的前提：本分支只在"没有待执行指令"时才走到 ——
            // 有指令在跑时不会进来，不存在把正在用的停止请求删掉的风险。
            bridge.ClearStop();

            // 没有指令：写一条 idle 心跳，证明"代理在线，等待指令"。
            // 任务在跑时不会走到这里，因此不会覆盖 Runner 的任务状态。
            var alive = bridge.UpdateStatus(status =>
            {
                status.Phase = "idle";
                status.StatusText = "代理在线，等待指令。";
                status.AgentUser = Environment.UserName;
                status.CurrentTask = string.Empty;
                return status;
            });

            if (alive)
            {
                bridgeFailures = 0;
            }
            else
            {
                // 连状态都写不出去 —— 共享目录已不可用，累加到阈值再退出，避免死撑
                bridgeFailures++;
                if (bridgeFailures >= AgentBridgeFailureLimit)
                {
                    AppPaths.WriteStartupError(new IOException(
                        $"会话代理无法写入指令桥目录 {bridge.BridgeDir}：{bridge.LastError}"));
                    ExitAgent();
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(AgentPollIntervalSeconds)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 在当前线程建一个消息泵。
    ///
    /// 用 <see cref="DispatcherQueueController"/> 而不是 XAML 窗口：前者只要
    /// CoreMessaging 的线程消息队列，不碰 XAML 运行时，也不加载任何主题资源。
    ///
    /// 拿不到就返回 null —— 通知属于"锦上添花"，不能因此让代理起不来。
    /// 降级后 <see cref="NotificationService"/> 不会被 Initialize，
    /// 其 <c>Show</c> 会直接返回 false，调用方照旧降级为仅记录日志。
    /// </summary>
    private static DispatcherQueueController? TryCreateDispatcher()
    {
        try
        {
            return DispatcherQueueController.CreateOnCurrentThread();
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            return null;
        }
    }

    private static void WaitLoop(Task loop)
    {
        try
        {
            loop.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            ExitAgent();
        }
    }

    private static void SafeShutdown(DispatcherQueueController controller)
    {
        try
        {
            controller.ShutdownQueue();
        }
        catch
        {
            // 进程本来就要退出，忽略
        }
    }

    private static void ExitAgent()
    {
        try
        {
            Environment.Exit(0);
        }
        catch
        {
            // 忽略
        }
    }
}
