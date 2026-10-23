// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;
using Yomori.Helpers;

namespace Yomori.ViewModels;

/// <summary>
/// 右侧执行状态栏里的一行「执行项目」—— 一条会话通道，或未分配通道的「本地执行」组。
///
/// <para>**为什么固定建好 8 行复用，而不是每秒重建集合**：状态栏以 1Hz 轮询刷新
/// （通道会话的属性不可观察，只能轮询）。若每拍 <c>Clear()</c> + <c>Add()</c>，
/// ItemsControl 会整体重建每个项的容器，列表在视觉上每秒闪一下 ——
/// 主页的「会话通道」概览就是那种写法，但它只在主页可见时才跑。
/// 通道数上限本来就是 8，一次建满、之后只改属性最省事也最稳。</para>
///
/// <para>字段全部是「显示用字符串」，不做任何计算 —— 取值与汇总都在
/// <c>MainWindow.RefreshSideStatus</c> 里一次算完，这里只负责扛住变更通知。</para>
/// </summary>
public sealed class SideStatusRow : ObservableObject
{
    private string _name = string.Empty;
    private string _progressText = string.Empty;
    private string _stateText = string.Empty;
    private Visibility _activeDot = Visibility.Collapsed;
    private Visibility _visible = Visibility.Collapsed;

    /// <summary>通道名（「本地执行」或配置里的通道显示名）。</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>该通道的完成数 / 总数，例如 <c>3 / 7</c>。</summary>
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    /// <summary>该通道的状态文案（运行中 / 已完成 / 已失联…）。</summary>
    public string StateText
    {
        get => _stateText;
        set => SetProperty(ref _stateText, value);
    }

    /// <summary>正在跑的那一行前面亮一个色点，方便一眼扫到。</summary>
    public Visibility ActiveDot
    {
        get => _activeDot;
        set => SetProperty(ref _activeDot, value);
    }

    /// <summary>行本身的可视性 —— 实际通道数少于 8 时，多出来的行整行收起。</summary>
    public Visibility Visible
    {
        get => _visible;
        set => SetProperty(ref _visible, value);
    }

    /// <summary>把某一行的显示内容整体刷一遍（值没变时 SetProperty 不会发通知）。</summary>
    public void Show(string name, string progressText, string stateText, bool active)
    {
        Name = name;
        ProgressText = progressText;
        StateText = stateText;
        ActiveDot = active ? Visibility.Visible : Visibility.Collapsed;
        Visible = Visibility.Visible;
    }

    /// <summary>这一拍用不到这一行（本轮通道数变少了）—— 收起，但不清空文本。</summary>
    public void Hide()
    {
        ActiveDot = Visibility.Collapsed;
        Visible = Visibility.Collapsed;
    }
}
