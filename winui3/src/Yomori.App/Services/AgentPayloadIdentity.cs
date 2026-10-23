using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Yomori.Services;

/// <summary>
/// 判断「已部署的代理副本」与「本次要铺的载荷」是不是同一份构建。
///
/// <para>为什么不能用 exe 的时间戳：主控端那一份副本的 manifest 会被 mt.exe 就地改写
/// （全量副本时代），改写后的时间戳只说明"什么时候改过"，不说明是哪个版本。</para>
///
/// <para>所以比的是**业务程序集本身**：<c>&lt;主名&gt;.dll</c> + <c>.deps.json</c> +
/// <c>.runtimeconfig.json</c> 三件套逐字节一致。主名从 exe 文件名派生，
/// 因为现在有两种载荷 —— 精简代理 <c>Yomori.Agent.exe</c>（Yomori.Agent.dll）
/// 与全量副本 <c>Yomori.exe</c>（Yomori.dll）—— 写死名字会让其中一种永远判不等，
/// 结果每次部署都全量重铺一遍。</para>
/// </summary>
public static class AgentPayloadIdentity
{
    public static bool Matches(string sourceExe, string targetExe)
    {
        try
        {
            if (!File.Exists(sourceExe) || !File.Exists(targetExe))
            {
                return false;
            }

            var sourceDir = Path.GetDirectoryName(sourceExe)!;
            var targetDir = Path.GetDirectoryName(targetExe)!;
            var stem = Path.GetFileNameWithoutExtension(sourceExe);

            var required = new List<string>
            {
                stem + ".dll",
                stem + ".deps.json",
                stem + ".runtimeconfig.json",
            };

            // PRI 只有带 XAML 的那一份才有（全量副本）。精简代理没有界面资源，
            // 它缺失是正常情况 —— 但**源里有就必须比**，否则"换了界面资源却没换代理"会被漏掉。
            if (File.Exists(Path.Combine(sourceDir, stem + ".pri")))
            {
                required.Add(stem + ".pri");
            }

            foreach (var name in required)
            {
                var source = Path.Combine(sourceDir, name);
                var target = Path.Combine(targetDir, name);

                if (!File.Exists(source) || !File.Exists(target))
                {
                    return false;
                }

                if (new FileInfo(source).Length != new FileInfo(target).Length)
                {
                    return false;
                }

                using var a = File.OpenRead(source);
                using var b = File.OpenRead(target);

                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b)))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // Unverifiable copies must not be reported as current.
            return false;
        }
    }
}
