// -*- coding: utf-8 -*-
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace YukinoChan.Helpers;

/// <summary>WinUI 3 没有直接暴露 HWND，这里做一层薄的取用封装。</summary>
public static class WindowHelper
{
    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    /// <summary>取窗口的 HWND；不是 WinUI 窗口就返回 0。</summary>
    public static IntPtr GetHwnd(Window? window)
    {
        if (window is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(window);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>给显式需要 HWND 的 WinRT 对象（文件选择器等）补上窗口句柄。</summary>
    public static void InitializeWithWindow(object target, Window? window)
    {
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ((IInitializeWithWindow)(object)target).Initialize(hwnd);
        }
        catch
        {
            // 目标对象不需要句柄时会抛异常，忽略即可
        }
    }
}
