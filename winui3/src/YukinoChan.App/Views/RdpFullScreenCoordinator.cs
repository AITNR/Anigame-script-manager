// -*- coding: utf-8 -*-
using System;
using YukinoChan.Models;
using YukinoChan.Services;

namespace YukinoChan.Views;

/// <summary>
/// 内嵌画面的「全屏」编排：全屏窗口 + WH_KEYBOARD_LL 键盘接管 + 宿主渲染挂起/恢复。
///
/// 抽成独立类的原因：这段逻辑**与画面控件挂在哪个页面无关**，只依赖一个 <see cref="RdpView"/> 宿主。
/// 实际有两处在用 —— 通道页（正常路径）与管理页（<c>--embed-vm</c> 自检路径）。
/// 原先它内联在管理页里，导致正常模式下用户在通道页点不到全屏（双击/VK_F11 都无处响应）。
///
/// 纪律（沿用原 RdpPage 在真机上验证过的做法）：
/// <list type="bullet">
/// <item>全屏 ≠ 搬运控件 —— WinUI 3 不允许跨窗口移动可视树元素，
///       所以是**第二个 RdpView 接管同一个 client**，会话与画面流不中断；</item>
/// <item>全屏期间宿主 <see cref="RdpView.SuspendRendering"/>，否则两路渲染把 CPU 翻倍；</item>
/// <item>退出全屏**先摘钩子再关窗** —— 顺序反了会让本地键盘在窗口关闭前被吞掉；</item>
/// <item>窗口被系统关掉（Alt+F4）也走同一条复位路径，用 <c>Closed</c> 统一收口。</item>
/// </list>
/// </summary>
internal sealed class RdpFullScreenCoordinator : IDisposable
{
    /// <summary>
    /// 全局唯一的"当前全屏者"。
    /// 内嵌画面全屏后，主窗口的页面仍可被导航重建，新页面会创建**新的**协调器实例；
    /// 没有这道守卫，用户在另一条通道页再点一次全屏就会出现两个窗口抢同一个 client。
    /// </summary>
    private static RdpFullScreenCoordinator? s_active;

    private readonly RdpView _host;
    private readonly Action<string> _log;
    private readonly Action<bool>? _onStateChanged;

    private LowLevelKeyboardHook? _kbdHook;
    private RdpFullScreenWindow? _fullWindow;
    private bool _detached;

    /// <param name="host">页面内的画面控件（全屏期間它挂起渲染，退出后恢复）。</param>
    /// <param name="log">写运行日志（进主控端日志区）。</param>
    /// <param name="onStateChanged">全屏状态变化回调：true = 已进入。用于切按钮文案。</param>
    public RdpFullScreenCoordinator(RdpView host, Action<string> log, Action<bool>? onStateChanged = null)
    {
        _host = host;
        _log = log;
        _onStateChanged = onStateChanged;
        _host.FullScreenToggleRequested += OnHostToggleRequested;
    }

    /// <summary>当前是否处于全屏（全屏窗口还活着）。</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>有没有可全屏的画面（没连上就没得全屏）。</summary>
    public bool CanEnter => _host.CurrentClient is not null;

    public void Toggle()
    {
        if (IsFullScreen)
        {
            Exit();
        }
        else
        {
            Enter();
        }
    }

    public void Enter()
    {
        if (IsFullScreen || _fullWindow is not null)
        {
            return;
        }

        var client = _host.CurrentClient;
        if (client is null)
        {
            _log("内嵌画面还没连接，无法进入全屏。");
            return;
        }

        // 同一时刻只允许一个全屏窗口：换一条通道再点全屏时，先把前一个收掉
        if (s_active is not null && !ReferenceEquals(s_active, this))
        {
            s_active.Exit();
        }

        var window = new RdpFullScreenWindow(client);
        _fullWindow = window;
        s_active = this;
        window.View.FullScreenToggleRequested += OnFullScreenViewToggleRequested;

        // Alt+F4 / 系统关闭：与 Exit() 走同一条复位路径（免得出现"窗口没了但钩子还在"）
        window.Closed += (_, _) =>
        {
            window.View.FullScreenToggleRequested -= OnFullScreenViewToggleRequested;

            // 全屏窗自己的渲染器先摘下来，再归还原宿主渲染
            window.View.DetachClient();

            var wasFullScreen = IsFullScreen;
            _fullWindow = null;
            IsFullScreen = false;

            if (ReferenceEquals(s_active, this))
            {
                s_active = null;
            }

            if (wasFullScreen)
            {
                _kbdHook?.Remove();
                _log("内嵌画面退出全屏（窗口被关闭），键盘接管已停用");
            }

            _host.ResumeRendering();
            _onStateChanged?.Invoke(false);
        };

        window.Activate();

        // 宿主挂起，避免全屏期间双路渲染（CPU 翻倍）
        _host.SuspendRendering();

        _kbdHook ??= new LowLevelKeyboardHook(Intercept, _log);
        _kbdHook.Install();

        IsFullScreen = true;
        _onStateChanged?.Invoke(true);
        _log("内嵌画面进入全屏，键盘接管已启用（F11 退出，双击画面亦可切换）");
    }

    public void Exit()
    {
        if (!IsFullScreen)
        {
            return;
        }

        // 钩子纪律：先摘钩子，再关窗。反过来的话关窗那几毫秒里本地键盘会失灵
        _kbdHook?.Remove();
        IsFullScreen = false;

        var window = _fullWindow;
        _fullWindow = null;
        window?.Close();   // Closed 回调里完成 DetachClient + Resume + 状态复位

        if (ReferenceEquals(s_active, this))
        {
            s_active = null;
        }

        // 兜底：万一 Closed 没触发，至少把宿主画面还回来（幂等，重复调用无害）
        _host.ResumeRendering();
        _onStateChanged?.Invoke(false);
    }

    private void OnHostToggleRequested(object? sender, EventArgs e) => Toggle();

    /// <summary>全屏窗口里的画面也支持双击 / F11 切换 —— 一律以"退出全屏"处理。</summary>
    private void OnFullScreenViewToggleRequested(object? sender, EventArgs e) => Exit();

    /// <summary>
    /// LL 钩子回调（UI 线程）：全屏期间所有键转投远端，返回 true 表示拦截本地。
    /// 这是"能操作内部"的关键 —— 没有它，Win / Alt+Tab / Ctrl+Esc 会被本地系统吃掉。
    /// </summary>
    private bool Intercept(ulong virtualKey, ushort scanCode, bool extended, bool isDown)
    {
        if (!IsFullScreen)
        {
            return false;
        }

        var view = _fullWindow?.View;
        if (view is null)
        {
            return false;
        }

        // F11（0x7A）：退出全屏。down 触发一次，up 只拦不放
        if (virtualKey == 0x7A)
        {
            if (isDown)
            {
                _ = _fullWindow?.DispatcherQueue.TryEnqueue(Exit);
            }

            return true;
        }

        // 其余键：走与页面内 KeyDown 同一张映射表，转投远端
        var (sc, ext) = RdpInputMapper.MapKey(scanCode, extended, virtualKey);
        if (sc == 0)
        {
            return false;   // 未知键放行，避免本地彻底失灵
        }

        return view.SendKey(isDown, ext, sc);
    }

    /// <summary>
    /// 页面离开时调用：**只解绑宿主事件，不结束全屏**。
    /// 全屏窗口是独立窗口，切页不该把它关掉；它自己的 <c>Closed</c> 回调会完成收尾。
    /// （WinUI 的 Frame 导航默认不缓存页面，切回来是**新实例 + 新协调器**，
    /// 靠 <see cref="s_active"/> 守卫避免出现第二个全屏窗口。）
    /// </summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _host.FullScreenToggleRequested -= OnHostToggleRequested;
    }

    public void Dispose()
    {
        Detach();

        if (IsFullScreen)
        {
            Exit();
        }

        _kbdHook?.Remove();
        _kbdHook = null;
    }
}
