// -*- coding: utf-8 -*-
using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using YukinoChan.ViewModels;

namespace YukinoChan;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = "雪乃酱 / 二游脚本助手";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        var size = new SizeInt32(1320, 860);
        AppWindow.Resize(size);

        try
        {
            var iconPath = System.IO.Path.Combine(Services.AppPaths.AssetsDir, "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // 图标缺失不应阻止启动
        }

        VM.ShutdownPromptRequested += OnShutdownPromptRequested;
        VM.RequestNavigation += (_, tag) => NavigateTo(tag);

        // 激活态跟踪 + 错过通知的补发：本地多用户 RDP 接管期间主控端会话被锁，
        // 锁定会话不弹 Toast 横幅（通知只进操作中心）。
        // 完成时 MainViewModel 会把通知落盘（PendingNotificationStore），
        // 这里在窗口重新激活（用户切回来 / 应用重启）时消费补发。
        Activated += OnWindowActivated;

        if (Content is FrameworkElement root)
        {
            root.KeyDown += OnRootKeyDown;
        }

        NavigateTo("home");
    }

    public MainViewModel VM => App.ViewModel;

    /// <summary>窗口当前是否处于激活态（会话锁定时会失活）。</summary>
    public static bool IsWindowActive { get; private set; }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        IsWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;

        if (IsWindowActive)
        {
            VM.PresentPendingNotification();
        }
    }

    private string _currentTag = string.Empty;

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        if (string.Equals(_currentTag, tag, StringComparison.Ordinal))
        {
            return;
        }

        _currentTag = tag;

        switch (tag)
        {
            case "tasks":
                ContentFrame.Navigate(typeof(Views.TasksPage));
                break;
            case "stats":
                ContentFrame.Navigate(typeof(Views.StatsPage));
                break;
            case "logs":
                ContentFrame.Navigate(typeof(Views.LogsPage));
                break;
            case "rdp":
                ContentFrame.Navigate(typeof(Views.RdpPage));
                break;
            case "settings":
                ContentFrame.Navigate(typeof(Views.SettingsPage));
                break;
            default:
                ContentFrame.Navigate(typeof(Views.HomePage));
                break;
        }

        VM.UpdateMascotForPage(tag);
        SyncNavigationSelection(tag);
    }

    private void SyncNavigationSelection(string tag)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string itemTag && itemTag == tag)
            {
                NavView.SelectedItem = navItem;
                return;
            }
        }

        foreach (var item in NavView.FooterMenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string itemTag && itemTag == tag)
            {
                NavView.SelectedItem = navItem;
                return;
            }
        }
    }

    // ---------------- 控制栏 ----------------

    private void OnStartClicked(object sender, RoutedEventArgs e) => VM.Start();

    private void OnPauseClicked(object sender, RoutedEventArgs e) => VM.Pause();

    private void OnResumeClicked(object sender, RoutedEventArgs e) => VM.Resume();

    private void OnStopClicked(object sender, RoutedEventArgs e) => VM.Stop();

    private void OnEmergencyStopClicked(object sender, RoutedEventArgs e) => VM.EmergencyStop();

    private void OnSaveClicked(object sender, RoutedEventArgs e) => VM.SaveConfig();

    private void OnCancelShutdownClicked(object sender, RoutedEventArgs e) => VM.CancelShutdown();

    private async void OnShutdownPromptRequested(object? sender, int delaySeconds)
    {
        var dialog = new Views.ShutdownCountdownDialog(delaySeconds)
        {
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            VM.CancelShutdown();
        }
    }

    // ---------------- 快捷键：F8 停止 / Ctrl+Alt+F8 紧急停止 ----------------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.F8)
        {
            if (ctrl && alt)
            {
                VM.EmergencyStop();
            }
            else
            {
                VM.Stop();
            }

            e.Handled = true;
        }
    }
}
