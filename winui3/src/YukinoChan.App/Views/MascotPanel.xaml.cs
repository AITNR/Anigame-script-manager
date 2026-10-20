// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

/// <summary>看板娘面板的三种版式。列宽由 <see cref="MainWindow"/> 负责，这里只决定显示哪一套内容。</summary>
public enum MascotPanelMode
{
    /// <summary>完整面板（320）。</summary>
    Expanded,

    /// <summary>收起成窄条，只留状态 / 进度 / 耗时（72）。</summary>
    Collapsed,

    /// <summary>整个关闭（列宽 0，右侧留一个把手可再打开）。</summary>
    Closed,
}

public sealed partial class MascotPanel : UserControl
{
    /// <summary>
    /// 具名 handler + Loaded/Unloaded 成对挂退。
    /// ⚠️ 原来写成匿名 lambda（`VM.PropertyChanged += (_, args) => ...`）就没法退订，
    /// 而 VM 是活到进程结束的单例 —— 每次新建这个控件都多一个订阅者。
    /// </summary>
    private readonly PropertyChangedEventHandler _vmPropertyChanged;

    /// <summary>用户按下「收起」：请宿主把列宽收成窄条。</summary>
    public event EventHandler? CollapseRequested;

    /// <summary>用户按下「展开」：请宿主还原成完整面板。</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>用户按下「关闭」：请宿主把整列收掉。</summary>
    public event EventHandler? CloseRequested;

    public MascotPanel()
    {
        InitializeComponent();

        _vmPropertyChanged = OnViewModelPropertyChanged;
        Loaded += (_, _) =>
        {
            VM.PropertyChanged -= _vmPropertyChanged;
            VM.PropertyChanged += _vmPropertyChanged;
        };
        Unloaded += (_, _) => VM.PropertyChanged -= _vmPropertyChanged;

        RefreshPlaceholder();
    }

    /// <summary>当前版式（宿主设置列宽后回填，供宿主判断"关闭后该还原成哪一档"）。</summary>
    public MascotPanelMode Mode { get; private set; } = MascotPanelMode.Expanded;

    public MainViewModel VM => App.ViewModel;

    /// <summary>
    /// 切换版式。只管内容可见性，**不碰自己的外宽** ——
    /// 外宽由 <see cref="MainWindow"/> 用列宽控制，两边各管一件事才不会互相覆盖。
    /// </summary>
    public void ApplyMode(MascotPanelMode mode)
    {
        Mode = mode;

        var expanded = mode != MascotPanelMode.Collapsed;
        var target = expanded ? (UIElement)ExpandedRoot : RailRoot;

        // 版式真的换了才需要淡入：Expanded ↔ Closed 用的是同一套内容，
        // 这时候再播一次淡入会让面板在"滑出去"的过程中先闪一下空白。
        var switched = target.Visibility != Visibility.Visible;

        ExpandedRoot.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RailRoot.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;

        if (!switched)
        {
            return;
        }

        // 切过去的那一套淡入一下：列宽补间负责"拉开/合上"，内容这边补一点渐变，
        // 看起来不像是在一个会变形的窗口里硬切版面。
        //
        // ⚠️ 只在控件已经挂进可视树之后才播：构造期（MainWindow 恢复上次版式时 IsLoaded 还是 false）
        // 动画不一定执行，没必要为了一次看不见的切换去起一条 Storyboard。
        if (IsLoaded)
        {
            FadeIn(target);
        }
        else
        {
            target.Opacity = 1;
        }
    }

    /// <summary>纯 Opacity 动画，走合成线程，不触发布局，也不会去碰内嵌的原生画面。</summary>
    private static void FadeIn(UIElement element)
    {
        // 用 From=0 而不是先手写 element.Opacity = 0：
        // 万一动画根本没跑起来，基础值仍然是 1（看得见），不会把界面留在透明状态。
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var board = new Storyboard();
        board.Children.Add(fade);

        // HoldEnd 会把动画值钉在属性上，之后谁再改 Opacity 都不生效 —— 收尾手动落回 1 并停掉
        board.Completed += (_, _) =>
        {
            element.Opacity = 1;
            board.Stop();
        };

        board.Begin();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.MascotImage))
        {
            RefreshPlaceholder();
        }
    }

    private void OnBubbleClicked(object sender, RoutedEventArgs e) => VM.OnMascotBubbleClicked();

    private void OnCollapseClicked(object sender, RoutedEventArgs e)
        => CollapseRequested?.Invoke(this, EventArgs.Empty);

    private void OnExpandClicked(object sender, RoutedEventArgs e)
        => ExpandRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClicked(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RefreshPlaceholder()
    {
        // 只有展开态有立绘（收起态窄条里不再放看板娘本体），所以只处理这一处
        MascotPlaceholder.Visibility = VM.MascotImage is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
