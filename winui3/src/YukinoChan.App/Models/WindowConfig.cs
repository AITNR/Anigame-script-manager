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
    private int? _x;
    private int? _y;
    private int _width;
    private int _height;
    private bool _maximized;

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

    /// <summary>把明显非法的数值收敛成"未记录"，真正的范围钳制交给 WindowPlacement。</summary>
    public void Sanitize()
    {
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
