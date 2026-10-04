// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YukinoChan.Models;

/// <summary>
/// 「导出配置」对话框里让用户勾选的部分。
///
/// 为什么不直接全导：分享配置时最容易出事的是**把本机的东西一起发出去**。
/// 开机自启动、窗口大小、主题这些换台机器就没意义甚至有害；密码更不该默认躺在文件里。
/// 所以导出变成"先勾再导"，默认勾最该分享的（任务 + 远程用户）。
/// </summary>
public sealed class ExportOptions
{
    /// <summary>本体设置：主题、关机/退出策略、截图开关、窗口大小。</summary>
    [JsonPropertyName("include_app_settings")]
    public bool IncludeAppSettings { get; set; }

    /// <summary>任务设置：任务清单（脚本、参数、通道归属、并发组等）。</summary>
    [JsonPropertyName("include_tasks")]
    public bool IncludeTasks { get; set; } = true;

    /// <summary>
    /// 远程用户设置：RDP 主机、账户名、通道配置。
    /// <b>不含密码</b>时也会导出主机与账户名（对方导入后知道该连谁）。
    /// </summary>
    [JsonPropertyName("include_remote_accounts")]
    public bool IncludeRemoteAccounts { get; set; } = true;

    /// <summary>
    /// 远程用户的密码（明文）。仅在 <see cref="IncludeRemoteAccounts"/> 为 true 时才可能被写入。
    /// 密码从 Windows 凭据管理器现取，正常 config.json 里从没有它。
    /// </summary>
    [JsonPropertyName("include_passwords")]
    public bool IncludePasswords { get; set; } = true;

    /// <summary>勾了密码但没勾远程用户 —— 相当于没勾密码，纠正成一致。</summary>
    public void Normalize()
    {
        if (!IncludeRemoteAccounts)
        {
            IncludePasswords = false;
        }
    }

    /// <summary>导出文件顶部的说明块，让接手的人一眼知道这份文件带了什么、密码是不是明文。</summary>
    [JsonPropertyName("_export_note")]
    public string Note { get; set; } = string.Empty;

    /// <summary>按勾选项生成一行中文说明。密码那项要写明是明文，别让人事后才发现。</summary>
    public string BuildNote()
    {
        Normalize();

        var parts = new List<string>();
        if (IncludeTasks)
        {
            parts.Add("任务设置");
        }

        if (IncludeAppSettings)
        {
            parts.Add("本体设置");
        }

        if (IncludeRemoteAccounts)
        {
            parts.Add(IncludePasswords
                ? "远程用户设置（含明文密码）"
                : "远程用户设置（主机与账户名，无密码）");
        }

        if (parts.Count == 0)
        {
            return "本文件由雪乃酱导出，但未勾选任何内容类别。";
        }

        return "本文件包含：" + string.Join("、", parts) + "。";
    }
}
