// -*- coding: utf-8 -*-
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class MascotPanel : UserControl
{
    /// <summary>
    /// 具名 handler + Loaded/Unloaded 成对挂退。
    /// ⚠️ 原来写成匿名 lambda（`VM.PropertyChanged += (_, args) => ...`）就没法退订，
    /// 而 VM 是活到进程结束的单例 —— 每次新建这个控件都多一个订阅者。
    /// </summary>
    private readonly PropertyChangedEventHandler _vmPropertyChanged;

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

        UpdatePlaceholderVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.MascotImage))
        {
            RefreshPlaceholder();
        }
    }

    public MainViewModel VM => App.ViewModel;

    private void OnBubbleClicked(object sender, RoutedEventArgs e) => VM.OnMascotBubbleClicked();

    private void UpdatePlaceholderVisibility()
    {
        RefreshPlaceholder();
    }

    private void RefreshPlaceholder()
    {
        MascotPlaceholder.Visibility = VM.MascotImage is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
