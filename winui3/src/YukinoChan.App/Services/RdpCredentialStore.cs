// -*- coding: utf-8 -*-
using System;
using System.Runtime.InteropServices;

namespace YukinoChan.Services;

/// <summary>
/// 目标账户密码的存取。
///
/// 为什么不直接用 mstsc 那套：<c>cmdkey /generic:TERMSRV/xxx</c> 只在 mstsc 里会自动取用，
/// 内嵌的 ActiveX 控件不会去读凭据管理器，所以那条路径对它无效。
/// 这里直接用 CredWrite 写一条会话级凭证，连接时 CredRead 取回来交给控件。
///
/// 密码只存在 Windows 凭据管理器里，不落 config.json。
/// </summary>
internal static class RdpCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    private const int ErrorNotFound = 1168;

    /// <summary>凭据管理器里显示的名字，加了前缀避免和别人的条目撞。</summary>
    private const string TargetPrefix = "YukinoChan/RDP/";

    public static bool Write(string host, string userName, string password)
    {
        if (host.Length == 0 || userName.Length == 0 || password.Length == 0)
        {
            return false;
        }

        var target = Marshal.StringToCoTaskMemUni(TargetPrefix + host);
        var user = Marshal.StringToCoTaskMemUni(userName);
        var blob = Marshal.StringToCoTaskMemUni(password);

        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)(password.Length * 2),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };

            return CredWriteW(ref credential, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static string? Read(string host)
    {
        if (host.Length == 0)
        {
            return null;
        }

        if (!CredReadW(TargetPrefix + host, CredTypeGeneric, 0, out var handle) || handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static void Delete(string host)
    {
        if (host.Length == 0)
        {
            return;
        }

        CredDeleteW(TargetPrefix + host, CredTypeGeneric, 0);
    }

    /// <summary>判断凭据是不是真的存在（比解析 cmdkey 的输出可靠）。</summary>
    public static bool Exists(string host) => Read(host) is not null;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>凭据不存在时的 Win32 错误码，用来区分"真没存"和"读取失败"。</summary>
    public static bool IsNotFound => Marshal.GetLastWin32Error() == ErrorNotFound;
}
