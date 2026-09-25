// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AxMSTSCLib;
using MSTSCLib;

namespace YukinoChan.RdpHost;

/// <summary>
/// 内嵌远程桌面宿主。
///
/// MsRdpClient 是 COM ActiveX，只有 WinForms 的 AxHost 能承载它；WinUI 3 里没有
/// WindowsFormsHost，所以这里的做法是：用一个隐藏的无边框 Form 把控件“养”出来拿到
/// 真实窗口句柄，再由调用方 SetParent 挂进自己的窗口。
/// </summary>
public sealed class RemoteDesktopHost : IDisposable
{
    private Form? _form;
    private AxMsRdpClient9NotSafeForScripting? _ax;
    private IMsRdpClientNonScriptable5? _ocx;
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>连接成功。</summary>
    public event EventHandler? Connected;

    /// <summary>已断开，参数为断开原因码。</summary>
    public event EventHandler<int>? Disconnected;

    /// <summary>出错，参数为已经转成中文的说明。</summary>
    public event EventHandler<string>? Error;

    /// <summary>登录完成（桌面已经出来）。</summary>
    public event EventHandler? LoginComplete;

    /// <summary>ActiveX 控件窗口句柄；挂进宿主窗口用的就是它。</summary>
    public IntPtr Handle => _handle;

    public bool IsCreated => _handle != IntPtr.Zero;

    /// <summary>是否已经 SetParent 挂到别人的窗口上了。</summary>
    public bool IsAttached { get; private set; }

    public bool IsConnected { get; private set; }

    public int DesktopWidth { get; private set; }

    public int DesktopHeight { get; private set; }

    /// <summary>
    /// 创建隐藏宿主并实例化 ActiveX。返回控件句柄；失败返回 0。
    /// 必须在 STA（UI）线程上调用。
    /// </summary>
    public bool Create(out string error)
    {
        error = string.Empty;

        if (_handle != IntPtr.Zero)
        {
            return true;
        }

        // ActiveX 只能在单线程单元（STA）里实例化。
        // WinUI 3 的 UI 线程本身就是 STA，但 view model 里有 await 的分支可能跑到线程池
        // （MTA）上去，那种情况下创建会直接抛异常，画面永远是灰的 —— 先拦住并说清楚。
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            error = "当前线程不是单线程单元（STA），无法创建内嵌远程桌面控件。";
            return false;
        }

        try
        {
            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(1024, 768),
                Text = "YukinoChan.RemoteDesktopHost",
            };

            _ax = new AxMsRdpClient9NotSafeForScripting();

            // 事件要在句柄创建之前挂上：AxHost 是在创建 OCX 时接上 COM 事件源的
            _ax.OnConnected += (_, _) => { IsConnected = true; Connected?.Invoke(this, EventArgs.Empty); };
            _ax.OnLoginComplete += (_, _) => LoginComplete?.Invoke(this, EventArgs.Empty);
            _ax.OnDisconnected += (_, e) =>
            {
                IsConnected = false;
                var reason = 0;
                try
                {
                    reason = e.discReason;
                }
                catch
                {
                    // 互操作取不到就按通用断开处理
                }

                Disconnected?.Invoke(this, reason);
            };
            _ax.OnFatalError += (_, e) =>
            {
                IsConnected = false;
                var code = 0;
                try
                {
                    code = e.errorCode;
                }
                catch
                {
                    // 同上
                }

                Error?.Invoke(this, RdpErrorText.FatalError(code));
            };
            _ax.OnLogonError += (_, e) =>
            {
                IsConnected = false;
                var code = 0;
                try
                {
                    code = e.lError;
                }
                catch
                {
                    // 同上
                }

                Error?.Invoke(this, RdpErrorText.LogonError(code));
            };

            ((ISupportInitialize)_ax).BeginInit();
            // 先别 Dock：Dock=Fill 会让 WinForms 的布局引擎在每次布局时把控件
            // 重新摆回宿主 Form 的尺寸和位置，把后来我们 SetParent 之后的 MoveWindow 覆盖掉
            // （表现就是画面尺寸永远是 Form 的初始大小、位置还带边框偏移）。
            _ax.Dock = DockStyle.None;
            _ax.Enabled = true;
            _form.Controls.Add(_ax);
            ((ISupportInitialize)_ax).EndInit();

            // 句柄只有真的显示过一次才会创建；创建完立刻藏起来，画面由宿主窗口负责
            _form.Show();
            _handle = _ax.Handle;
            _form.Hide();

            if (_handle == IntPtr.Zero)
            {
                error = "远程桌面控件创建失败：拿不到窗口句柄。";
                return false;
            }

            // 关键一步：把控件从 Form 的控件树里摘出去。
            // 它现在已经有了独立句柄，不再需要 WinForms 托管；留在树里的话，
            // 一旦 Form 有任何布局动作，就会把控件重新摆回 Form 的坐标系，
            // 把我们 SetParent 之后的 MoveWindow 全部作废。
            _form.Controls.Remove(_ax);

            try
            {
                _ocx = (IMsRdpClientNonScriptable5)_ax.GetOcx();
            }
            catch (Exception ex)
            {
                // 拿不到扩展接口只会少几个高级设置，不影响基本连接
                System.Diagnostics.Debug.WriteLine($"[RdpHost] IMsRdpClientNonScriptable5 不可用：{ex.Message}");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"无法创建内嵌远程桌面控件：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 在给定窗口里找 WinUI 3 的内容桥子窗口。找不到就返回原窗口。
    /// 挂载优先挂到内容桥里，否则会被合成器压住。
    /// </summary>
    public static IntPtr ResolveAttachParent(IntPtr windowHwnd)
    {
        if (windowHwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var bridge = RdpNative.FindContentBridge(windowHwnd);
        return bridge != IntPtr.Zero ? bridge : windowHwnd;
    }

    /// <summary>列出窗口的所有子窗口（类名），排查挂载问题用。</summary>
    public static string DescribeWindowChildren(IntPtr windowHwnd) =>
        string.Join(", ", RdpNative.DescribeChildren(windowHwnd));

    /// <summary>取某个窗口的父窗口句柄。</summary>
    public static IntPtr GetParentWindow(IntPtr hwnd) => RdpNative.GetParent(hwnd);

    /// <summary>
    /// 把控件挂进指定父窗口，并摆到给定位置（单位：物理像素）。
    ///
    /// 关键点：WinUI 3 的整个内容区是一个 DesktopChildSiteBridge 原生子窗口，覆盖满客户区。
    /// 我们 SetParent 到主窗口后只是它的"兄弟"，默认 Z 序在它之下 → 完全被遮住 → 灰屏。
    /// 所以挂完必须 SetWindowPos(HWND_TOP) 把自己抬到最前。
    /// </summary>
    public bool Attach(IntPtr parent, int x, int y, int width, int height)
    {
        if (_handle == IntPtr.Zero || parent == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var style = RdpNative.GetStyle(_handle).ToInt64();
            style &= ~RdpNative.WsPopup;
            style |= RdpNative.WsChild | RdpNative.WsVisible | RdpNative.WsClipSiblings;
            RdpNative.SetStyle(_handle, new IntPtr(style));

            RdpNative.SetParent(_handle, parent);
            RdpNative.MoveWindow(_handle, x, y, width, height, true);
            // 抬到同级最前，别被 WinUI 的内容桥盖住
            RdpNative.BringToTop(_handle);
            RdpNative.ShowWindow(_handle, RdpNative.SwShow);
            IsAttached = true;
            return true;
        }
        catch
        {
            IsAttached = false;
            return false;
        }
    }

    /// <summary>
    /// 把「窗口内相对 XAML 根的位置」换算成「父窗口客户区坐标」。
    /// 原生子窗口 MoveWindow 用的是客户区坐标系，跟 XAML 坐标系之间差一个客户区原点 + XAML 根偏移，
    /// 不换算的话画面会整体偏移（甚至跑到窗口外）。
    /// </summary>
    /// <param name="windowHwnd">主窗口句柄（客户区就指它）。</param>
    /// <param name="xamlRootOffsetX">XAML 根在该窗口里的偏移（物理像素）。</param>
    /// <param name="xamlRootOffsetY">同上。</param>
    /// <param name="offsetX">目标区域相对 XAML 根的偏移（物理像素）。</param>
    /// <param name="offsetY">同上。</param>
    public static (int X, int Y) MapToClientArea(
        IntPtr windowHwnd, int xamlRootOffsetX, int xamlRootOffsetY, int offsetX, int offsetY)
    {
        if (!RdpNative.TryClientOriginInScreen(windowHwnd, out var originX, out var originY))
        {
            return (Math.Max(0, offsetX), Math.Max(0, offsetY));
        }

        var screenX = originX + xamlRootOffsetX + offsetX;
        var screenY = originY + xamlRootOffsetY + offsetY;
        var (clientX, clientY) = RdpNative.ToClient(windowHwnd, screenX, screenY);
        return (Math.Max(0, clientX), Math.Max(0, clientY));
    }

    /// <summary>从父窗口摘下来并藏起来（会话保持不断）。</summary>
    public void Detach()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        RdpNative.ShowWindow(_handle, RdpNative.SwHide);
        IsAttached = false;
    }

    public void Move(int x, int y, int width, int height)
    {
        if (_handle == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        RdpNative.MoveWindow(_handle, x, y, width, height, true);
        // 窗口尺寸一变，WinUI 的内容桥很可能又把自己刷到前面了，每次同步位置都再抬一次
        RdpNative.BringToTop(_handle);
    }

    /// <summary>
    /// 发起连接。凭据优先用显式传入的密码，没有就交给 Windows 凭据管理器（cmdkey）。
    /// </summary>
    public bool Connect(string server, string userName, string? password, int width, int height, out string message)
    {
        message = string.Empty;

        if (_ax is null)
        {
            message = "远程桌面控件尚未创建。";
            return false;
        }

        try
        {
            DesktopWidth = width;
            DesktopHeight = height;

            _ax.Server = server;
            _ax.UserName = userName;
            _ax.DesktopWidth = width;
            _ax.DesktopHeight = height;
            _ax.ColorDepth = 32;

            var advanced = _ax.AdvancedSettings9;
            // 画面按控件大小缩放，分辨率和显示区域不一致也不会出现滚动条
            advanced.SmartSizing = true;
            advanced.EnableCredSspSupport = true;
            // 目标账户可能没加入 Remote Desktop Users，登录失败会直接报错误码而不是等一个看不见的弹窗
            advanced.EnableAutoReconnect = false;

            if (!string.IsNullOrEmpty(password))
            {
                try
                {
                    advanced.ClearTextPassword = password;
                }
                catch (Exception ex)
                {
                    // 某些系统策略禁止明文密码，这种情况退回凭据管理器
                    System.Diagnostics.Debug.WriteLine($"[RdpHost] 明文密码不可用：{ex.Message}");
                }
            }

            if (_ocx is not null)
            {
                try
                {
                    _ocx.PromptForCredentials = false;
                    _ocx.AllowPromptingForCredentials = false;
                    // 关掉顶部连接栏，画面就是纯粹的远程桌面
                    _ocx.DisableConnectionBar = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RdpHost] 扩展设置失败：{ex.Message}");
                }
            }

            _ax.Connect();
            message = $"正在连接 {server}（{width}×{height}）…";
            return true;
        }
        catch (Exception ex)
        {
            message = $"发起远程桌面连接失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>会话中动态改分辨率（RDP 8.1+ 支持，失败就保持原样）。</summary>
    public bool UpdateDesktopSize(int width, int height)
    {
        if (_ax is null || !IsConnected || width <= 0 || height <= 0)
        {
            return false;
        }

        if (width == DesktopWidth && height == DesktopHeight)
        {
            return true;
        }

        try
        {
            _ax.DesktopWidth = width;
            _ax.DesktopHeight = height;
            // 后三个参数是显示器物理尺寸/方向/缩放百分比，传 0 表示沿用默认值
            _ax.UpdateSessionDisplaySettings((uint)width, (uint)height, 0, 0, 0, 100, 100);
            DesktopWidth = width;
            DesktopHeight = height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Disconnect()
    {
        if (_ax is null)
        {
            return;
        }

        try
        {
            if (IsConnected)
            {
                _ax.Disconnect();
            }
        }
        catch
        {
            // 断开时的异常没有处理价值
        }

        IsConnected = false;
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
            Disconnect();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _handle = IntPtr.Zero;
            _ocx = null;
            _ax?.Dispose();
            _ax = null;
            _form?.Dispose();
            _form = null;
        }
        catch
        {
            // 关闭时的异常没有处理价值
        }
    }
}
