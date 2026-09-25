// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YukinoChan.Services;

/// <summary>
/// 统一进程监控抽象，让普通子进程与 ShellExecuteEx(runas) 提权进程可以沿用同一套监控逻辑。
/// </summary>
public interface IMonitoredProcess
{
    int Pid { get; }

    /// <summary>返回退出码；仍在运行则返回 null。</summary>
    int? Poll();

    /// <summary>等待指定毫秒；已退出返回 true。</summary>
    bool Wait(int milliseconds);

    void Terminate();
}

public sealed class ManagedProcessAdapter : IMonitoredProcess
{
    private readonly Process _process;

    public ManagedProcessAdapter(Process process)
    {
        _process = process;
    }

    public int Pid => _process.Id;

    public int? Poll()
    {
        if (_process.HasExited)
        {
            return _process.ExitCode;
        }

        return null;
    }

    public bool Wait(int milliseconds) => _process.WaitForExit(milliseconds);

    public void Terminate()
    {
        try
        {
            if (_process.HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch
        {
            // 进程可能已退出或权限不足，忽略
        }
    }
}

/// <summary>用 ShellExecuteEx(runas) 启动的提权进程适配器。</summary>
public sealed class ElevatedProcessAdapter : IMonitoredProcess
{
    private IntPtr _handle;
    private int? _exitCode;

    public ElevatedProcessAdapter(IntPtr handle, int pid)
    {
        _handle = handle;
        Pid = pid;
    }

    public int Pid { get; }

    public int? Poll()
    {
        if (_exitCode is not null)
        {
            return _exitCode;
        }

        var result = NativeMethods.WaitForSingleObject(_handle, 0);
        if (result == NativeMethods.WaitTimeout)
        {
            return null;
        }

        if (result == NativeMethods.WaitObject0)
        {
            if (NativeMethods.GetExitCodeProcess(_handle, out var code))
            {
                _exitCode = (int)code;
            }
            else
            {
                _exitCode = 0;
            }

            try
            {
                NativeMethods.CloseHandle(_handle);
            }
            catch
            {
                // 忽略句柄关闭失败
            }

            _handle = IntPtr.Zero;
            return _exitCode;
        }

        return null;
    }

    public bool Wait(int milliseconds)
    {
        var remaining = milliseconds;
        var step = 100;
        while (true)
        {
            if (Poll() is not null)
            {
                return true;
            }

            if (remaining <= 0)
            {
                return false;
            }

            var slice = Math.Min(step, remaining);
            NativeMethods.WaitForSingleObject(_handle, (uint)slice);
            remaining -= slice;
        }
    }

    public void Terminate()
    {
        try
        {
            if (Pid > 0)
            {
                ProcessHelper.TerminatePidTree(Pid);
                return;
            }
        }
        catch
        {
            // 落到 TerminateProcess 兜底
        }

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.TerminateProcess(_handle, 1);
        }
    }
}

public static class ProcessLauncher
{
    /// <summary>
    /// 启动子进程。若目标带 requireAdministrator 清单（WinError 740），
    /// 自动改用 ShellExecuteEx(runas) 弹出 UAC，而不是让用户手动右键管理员启动。
    ///
    /// 注意：这里【不能】加 CreateNoWindow —— 与 Python 原版对齐。
    /// 被启动的 .exe / .bat 会自己弹出控制台窗口，这正是用户想在目标会话屏幕上看到的效果。
    /// （内部工具调用如 tasklist / powershell 仍应隐藏，见 ProcessHelper.RunHidden。）
    /// </summary>
    public static IMonitoredProcess Start(string fileName, string arguments, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("进程启动返回空句柄。");
            }

            return new ManagedProcessAdapter(process);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            return StartElevated(fileName, arguments, workingDirectory);
        }
    }

    /// <summary>拆分后的完整命令行提权启动。</summary>
    public static IMonitoredProcess StartElevated(string fileName, string arguments, string workingDirectory)
    {
        var info = new NativeMethods.ShellExecuteInfoW
        {
            cbSize = Marshal.SizeOf<NativeMethods.ShellExecuteInfoW>(),
            fMask = NativeMethods.SeeMaskNoCloseProcess,
            hwnd = IntPtr.Zero,
            lpVerb = "runas",
            lpFile = fileName,
            lpParameters = arguments,
            lpDirectory = workingDirectory,
            nShow = NativeMethods.SwShowNormal,
        };

        if (!NativeMethods.ShellExecuteExW(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1223)
            {
                throw new InvalidOperationException("用户取消了 UAC 管理员权限确认。");
            }

            throw new Win32Exception(error);
        }

        var pid = NativeMethods.GetProcessId(info.hProcess);
        return new ElevatedProcessAdapter(info.hProcess, (int)pid);
    }
}
