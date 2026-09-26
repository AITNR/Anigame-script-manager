// -*- coding: utf-8 -*-
using System;
using System.Text.Json.Serialization;
using YukinoChan.Helpers;

namespace YukinoChan.Models;

/// <summary>
/// 任务页「执行通道」下拉的一项。
///
/// 第一项固定是 <c>Id = ""</c> 的「本地执行」（D6：它是**显式选项**，不是"漏填时的兜底"）——
/// 新建任务的默认值也是它，免得"忘了选通道就悄悄跑远程"。
/// </summary>
public sealed class ChannelChoice
{
    /// <summary>空串表示本地执行（<c>TaskConfig.ChannelId</c> 的语义）。</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// 会话通道：一条「主控端 ↔ 目标会话」的完整通道 —— 目标主机 + 目标账户 + 指令桥目录 + 收尾策略。
///
/// 为什么要有它：原来全局只有一组 target_host / target_user，只能跑一路远程；
/// 拆成通道后，任务在「任务执行」页各选自己归哪个通道，开始执行时按通道分组、
/// 通道之间并行、通道内部仍按 order 串行。
///
/// 密码不落在这里（只记"是否已保存"），实际凭据在 Windows 凭据管理器里按 host|user 存。
/// 配置键名全 snake_case，与 config.json 其余部分一致。
/// </summary>
public sealed class RdpChannel : ObservableObject, ICloneable
{
    /// <summary>旧配置（只有一组 target_*）迁移出来的默认通道 id，固定值便于识别与排障。</summary>
    public const string LegacyDefaultId = "default";

    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _host = RdpTargets.DefaultHost;
    private string _user = string.Empty;
    private string _bridgePath = string.Empty;
    private bool _credentialSaved;
    private string _sessionFinish = SessionFinishModes.Keep;
    private int _desktopWidth;
    private int _desktopHeight;
    private bool _enabled = true;

    /// <summary>生成一个通道 id（8 位十六进制，够用且不啰嗦）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, (value ?? string.Empty).Trim());
    }

    /// <summary>菜单项与下拉框里显示的名字。</summary>
    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, (value ?? string.Empty).Trim()))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>目标主机：IP / 主机名，可带端口（默认 127.0.0.1 本机环回）。</summary>
    [JsonPropertyName("host")]
    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, RdpTargets.Normalize(value)))
            {
                OnPropertyChanged(nameof(ListSubtitle));
            }
        }
    }

    /// <summary>目标 Windows 账户，例如 "GamePC\Player2" 或 "Player2"。</summary>
    [JsonPropertyName("user")]
    public string User
    {
        get => _user;
        set
        {
            if (SetProperty(ref _user, (value ?? string.Empty).Trim()))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(ListSubtitle));
                OnPropertyChanged(nameof(IsConfigured));
            }
        }
    }

    /// <summary>
    /// 指令桥目录。留空 = 按用户名自动派生（<c>&lt;ProgramData&gt;\YukinoChan\rdp\&lt;用户名&gt;</c>），
    /// 这样目标会话里的代理不需要额外配置就能找到自己的桥；远程主机才需要显式填共享目录。
    /// </summary>
    [JsonPropertyName("bridge_path")]
    public string BridgePath
    {
        get => _bridgePath;
        set => SetProperty(ref _bridgePath, (value ?? string.Empty).Trim());
    }

    /// <summary>该通道的凭据是否已存入 Windows 凭据管理器（按 host|user 一条）。</summary>
    [JsonPropertyName("credential_saved")]
    public bool CredentialSaved
    {
        get => _credentialSaved;
        set
        {
            if (SetProperty(ref _credentialSaved, value))
            {
                OnPropertyChanged(nameof(ListSubtitle));
            }
        }
    }

    /// <summary>该通道任务全部结束后的会话处理方式，见 <see cref="SessionFinishModes"/>。</summary>
    [JsonPropertyName("session_finish")]
    public string SessionFinish
    {
        get => _sessionFinish;
        set => SetProperty(ref _sessionFinish, SessionFinishModes.Normalize(value));
    }

    /// <summary>远程桌面宽度，0 表示自适应。</summary>
    [JsonPropertyName("desktop_width")]
    public int DesktopWidth
    {
        get => _desktopWidth;
        set => SetProperty(ref _desktopWidth, RdpResolutions.Normalize(value, RdpResolutions.MinWidth, RdpResolutions.MaxWidth));
    }

    /// <summary>远程桌面高度，0 表示自适应。</summary>
    [JsonPropertyName("desktop_height")]
    public int DesktopHeight
    {
        get => _desktopHeight;
        set => SetProperty(ref _desktopHeight, RdpResolutions.Normalize(value, RdpResolutions.MinHeight, RdpResolutions.MaxHeight));
    }

    /// <summary>停用后该通道的任务不会被下发（开始执行时归入"未启动"并给出原因）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(ListSubtitle));
            }
        }
    }

    /// <summary>
    /// 通道列表里名称之外的那行小字：目标 + 未配账户 / 已停用 / 凭据已存。
    /// 放在模型上而不是界面里，是为了让列表项模板只绑一个属性（三个来源都能通知到）。
    /// </summary>
    [JsonIgnore]
    public string ListSubtitle
    {
        get
        {
            var user = string.IsNullOrWhiteSpace(User) ? "未配账户" : User;
            var state = Enabled ? string.Empty : " · 已停用";
            var cred = CredentialSaved ? " · 凭据已存" : string.Empty;
            return $"{Host} / {user}{state}{cred}";
        }
    }

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(User) ? User
        : "未命名通道";

    /// <summary>是否配好了目标账户（没配的通道不能执行，规划时进"未启动"）。</summary>
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(User);

    public RdpChannel Clone() => (RdpChannel)MemberwiseClone();

    object ICloneable.Clone() => Clone();

    /// <summary>规整为合法值（id 兜底、枚举兜底、分辨率钳位）。</summary>
    public void Sanitize(int fallbackIndex = 1)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = NewId();
        }

        Host = RdpTargets.Normalize(Host);
        User = (User ?? string.Empty).Trim();
        BridgePath = (BridgePath ?? string.Empty).Trim();
        SessionFinish = SessionFinishModes.Normalize(SessionFinish);
        DesktopWidth = RdpResolutions.Normalize(DesktopWidth, RdpResolutions.MinWidth, RdpResolutions.MaxWidth);
        DesktopHeight = RdpResolutions.Normalize(DesktopHeight, RdpResolutions.MinHeight, RdpResolutions.MaxHeight);

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = IsConfigured ? User : $"通道 {Math.Max(1, fallbackIndex)}";
        }
    }
}
