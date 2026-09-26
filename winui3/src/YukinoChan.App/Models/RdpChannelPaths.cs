// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;

namespace YukinoChan.Models;

/// <summary>
/// 会话通道的桥目录规则（纯函数，不引 WinUI，可脱离界面单测）。
///
/// 为什么默认按「用户名」派生目录：
///   代理是由**公共启动目录**的快捷方式拉起的，公共启动目录只有一个快捷方式、
///   对所有账户生效，参数是固定的 —— 不可能给每个账户传不同的 --bridge:。
///   于是让两端各自用「自己是哪个账户」推同一个目录：
///     代理侧 = &lt;根&gt;\&lt;Environment.UserName&gt;，主控侧 = &lt;根&gt;\&lt;通道的 user&gt;，
///   天然对齐、零配置。远程主机的通道才需要显式填共享目录（走 bridge_path 覆盖）。
/// </summary>
public static class RdpChannelPaths
{
    /// <summary>桥目录根下的子目录名（与既有 RdpBridge 保持一致）。</summary>
    public const string BridgeFolderName = "rdp";

    /// <summary>目录名长度上限，防止畸形账户名搞出超长路径。</summary>
    private const int MaxSlugLength = 32;

    /// <summary>账户名没法用作目录名时的兜底段。</summary>
    public const string FallbackSlug = "default";

    /// <summary>桥目录根：&lt;ProgramData&gt;\YukinoChan\rdp（所有账户可读）。</summary>
    public static string BridgeRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "YukinoChan",
        BridgeFolderName);

    /// <summary>
    /// 账户名 → 目录名：先取 "域\账户" 的账户段，再只保留字母/数字/连字符/下划线并转小写。
    /// 中文等 Unicode 字母属于字母，会原样保留（Windows 目录名合法）。
    /// 兜底返回 <see cref="FallbackSlug"/>，保证永远不会得到空目录名或带路径分隔符的名字。
    /// </summary>
    public static string Slug(string? userName)
    {
        var text = (userName ?? string.Empty).Trim();

        // "MACHINE\Player2" / "DOMAIN\Player2" → Player2：
        // 反斜杠在目录名里非法，且同一台机器上不同域/机名的同名账户并不会同时存在。
        var slash = text.LastIndexOf('\\');
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        var builder = new StringBuilder(text.Length);
        var lastSeparator = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastSeparator = false;
            }
            else if (!lastSeparator)
            {
                // 连续非法字符折叠成一个下划线（"a   b" 不应变成 "a___b"）
                builder.Append('_');
                lastSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('_', '.', ' ');

        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].Trim('_', '.', ' ');
        }

        return slug.Length == 0 ? FallbackSlug : slug;
    }

    /// <summary>
    /// 通道的桥目录：显式配了 bridge_path 就用它（远程主机共享目录），否则按通道的账户名派生。
    /// </summary>
    /// <param name="channel">通道（null 时退化为兜底目录）。</param>
    /// <param name="bridgeRoot">桥目录根，null 表示用 <see cref="BridgeRoot"/>（测试时注入）。</param>
    public static string BridgeDirFor(RdpChannel? channel, string? bridgeRoot = null)
        => Resolve(channel?.BridgePath, channel?.User, bridgeRoot);

    /// <summary>
    /// 桥目录的通用解析（没有通道对象、只有"桥目录 + 目标账户"两个散值时用，例如预检与等待代理）。
    /// </summary>
    public static string Resolve(string? bridgePath, string? user, string? bridgeRoot = null)
    {
        var custom = (bridgePath ?? string.Empty).Trim();
        if (custom.Length > 0)
        {
            return custom;
        }

        return Path.Combine(ResolveRoot(bridgeRoot), Slug(user));
    }

    /// <summary>代理侧（目标会话里）的桥目录：按运行它的那个账户名派生。</summary>
    public static string AgentBridgeDir(string? userName, string? bridgeRoot = null)
        => Path.Combine(ResolveRoot(bridgeRoot), Slug(userName));

    private static string ResolveRoot(string? bridgeRoot)
        => string.IsNullOrWhiteSpace(bridgeRoot) ? BridgeRoot() : bridgeRoot.Trim();
}
