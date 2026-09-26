// -*- coding: utf-8 -*-
using System;

namespace YukinoChan.Services;

/// <summary>
/// 主窗口几何计算：把 config.json 里记下的窗口位置/尺寸收敛到当前显示器工作区内。
///
/// 刻意做成**不引用任何 WinUI 类型**的纯整数运算，原因有两个：
///  1. 多显示器、分辨率变化、外接屏拔掉这些情况都会让"上次记录的坐标"变得不可用，
///     必须能直接单测（_smoke 里跑），不能只在真机上肉眼试；
///  2. 窗口几何计算本身与 WinUI 无关，没必要为了它把 Services 绑死到 UI 框架。
/// </summary>
public static class WindowPlacement
{
    /// <summary>从未记录过尺寸时的默认窗口宽度（物理像素）。</summary>
    public const int DefaultWidth = 1320;

    /// <summary>从未记录过尺寸时的默认窗口高度（物理像素）。</summary>
    public const int DefaultHeight = 860;

    /// <summary>窗口最小宽度：再小左侧导航（196）+ 看板娘（320）就放不下了。</summary>
    public const int MinWidth = 960;

    /// <summary>窗口最小高度：再小控制栏与页面内容会被压扁。</summary>
    public const int MinHeight = 640;

    /// <summary>标题栏至少要露出这么多像素，否则窗口没法拖动/关闭。</summary>
    public const int MinVisibleTitleStrip = 80;

    /// <summary>显示器工作区（物理像素，已排除任务栏）。Width/Height 为 0 表示未知。</summary>
    public readonly record struct Bounds(int X, int Y, int Width, int Height);

    /// <summary>窗口左上角坐标与宽高（物理像素）。</summary>
    public readonly record struct Placement(int X, int Y, int Width, int Height);

    /// <summary>
    /// 计算窗口启动时应当恢复到的矩形。
    ///
    /// 规则（顺序即优先级）：
    ///  1. 工作区未知 → 原样返回记录值（缺失部分用默认值），交给系统决定；
    ///  2. 尺寸缺失/非法 → 用默认尺寸；尺寸超界 → 钳到 [最小值, 工作区]，
    ///     工作区比最小值还小时以工作区为准（小屏上不该出现比屏幕还大的窗口）；
    ///  3. 位置缺失，或标题栏落在工作区外（换了显示器 / 拔掉外接屏 / 改了缩放）
    ///     → 在工作区里居中，这是唯一不会把窗口弄丢的做法。
    /// </summary>
    public static Placement Resolve(int? x, int? y, int width, int height, Bounds workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return new Placement(
                x ?? 0,
                y ?? 0,
                width > 0 ? width : DefaultWidth,
                height > 0 ? height : DefaultHeight);
        }

        var maxWidth = workArea.Width;
        var maxHeight = workArea.Height;
        var minWidth = Math.Min(MinWidth, maxWidth);
        var minHeight = Math.Min(MinHeight, maxHeight);

        var resolvedWidth = width > 0
            ? Math.Clamp(width, minWidth, maxWidth)
            : Math.Clamp(DefaultWidth, minWidth, maxWidth);
        var resolvedHeight = height > 0
            ? Math.Clamp(height, minHeight, maxHeight)
            : Math.Clamp(DefaultHeight, minHeight, maxHeight);

        var centerX = workArea.X + Math.Max(0, (workArea.Width - resolvedWidth) / 2);
        var centerY = workArea.Y + Math.Max(0, (workArea.Height - resolvedHeight) / 2);

        if (x is null || y is null)
        {
            return new Placement(centerX, centerY, resolvedWidth, resolvedHeight);
        }

        var posX = x.Value;
        var posY = y.Value;

        // 至少留一条标题栏在工作区里，用户才拖得动、关得掉
        var minX = workArea.X - resolvedWidth + MinVisibleTitleStrip;
        var maxX = workArea.X + workArea.Width - MinVisibleTitleStrip;
        var minY = workArea.Y;
        var maxY = workArea.Y + workArea.Height - MinVisibleTitleStrip;

        if (posX < minX || posX > maxX || posY < minY || posY > maxY)
        {
            return new Placement(centerX, centerY, resolvedWidth, resolvedHeight);
        }

        return new Placement(posX, posY, resolvedWidth, resolvedHeight);
    }
}
