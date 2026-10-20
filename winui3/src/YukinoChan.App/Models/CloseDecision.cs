// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace YukinoChan.Models;

/// <summary>点关闭按钮之后实际要发生的事。</summary>
public enum CloseOutcome
{
    /// <summary>弹窗问用户。</summary>
    Ask,

    /// <summary>只把窗口收进状态栏，进程继续跑。</summary>
    HideToTray,

    /// <summary>结束进程。</summary>
    Exit,
}

/// <summary>
/// 关闭按钮的决策逻辑（纯函数，不碰窗口、不碰配置读写，可直接进冒烟工程）。
///
/// <para>把判断从 <c>MainWindow.AppWindow.Closing</c> 里抽出来的理由：那个事件是同步的、
/// 还夹着弹窗与隐藏窗口的副作用，逻辑一旦埋进去就只能靠手点验证。抽成纯函数之后，
/// "四种策略 × 是否在跑" 这八种组合可以在冒烟里一次跑完。</para>
/// </summary>
public static class CloseDecision
{
    /// <summary>设置页下拉框的四个选项（顺序即展示顺序）。</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(WindowConfig.CloseActionAsk, "每次询问"),
        new(WindowConfig.CloseActionTray, "最小化到状态栏"),
        new(WindowConfig.CloseActionExit, "直接退出"),
        new(WindowConfig.CloseActionAuto, "任务执行时隐藏，空闲时退出"),
    };

    /// <summary>
    /// 根据策略与"当前是否在跑任务"决定动作。
    ///
    /// <para><c>auto</c> 的语义只有一句：<b>跑着任务就别退，收进状态栏；没跑就退</b>。
    /// 之所以要"跑着就收"，是因为用户点关闭时多半只是想把它挪开，而不是中断一晚上的挂机。</para>
    /// </summary>
    public static CloseOutcome Decide(string? action, bool isRunning)
    {
        return WindowConfig.NormalizeCloseAction(action) switch
        {
            WindowConfig.CloseActionTray => CloseOutcome.HideToTray,
            WindowConfig.CloseActionExit => CloseOutcome.Exit,
            WindowConfig.CloseActionAuto => isRunning ? CloseOutcome.HideToTray : CloseOutcome.Exit,
            _ => CloseOutcome.Ask,
        };
    }

    /// <summary>下拉框里显示的中文名（未知值当作"每次询问"）。</summary>
    public static string Label(string? action)
    {
        var normalized = WindowConfig.NormalizeCloseAction(action);

        foreach (var item in Items)
        {
            if (string.Equals(item.Key, normalized, StringComparison.Ordinal))
            {
                return item.Value;
            }
        }

        return "每次询问";
    }

    /// <summary>
    /// 这次交互该把 <c>close_action</c> 落成什么值；返回 null 表示"保持原样"。
    ///
    /// <para>三条规则，对应弹窗上那两个互斥的勾选框：</para>
    /// <list type="bullet">
    ///   <item>勾了"任务执行时隐藏到状态栏，空闲时退出" → 落 <c>auto</c>，且**压过**另一个勾选（两者互斥，理论上不会同时为 true）；</item>
    ///   <item>勾了"记住我的选择" → 按这次点的按钮落 <c>tray</c> 或 <c>exit</c>；</item>
    ///   <item>都没勾（或点了取消） → 什么都不记，下次照样问。</item>
    /// </list>
    /// </summary>
    public static string? PersistFor(CloseOutcome clicked, bool remember, bool smartMode)
    {
        if (smartMode)
        {
            return WindowConfig.CloseActionAuto;
        }

        if (!remember)
        {
            return null;
        }

        return clicked switch
        {
            CloseOutcome.Exit => WindowConfig.CloseActionExit,
            CloseOutcome.HideToTray => WindowConfig.CloseActionTray,

            // 点了取消却勾着"记住"：不记。取消就是取消。
            _ => null,
        };
    }
}
