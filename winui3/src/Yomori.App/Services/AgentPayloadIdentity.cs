using System;
using System.IO;
using System.Security.Cryptography;

namespace Yomori.Services;

/// <summary>代理 EXE 的 manifest 会被改写，不能用它的时间戳判断业务 DLL 是否已更新。</summary>
public static class AgentPayloadIdentity
{
    public static bool Matches(string sourceExe, string targetExe)
    {
        try
        {
            if (!File.Exists(sourceExe) || !File.Exists(targetExe)) return false;
            var sourceDir = Path.GetDirectoryName(sourceExe)!;
            var targetDir = Path.GetDirectoryName(targetExe)!;
            // DLL contains application code; deps/runtimeconfig/PRI must belong to the same build.
            foreach (var name in new[] { "Yomori.dll", "Yomori.deps.json", "Yomori.runtimeconfig.json", "Yomori.pri" })
            {
                var source = Path.Combine(sourceDir, name);
                var target = Path.Combine(targetDir, name);
                if (!File.Exists(source) || !File.Exists(target)) return false;
                if (new FileInfo(source).Length != new FileInfo(target).Length) return false;
                using var a = File.OpenRead(source);
                using var b = File.OpenRead(target);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b))) return false;
            }
            return true;
        }
        catch { return false; } // Unverifiable copies must not be reported as current.
    }
}
