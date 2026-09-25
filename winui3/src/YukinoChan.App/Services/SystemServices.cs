// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace YukinoChan.Services;

/// <summary>Windows 开机自启动（HKCU Run）。</summary>
public static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "雪乃酱 / 二游脚本助手";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                key.SetValue(ValueName, CurrentStartupCommand(), RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // 注册表写入失败不应影响主流程
        }
    }

    private static string CurrentStartupCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            exe = Path.Combine(AppPaths.BaseDir, "YukinoChan.exe");
        }

        return $"\"{exe}\"";
    }
}

/// <summary>延迟关机 / 取消关机。</summary>
public static class ShutdownService
{
    public static bool Schedule(int delaySeconds)
    {
        var delay = Math.Max(1, delaySeconds);
        return RunHidden("shutdown", $"/s /t {delay}");
    }

    public static bool Cancel() => RunHidden("shutdown", "/a");

    private static bool RunHidden(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}
