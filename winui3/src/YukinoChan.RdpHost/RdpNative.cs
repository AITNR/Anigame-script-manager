// -*- coding: utf-8 -*-
using System;
using System.Runtime.InteropServices;

namespace YukinoChan.RdpHost;

/// <summary>把 ActiveX 挂进宿主窗口需要的 Win32 API。</summary>
internal static class RdpNative
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;

    public const uint WsChild = 0x40000000;
    public const uint WsVisible = 0x10000000;
    public const uint WsPopup = 0x80000000;
    public const uint WsClipSiblings = 0x04000000;
    public const uint WsClipChildren = 0x02000000;

    public const int SwShow = 5;
    public const int SwHide = 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int cx, int cy, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ---- Z 序：WinUI 3 的内容渲染在一个子窗口桥里，会盖住后挂的同级原生窗口，
    //      必须显式把自己的窗口抬到最前面。----
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const int HwndTop = 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>
    /// 客户区坐标 → 屏幕坐标用的转换。
    /// SetParent 到别人窗口后，MoveWindow 用的是父窗口客户区坐标，
    /// 而 XAML 给的是相对 XAML 根的位置，两者差着标题栏那一截，必须换算。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref Point lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>把原生子窗口抬到同级窗口的最前面（不激活、不改尺寸位置）。</summary>
    public static void BringToTop(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hWnd, new IntPtr(HwndTop), 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>
    /// 取一个窗口的客户区原点在屏幕上的坐标。
    /// 用于把 XAML 坐标换算成原生子窗口能用的客户区坐标。
    /// </summary>
    public static bool TryClientOriginInScreen(IntPtr hWnd, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var point = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(hWnd, ref point))
        {
            return false;
        }

        x = point.X;
        y = point.Y;
        return true;
    }

    /// <summary>屏幕坐标 → 窗口客户区坐标。</summary>
    public static (int X, int Y) ToClient(IntPtr hWnd, int screenX, int screenY)
    {
        var point = new Point { X = screenX, Y = screenY };
        ScreenToClient(hWnd, ref point);
        return (point.X, point.Y);
    }

    // ---- 子窗口枚举：用来找 WinUI 3 的内容桥窗口 ----

    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>取窗口类名。</summary>
    public static string ClassNameOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(256);
        GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>
    /// 在某个窗口的子窗口里找 WinUI 3 的内容桥（DesktopChildSiteBridge）。
    ///
    /// WinUI 3 把整块内容渲染在这个子窗口里，它盖满客户区。
    /// 我们把原生控件 SetParent 到**它**（而不是顶层窗口），才是真正的"嵌进内容里"，
    /// Z 序和坐标都跟 XAML 一致，不用再跟合成器抢层级。
    /// </summary>
    public static IntPtr FindContentBridge(IntPtr windowHwnd)
    {
        if (windowHwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        EnumChildWindows(windowHwnd, (child, _) =>
        {
            var name = ClassNameOf(child);
            // 不同 WinAppSDK 版本类名略有差异，按关键字匹配
            if (name.Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ContentSiteBridge", StringComparison.OrdinalIgnoreCase))
            {
                found = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>列出某个窗口的所有直接子窗口及类名，排查挂载问题时用。</summary>
    public static System.Collections.Generic.List<string> DescribeChildren(IntPtr windowHwnd)
    {
        var result = new System.Collections.Generic.List<string>();
        if (windowHwnd == IntPtr.Zero)
        {
            return result;
        }

        EnumChildWindows(windowHwnd, (child, _) =>
        {
            result.Add($"0x{child.ToInt64():X}:{ClassNameOf(child)}");
            return true;
        }, IntPtr.Zero);

        return result;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    public static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    public static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr GetStyle(IntPtr hWnd) =>
        Environment.Is64BitProcess ? GetWindowLongPtr64(hWnd, GwlStyle) : GetWindowLongPtr32(hWnd, GwlStyle);

    public static IntPtr SetStyle(IntPtr hWnd, IntPtr style) =>
        Environment.Is64BitProcess ? SetWindowLongPtr64(hWnd, GwlStyle, style) : SetWindowLongPtr32(hWnd, GwlStyle, style);
}
