// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Yomori.Helpers;

/// <summary>
/// 首页图库的挑选逻辑：从一个文件夹里随机抽一张图显示。
///
/// <para>为什么单独抽成纯 .NET 类：目录枚举 + 随机 + "别连着抽到同一张" 这三件事
/// 全是判定逻辑，没有一行碰 UI。放在 View 里就只能靠肉眼反复切页面验证，
/// 而放在这里冒烟工程能直接链进去跑（见 _smoke/HomeBannerPickerCheck.cs）。</para>
///
/// <para>⚠️ 不要在这里引入任何 <c>Microsoft.UI.Xaml</c> 类型 ——
/// 该类会被 <c>_smoke/Smoke.csproj</c> 以纯 <c>net8.0-windows</c> 编译进去。</para>
/// </summary>
public static class HomeBannerPicker
{
    /// <summary>
    /// 允许出现在图库里的扩展名。与「更换图片…」的过滤器、以及
    /// <c>BitmapImage.SetSourceAsync</c> 实际能吃下的格式保持一致
    /// （webp 支持取决于系统 WIC 解码器，读不出来时页面会写清原因，不会静默空白）。
    /// </summary>
    public static readonly string[] SupportedExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
    };

    /// <summary>该扩展名是否是图库认的图片。大小写不敏感（<c>.JPG</c> 也认）。</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0
               && SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 列出目录里所有图片，按文件名排序。
    ///
    /// 排序不是为了显示好看，是为了**确定性**：枚举顺序取决于文件系统，
    /// 不排序的话"抽中的是哪一张"连复现路径都没有。目录不存在/没权限一律返回空列表
    /// （页面对空的处理是显示占位与提示，不该因为一个坏目录把首页搞崩）。
    /// </summary>
    public static IReadOnlyList<string> EnumerateImages(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(IsSupported)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            // 目录被删/被占用/没权限：当作没有图处理（有占位提示兜底），不往上抛。
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 随机抽一张。
    ///
    /// <paramref name="avoidPath"/> 是上一次显示的那张：目录里还有别的图时把它排除掉，
    /// 否则三张图里总有一次「切回来还是同一张」，看起来像随机没生效。
    /// 只剩一张时照样返回它（宁可重复，也不能没图）。
    /// </summary>
    /// <param name="random">注入用（冒烟里给固定种子）；生产传 null 用 <see cref="Random.Shared"/>。</param>
    /// <returns>选中的绝对路径；目录里没有可用图片时返回 null。</returns>
    public static string? PickRandom(string? directory, string? avoidPath = null, Random? random = null)
    {
        var all = EnumerateImages(directory);
        if (all.Count == 0)
        {
            return null;
        }

        IReadOnlyList<string> pool = all;
        if (all.Count > 1 && !string.IsNullOrWhiteSpace(avoidPath))
        {
            var filtered = all
                .Where(p => !string.Equals(p, avoidPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (filtered.Length > 0)
            {
                pool = filtered;
            }
        }

        var rng = random ?? Random.Shared;
        return pool[rng.Next(pool.Count)];
    }
}
