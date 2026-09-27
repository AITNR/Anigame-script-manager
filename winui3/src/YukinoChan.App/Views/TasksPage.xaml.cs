// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YukinoChan.Helpers;
using YukinoChan.Models;
using YukinoChan.Services;
using YukinoChan.ViewModels;

namespace YukinoChan.Views;

/// <summary>
/// 任务执行页 —— **按执行通道编排任务**（计划书 <c>docs/tasks-by-channel-plan.md</c>）。
///
/// 页面只做三件事：把 VM 的「通道 / 通道内任务」视图显示出来、把按钮转发给 VM、同步选中态。
/// 排序、改归属、重编序号这些逻辑全在 <see cref="TaskScopePlanner"/>（纯函数，可脱离 WinUI 测）——
/// 塞进页面里就没法写冒烟了。
/// </summary>
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
            switch (args.PropertyName)
            {
                case nameof(MainViewModel.SelectedTask):
                    SyncTaskSelection();
                    SyncForm();
                    break;

                case nameof(MainViewModel.SelectedScope):
                    SyncScopeSelection();
                    SyncTaskSelection();
                    SyncForm();
                    break;

                // 任务视图被重建（增删 / 排序 / 改派）后要把列表选中态对回去
                case nameof(MainViewModel.ScopeTasks):
                    SyncTaskSelection();
                    break;
            }
        };

        SyncScopeSelection();
        SyncTaskSelection();
        SyncForm();
    }

    public MainViewModel VM => App.ViewModel;

    // ---------------- 左：执行通道 ----------------

    private void OnScopeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ScopeList.SelectedItem is not ChannelScope scope)
        {
            return;
        }

        VM.SelectedScope = scope;

        // 切通道后默认看这条通道的第一个任务 —— 沿用上一条通道的选中项会指向别的通道，看着像串台
        VM.SelectedTask = VM.ScopeTasks.FirstOrDefault();
    }

    /// <summary>去「会话通道」父项下的「通道管理…」子项（增删通道、填凭据）。</summary>
    private void OnOpenChannels(object sender, RoutedEventArgs e) => VM.Navigate("channel-mgmt");

    // ---------------- 中：通道内的任务 ----------------

    private void OnAddTask(object sender, RoutedEventArgs e) => VM.AddTaskToScope();

    private void OnDeleteTask(object sender, RoutedEventArgs e) => VM.DeleteSelectedTask();

    private void OnMoveUp(object sender, RoutedEventArgs e) => VM.MoveSelectedTaskInScope(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => VM.MoveSelectedTaskInScope(1);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        VM.SelectedTask = TaskList.SelectedItem as TaskConfig;
    }

    // ---------------- 右：任务详情 ----------------

    /// <summary>
    /// 「移到通道…」菜单：点击时现填 —— 通道随时可能被增删改，写死必然过期。
    ///
    /// 顺带说明为什么它是**代码里 new** 的而不是写在 XAML 里：放在 <c>Page.Resources</c> 并带
    /// x:Name 时，XAML 生成的 <c>Connect()</c> 会把具名资源的连接序号与后面控件的序号错位，
    /// 页面一构造就抛 <c>InvalidCastException</c>（把 Flyout 当 Button 转型）—— 页面根本进不去。
    /// </summary>
    private void OnMoveToChannelClick(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        foreach (var scope in VM.ChannelScopes)
        {
            if (VM.SelectedScope is not null &&
                string.Equals(scope.Id, VM.SelectedScope.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var id = scope.Id;
            var item = new MenuFlyoutItem { Text = scope.Name };
            item.Click += (_, _) => VM.MoveSelectedTaskToScope(id);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "没有别的通道", IsEnabled = false });
        }

        flyout.ShowAt((FrameworkElement)sender);
    }

    // ---------------- 选中态同步 ----------------

    private void SyncScopeSelection()
    {
        _syncing = true;
        try
        {
            ScopeList.SelectedItem = VM.SelectedScope;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncTaskSelection()
    {
        _syncing = true;
        try
        {
            TaskList.SelectedItem = VM.SelectedTask;
        }
        finally
        {
            _syncing = false;
        }

        ScopeEmptyState.Visibility = VM.ScopeTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
            OwnerBar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

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
