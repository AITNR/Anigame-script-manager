// -*- coding: utf-8 -*-
using System;
using System.Text.Json.Serialization;
using YukinoChan.Helpers;

namespace YukinoChan.Models;

/// <summary>
/// 主窗口的位置与尺寸（"记忆上次大小"）。
///
/// 约定：
///  1. 单位为物理像素，与 AppWindow.Position / AppWindow.Size 一致；
///  2. X / Y 为 null、width / height 为 0 都表示"没有记录过"，由
///     <see cref="Services.WindowPlacement"/> 用默认值或屏幕居中兜底；
///  3. Maximized 只记"上次是不是最大化"，具体还原尺寸另存于 width / height，
///     这样最大化状态下退出、下次还原回小窗时不会拿到整屏尺寸。
/// </summary>
public sealed class WindowConfig : ObservableObject, ICloneable
{
    /// <summary>看板娘面板：完整显示（默认）。</summary>
    public const string MascotPanelExpanded = "expanded";

    /// <summary>看板娘面板：收起成窄条，只留状态 / 进度 / 耗时。</summary>
    public const string MascotPanelCollapsed = "collapsed";

    /// <summary>看板娘面板：整个关闭，右侧留一个把手可再打开。</summary>
    public const string MascotPanelClosed = "closed";

    /// <summary>关闭按钮：每次都问（默认）。</summary>
    public const string CloseActionAsk = "ask";

    /// <summary>关闭按钮：直接收进状态栏（不再问）。</summary>
    public const string CloseActionTray = "tray";

    /// <summary>关闭按钮：直接退出（不再问）。</summary>
    public const string CloseActionExit = "exit";

    /// <summary>关闭按钮：任务执行时隐藏到状态栏，空闲时退出。</summary>
    public const string CloseActionAuto = "auto";

    private int? _x;
    private int? _y;
    private int _width;
    private int _height;
    private bool _maximized;
    private string _mascotPanel = MascotPanelExpanded;
    private string _closeAction = CloseActionAsk;

    /// <summary>窗口左上角 X（物理像素）。null = 未记录。</summary>
    [JsonPropertyName("x")]
    public int? X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    /// <summary>窗口左上角 Y（物理像素）。null = 未记录。</summary>
    [JsonPropertyName("y")]
    public int? Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    /// <summary>窗口宽度（物理像素）。0 = 未记录，用默认宽度。</summary>
    [JsonPropertyName("width")]
    public int Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    /// <summary>窗口高度（物理像素）。0 = 未记录，用默认高度。</summary>
    [JsonPropertyName("height")]
    public int Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    /// <summary>上次退出时是否处于最大化。</summary>
    [JsonPropertyName("maximized")]
    public bool Maximized
    {
        get => _maximized;
        set => SetProperty(ref _maximized, value);
    }

    /// <summary>
    /// 看板娘面板的版式：<c>expanded</c> / <c>collapsed</c> / <c>closed</c>。
    ///
    /// 放在 window 节点里，因为它和窗口几何是同一类东西 —— 纯界面偏好，
    /// 关掉之后下次启动也该还是关掉的。取值一律经 <see cref="NormalizeMascotPanel"/> 收敛。
    /// </summary>
    [JsonPropertyName("mascot_panel")]
    public string MascotPanel
    {
        get => _mascotPanel;
        set => SetProperty(ref _mascotPanel, value);
    }

    /// <summary>把任意字符串收敛成三个合法值之一（未知/空 → 完整显示，老配置也就自然变成默认值）。</summary>
    public static string NormalizeMascotPanel(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            MascotPanelCollapsed => MascotPanelCollapsed,
            MascotPanelClosed => MascotPanelClosed,
            _ => MascotPanelExpanded,
        };
    }

    /// <summary>
    /// 点关闭按钮时怎么处理窗口。<c>ask</c> / <c>tray</c> / <c>exit</c> / <c>auto</c>。
    ///
    /// <para>放在 window 节点里，理由同 <see cref="MascotPanel"/>：纯界面偏好，与窗口几何是一类东西。
    /// 未知值一律回落 <c>ask</c> —— 老配置里没有这个键，行为就是"每次都问"，最安全。</para>
    /// </summary>
    [JsonPropertyName("close_action")]
    public string CloseAction
    {
        get => _closeAction;
        set => SetProperty(ref _closeAction, NormalizeCloseAction(value));
    }

    /// <summary>把任意字符串收敛成四个合法值之一（未知/空 → 每次询问）。</summary>
    public static string NormalizeCloseAction(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            CloseActionTray => CloseActionTray,
            CloseActionExit => CloseActionExit,
            CloseActionAuto => CloseActionAuto,
            _ => CloseActionAsk,
        };
    }

    /// <summary>把明显非法的数值收敛成"未记录"，真正的范围钳制交给 WindowPlacement。</summary>
    public void Sanitize()
    {
        MascotPanel = NormalizeMascotPanel(MascotPanel);
        CloseAction = NormalizeCloseAction(CloseAction);

        if (Width < 0)
        {
            Width = 0;
        }

        if (Height < 0)
        {
            Height = 0;
        }

        if (X is < -32768 or > 32768)
        {
            X = null;
        }

        if (Y is < -32768 or > 32768)
        {
            Y = null;
        }
    }

    public object Clone() => (WindowConfig)MemberwiseClone();
}
