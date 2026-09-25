// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Helpers;
using YukinoChan.Models;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

public sealed partial class TasksPage : Page
{
    private bool _syncing;

    public TasksPage()
    {
        InitializeComponent();

        TimeoutActionBox.ItemsSource = VM.TimeoutActionItems;
        WaitModeBox.ItemsSource = VM.WaitModeItems;
        ConcurrentPolicyBox.ItemsSource = VM.ConcurrentPolicyItems;

        VM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedTask))
            {
                SyncForm();
            }
        };

        SyncForm();
    }

    public MainViewModel VM => App.ViewModel;

    // ---------------- 列表操作 ----------------

    private void OnAddTask(object sender, RoutedEventArgs e)
    {
        VM.AddTask();
        TaskList.SelectedItem = VM.SelectedTask;
    }

    private void OnDeleteTask(object sender, RoutedEventArgs e)
    {
        VM.DeleteSelectedTask();
        TaskList.SelectedItem = VM.SelectedTask;
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        VM.MoveSelectedUp();
        TaskList.SelectedItem = VM.SelectedTask;
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        VM.MoveSelectedDown();
        TaskList.SelectedItem = VM.SelectedTask;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        VM.SelectedTask = TaskList.SelectedItem as TaskConfig;
    }

    // ---------------- 表单同步 ----------------

    private void SyncForm()
    {
        _syncing = true;
        try
        {
            var task = VM.SelectedTask;
            FormHost.DataContext = task;

            var hasSelection = task is not null;
            FormHost.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;

            if (task is null)
            {
                return;
            }

            TimeoutBox.Value = task.TimeoutMinutes;
            ConfirmEnterBox.Value = task.ConfirmEnterDelaySeconds;
            SelectByKey(TimeoutActionBox, task.TimeoutAction);
            SelectByKey(WaitModeBox, task.WaitMode);
            SelectByKey(ConcurrentPolicyBox, task.ConcurrentPolicy);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SelectByKey(ComboBox box, string key)
    {
        if (box.ItemsSource is not IEnumerable<KeyValuePair<string, string>> items)
        {
            return;
        }

        var index = 0;
        foreach (var item in items)
        {
            if (item.Key == key)
            {
                box.SelectedIndex = index;
                return;
            }

            index++;
        }

        box.SelectedIndex = 0;
    }

    private static string? SelectedKey(ComboBox box, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is KeyValuePair<string, string> pair)
        {
            return pair.Key;
        }

        return null;
    }

    private void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        VM.SelectedTask.TimeoutMinutes = (int)Math.Round(double.IsNaN(args.NewValue) ? 0 : args.NewValue);
    }

    private void OnConfirmEnterChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        VM.SelectedTask.ConfirmEnterDelaySeconds = (int)Math.Round(double.IsNaN(args.NewValue) ? 0 : args.NewValue);
    }

    private void OnTimeoutActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        var key = SelectedKey((ComboBox)sender, e);
        if (key is not null)
        {
            VM.SelectedTask.TimeoutAction = key;
        }
    }

    private void OnWaitModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        var key = SelectedKey((ComboBox)sender, e);
        if (key is not null)
        {
            VM.SelectedTask.WaitMode = key;
        }
    }

    private void OnConcurrentPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        var key = SelectedKey((ComboBox)sender, e);
        if (key is not null)
        {
            VM.SelectedTask.ConcurrentPolicy = key;
        }
    }

    private async void OnBrowseScript(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedTask is null)
        {
            return;
        }

        var path = await DialogHelper.PickOpenFileAsync(
            ("脚本文件", ".exe"),
            ("脚本文件", ".bat"),
            ("脚本文件", ".cmd"),
            ("脚本文件", ".py"),
            ("所有文件", "."));

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        VM.SelectedTask.ScriptPath = path;

        if (string.IsNullOrWhiteSpace(VM.SelectedTask.Name) || VM.SelectedTask.Name.StartsWith("新任务", StringComparison.Ordinal))
        {
            VM.SelectedTask.Name = Path.GetFileNameWithoutExtension(path);
        }
    }
}
