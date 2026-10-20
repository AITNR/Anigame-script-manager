// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace YukinoChan.Services;

/// <summary>
/// 状态栏（通知区域）图标。
///
/// <para>WinUI 3 没有 WinForms 的 <c>NotifyIcon</c>，只能自己来：自建一个**从不显示**的顶层窗口
/// 接收托盘回调，再用 <c>Shell_NotifyIcon</c> 把图标挂上去。</para>
///
/// <para>三个必须照做的细节，少一个就出怪事：</para>
/// <list type="number">
///   <item><b>窗口不能是消息专用窗口（HWND_MESSAGE）</b>：右键菜单 <c>TrackPopupMenu</c>
///         需要窗口能被 <c>SetForegroundWindow</c>，消息专用窗口在部分系统上表现异常。</item>
///   <item><b>WndProc 委托必须保活</b>：窗口类里存的是函数指针，委托被 GC 回收后回调就是野指针，
///         表现是"点一下托盘图标进程就没了"。这里用实例字段 + 静态活动表双重兜住。</item>
///   <item><b>监听 <c>TaskbarCreated</c></b>：explorer 崩溃重启后，之前注册的图标全部失效，
///         不重新 <c>NIM_ADD</c> 就永远不回来了（用户会以为程序挂了）。</item>
/// </list>
///
/// <para>所有成员只能在创建它的线程（UI 线程）上调用 —— 窗口消息与菜单都挂在那条消息循环上。</para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const string WindowClassName = "YukinoChanTrayWindow";
    private const uint TrayIconId = 1;

    private const int CommandShow = 1;
    private const int CommandStartStop = 2;
    private const int CommandCloseBehavior = 3;
    private const int CommandExit = 4;

    /// <summary>活动实例表：防止 TrayIcon 被回收后窗口类里的函数指针变成野指针。</summary>
    private static readonly List<TrayIcon> s_live = new();

    private readonly string _iconPath;
    private readonly TrayNative.WndProcDelegate _wndProc;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _installed;
    private bool _disposed;

    private uint _taskbarCreatedMessage;
    private string _tip = "雪乃酱";

    /// <summary>菜单里"开始执行 / 停止执行"的文案，由界面侧在打开菜单前写进来。</summary>
    private string _startStopLabel = "开始执行";

    /// <summary>菜单是否正开着（<c>TrackPopupMenu</c> 模态期间 WndProc 会重入）。</summary>
    private bool _menuOpen;

    public TrayIcon(string iconPath)
    {
        _iconPath = iconPath;

        // 委托存进字段（而不是传给 RegisterClass 后就丢），保住它的生命周期
        _wndProc = WndProc;
    }

    /// <summary>工具提示（最多 127 个字符，超出会被系统截断）。</summary>
    public string Tip
    {
        get => _tip;
        set
        {
            var text = string.IsNullOrWhiteSpace(value) ? "雪乃酱" : value.Trim();
            _tip = text.Length > 127 ? text[..127] : text;
            UpdateTip();
        }
    }

    /// <summary>当前是否已挂上托盘。注册失败时界面侧**不许**继续隐藏窗口（见 MainWindow.HideToTray）。</summary>
    public bool IsInstalled => _installed;

    /// <summary>最近一次失败原因，供界面写进运行日志。</summary>
    public string LastError { get; private set; } = string.Empty;

    public event EventHandler? ShowRequested;

    /// <summary>"开始执行 / 停止执行"（菜单文案按当时状态决定）。</summary>
    public event EventHandler? StartStopRequested;

    public event EventHandler? CloseBehaviorRequested;

    public event EventHandler? ExitRequested;

    /// <summary>系统准备关机 / 注销（WM_QUERYENDSESSION）。收到后必须放行关闭，绝不能拦。</summary>
    public event EventHandler? SystemSessionEnding;

    /// <summary>
    /// 创建隐藏窗口并挂上图标。失败返回 false（<see cref="LastError"/> 里有原因），
    /// 调用方必须据此回退到"最小化到任务栏"，不能静默隐藏 —— 否则用户会以为程序关了。
    /// </summary>
    public bool TryInstall()
    {
        if (_disposed)
        {
            return false;
        }

        if (_installed)
        {
            return true;
        }

        try
        {
            if (_hwnd == IntPtr.Zero && !CreateHiddenWindow())
            {
                return false;
            }

            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadTrayIcon();
            }

            if (!AddIcon())
            {
                return false;
            }

            _installed = true;

            if (!s_live.Contains(this))
            {
                s_live.Add(this);
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = "托盘图标注册异常：" + ex.Message;
            return false;
        }
    }

    /// <summary>更新提示文案（未安装时什么都不做）。</summary>
    public void UpdateTip()
    {
        if (!_installed)
        {
            return;
        }

        var data = NewData();
        data.uFlags = TrayNative.NIF_TIP;
        data.szTip = _tip;

        TrayNative.Shell_NotifyIconW(TrayNative.NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_installed)
            {
                var data = NewData();
                data.uFlags = 0;
                TrayNative.Shell_NotifyIconW(TrayNative.NIM_DELETE, ref data);
                _installed = false;
            }
        }
        catch
        {
            // 删不掉也不该阻断退出；进程结束时系统会清掉
        }

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                TrayNative.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            TrayNative.UnregisterClassW(WindowClassName, IntPtr.Zero);
        }
        catch
        {
            // 同上
        }

        try
        {
            if (_hIcon != IntPtr.Zero)
            {
                TrayNative.DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }
        catch
        {
            // 同上
        }

        s_live.Remove(this);
    }

    // ---------------- 内部 ----------------

    private bool CreateHiddenWindow()
    {
        var instance = TrayNative.GetModuleHandleW(null);

        var wndClass = new TrayNative.WNDCLASSW
        {
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = WindowClassName,
        };

        var atom = TrayNative.RegisterClassW(ref wndClass);
        if (atom == 0)
        {
            // 1410 = 类已存在。同一进程里只该有一个托盘窗口，但重建（先 Dispose 再 Install）
            // 时可能撞上；此时沿用已有类即可，WndProc 仍是本实例的。
            var error = Marshal.GetLastWin32Error();
            if (error != TrayNative.ERROR_CLASS_ALREADY_EXISTS)
            {
                LastError = $"托盘窗口类注册失败（错误码 {error}）";
                return false;
            }
        }

        // 真实顶层窗口，但从不 Show：既能收回调消息，也满足 TrackPopupMenu 对前台窗口的要求
        _hwnd = TrayNative.CreateWindowExW(
            TrayNative.WS_EX_TOOLWINDOW,
            WindowClassName,
            "YukinoChanTray",
            TrayNative.WS_POPUP,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            LastError = $"托盘窗口创建失败（错误码 {Marshal.GetLastWin32Error()}）";
            return false;
        }

        // explorer 重启广播：注册一次，之后在 WndProc 里比对
        _taskbarCreatedMessage = TrayNative.RegisterWindowMessageW("TaskbarCreated");
        return true;
    }

    private IntPtr LoadTrayIcon()
    {
        var size = TrayNative.GetSystemMetrics(TrayNative.SM_CXSMICON);
        if (size <= 0)
        {
            size = 16;
        }

        if (!string.IsNullOrWhiteSpace(_iconPath) && File.Exists(_iconPath))
        {
            var handle = TrayNative.LoadImageW(
                IntPtr.Zero,
                _iconPath,
                TrayNative.IMAGE_ICON,
                size,
                size,
                TrayNative.LR_LOADFROMFILE);

            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        // ico 丢了 / 读不出来：回落系统默认图标，别让图标位空着
        return TrayNative.LoadIconW(IntPtr.Zero, TrayNative.IDI_APPLICATION);
    }

    private TrayNative.NOTIFYICONDATAW NewData() => new()
    {
        cbSize = Marshal.SizeOf<TrayNative.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        uCallbackMessage = TrayNative.TrayCallbackMessage,
        hIcon = _hIcon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private bool AddIcon()
    {
        var data = NewData();
        data.uFlags = TrayNative.NIF_MESSAGE | TrayNative.NIF_ICON | TrayNative.NIF_TIP;

        if (TrayNative.Shell_NotifyIconW(TrayNative.NIM_ADD, ref data))
        {
            LastError = string.Empty;
            return true;
        }

        LastError = "托盘图标注册失败（Shell_NotifyIcon 返回 false，可能是通知区域正忙）";
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 这里绝不能让异常越过托管边界（会直接掀进程），整段兜住
        try
        {
            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // explorer 重启：旧图标已随通知区域消失，重新挂一次
                _installed = false;
                if (AddIcon())
                {
                    _installed = true;
                }

                return IntPtr.Zero;
            }

            switch (msg)
            {
                case TrayNative.TrayCallbackMessage:
                {
                    // 未启用 NOTIFYICON_VERSION_4，lParam 低 16 位就是鼠标消息
                    var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);

                    if (mouseMessage is TrayNative.WM_LBUTTONUP or TrayNative.WM_LBUTTONDBLCLK)
                    {
                        ShowRequested?.Invoke(this, EventArgs.Empty);
                        return IntPtr.Zero;
                    }

                    if (mouseMessage is TrayNative.WM_RBUTTONUP or TrayNative.WM_CONTEXTMENU)
                    {
                        ShowMenu();
                        return IntPtr.Zero;
                    }

                    return IntPtr.Zero;
                }

                case TrayNative.WM_QUERYENDSESSION:
                    // 关机 / 注销：先告诉界面放行关闭，再回 1（同意结束会话）。
                    // 这里如果回 0，Windows 会判定"此应用阻止关机"，用户会被弹窗拦住。
                    SystemSessionEnding?.Invoke(this, EventArgs.Empty);
                    return new IntPtr(1);

                case TrayNative.WM_ENDSESSION:
                    return IntPtr.Zero;

                case TrayNative.WM_CLOSE:
                    // 隐藏窗口不该被关掉；真要销毁走 Dispose 里的 DestroyWindow
                    return IntPtr.Zero;

                default:
                    return TrayNative.DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private void ShowMenu()
    {
        // TrackPopupMenu 是**模态**的：菜单开着的时候 WndProc 还会被重入。
        // 不挡一下的话，重复的右键消息会把菜单叠起来（第二个菜单关掉后第一个还挂着）。
        if (_menuOpen)
        {
            return;
        }

        _menuOpen = true;

        var menu = TrayNative.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            _menuOpen = false;
            return;
        }

        try
        {
            AppendMenuItem(menu, CommandShow, "显示主界面", enabled: true);
            AppendSeparator(menu);
            AppendMenuItem(menu, CommandStartStop, _startStopLabel, enabled: true);
            AppendMenuItem(menu, CommandCloseBehavior, "关闭行为设置…", enabled: true);
            AppendSeparator(menu);
            AppendMenuItem(menu, CommandExit, "退出", enabled: true);

            if (!TrayNative.GetCursorPos(out var point))
            {
                point = default;
            }

            // 经典组合：先把窗口顶到前台再弹菜单，否则点菜单外面菜单不消失
            TrayNative.SetForegroundWindow(_hwnd);

            var command = TrayNative.TrackPopupMenu(
                menu,
                TrayNative.TPM_RIGHTBUTTON | TrayNative.TPM_RETURNCMD | TrayNative.TPM_NONOTIFY,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            // 菜单关掉之后补一条空消息，把前台的"菜单模式"结束掉（同上，缺了菜单会赖着不走）
            TrayNative.PostMessageW(_hwnd, TrayNative.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CommandShow:
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandStartStop:
                    StartStopRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandCloseBehavior:
                    CloseBehaviorRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            TrayNative.DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    /// <summary>设置菜单项文案（在弹出菜单之前调用，避免菜单打开后改字）。</summary>
    public void SetStartStopLabel(bool isRunning) => _startStopLabel = isRunning ? "停止执行" : "开始执行";

    private static void AppendMenuItem(IntPtr menu, int command, string text, bool enabled)
    {
        var flags = TrayNative.MF_STRING | (enabled ? 0u : TrayNative.MF_GRAYED);
        TrayNative.AppendMenuW(menu, flags, new IntPtr(command), text);
    }

    private static void AppendSeparator(IntPtr menu) =>
        TrayNative.AppendMenuW(menu, TrayNative.MF_SEPARATOR, IntPtr.Zero, null);
}
