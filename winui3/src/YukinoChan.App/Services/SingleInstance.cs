// -*- coding: utf-8 -*-
using System;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace YukinoChan.Services;

/// <summary>
/// 主控端单实例。
///
/// <para>为什么必须有：窗口能收进状态栏之后，程序"看不见但还在跑"，用户很容易再双击一次 exe ——
/// 没有互斥就是两个进程、两个托盘图标、两套任务在跑（两条脚本抢同一个游戏存档，比崩了更糟）。</para>
///
/// <para>实现选型：<c>Mutex</c> 判"是否已经有一份在跑"，<c>EventWaitHandle</c> 负责"把已有窗口叫出来"。
/// 为什么不用窗口消息（<c>RegisterWindowMessage</c> + <c>PostMessage</c>）：WinUI 3 不暴露窗口过程，
/// 想收自定义消息就得对主窗口做子类化（<c>SetWindowSubclass</c>），那是会跟 WinUI 自己的
/// WndProc 打架的活；命名事件 + 后台线程等待再 <c>TryEnqueue</c> 回 UI 线程，干净得多。</para>
///
/// <para>名字一律用 <c>Local\</c> 前缀（本会话内唯一），不用 <c>Global\</c>：多用户同时各开一份是合理的，
/// 而且 Global 需要额外的特权，管理员进程虽然拿得到，但没必要。</para>
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\YukinoChan.MainWindow";
    private const string ActivateEventName = @"Local\YukinoChan.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static Thread? _listener;

    /// <summary>
    /// 抢占主实例身份。返回 false 表示"已经有一份在跑"，调用方应
    /// <see cref="SignalExisting"/> 后立即退出。
    ///
    /// <para>判据是 <c>createdNew</c> 而不是"能不能拿到锁"：同名 Mutex 是普通互斥体，
    /// 第二个进程同样能拿到所有权，只有创建标志能可靠区分谁是第一个。</para>
    /// </summary>
    public static bool TryClaim()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

            if (!createdNew)
            {
                // 已经有一份在跑：把自己这把放掉，别占着
                mutex.Dispose();
                return false;
            }

            _mutex = mutex;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 打不开同名内核对象（权限差异）：宁可允许启动，也不要因为判不出来就开不了程序
            return true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>本进程是否持有主实例身份（诊断用；同时也让 <see cref="_mutex"/> 的引用有实际用途）。</summary>
    public static bool IsOwner => _mutex is not null;

    /// <summary>通知已经在跑的那一份"把窗口叫出来"。没有实例在听就算了。</summary>
    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch
        {
            // 对方正好在退出 / 权限不够：用户再点一次就行，不值得报错
        }
    }

    /// <summary>
    /// 在后台线程等"激活"信号，收到就回 UI 线程执行 <paramref name="onActivate"/>。
    ///
    /// <para>必须是后台线程：这个循环是 <c>while(true)</c>，挂在前台会拖住进程退出。</para>
    /// </summary>
    public static void StartListener(DispatcherQueue queue, Action onActivate)
    {
        if (_listener is not null)
        {
            return;
        }

        try
        {
            _activateEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivateEventName);

            _listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!_activateEvent.WaitOne())
                        {
                            return;
                        }

                        queue.TryEnqueue(() => onActivate());
                    }
                    catch
                    {
                        // 队列已关闭（正在关窗口）：安静退出
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "yukino-single-instance",
            };

            _listener.Start();
        }
        catch
        {
            // 建不起来只是"再点一次 exe 不会叫回窗口"，不该影响正常使用
        }
    }
}
