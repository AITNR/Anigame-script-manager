# 雪乃酱 · 关闭按钮行为与状态栏托盘 计划书

- **版本**：v1 已实施（2026-10-20）
- **状态**：M1–M4 代码已完成并通过 Debug/Release 编译；**真机 GUI 验收（M5）待做**，清单见 §10。
- **适用范围**：`repo/winui3/src/YukinoChan.App`（主控端界面；代理无窗口，不涉及）
- **取代**：`task-resume-plan.md`（断点续跑，已作废并删除，本方案不再包含任何续跑内容）

### 已拍板（2026-10-20 与 AITNR 确认）

| #   | 决策点 | 结论 |
| --- | --- | --- |
| D1 | 关闭按钮行为 | 点关闭**不直接退出**，先弹窗，二选一：最小化到状态栏 / 退出 |
| D2 | 弹窗里的勾选项 | 两个：① 记住我的选择 ② 任务执行时隐藏到状态栏，空闲时退出 |
| D3 | 两个勾选项的关系 | **互斥**，只能勾一个（勾一个自动取消另一个） |

---

## 1. 目标与非目标

### 1.1 目标

1. 点标题栏关闭 / `Alt+F4` / 任务栏右键「关闭窗口」时，**先问一句**再决定，不是直接结束进程。
2. 勾了「记住我的选择」就按记忆执行，不再每回都问；记忆要能在设置页改回来。
3. 勾了「任务执行时隐藏到状态栏，空闲时退出」就进入**按状态自动决策**：跑任务时只收进托盘，跑完自动退出。
4. 托盘图标可用：双击 / 单击恢复窗口，右键有菜单（显示主界面、开始/停止、退出、关闭行为设置）。
5. 现有退出语义不被破坏：自动退出、关机倒计时、注销关机、崩溃看护、RDP 画面释放，全都还得对。

### 1.2 非目标

- 不改「最小化」按钮的行为（最小化仍是到任务栏，不进托盘）。
- 不做开机自动隐藏（启动时一律显示主窗口）。
- 不改任务执行语义、不改 RDP 通道逻辑、不改代理。
- 不做"关闭到托盘后定时退出/内存回收"这类后台策略。
- 不解决"退出时本地任务是否继续跑"的语义变更：**退出 = 主控端进程结束**，本地组任务按现有行为清理，远程通道的任务在被控会话里继续（这是今天的既有行为，本方案不动，只在弹窗文案里写清楚）。

---

## 2. 现状盘点

| 位置 | 现状 | 后果 |
|---|---|---|
| `Views/MainWindow.xaml.cs:101` | 只有 `Closed` 事件：落盘窗口位置 + `VM.DisposeEmbedClient()` | 关窗即销毁窗口，进程随之退出 |
| 全工程搜索 `托盘 / Tray / NotifyIcon / Shell_NotifyIcon` | **零命中** | 完全没有任何托盘能力，要从零写 |
| 全工程搜索 `Mutex / SingleInstance` | 零命中 | 收进托盘后再双击 exe 会**起第二个实例**，两个都能跑任务 |
| `ViewModels/MainViewModel.cs:5443` | `AutoExitAfterDone` 直接 `App.MainWindow?.Close()` | 加了关闭拦截后，自动退出会被自己的弹窗挡住 —— 必须标记为"程序性退出" |
| `Views/MainWindow.xaml.cs:754 OnShutdownPromptRequested` | 用 `Content.XamlRoot` 弹关机倒计时 | 窗口若隐在托盘，倒计时对话框**用户看不见**，到点机器就关了 |
| `Services/UiHangWatchdog.cs` | `SendMessageTimeout` 探活 UI 线程 | 隐藏不影响，无需改（窗口对象还在） |
| `Models/WindowConfig.cs` | 已有 `mascot_panel` 这类"界面偏好"，`Clone()` 用 `MemberwiseClone` | 新字段放这里最合适，克隆自动带上，不用改 `CloneWithTasks` |
| `Helpers/DialogHelper.cs:129 ShowAsync` | 全局唯一 ContentDialog 入口（带闸门，防"同时只能有一个"崩溃） | 新弹窗必须走它 |

**可复用资产**：
- `Helpers/DialogHelper.ShowAsync`（闸门 + 吞异常）—— 不自建对话框调度。
- `Services/WindowPlacement.Resolve`（:47）—— 恢复窗口时复用它的"至少露出 80px 标题条"钳制，防显示器拔掉后窗口跑到看不见的地方。
- `Services/NotificationService.Show` —— "已隐藏到状态栏"的提示直接走 Toast，不用托盘气泡（`NIF_INFO`）那套兼容性差的旧机制。
- `Helpers/WindowHelper.GetHwnd` —— 取主窗口句柄。
- `Services/NativeMethods` 里已有的 `EnumWindows` / `GetWindowThreadProcessId` / `GetSystemMetrics` —— 单实例激活与 DPI 取尺寸要用。

---

## 3. 交互设计

### 3.1 弹窗长什么样

```
┌──────────────────────────────────────────────────────────┐
│  关闭雪乃酱？                                             │
│                                                           │
│  当前正在执行 2 个任务，退出会中止它们。                    │
│  （没有任务在跑时，这行换成：当前没有任务在执行。）          │
│  （有远程通道在跑时追加：通道「主力机」的任务在被控机继续。）  │
│                                                           │
│  ─────────────────────────────────────────────────────    │
│  [ ] 记住我的选择                                          │
│  [ ] 任务执行时隐藏到状态栏，空闲时退出                     │
│      跑任务时只是收进托盘，任务跑完自动退出                 │
│  ─────────────────────────────────────────────────────    │
│                                                           │
│        [ 最小化到状态栏 ]   [ 退出 ]   [ 取消 ]            │
└──────────────────────────────────────────────────────────┘
```

- **主按钮** = 最小化到状态栏（默认焦点；`ContentDialogResult.Primary`）
- **次按钮** = 退出（`Secondary`）
- **关闭按钮 / Esc / 点空白** = 取消（`None`）—— 什么都不做，窗口留着。这是最安全的默认，绝不能把 Esc 映射成退出。
- 顶部文案随状态变（`IsRunning` 与活动会话数实时取），让人在按下之前就知道会损失什么。

### 3.2 两个勾选项的语义（互斥）

| 勾选 | 含义 | 落盘 |
|---|---|---|
| 记住我的选择 | "这次点的按钮，以后都照做，别再问了" | `close_action = tray` 或 `exit`（取决于点的是哪个按钮） |
| 任务执行时隐藏到状态栏，空闲时退出 | "别按我点的按钮，按当前状态决定" | `close_action = auto` |

**为什么必须互斥**：两条都是"记住一条长期策略"，同时生效就互相打架（一条说"永远退出"，一条说"跑任务时别退出"）。

**勾「记住我的选择」但按了「取消」** → 什么都不写，`close_action` 保持原值（下次照样问）。

### 3.3 勾了「智能模式」时，按钮怎么办

勾上「任务执行时隐藏到状态栏，空闲时退出」后，"退出"这个按钮就和当前状态可能矛盾（正在跑的时候点退出，实际只会隐藏）。所以勾上时**按钮跟着状态变**：

| 当前状态 | 主按钮文案 | 次按钮 |
|---|---|---|
| 正在执行任务 | 「隐藏到状态栏」 | 「取消」 |
| 空闲（没任务在跑） | 「退出」 | 「取消」 |

一句话原则：**按钮上写的一定是按下后真会发生的事**，不做"嘴上说退出、实际隐藏"这种事。

### 3.4 勾选互斥的实现细节（别踩的坑）

WinUI 的 `CheckBox` **没有 `Toggled` 事件**（只有 `Checked` / `Unchecked`），而程序化把 `IsChecked` 设为 false 同样会触发 `Unchecked` —— 直接互斥会递归。

实现方式：一个 `_syncingChecks` 布尔守卫 + 两个 `Unchecked`/`Checked` 处理器，守卫为 true 时直接返回。这条在本项目里属于"已知坑"，实现时照做，别试图用绑定表达式硬解。

---

## 4. 策略模型

### 4.1 配置项

`config.json` → `window` 节新增一个键（与既有 `mascot_panel` 同级，都是纯界面偏好）：

```jsonc
"window": {
  "x": 120, "y": 80, "width": 1320, "height": 860,
  "maximized": false,
  "mascot_panel": "expanded",
  "close_action": "ask"        // ask | tray | exit | auto
}
```

| 值 | 关闭时行为 | 弹窗 |
|---|---|---|
| `ask`（默认，老配置缺键也走它） | —— | 每次都问 |
| `tray` | 直接收进托盘 | 不问 |
| `exit` | 直接退出 | 不问 |
| `auto` | 正在跑任务 → 收进托盘；空闲 → 退出 | 不问 |

`WindowConfig.NormalizeCloseAction` 收敛非法值到 `ask`，`Sanitize()` 里调用（照抄 `NormalizeMascotPanel` 的写法）。`Clone()` 是 `MemberwiseClone`，新字段自动带上，无需改。

### 4.2 一次关闭的完整决策

```
点关闭（AppWindow.Closing）
  ├─ _exiting（程序性退出 / 已在退出流程） → 放行，真关
  ├─ _systemShuttingDown（收到 WM_QUERYENDSESSION） → 放行，真关
  ├─ _closeDialogOpen（弹窗已经在） → Cancel，丢弃这次重复请求
  ├─ close_action == tray → Cancel + 隐藏
  ├─ close_action == exit → 放行（真退出）
  ├─ close_action == auto → 正在执行 ? (Cancel + 隐藏) : 放行（真退出）
  └─ close_action == ask → Cancel + 弹窗
        ├─ 取消 → 什么都不做
        ├─ 最小化 → 隐藏；勾了"记住"就落 tray
        ├─ 退出   → RequestExit()
        └─ 智能模式勾着 → 按 §3.3 的实际动作走，落 auto
```

**注意顺序**：`AppWindow.Closing` 是**同步**事件，`args.Cancel` 必须在**进入弹窗之前**就置 `true`（因为处理器不能 `await`）。也就是"先拦下来，再慢慢问"，而不是"等问完了再决定拦不拦"。

### 4.3 `auto` 模式的自动退出规则

- 触发点：`VM.IsRunning` 由 `true` → `false` 的边沿（`MainWindow.OnViewModelPropertyChanged` 里已经订阅了 `PropertyChanged`，加一个分支即可）。
- **前置条件：窗口仍是隐藏状态**。用户中途把窗口叫回来看，就取消本次自动退出 —— 否则会在他正看着界面的时候"啪"地关掉。
- 延迟 3 秒再退（对齐现有 `AutoExitAfterDone` 的 1.5 秒延迟思路，但多给一点，让汇总 Toast 先弹出来）。
- 退出前同样走 `RequestExit()`（见 §6.4），不做特殊路径。

---

## 5. 托盘图标：从零实现的技术方案

WinUI 3 **没有** WinForms 的 `NotifyIcon`，也没有自带托盘 API，只能 P/Invoke `Shell_NotifyIcon`。这块是本方案的主要工作量与主要风险。

### 5.1 组件构成

| 部件 | 做法 |
|---|---|
| 图标句柄 | `LoadImageW(null, "assets/icon.ico", IMAGE_ICON, cx, cy, LR_LOADFROMFILE)`，`cx/cy` 取 `GetSystemMetrics(SM_CXSMICON)`（不写死 16，高 DPI 会糊）；失败回落 `LoadIconW(null, IDI_APPLICATION)` |
| 收消息的窗口 | 自建一个**隐藏的顶层窗口**（`CreateWindowExW` + 自己的窗口类 + `WndProc`），尺寸 0、从不 Show。**不用 `HWND_MESSAGE`**：右键菜单 `TrackPopupMenu` 需要窗口能被 `SetForegroundWindow`，消息专用窗口会失败 |
| 委托保活 | `WndProc` 委托存静态字段，否则被 GC 回收 → 回调野指针（这类崩溃极难查） |
| 注册 | `NIM_ADD` + `NIF_MESSAGE|NIF_ICON|NIF_TIP`，回调消息 `WM_APP + 0x21` |
| 版本 | 先按默认（v3）行为，`lParam` 低 16 位取鼠标消息。`NIM_SETVERSION` 到 4 会改变回调参数语义（低字变消息、高字变图标 id），**首版不启用**，避免为了一个用不上的特性引入坑 |
| 资源管理器重启 | 监听 `RegisterWindowMessageW("TaskbarCreated")`，收到就重新 `NIM_ADD`（explorer 崩一次图标就没了，不做这件事会被当成"程序挂了"） |
| 退出清理 | `NIM_DELETE` → `DestroyWindow` → `UnregisterClass`；时机必须在主窗口 `Closed` 里、进程结束之前 |

### 5.2 交互

| 操作 | 行为 |
|---|---|
| 左键单击 / 双击 | 恢复主窗口（`Show` + `SetForegroundWindow` + 必要时 `SW_RESTORE`） |
| 右键 | 菜单：显示主界面 / 开始执行（执行中显示为"停止执行"）/ 关闭行为设置… / 退出 |
| 悬停 | Tooltip：「雪乃酱 — 正在执行 2 个任务」或「雪乃酱 — 空闲」 |

- 菜单项"开始执行"调用 `VM.Start()`；"停止执行"调用 `VM.Stop()`。两者在隐藏状态下都得能跑（它们本来就不依赖窗口可见性）。
- "退出"走 `RequestExit()`，**不走关闭弹窗**（用户已经在托盘菜单里明确点了退出）。
- 菜单需要文件态判断：`TrackPopupMenu` 每次重建（`TPM_RETURNCMD`），不要缓存 `HMENU`。
- 经典坑：`TrackPopupMenu` 前 `SetForegroundWindow(hwnd)`、之后 `PostMessageW(hwnd, WM_NULL, 0, 0)`，否则点菜单外面菜单不消失。这条要真机验证。

### 5.3 失败必须可见

托盘注册失败（图标文件丢失、`Shell_NotifyIcon` 返回 false、explorer 不在）时：

**绝不能静默隐藏窗口** —— 用户会以为程序关掉了，其实进程还在跑任务，再也找不回来。
规则：`TrayIcon.TryInstall()` 失败 → **不隐藏**，回退为"最小化到任务栏"（`AppWindow.Minimize()`），并 `VM.AppendLog` 写一行原因 + 轻提示。

---

## 6. 隐藏 / 恢复 / 退出

### 6.1 隐藏

```csharp
_wasMaximizedBeforeHide = presenter.State == OverlappedPresenterState.Maximized;
AppWindow.Hide();          // 1.4+ 的 API，不用裸 ShowWindow(SW_HIDE)
_tray.SetVisible(true);
```

- 隐藏前把窗口几何落盘（复用 `PersistWindowPlacement()`），别等 `Closed`。
- 首次隐藏发一次 Toast「已隐藏到状态栏，任务仍在执行，双击托盘图标可以回来」。只发一次（会话内 `_trayHintShown` 标记，不落盘）。

### 6.2 恢复

```csharp
AppWindow.Show();
if (_wasMaximizedBeforeHide) presenter.Maximize();     // 只在原本最大化时恢复
AppWindow.MoveAndResize(WindowPlacement.Resolve(...)); // 复用钳制，防显示器拔了以后跑到屏幕外
SetForegroundWindow(hwnd);
Activate();
```

- 恢复后 `Activated` 事件会触发 → 顺带把 `PendingNotificationStore` 里错过的那条通知补发出去（现有逻辑，白捡）。
- **画面重排**：窗口隐藏期间，多画面页的 1Hz `LayoutSurfaces()`（`Views/RdpMultiViewPage.xaml.cs:88`）不要继续跑——隐藏时 surface 尺寸可能是 0，白跑还可能踩到失效对象（这正是 MEMORY 里记的头号未修问题的同类触发路径）。做法：隐藏时 `_refreshTimer.Stop()`，恢复时 `Start()`。**这条单独一个提交、单独验证**，不和本方案其它改动混在一起，保留归因能力。

### 6.3 退出流程图（唯一出口）

```
RequestExit()
  ├─ _exiting = true
  ├─ SaveConfig()                     // 配置落盘（含 close_action）
  ├─ _tray?.Dispose()                 // NIM_DELETE + DestroyWindow
  ├─ AppWindow.Close()                // 此时 Closing 因 _exiting 直接放行
  └─ Closed 事件：PersistWindowPlacement + DisposeEmbedClient（现有逻辑）
```

- 所有"要退出"的地方都走它：弹窗的【退出】、托盘菜单的【退出】、`auto` 模式到点、`AutoExitAfterDone`。
- `AutoExitAfterDone` 现在直接 `App.MainWindow?.Close()`（:5451）→ 改成 `App.MainWindow?.RequestExit()`。不改的话，开了"任务完成后自动退出"的用户每次都会看到"关闭雪乃酱？"弹窗，属于明显的回归。
- 退出时若本地任务在跑：不弹二次确认（弹窗里已经警告过），按现有 `CleanupForStop` 语义随进程结束清理；远程通道**不**发 `stop.json`（既有行为：主控端退出不影响代理）。

### 6.4 单实例（本方案的强制依赖）

收进托盘后程序"看不见但在跑"，用户很可能再双击一次 exe。现在没有互斥 → 两个实例、两个托盘图标、两套任务。

做法：

1. `Main` 里 `new Mutex(true, @"Local\YukinoChan.MainWindow", out createdNew)`；`Local\` 不用 `Global\`（跨会话没必要，还多一个特权要求）。
2. `!createdNew` → 发 `RegisterWindowMessageW("YukinoChan.Activate")` 给已有窗口，然后 `Environment.Exit(0)`。
3. 找已有窗口：`EnumWindows` + `GetWindowThreadProcessId` 匹配同名 exe 的进程（`NativeMethods` 里两个 API 都有现成的），或者按 WinUI 的窗口类名找。
4. 已有实例收到该消息 → `DispatcherQueue.TryEnqueue` 回 UI 线程 → 恢复窗口 + 激活。

**注意**：主控端是 `requireAdministrator`，第二次双击仍会先弹 UAC —— 这是既有体验，本方案不改。

---

## 7. UI 其它入口（不留"死路"）

| 入口 | 作用 |
|---|---|
| 设置页 → 外观 | 新增下拉「关闭按钮」：每次询问 / 最小化到状态栏 / 直接退出 / 任务执行时隐藏、空闲时退出（照抄 `ThemeBox` 的 `ItemsSource` + `SelectionChanged` 写法） |
| 托盘右键 → 关闭行为设置… | 恢复窗口并导航到设置页（否则勾了"记住"的用户永远找不到怎么改回来） |
| 总览页 | 不加新卡片（避免打扰）；仅当 `close_action != ask` 时在设置页里提示"当前不再是每次询问" |

没有这两个入口，勾了"记住我的选择"就等于把选项永久锁死 —— 只能手改 `config.json`，属于设计缺陷，必须堵上。

---

## 8. 冲突与边界（逐条点名）

| # | 场景 | 现状 | 处理 |
|---|---|---|---|
| C1 | 任务完成自动退出 | `App.MainWindow?.Close()` | 改走 `RequestExit()`，否则被自己的弹窗拦住 |
| C2 | 关机倒计时对话框 | 用 `Content.XamlRoot`，窗口隐藏时不可见 | `OnShutdownPromptRequested` 先恢复窗口再弹；否则倒计时看不见、到点直接关机 |
| C3 | 系统关机 / 注销 | —— | 隐藏窗口的 `WndProc` 收 `WM_QUERYENDSESSION` → 置 `_systemShuttingDown=true`；`Closing` 里放行。**绝不 `Cancel` 系统关机**（Windows 会判定"此应用阻止关机"） |
| C4 | 全屏画面窗口关闭 | `RdpFullScreenCoordinator.cs:148` 自己 `Close()` | 那是另一个窗口，不挂本处理器，不受影响（实现时确认只挂主窗口的 `AppWindow.Closing`） |
| C5 | explorer 重启 | 无处理 | 监听 `TaskbarCreated` 重建图标 |
| C6 | 显示器拔掉后恢复 | 窗口可能落在不可见区域 | 复用 `WindowPlacement.Resolve` 钳制 |
| C7 | 托盘注册失败 | —— | 不隐藏，回退最小化 + 日志（§5.3） |
| C8 | 弹窗期间再点关闭 | 会有第二个 `Closing` | `_closeDialogOpen` 守卫，直接 `Cancel` |
| C9 | 隐藏期间任务报错要弹通知 | Toast 正常 | 不自动恢复窗口（打扰），除非用户在设置里另开（本轮不做） |
| C10 | RDP 会话锁定（多用户切换） | 锁定会话里托盘不可见 | 属正常现象，锁回来就恢复；不额外处理，写进已知限制 |
| C11 | 崩溃 | `App.OnUnhandledException` 已兜 | 无需改；`_tray` 用 `try/finally` 清理，避免残留僵尸图标 |

---

## 9. 里程碑

| 里程碑 | 内容 | 验收 |
|---|---|---|
| **M1 托盘底座**（1.5–2 天） | `Services/TrayIcon.cs`（隐藏窗口 + WndProc + Shell_NotifyIcon + 菜单 + TaskbarCreated + DPI）；只做"图标出现 / 双击恢复 / 右键菜单可点可消失 / 退出时干净消失" | 真机点 20 次不残留、不崩；explorer 重启后图标自动回来 |
| **M2 关闭拦截 + 弹窗**（1–1.5 天） | `AppWindow.Closing`、`Views/CloseConfirmDialog`（代码 new，走 `DialogHelper.ShowAsync`）、互斥勾选、按钮文案随状态变 | 四种 `close_action` 组合全对；Esc = 取消 |
| **M3 隐藏/恢复 + 退出统一**（1 天） | `RequestExit()`、`AutoExitAfterDone` 改道、关机倒计时先恢复窗口、`WM_QUERYENDSESSION` 放行 | C1/C2/C3 全过；隐藏-恢复 10 次画面正常 |
| **M4 单实例 + 设置页**（1 天） | Mutex + 激活已有窗口；设置页下拉；托盘菜单跳设置页 | 双击 exe 只激活不新起；改回来能立刻生效 |
| **M5 真机验收**（0.5 天） | §10 全表 + 30 分钟挂机 | 全过 |

M1 单独先做：托盘底座是本方案唯一"看不见但最容易崩"的部分（P/Invoke + WndProc + GC 保活），先把它做成可独立验证的一块。

---

## 10. 验收清单（真机，逐条做）

| # | 场景 | 预期 |
|---|---|---|
| A1 | 默认配置点关闭 | 弹窗；Esc 关闭弹窗 → 窗口还在、进程还在 |
| A2 | 点【退出】 | 进程结束，无僵尸托盘图标 |
| A3 | 点【最小化到状态栏】 | 窗口消失，托盘有图标；双击托盘恢复，窗口位置/最大化状态与隐藏前一致 |
| A4 | 勾「记住我的选择」+【最小化】后重启 | 点关闭直接收托盘，不再问 |
| A5 | 勾「记住我的选择」+【退出】后重启 | 点关闭直接退出，不再问 |
| A6 | 勾「智能模式」 | 执行中点关闭 → 只进托盘；跑完 3 秒内自动退出 |
| A7 | 智能模式 + 跑到一半把窗口叫回来 | 跑完**不**自动退出（人已经在看了） |
| A8 | 智能模式 + 空闲点关闭 | 直接退出（按钮文案此时就是"退出"） |
| A9 | 勾选互斥 | 勾 A 后 B 自动取消，反之亦然；快速连点 20 次不递归、不崩 |
| A10 | 两个勾都不勾 | 本次选择生效，`config.json` 的 `close_action` 不变 |
| A11 | 设置页改回「每次询问」 | 立刻恢复弹窗 |
| A12 | 托盘右键菜单 | 菜单正常弹出、点空白处能消失；"开始执行/停止执行"文案随状态变 |
| A13 | 双击 exe（已收托盘） | 只把已有窗口叫出来，不起第二个进程、不多一个图标 |
| A14 | explorer 重启（任务管理器结束 explorer） | 图标自动回来 |
| A15 | 任务完成自动退出 | 直接退出，**不弹**关闭确认 |
| A16 | 关机倒计时（窗口隐在托盘） | 先自动恢复窗口再弹倒计时，能看见、能取消 |
| A17 | 系统注销 / 关机 | 不拦、不弹窗、不报"阻止关机" |
| A18 | 图标文件缺失（临时改名 assets/icon.ico） | 回落系统默认图标；若托盘完全注册失败 → 不隐藏、最小化到任务栏 + 日志 |
| A19 | 隐藏期间任务报错 | Toast 正常弹出；窗口不自动跳出来 |
| A20 | 隐藏-恢复循环 10 次 + 多画面页 | 画面每次都正常重排，不灰、不闪、不崩；连续挂机 30 分钟无异常 |

**硬要求**：A20 必须实跑 ≥30 分钟。本项目的头号未修问题就是"多画面页 1Hz 重排摸到失效对象"，而"隐藏→恢复"正好会制造一次布局环境变化 —— 不看代码宣布通过，要有日志与观察记录。

---

## 11. 冒烟测试（`_smoke`，不依赖 GUI 的部分）

| 组 | 内容 |
|---|---|
| 1 | `NormalizeCloseAction`：四个合法值、大小写、未知值回落、空值 |
| 2 | 配置往返：`window.close_action` snake_case 落盘、老配置缺键走 `ask`、`Clone()` 带上 |
| 3 | 决策纯函数：`DecideClose(action, isRunning)` → 隐藏/退出/询问 × 四个策略值（把 §4.2 的分支抽成纯函数再测，别把判断埋进事件处理器） |
| 4 | 自动退出前置条件：隐藏 + `IsRunning` 由 true→false → 该退；中途恢复过 → 不退 |
| 5 | 单实例：同一 Mutex 名第二次创建返回 `createdNew=false`（可用子进程验证） |

GUI 交互（弹窗、菜单、托盘）沙箱点不了，按项目惯例：编译 0 错误 0 警告 + 纯逻辑冒烟全绿，交互留给 M5 真机。

---

## 12. 风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | `Shell_NotifyIcon` / 隐藏窗口写错（结构体 `cbSize`、委托被 GC、消息循环线程不对） | 图标不出现 / 偶发崩溃 | 结构体逐字段核对；WndProc 静态保活；在 UI 线程创建窗口；M1 单独验证 |
| R2 | 静默隐藏导致"程序找不回来" | 用户以为崩了，实际任务在跑 | 托盘注册失败不隐藏；首次隐藏发 Toast |
| R3 | 拦截关闭把系统关机也拦了 | 关机被阻止 / 数据丢失 | `WM_QUERYENDSESSION` 放行 + 禁 `Cancel` |
| R4 | 自动退出被自己的弹窗挡住（回归） | "自动退出"失效 | 所有程序性退出统一走 `RequestExit()`，`_exiting` 短路 |
| R5 | 双实例 | 两个托盘图标、两套任务 | Mutex + 激活已有窗口（M4 必做，不能延后） |
| R6 | 隐藏-恢复后画面异常 | 灰屏 / 白块 / 崩溃 | 隐藏时停 1Hz 重排、恢复时重启并钳制几何；A20 长跑验证 |
| R7 | 记忆功能把选项锁死 | 用户改不回来 | 设置页下拉 + 托盘菜单入口（§7） |
| R8 | `TrackPopupMenu` 菜单不消失 | 交互卡死感 | `SetForegroundWindow` + 事后 `PostMessage(WM_NULL)` |
| R9 | 托盘图标与任务栏图标不一致（ico 缺尺寸） | 高 DPI 下糊 | 按 `SM_CXSMICON` 取尺寸加载；`assets/icon.ico` 需含 16/20/24/32 多尺寸（核对现有 ico） |

---

## 13. 已知限制（写进 USER_GUIDE）

1. 收进托盘**不等于**任务在后台隔离运行：它还是同一个进程，只是窗口不可见。
2. 退出 = 主控端进程结束。本地组任务会被清理；**远程通道的任务在被控机继续跑**（既有行为）。
3. 多用户 RDP 切换导致当前会话被锁定期间，托盘图标不可见（锁回来就有）。
4. 弹窗里的「记住我的选择」不会记住"取消"。
5. 智能模式下，只有"因为是关闭而隐藏"且"没再被叫回来"才会在跑完后自动退出。
6. 托盘菜单的"开始执行"在窗口隐藏时也能用，但它**会先把窗口叫回来** ——
   实现时改了主意：隐藏状态下点开始，`MainViewModel.Start()` 可能弹"没有启用的任务"等提示框，
   而隐藏窗口里的对话框用户看不见，表现为"点了没反应"。叫回窗口最省事也最不容易误会。

---

## 14. 待拍板

| # | 问题 | 我的建议 |
|---|---|---|
| Q1 | 弹窗要不要第三颗「取消」按钮？ | **要**。Esc / 右上角叉必然存在，必须给它一个安全含义（什么都不做），否则只能映射成"退出"这种危险动作 |
| Q2 | 智能模式勾上后，是"按状态自动决定"还是"仍按点的按钮、只是记住"？ | **按状态自动决定**，且按钮文案跟着改（§3.3）。否则"点退出却只是隐藏"会很不讲道理 |
| Q3 | 勾了「记住我的选择」但点「取消」，记不记？ | **不记**。取消就是取消 |
| Q4 | 智能模式跑完自动退出前，要不要弹 3 秒倒计时气泡/通知？ | **发一条 Toast**（"任务已结束，程序即将退出"），不弹窗打断 |
| Q5 | 主控端单实例（M4）能不能砍掉延后？ | **不能**。托盘 + 无互斥 = 用户双击就多一份，属于功能自带的必修项 |

---

## 附录 A：新增 / 改动文件清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `Services/TrayIcon.cs` | 新增 | `Shell_NotifyIcon` 封装 + 隐藏顶层窗口 + `WndProc` + 右键菜单 + `TaskbarCreated` 重建 + 图标加载/释放 |
| `Services/TrayNative.cs` | 新增 | `NOTIFYICONDATAW`、`CreateWindowExW`、`TrackPopupMenu`、`LoadImageW` 等 P/Invoke（与业务分开，便于核对结构体） |
| `Services/SingleInstance.cs` | 新增 | `Mutex` + `WM_*` 激活消息 + `EnumWindows` 找已有窗口 |
| `Views/CloseConfirmDialog.cs` | 新增 | 代码构建 `ContentDialog`（走 `DialogHelper.ShowAsync`），含互斥勾选与动态按钮文案 |
| `Models/CloseDecision.cs` | 新增 | `DecideClose(action, isRunning)` 等纯函数 + `NormalizeCloseAction`（供冒烟） |
| `Models/WindowConfig.cs` | 改动 | 新增 `close_action` + `NormalizeCloseAction` + `Sanitize` |
| `Views/MainWindow.xaml.cs` | 改动 | `AppWindow.Closing` 拦截、隐藏/恢复、`RequestExit()`、`IsRunning` 边沿、关机倒计时先恢复、`Closed` 清理托盘、隐藏时停 1Hz 重排 |
| `ViewModels/MainViewModel.cs` | 改动 | `AutoExitAfterDone` 改走 `RequestExit()`（:5451）；供弹窗取"正在跑几块"的只读状态 |
| `Views/SettingsPage.xaml(.cs)` | 改动 | 「关闭按钮」下拉 + 落盘 |
| `Services/WindowPlacement.cs` | 复用 | 恢复时钳制窗口位置（不改） |
| `Program.cs` | 改动 | 单实例 `Mutex` + 激活已有实例 |
| `winui3/README.md` / `CHANGELOG.md` / `USER_GUIDE.md` / `docs/known-limits-and-roadmap.md` | 改动 | 说明关闭行为、托盘、已知限制 |
| `_smoke/CloseBehaviorCheck.cs` | 新增（工作区） | §11 的纯逻辑用例 |

## 附录 B：术语

- **状态栏 / 托盘**：任务栏右侧通知区域（与"状态栏"在日常说法里混用，界面上统一写「状态栏」，与弹窗文案一致）。
- **智能模式**：`close_action=auto`，任务执行时隐藏、空闲时退出。
- **程序性退出**：由程序自己发起的退出（自动退出、托盘菜单退出），与用户点关闭按钮区分，不弹确认。

## 附录 C：实施记录（2026-10-20）

### 已落地

| 文件 | 状态 | 说明 |
|---|---|---|
| `Services/TrayNative.cs` | 新增 | `NOTIFYICONDATAW` / 窗口类 / 菜单 / 图标加载的 P/Invoke，与业务分开便于核对 |
| `Services/TrayIcon.cs` | 新增 | 隐藏顶层窗口 + WndProc + `Shell_NotifyIcon` + 右键菜单 + `TaskbarCreated` 重建 + 图标释放 |
| `Services/SingleInstance.cs` | 新增 | 命名互斥体判唯一；**实现改用命名事件**而不是计划里的 `RegisterWindowMessage` —— 不子类化 WinUI 窗口，风险低得多 |
| `Models/CloseDecision.cs` | 新增 | `Decide` / `Label` / `Items` / `PersistFor`，纯函数 |
| `Models/WindowConfig.cs` | 改动 | `close_action` + `NormalizeCloseAction` + `Sanitize` |
| `Views/CloseConfirmDialog.cs` | 新增 | 代码构建的 `ContentDialog`；两个互斥勾选（`_syncing` 守卫）；智能模式下主按钮文案随状态变 |
| `Views/MainWindow.xaml.cs` | 改动 | `AppWindow.Closing` 拦截、`HideToTray` / `RestoreFromTray`、`RequestExit`、`ScheduleAutoExit`、关机倒计时先恢复、`Closed` 摘托盘 |
| `Views/RdpMultiViewPage.xaml.cs` | 改动 | 新增 `SuspendRefresh` / `ResumeRefresh`（隐藏期间停 1Hz 重排） |
| `ViewModels/MainViewModel.cs` | 改动 | `SaveCloseAction` / `CloseActionItems` / `DescribeRunningForClose`；自动退出改走 `RequestExit` |
| `Views/SettingsPage.xaml(.cs)` | 改动 | 「关闭按钮」四档下拉 |
| `Program.cs` | 改动 | 单实例闸门（第二实例写 `MarkHandoff`，避免覆盖在跑那一份的退出标记） |
| `App.xaml.cs` | 改动 | 起单实例监听线程，收到信号就 `RestoreFromTray` |
| `_smoke/CloseBehaviorCheck.cs` | 新增 | 30 条纯逻辑断言 |

### 验证

- `dotnet build` Debug / Release：**0 错误**（4 条警告均在 `RdpEmbeddedClient` / `RdpD3DRenderer`，是本次未触碰的既有警告）。
- `_smoke`：新增 30 条全部 PASS；其余 9 条失败经核对**在 HEAD 上同样失败**（`session.Tick(status`、`EventList.Visibility` 等字符串在 HEAD 里就不存在），属既有陈旧断言，与本次改动无关。

### 未做（需真机）

§10 的 A1–A20 全部待真机执行，其中三条最要紧：

1. **A20 隐藏/恢复 10 次 + 挂机 30 分钟**：确认多画面页在"停/启 1Hz 重排"之后不灰、不崩。
2. **A12 托盘右键菜单**：确认菜单能弹出、点外面能消失（`TrackPopupMenu` 对隐藏窗口的前台化限制）。
3. **A16 关机倒计时**：窗口隐在托盘时先恢复再弹倒计时。
