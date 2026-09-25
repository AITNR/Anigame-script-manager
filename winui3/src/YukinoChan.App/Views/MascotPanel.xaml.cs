// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class MascotPanel : UserControl
{
    public MascotPanel()
    {
        InitializeComponent();
        UpdatePlaceholderVisibility();
    }

    public MainViewModel VM => App.ViewModel;

    private void OnBubbleClicked(object sender, RoutedEventArgs e) => VM.OnMascotBubbleClicked();

    private void UpdatePlaceholderVisibility()
    {
        VM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.MascotImage))
            {
                RefreshPlaceholder();
            }
        };

        RefreshPlaceholder();
    }

    private void RefreshPlaceholder()
    {
        MascotPlaceholder.Visibility = VM.MascotImage is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
