// -*- coding: utf-8 -*-
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// 一个第三方组件的元数据。对应 <c>winui3/third_party/components.json</c> 里的一项。
///
/// <para><b>为什么清单放在 JSON 里、而不是写在代码里</b>：同一份清单有两个消费者 ——
/// 打包脚本要拿它生成 <c>THIRD-PARTY-NOTICES.txt</c>（含许可证原文），
/// 「设置 → 关于 → 开源许可」要拿它渲染界面。各写一份的话，
/// 改了 vcpkg 里的 FreeRDP 版本、只更新了一边，另一处就会悄悄说着旧版本。</para>
///
/// <para>这个类只是**读**的那一侧：字段名必须与 JSON 对应（见
/// <see cref="Services.ThirdPartyNotices"/> 里的命名策略），多一个少一个都会让界面显示空白。</para>
/// </summary>
public sealed class ThirdPartyComponent
{
    /// <summary>vcpkg 的 share 目录名（仅原生组件有）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面与声明里显示的名字。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>许可证名称，如 <c>Apache License 2.0</c>。</summary>
    public string License { get; set; } = string.Empty;

    /// <summary>版权行（托管组件用；原生组件的版权在许可证原文里）。</summary>
    public string Copyright { get; set; } = string.Empty;

    /// <summary>为什么需要它 —— 这一栏是给用户看的，要能说清用途。</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>项目主页。</summary>
    public string Homepage { get; set; } = string.Empty;

    /// <summary>随包分发的 DLL 文件（仅原生组件有）。</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>
    /// 许可证原文必须包含的特征串（仅原生组件有）。
    /// 用来判定"抓到的确实是许可证正文"：文件存在但内容是空的/被替换成别的东西时，
    /// 光看大小是查不出来的。冒烟测试会拿它对仓库里冻结的许可证副本做校验。
    /// </summary>
    public List<string> MustContain { get; set; } = new();

    /// <summary>NuGet 包名；空串表示不是 NuGet 包（如 .NET 运行时）。</summary>
    public string Package { get; set; } = string.Empty;

    /// <summary>固定版本标注（仅 .NET 运行时这类不来自 csproj 的项）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>构建期工具，不随发布包分发 —— 界面上要标出来，免得用户以为包里带着它。</summary>
    public bool BuildOnly { get; set; }

    /// <summary>
    /// 许可证原文借用哪个组件的（WinPR 是 FreeRDP 仓库的一部分，vcpkg 不给它单独的
    /// copyright 文件，所以它指向 <c>freerdp</c>）。为空表示用 <see cref="Key"/> 自己那份。
    /// </summary>
    public string CopyrightFrom { get; set; } = string.Empty;
}
