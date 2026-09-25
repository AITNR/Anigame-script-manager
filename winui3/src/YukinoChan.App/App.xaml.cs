// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using YukinoChan.Services;

namespace YukinoChan;

public partial class App : Application
{
    /// <summary>Agent 登录后等待桌面环境就绪的时间（秒）。</summary>
    private const int AgentStartupDelaySeconds = 5;

    /// <summary>
    /// 常驻等待指令时的轮询间隔（秒）。
    /// 既是"查一次有没有新指令"的节奏，也是"写一次心跳"的节奏 ——
    /// 主控端 RdpHeartbeat.TimeoutSeconds = 20 秒，1 秒一次留足余量，不会误报失联。
    /// </summary>
    private const int AgentPollIntervalSeconds = 1;

    /// <summary>
    /// 连续写心跳失败多少次就判定桥目录彻底不可用（次）。
    /// 只有到这一步才退出 —— "没有指令"绝不该是退出的理由。
    /// </summary>
    private const int AgentBridgeFailureLimit = 30;

    public static MainWindow? MainWindow { get; private set; }

    /// <summary>全局共享视图模型，必须在 UI 线程上创建（内部会捕获 DispatcherQueue）。</summary>
    public static ViewModels.MainViewModel ViewModel { get; private set; } = null!;

    public static bool IsAgentMode { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var args0 = Environment.GetCommandLineArgs();
        if (RdpAgentRunner.IsAgentInvocation(args0))
        {
            IsAgentMode = true;
            // 远程场景下桥目录由 --bridge: 指定，必须在使用 RdpBridge 之前生效
            RdpBridge.Configure(RdpAgentRunner.ExtractBridgePath(args0));
            LaunchAgentMode();
            return;
        }

        ViewModel = new ViewModels.MainViewModel();
        MainWindow = new MainWindow();
        MainWindow.Activate();
        NotificationService.Initialize(MainWindow.DispatcherQueue);
        ViewModel.Initialize();
    }

    /// <summary>
    /// RDP 会话代理模式：不显示主界面，只用一个隐藏窗口维持进程与消息循环。
    ///
    /// 【常驻】代理随目标账户登录由公共启动目录的快捷方式拉起后，不再"读一次指令就退出"：
    /// 读不到指令就保持在线等待（持续刷新心跳），读到指令才执行，执行完回到等待状态。
    /// 这样主控端在任何时候下发指令，都有人在场接手 —— 不必、也不能再靠"隔空拉起"。
    /// </summary>
    private async void LaunchAgentMode()
    {
        Microsoft.UI.Xaml.Window? host = null;
        try
        {
            host = new Microsoft.UI.Xaml.Window { Title = "雪乃酱 · RDP 会话代理" };
            host.Activate();
            host.AppWindow.Hide();
            NotificationService.Initialize(host.DispatcherQueue);

            // 刚登录时桌面环境可能还没就绪，等一下再开工，避免脚本启动失败
            await Task.Delay(TimeSpan.FromSeconds(AgentStartupDelaySeconds)).ConfigureAwait(true);

            await RunAgentLoopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            ExitAgent();
        }
        finally
        {
            _ = host;
        }
    }

    /// <summary>
    /// 常驻主循环：等指令 → 执行 → 回到等待，只有在桥目录彻底不可用时才退出。
    ///
    /// 关键约束：
    ///   1. "没有指令"进入等待，不再退出（旧实现读不到就 ExitAgent，正是事故根因）。
    ///   2. 等待期间持续写心跳（UpdatedAt 每秒刷新，Phase=idle），主控端据此判定"代理在线"。
    ///   3. 写心跳只在【等待】分支执行；任务执行期间本方法正 await 在 RunAsync 上，
    ///      不会插手，从而不会覆盖 RdpAgentRunner 正在写的任务状态。
    /// </summary>
    private async Task RunAgentLoopAsync()
    {
        var bridgeFailures = 0;

        while (true)
        {
            if (RdpBridge.TryReadCommand(out var command) && command is not null)
            {
                bridgeFailures = 0;

                // 立刻删掉指令，防止代理被再次拉起时重复执行同一批任务
                RdpAgentRunner.ClearCommand();

                // 注意：这里【不挂】Completed → ExitAgent。
                // 常驻代理执行完必须回到等待状态继续待命，而不是退出。
                var runner = new RdpAgentRunner(command);
                await runner.RunAsync(CancellationToken.None).ConfigureAwait(true);
                continue;
            }

            // 没有指令：写一条 idle 心跳，证明"代理在线，等待指令"。
            // 任务在跑时不会走到这里，因此不会覆盖 Runner 的任务状态。
            var alive = RdpBridge.UpdateStatus(status =>
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
                        $"会话代理无法写入指令桥目录 {RdpBridge.BridgeDir}：{RdpBridge.LastError}"));
                    ExitAgent();
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(AgentPollIntervalSeconds)).ConfigureAwait(true);
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

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Services.AppPaths.WriteStartupError(e.Exception);
        }
        catch
        {
            // 启动阶段兜底，失败也不再抛出，避免二次崩溃
        }

        e.Handled = true;
    }
}
