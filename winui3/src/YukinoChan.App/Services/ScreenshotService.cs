// -*- coding: utf-8 -*-
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace YukinoChan.Services;

/// <summary>超时现场截图（全屏，对应 Python 版 QScreen.grabWindow）。</summary>
public static class ScreenshotService
{
    public static string? CaptureFullScreen(string directory, string fileName)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
            var y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
            var width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
            var height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height));
            }

            var path = Path.Combine(directory, fileName);
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
