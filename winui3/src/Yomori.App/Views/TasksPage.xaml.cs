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

        // 保险：即使抓手没抢到指针（被 ListViewItem 先捕获 / 被它标成 Handled），
        // 移动与松开事件仍会从捕获元素冒泡到 ListView —— 在这里再收一道。
        // 两个处理函数对「没在拖」是空转，重复触发无害（EndDrag 后 _dragTask 为 null）。
        TaskList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnTaskGripPointerMoved), true);
        TaskList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTaskGripPointerReleased), true);

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

    // ---------------- 中：拖放排序（抓手拖动，实时重排） ----------------

    /// <summary>
    /// 拖动用到的全部状态：
    /// 被拖的任务 / 抓手元素 / 该任务的容器（ListViewItem）/ 施加在容器上的位移 /
    /// 指针 id / 是否已过起拖阈值 / 按下时的 Y / 抓取点在行内的偏移 / 最近一次指针 Y。
    /// </summary>
    private TaskConfig? _dragTask;
    private FrameworkElement? _dragGrip;
    private FrameworkElement? _dragContainer;
    private TranslateTransform? _dragTranslate;
    private uint _dragPointerId;
    private bool _dragActive;
    private double _dragStartY;
    private double _dragGrabOffset;
    private double _lastPointerY;

    /// <summary>
    /// 抓手按下：**抢在 ListViewItem 前面把指针捕获过来**。
    ///
    /// 这是整套能动的关键。之前两版都栽在这里：
    ///   ① 抓手挂 `CanDrag="True"` 自己做 DragStarting/Drop —— 指针被 ListViewItem 的
    ///      按下/选择逻辑接走，子元素的拖拽手势根本不启动；
    ///   ② 改用 ListView 内置重排（`CanReorderItems` + `ReorderMode`）—— 同样按不动。
    /// 所以这里既不看 WinUI 的拖放，也不让它插手：`CapturePointer` 之后移动/松开事件都归抓手，
    /// 再 `e.Handled = true` 挡住 ListViewItem 自己的按下处理（否则它随后又把指针抢回去）。
    ///
    /// 代价：点抓手不再顺带选中那一行 —— 抓手只有二十来像素宽，点行内文字照常选中。
    /// </summary>
    private void OnTaskGripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement grip || grip.DataContext is not TaskConfig task)
        {
            return;
        }

        _dragTask = task;
        _dragGrip = grip;
        _dragPointerId = e.Pointer.PointerId;
        _dragStartY = e.GetCurrentPoint(TaskList).Position.Y;
        _lastPointerY = _dragStartY;
        _dragActive = false;

        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// 移动：过了 4px 阈值才真正起拖（否则点击会被判成拖动），之后
    /// ① 让被拖的行追着指针走；② 指针越过相邻行中线就把它在集合里挪过去 ——
    /// 集合一动，别的行就自动让位/复位，这就是"其它控件避开"的效果。
    /// </summary>
    private void OnTaskGripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragTask is null || e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        _lastPointerY = e.GetCurrentPoint(TaskList).Position.Y;

        if (!_dragActive)
        {
            if (Math.Abs(_lastPointerY - _dragStartY) < 4)
            {
                return;
            }

            if (!BeginDragVisual())
            {
                return;
            }

            _dragActive = true;
        }

        FollowPointer();
        StepTowardsPointer();
        e.Handled = true;
    }

    private void OnTaskGripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragTask is null || e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        EndDrag(e);
    }

    /// <summary>指针被系统抢走（滚动、窗口失焦、Esc）时收尾，别把行留在半透明/位移状态。</summary>
    private void OnTaskGripPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag(null);

    // ---------------- 拖动的三段内部逻辑 ----------------

    /// <summary>起拖：给被拖那行的容器挂上位移变换、压暗一点、抬到兄弟之上。</summary>
    private bool BeginDragVisual()
    {
        if (_dragTask is null)
        {
            return false;
        }

        _dragContainer = TaskList.ContainerFromItem(_dragTask) as FrameworkElement;
        if (_dragContainer is null)
        {
            return false;
        }

        _dragTranslate = new TranslateTransform();
        _dragContainer.RenderTransform = _dragTranslate;
        _dragContainer.Opacity = 0.92;
        Canvas.SetZIndex(_dragContainer, 100);

        // 位移为 0 时量一次，记下抓取点在行内的偏移 —— 之后不管行挪到哪都保持这个手感
        _dragGrabOffset = _lastPointerY - TopOf(_dragContainer);

        // 集合 Move 之后布局会重排，位移基准跟着变，必须重算一次（否则会看到跳动）
        TaskList.LayoutUpdated += OnDragLayoutUpdated;
        return true;
    }

    private void OnDragLayoutUpdated(object? sender, object e) => FollowPointer();

    /// <summary>
    /// 让被拖的行跟住指针：把「行顶 = 指针 - 抓取偏移」这条不变量重新满足一遍。
    ///
    /// 量之前必须把位移归零 —— `TransformToVisual` 会把元素自身的 RenderTransform 算进去，
    /// 不归零就成了「位移取决于位移」的自指。
    /// </summary>
    private void FollowPointer()
    {
        if (!_dragActive || _dragTask is null || _dragTranslate is null)
        {
            return;
        }

        SyncDragContainer();
        if (_dragContainer is null)
        {
            return;
        }

        _dragTranslate.Y = 0;
        var top = TopOf(_dragContainer);
        _dragTranslate.Y = _lastPointerY - _dragGrabOffset - top;
    }

    /// <summary>
    /// 指针越过下一行 / 上一行的中线就交换一位。
    ///
    /// 一次移动事件最多走一位：`Move` 之后布局还没跑完，同一 tick 里连读容器位置会读到旧值。
    /// 拖动时移动事件很密，跟得上；也不会因为一次跳跃而算错落点。
    /// </summary>
    private void StepTowardsPointer()
    {
        if (_dragTask is null)
        {
            return;
        }

        var from = VM.ScopeTasks.IndexOf(_dragTask);
        if (from < 0)
        {
            return;
        }

        var below = from + 1;
        if (below < TaskList.Items.Count
            && TaskList.ContainerFromIndex(below) is FrameworkElement next
            && _lastPointerY > TopOf(next) + next.ActualHeight / 2)
        {
            VM.MoveTaskInScopeTo(_dragTask, below);
            return;
        }

        var above = from - 1;
        if (above >= 0
            && TaskList.ContainerFromIndex(above) is FrameworkElement previous
            && _lastPointerY < TopOf(previous) + previous.ActualHeight / 2)
        {
            VM.MoveTaskInScopeTo(_dragTask, above);
        }
    }

    /// <summary>
    /// 确认位移还挂在「装着这条任务」的容器上。
    ///
    /// `ObservableCollection.Move` 之后容器有可能被回收给别的行（按位置复用），
    /// 那样位移就会跟着跑偏到别的行上 —— 发现装错项就把位移摘下来挂回正确的那只。
    /// </summary>
    private void SyncDragContainer()
    {
        if (_dragTask is null || _dragTranslate is null)
        {
            return;
        }

        if (_dragContainer is not null && ReferenceEquals(TaskList.ItemFromContainer(_dragContainer), _dragTask))
        {
            return;
        }

        if (_dragContainer is not null)
        {
            _dragContainer.RenderTransform = null;
            _dragContainer.Opacity = 1;
            Canvas.SetZIndex(_dragContainer, 0);
        }

        _dragContainer = TaskList.ContainerFromItem(_dragTask) as FrameworkElement;
        if (_dragContainer is null)
        {
            return;
        }

        _dragContainer.RenderTransform = _dragTranslate;
        _dragContainer.Opacity = 0.92;
        Canvas.SetZIndex(_dragContainer, 100);
    }

    /// <summary>
    /// 收尾：摘位移、恢复不透明度与层级。次序已经在拖动过程中即时改过了，
    /// 这里不再做任何落位动作 —— 摘掉位移后行会"落"进它此刻的位置。
    /// </summary>
    private void EndDrag(PointerRoutedEventArgs? e)
    {
        TaskList.LayoutUpdated -= OnDragLayoutUpdated;

        if (_dragContainer is not null)
        {
            _dragContainer.RenderTransform = null;
            _dragContainer.Opacity = 1;
            Canvas.SetZIndex(_dragContainer, 0);
        }

        if (_dragGrip is not null && e is not null)
        {
            _dragGrip.ReleasePointerCapture(e.Pointer);
        }

        _dragTask = null;
        _dragGrip = null;
        _dragContainer = null;
        _dragTranslate = null;
        _dragActive = false;
    }

    private double TopOf(FrameworkElement element) =>
        element.TransformToVisual(TaskList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

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
