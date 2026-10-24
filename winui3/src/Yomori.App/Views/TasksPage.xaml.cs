// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Yomori.Helpers;
using Yomori.Models;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

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

    /// <summary>
    /// VM.PropertyChanged 的处理器 —— **必须是具名字段并在 Unloaded 里退订**。
    ///
    /// ⚠️ 这里以前是 `VM.PropertyChanged += (_, args) => {...}` 匿名 lambda：无法退订。
    /// 而 VM 是全局单例、活到进程结束，页面每次导航都新建（MainWindow 用
    /// <c>ContentFrame.Navigate</c>，不做缓存），于是**每进一次任务页就多一个订阅者**，
    /// 旧的全都还活着。导入配置时 <c>Tasks.Clear()</c> + 逐个 Add + 多次 RefreshScopeTasks()
    /// 会广播给所有累积的僵尸订阅者，每个都去碰一个早已离开可视树、甚至已被
    /// Frame 换掉的 ListView —— 轻则抛 WinRT 异常（Message 常为空串，日志里只留
    /// 一句「导入配置失败：」），重则把 UI 线程拖进死循环。
    /// </summary>
    private readonly PropertyChangedEventHandler _vmPropertyChanged;

    public TasksPage()
    {
        InitializeComponent();

        TimeoutActionBox.ItemsSource = VM.TimeoutActionItems;
        WaitModeBox.ItemsSource = VM.WaitModeItems;
        ConcurrentPolicyBox.ItemsSource = VM.ConcurrentPolicyItems;

        _vmPropertyChanged = OnViewModelPropertyChanged;
        VM.PropertyChanged += _vmPropertyChanged;

        // 拖动排序要判断「这次按下是不是抓手」。ListViewItem 会把 PointerPressed 标成
        // Handled，普通订阅收不到，所以必须 handledEventsToo: true。
        TaskList.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnTaskListPointerPressed),
            handledEventsToo: true);

        // 页面离开可视树就退订。Frame 可能缓存页面，所以 Loaded 时要重新挂上，
        // 否则从别的页返回时选中态再也不跟着 VM 走。
        // 退订后再挂是幂等的（同一 handler 挂两次也会被去重），这里仍写成
        // 「先 - 再 +」是为了让「任何时候最多只有一个订阅」的意图显式。
        Loaded += (_, _) =>
        {
            VM.PropertyChanged -= _vmPropertyChanged;
            VM.PropertyChanged += _vmPropertyChanged;
        };
        Unloaded += (_, _) => VM.PropertyChanged -= _vmPropertyChanged;

        SyncScopeSelection();
        SyncTaskSelection();
        SyncForm();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
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

    /// <summary>
    /// 通道行右侧的开关：确认这条通道本轮要不要执行。
    ///
    /// 走 <c>Toggled</c> 而不是纯 <c>x:Bind</c> 两向绑定 —— <see cref="ChannelScope"/> 只是展示模型，
    /// 真正要改的是通道配置里的 <c>enabled</c>（「通道管理」页那个开关同一份），必须由 VM 落盘。
    /// 列表项实体化时 x:Bind 也会填一次 IsOn 触发本事件，VM 里对「值没变」做了空转处理。
    /// </summary>
    private void OnScopeEnabledToggled(object sender, RoutedEventArgs e)
    {
        // ⚠️ 必须读控件自己的 IsOn，不能读 scope.Enabled —— Toggled 早于 TwoWay 绑定回写，
        //    这时 scope.Enabled 还是**旧值**。读旧值的症状：把开关拨到"开"，配置却仍是停用，
        //    行上还挂着「通道已停用 —— 本轮不会下发」（2026-10-24 用户实测就是这个）。
        if (sender is ToggleSwitch toggle && toggle.DataContext is ChannelScope scope)
        {
            VM.SetScopeEnabled(scope, toggle.IsOn);
        }
    }

    /// <summary>去「会话通道」父项下的「通道管理…」子项（增删通道、填凭据）。</summary>
    private void OnOpenChannels(object sender, RoutedEventArgs e) => VM.Navigate("channel-mgmt");

    // ---------------- 中：通道内的任务 ----------------

    private void OnAddTask(object sender, RoutedEventArgs e) => VM.AddTaskToScope();

    private void OnDeleteTask(object sender, RoutedEventArgs e) => VM.DeleteSelectedTask();

    private void OnMoveUp(object sender, RoutedEventArgs e) => VM.MoveSelectedTaskInScope(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => VM.MoveSelectedTaskInScope(1);

    // ---------------- 中：拖放排序（抓手拖动） ----------------

    /// <summary>
    /// 这次按住的是不是抓手。ListView 的拖放是**整行都能起拖**的（<c>CanDragItems</c>），
    /// 所以要在 <c>DragItemsStarting</c> 里把「不是从抓手按下的」那些取消掉，
    /// 否则在右侧开关上按住稍微一挪就开始拖动行，开关拨不动。
    /// </summary>
    private bool _dragFromGrip;

    /// <summary>
    /// 记录按下的位置是不是抓手。
    ///
    /// ① 用 <c>AddHandler(..., handledEventsToo: true)</c>：ListViewItem 会把 PointerPressed
    ///    标成 Handled，普通订阅收不到；
    /// ② 只在这里判源、不另挂抓手的 PointerPressed：冒泡顺序是「源元素 → 祖先」，
    ///    两处都挂反而要靠顺序猜，容易写成"先置真再被清零"。
    /// </summary>
    private void OnTaskListPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        var fromGrip = false;

        while (node is not null && !ReferenceEquals(node, TaskList))
        {
            if (node is FrameworkElement { Tag: "TaskGrip" })
            {
                fromGrip = true;
                break;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        _dragFromGrip = fromGrip;
    }

    /// <summary>
    /// 只有从抓手起拖才放行。
    ///
    /// 为什么最后用了 ListView **内置**的拖放重排（<c>CanReorderItems</c> + <c>ReorderMode</c>）：
    /// 一开始是给抓手挂 <c>CanDrag="True"</c> 自己做 DragStarting/DragOver/Drop，实测**拖不动** ——
    /// 指针被 ListViewItem 的按下/选择逻辑接走，子元素的拖拽手势根本不启动。
    /// 内置重排是平台自己实现的那一套，还能白拿一条插入位置指示线。
    /// </summary>
    private void OnTaskItemsDragStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (!_dragFromGrip)
        {
            args.Cancel = true;
            return;
        }

        _dragFromGrip = false;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText("reorder");
    }

    /// <summary>
    /// 拖完把「界面上此刻的次序」翻译成 order。
    /// 内置重排已经改过 <c>VM.ScopeTasks</c>（ItemsSource 就是它），这里只做落账与序号重编。
    /// </summary>
    private void OnTaskItemsDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        => VM.ApplyScopeOrderFromView();

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
            // 同 SyncTaskSelection：ChannelScopes 每次刷新都被整体重建，
            // 旧引用必然不在集合里 —— 绝不能把它直接塞给 ListView.SelectedItem。
            var scope = VM.SelectedScope;
            if (scope is null || !VM.ChannelScopes.Contains(scope))
            {
                ScopeList.SelectedItem = null;
            }
            else
            {
                ScopeList.SelectedItem = scope;
            }
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
            // ⚠️ 赋一个「不在 ItemsSource 里」的对象给 ListView.SelectedItem，
            //    WinUI 会反复测量/布局失效把 UI 线程拖进死循环（2026-10-04 导入配置后卡死）。
            //    集合刚被整体替换（增删 / 排序 / 改派 / 导入）时旧引用必然已失效，
            //    所以这里先置空再设 —— 让列表始终回到一个自洽的选中态。
            var selected = VM.SelectedTask;
            if (selected is null || !VM.ScopeTasks.Contains(selected))
            {
                TaskList.SelectedItem = null;
            }
            else
            {
                TaskList.SelectedItem = selected;
            }
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
