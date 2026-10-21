// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// 第三方组件清单的运行时入口：读嵌入资源 <c>components.json</c>，供「关于 → 开源许可」渲染。
///
/// <para><b>清单是嵌入资源而不是外部文件</b>，理由有两个：</para>
/// <list type="number">
///   <item>开发期 <see cref="AppPaths.BaseDir"/> 会回溯到仓库根（那里没有发布包的附属文件），
///         外部文件在开发机上必然读不到 —— 界面就会在"开发者自己跑"的时候空着；</item>
///   <item>清单是**编译期常量级别**的信息，跟着 exe 走才不会出现"程序换了、清单还是旧的"。</item>
/// </list>
///
/// <para>许可证**原文**（几万字的 Apache-2.0 全文）不嵌入，留在发布包的
/// <c>THIRD-PARTY-NOTICES.txt</c> 里，界面上给一个入口打开它。</para>
///
/// <para>读取失败不抛异常：清单缺失属于"声明不全"，但不该让「关于」页面崩掉。
/// 失败时返回空清单，界面会明确显示"清单读取失败"，绝不假装一切正常。</para>
/// </summary>
public static class ThirdPartyNotices
{
    /// <summary>csproj 里给这份嵌入资源定死的名字（见 YukinoChan.App.csproj）。</summary>
    private const string ResourceName = "YukinoChan.ThirdParty.components.json";

    /// <summary>随包分发的完整声明文件名（含各组件许可证原文）。</summary>
    public const string FullNoticeFileName = "THIRD-PARTY-NOTICES.txt";

    private static readonly Lazy<(List<ThirdPartyComponent> Native, List<ThirdPartyComponent> Managed, string? Error)>
        Cached = new(Load);

    /// <summary>原生层组件（随包分发的 DLL）。读不到清单时为空。</summary>
    public static IReadOnlyList<ThirdPartyComponent> Native => Cached.Value.Native;

    /// <summary>托管层组件（NuGet 包与 .NET 运行时）。读不到清单时为空。</summary>
    public static IReadOnlyList<ThirdPartyComponent> Managed => Cached.Value.Managed;

    /// <summary>清单读取失败的原因；正常时为 null。</summary>
    public static string? LoadError => Cached.Value.Error;

    /// <summary>
    /// 完整声明文件（含许可证原文）的路径。开发期这个文件不存在 ——
    /// 它是打包脚本生成的，只落在发布目录里。
    /// </summary>
    public static string FullNoticePath => Path.Combine(AppPaths.BaseDir, FullNoticeFileName);

    /// <summary>完整声明文件是否可用（决定"查看许可证原文"按钮显不显示）。</summary>
    public static bool HasFullNotice => File.Exists(FullNoticePath);

    private static (List<ThirdPartyComponent>, List<ThirdPartyComponent>, string?) Load()
    {
        var native = new List<ThirdPartyComponent>();
        var managed = new List<ThirdPartyComponent>();

        try
        {
            var assembly = typeof(ThirdPartyNotices).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return (native, managed, $"嵌入资源 {ResourceName} 不存在（csproj 的 EmbeddedResource 配错了？）");
            }

            var payload = JsonSerializer.Deserialize<ComponentsPayload>(stream, Options);
            if (payload is null)
            {
                return (native, managed, "components.json 解析结果为空");
            }

            native.AddRange(payload.Native ?? new List<ThirdPartyComponent>());
            managed.AddRange(payload.Managed ?? new List<ThirdPartyComponent>());
            return (native, managed, null);
        }
        catch (Exception ex)
        {
            // 清单坏了不该连带把「关于」页面弄崩，但也绝不能装作没事
            return (native, managed, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        // components.json 用 camelCase（mustContain / copyrightFrom / buildOnly…）
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class ComponentsPayload
    {
        public List<ThirdPartyComponent>? Native { get; set; }

        public List<ThirdPartyComponent>? Managed { get; set; }
    }
}
