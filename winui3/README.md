# YukinoChan WinUI 3 版（雪乃酱 · 二游脚本统合管理器）

> 本目录是雪乃酱的 **C# / WinUI 3 重写版**，与仓库根目录的 Python + PySide6 版本（`main.py` / `core/` / `ui/`）功能等价。
> Python 版进入 **legacy 维护状态**，不再新增功能；后续开发以本目录为准。

开发基线：**v2.0 WinUI 3**（原 Python 版基线 v31.17.1）

> **v2.0 相对 Python 版新增的能力**：
> 远程会话从「单通道 + 系统 mstsc 窗口」升级为 **多条「会话通道」并行执行** ——
> 任务可分配给不同目标账户 / 不同机器，通道之间并行、通道内部串行；
> 远程画面用原生 FreeRDP **内嵌**在雪乃酱窗口里（可同屏并列多路），鼠标键盘直接操作。
> 详见 [§七、会话通道](#七会话通道多通道并行执行) 与 [docs/rdp-multi-channel-plan.md](docs/rdp-multi-channel-plan.md)。

---

## 一、为什么要重写

原 Python 版约 5600 行，界面层基于 PySide6（Qt），存在几个绕不开的问题：

| 问题 | 说明 |
|---|---|
| 分发体积 | PyInstaller 打包后体积大，冷启动慢 |
| 主题割裂 | Qt 自定义皮肤与 Windows 11 系统主题、Mica、亚克力、系统强调色无法统一 |
| DPI 表现 | 高 DPI / 多显示器缩放下偶发错位 |
| 依赖链 | Python + PySide6 + psutil 运行时依赖，用户环境差异大 |

改成 C# + Windows App SDK（WinUI 3）之后：

- 原生 Fluent Design，直接吃到 **Mica 背景、系统主题、系统强调色、圆角、阴影**
- 自定义标题栏（`ExtendsContentIntoTitleBar`），无 Qt 那种边框割裂感
- `PerMonitorV2` 高 DPI，多显示器拖拽不糊不错位
- 单一 exe 直接运行（unpackaged 模式），无需 Python 运行时
- 进程/窗口监控直接用 Win32 P/Invoke，少一层 Python ↔ 系统调用开销

---

## 二、环境要求

| 项目 | 版本 |
|---|---|
| 操作系统 | Windows 10 1809（build 17763）及以上，推荐 Windows 11 |
| .NET SDK | 8.0（本机验证 8.0.413） |
| Windows App SDK | 1.8.260921001（NuGet 自动还原） |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 |
| 目标框架 | `net8.0-windows10.0.19041.0` |
| 架构 | x64（同时声明 x86 / ARM64，需自行改平台编译） |

> 需要联网还原 NuGet 包。如果在受限网络下，设置代理后再还原：
> ```bash
> export http_proxy=http://127.0.0.1:7897 https_proxy=http://127.0.0.1:7897
> ```

---

## 三、编译与运行

```bash
cd winui3
dotnet restore
dotnet build -p:Platform=x64            # Debug
dotnet build -c Release -p:Platform=x64 # Release
```

产物：

```text
winui3/src/YukinoChan.App/bin/x64/Debug/net8.0-windows10.0.19041.0/YukinoChan.exe
winui3/src/YukinoChan.App/bin/x64/Release/net8.0-windows10.0.19041.0/YukinoChan.exe
```

工程采用 **非打包（unpackaged）模式**（`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`），
因此 `YukinoChan.exe` **可以直接双击运行**，不需要 MSIX 安装、不需要侧载。

也可以用 Visual Studio 2022 打开 `winui3/YukinoChan.sln` 直接 F5。

---

## 四、目录结构

```text
winui3/
├─ YukinoChan.sln                   # 只包含 YukinoChan.App
├─ docs/                            # 设计文档（内嵌 RDP / 多会话通道计划书）
├─ src/YukinoChan.RdpNative/        # 原生 FreeRDP 封装（ycn_rdp.c / .h + CMakeLists.txt）
│  └─ ycn_rdp.c                     #   多会话上限 YCN_MAX_SESSIONS = 8
└─ src/YukinoChan.App/
   ├─ YukinoChan.App.csproj
   ├─ app.manifest                 # asInvoker + PerMonitorV2 + UTF-8
   ├─ App.xaml / App.xaml.cs       # 应用入口、全局资源、异常兜底
   ├─ Themes/Styles.xaml           # 雪乃酱雾蓝主题（YukinoAccent 等 Brush / 卡片 / 气泡样式）
   ├─ Models/
   │  ├─ Enums.cs                  # WaitModes / TimeoutActions / ConcurrentPolicies / MascotStates
   │  ├─ RdpModels.cs              # RdpConfig / RdpResolutions / RdpCommand / RdpStatus / RdpTaskEvent / RdpHeartbeat
   │  ├─ RdpChannel.cs             # 会话通道模型 + ChannelChoice（任务页下拉项）
   │  ├─ RdpChannelPaths.cs        # 桥目录规则（按账户名分目录，纯函数）
   │  ├─ RdpChannelPlanner.cs      # 分组 + 启动计划 + 并发上限（纯函数）
   │  ├─ RunSummary.cs             # 整轮汇总 / 看板娘「取最差」（纯函数）
   │  ├─ ChannelOverviewItem.cs    # 主页通道概览快照行
   │  ├─ RdpClientStateMachine.cs  # 内嵌客户端连接状态机
   │  ├─ RdpInputMapper.cs         # 鼠标 / 键盘 → RDP 输入事件
   │  ├─ RdpErrorText.cs           # RDP 错误码 → 中文说明
   │  ├─ TaskConfig.cs             # 单条任务配置（含 channel_id，键名与 config.json 完全一致）
   │  ├─ AppConfig.cs              # 全局配置 + 旧单通道配置迁移
   │  ├─ WindowConfig.cs           # 主窗口位置/尺寸（记忆上次窗口大小）
   │  └─ MonitorSpec.cs            # 监控目标描述
   ├─ Services/
   │  ├─ AppPaths.cs               # 配置/日志/统计/素材路径统一入口
   │  ├─ WindowPlacement.cs        # 窗口几何收敛（钳到当前显示器工作区，纯函数可单测）
   │  ├─ RdpBridge.cs              # 指令桥（按通道实例化，ProgramData 下 command / status / stop.json）
   │  ├─ RdpChannelSession.cs      # 【每通道一份】运行时状态：指令归属 / 进度 / 心跳 / 收尾幂等
   │  ├─ RdpSessionService.cs      # 本机环回 RDP：预检、凭据、连接、会话查询、断开/注销、代理部署
   │  ├─ RdpAgentRunner.cs         # 目标会话里的常驻执行代理（--rdp-agent 模式）
   │  ├─ RdpEmbeddedClient.cs      # 内嵌会话客户端（每实例一条会话）
   │  ├─ RdpD3DRenderer.cs         # D3D11 交换链渲染
   │  ├─ RdpNativeInterop.cs       # ycn_rdp.dll 的 P/Invoke 封装
   │  ├─ RdpCredentialStore.cs     # 凭据管理器（键 = 主机|账户）
   │  ├─ LowLevelKeyboardHook.cs   # 全屏时 WH_KEYBOARD_LL 键盘接管
   │  ├─ PendingNotificationStore.cs # 锁屏期间落暂存，回前台补发
   │  ├─ NotificationService.cs    # Windows 系统通知（Toast）
   │  ├─ ConfigManager.cs          # config.json 读写 + 旧键迁移 + 损坏备份
   │  ├─ FileLogger.cs             # 按启动批次分文件日志 + SESSION 标记
   │  ├─ RuntimeStatsManager.cs    # 耗时统计（json / csv / summary）
   │  ├─ NativeMethods.cs          # Win32 P/Invoke 声明
   │  ├─ ProcessHelper.cs          # tasklist / taskkill / 窗口枚举 / 关键词匹配
   │  ├─ MonitoredProcess.cs       # 进程包装 + UAC 提权启动（runas）
   │  ├─ CommandLine.cs            # Windows 风格命令行拆分（CommandLineToArgvW）
   │  ├─ ScriptRunner.cs           # 任务执行引擎（核心）
   │  ├─ MascotService.cs          # 看板娘状态机 / 素材选取 / 气泡文案
   │  ├─ SystemServices.cs         # 开机自启动（HKCU Run）、自动关机
   │  └─ ScreenshotService.cs      # 超时现场全屏截图
   ├─ Helpers/
   │  ├─ ObservableObject.cs       # INotifyPropertyChanged 基类
   │  ├─ RelayCommand.cs           # ICommand（含 AsyncRelayCommand）
   │  ├─ KeywordHelper.cs          # 关键词归一化（兼容 ; , ， 分隔）
   │  ├─ FormatHelper.cs           # 时间格式化
   │  ├─ WindowHelper.cs           # 取主窗口 HWND（WinUI 3 未直接暴露）
   │  └─ DialogHelper.cs           # 文件选择器 / 消息对话框
   ├─ ViewModels/
   │  └─ MainViewModel.cs          # 单一主 ViewModel：任务 / 通道 / 并行调度 / 面板聚合
   └─ Views/
      ├─ MainWindow.xaml(.cs)      # NavigationView + 自定义标题栏 + 动态「执行中的通道」分组
      ├─ ChannelsPage.xaml(.cs)    # 会话通道：总开关 / 通道列表与增删复制 / 编辑卡 / 每通道预检
      ├─ RdpChannelPage.xaml(.cs)  # 单条通道的画面页（单画面 / 多画面 + 该通道进度与事件）
      ├─ RdpSurfaceCell.xaml(.cs)  # 多画面里的一格（画面 + 通道名/状态 + 全屏·弹出·断开）
      ├─ RdpView.xaml(.cs)         # 内嵌画面控件（D3D 交换链 + 输入 + 帧率统计）
      ├─ RdpFullScreenCoordinator.cs / RdpFullScreenWindow.cs   # 全屏编排（两页共用）
      ├─ RdpChannelWindow.cs       # 通道「弹出独立窗口」（复用同一 client，不带钩子）
      ├─ MascotPanel.xaml(.cs)     # 右侧看板娘 + 状态网格 + 发言气泡
      ├─ HomePage.xaml(.cs)        # 总览（含会话通道概览卡）
      ├─ TasksPage.xaml(.cs)       # 任务管理（列表 + 分组表单 + 执行通道下拉）
      ├─ StatsPage.xaml(.cs)       # 耗时统计
      ├─ LogsPage.xaml(.cs)        # 运行日志
      ├─ SettingsPage.xaml(.cs)    # 设置
      └─ ShutdownCountdownDialog.xaml(.cs)  # 关机倒计时
```

代码量约 **19000 行**（76 个 `.cs` / `.xaml`，不含 obj 下的自动生成文件），全部在 `YukinoChan.App`。
> 早期那个承载 COM ActiveX 的 WinForms 类库 `YukinoChan.RdpHost` 已随「改用 FreeRDP 内嵌」一并删除，不再出现在目录里。

---

## 五、移植对照表

### 核心逻辑

| Python（原版） | C# / WinUI 3（新版） | 说明 |
|---|---|---|
| `core/core.py` | `Services/ScriptRunner.cs` | 任务执行引擎全量移植 |
| `core/runtime/runtime_watchdog.py` | `Services/ScriptRunner.cs` + `ProcessHelper.cs` | 监控循环内联合并 |
| `ui/main_window.py` | `Views/MainWindow.xaml(.cs)` + `ViewModels/MainViewModel.cs` | 拆分为视图 + 视图模型 |
| `ui/cards.py` | `Views/HomePage.xaml` | 卡片式首页 |
| `ui/card_scene.py` | `Views/MascotPanel.xaml` + `Services/MascotService.cs` | 看板娘面板 |
| `config.json` | `Models/AppConfig.cs` / `TaskConfig.cs` | 键名完全一致，双向兼容 |
| `app_dir()` 路径回溯 | `Services/AppPaths.cs` | 同样向上回溯找 `config.json` / `assets` |
| `shlex.split(posix=False)` | `CommandLine.Split` | 用 `CommandLineToArgvW` 实现等价语义 |
| `subprocess` + `runas` | `Services/MonitoredProcess.cs` | `ShellExecuteExW(runas)` 提权 |

### 界面

| 原 Qt 组件 | WinUI 3 对应 |
|---|---|
| `QMainWindow` | `Window` + `NavigationView` + `Frame` |
| `QListWidget` | `ListView` |
| `QStackedWidget` | `Frame.Navigate` |
| `QGroupBox` | `Expander` |
| `QSpinBox` / `QDoubleSpinBox` | `NumberBox` |
| `QCheckBox` | `ToggleSwitch` / `CheckBox` |
| `QMessageBox` | `ContentDialog` |
| `QSystemTrayIcon` 提示 | `InfoBar` |
| 自定义标题栏 | `ExtendsContentIntoTitleBar` + `SetTitleBar` |
| — | **新增**：`MicaBackdrop` 背景 |

### 主题

原 6 套 Qt 皮肤（`yukino` / `campus` / `fresh` / `fantasy` …）收敛为 WinUI 3 原生三档：

| 值 | 含义 |
|---|---|
| `system` | 跟随系统（默认） |
| `light` | 浅色 |
| `dark` | 深色 |

旧配置里的 `yukino` / `campus` / `fresh` / `fantasy` 仍会被识别为合法值并平滑降级为 **跟随系统**，不会报错、不会被强制改写。

---

## 六、已完整移植的执行语义

以下行为与原 Python 版 **逐条对齐**，不是"看起来差不多"：

**监控关键词优先级**

```text
window_keywords  >  process_keywords  >  legacy wait_mode  >  direct_process
```

**三层清理顺序**（超时 / 停止 / 紧急停止共用）

```text
启动脚本进程  →  目标进程关键词  →  游戏/扩展进程
```

**并发组**

- `wait_all`：等组内全部任务完成
- `wait_first`：组内任一完成即推进

**超时动作**

- `kill_and_continue`：杀进程后继续下一项
- `skip_and_continue`：不杀进程，直接下一项
- `stop_all`：停止整条任务链

**监控循环**

| 参数 | 值 |
|---|---|
| 监控目标连续消失宽限 | 15 秒 |
| 监控目标出现截止 | 180 秒 |
| 启动成功确认窗口 | 2 秒 |
| 过早退出阈值 | 超时时间 / 6 |

**看板娘状态机**

- 状态：`idle` / `work` / `rest` / `error`
- `error` 锁定：`15 秒 × 错误次数` 叠加后自动释放
- 气泡优先级：`error 100` > `work 70` > `rest 50` > `guide 30` > `idle 20`
- 素材目录：`assets/mascot/{state}.png` 或 `{state}_*.png`

**日志**

- 单文件上限按启动批次自动分配 `2026-05-26.log` / `(2)` / `(3)` …
- 会话标记：`===== SESSION START : 任务名 =====` / `===== SESSION END : 任务名 =====`

**其他**

- 超时现场截图：`logs/screenshots/{session}_{HHmmss}_{task}_timeout_{before,after}_kill.png`
- 异常报告：`logs/last_abnormal_report.json`，下次启动在首页 InfoBar 主动汇报
- 开机自启动：HKCU `Software\Microsoft\Windows\CurrentVersion\Run`，值名「雪乃酱 / 二游脚本助手」
- 自动关机：`shutdown /s /t {delay}`，倒计时对话框可取消（`shutdown /a`）
- 快捷键：`F8` 停止执行，`Ctrl+Alt+F8` 紧急停止

---

## 七、会话通道（多通道并行执行）

### 能做什么

一条**会话通道** = 一台目标主机 + 一个目标账户 + 它自己的凭据与指令桥目录。

把任务分配给不同通道，点「开始执行」后：

- **通道之间并行**：每条通道各自下发指令、各自连自己的目标账户，互不等待；
- **通道内部串行**：同一条通道里的任务仍按「任务执行」页的顺序一条条跑；
- **没分配通道的任务**（下拉里选「本地执行（当前会话）」）跑在雪乃酱自己所在的会话里，
  和通道任务同时进行。

远程画面直接**内嵌**在雪乃酱窗口里（FreeRDP + D3D 交换链），鼠标键盘可以直接操作；
也可以改成用系统自带的 `mstsc` 开独立窗口。执行期间左侧菜单会在「会话通道」下面
动态列出本轮每条通道，点进去就是那条通道自己的画面、进度、心跳与事件。

**「连接」与「开始执行」是分开的两件事**：

1. 想先看画面：在通道页点**「连接本通道」** → 立刻建立内嵌连接，先手动把游戏 / 脚本准备好；
2. 点**「开始执行」** → 按任务分配下发到各通道（还没连的会先自动连接）。

断开画面（画面格子上的「断开」）只释放内嵌连接，**目标会话和里面的代理都保留**；
要销毁会话得用「注销目标账户」或把该通道的收尾方式设成「注销」。

### 目标主机可以填什么

| 用法 | 目标主机填什么 | 典型场景 |
|---|---|---|
| 切本机另一个账户（默认） | `127.0.0.1` | 游戏或脚本装在另一个账户下、需要另一套用户环境、想把挂机环境和日常环境隔开 |
| 连另一台机器 | 局域网 IP / 主机名 / 公网地址，可带端口（如 `192.168.1.20:3390`） | 主机和挂机机分开、多台机器统一由一台下发任务 |

**一台机器上放多条通道**：每条通道请配**不同账户** —— 同一个账户开两条通道没有意义（它们会抢同一个会话）。
另外，本机能不能同时跑多条通道，取决于**系统是否允许多个交互式会话并存**：系统允许多会话时可以真正并行；
普通 Windows 客户端版同一时刻只允许一个交互式会话，第二条本机通道会把前一个桌面顶掉 ——
这种情况下建议把通道分到不同机器。通道管理页对「同一主机有多条通道」只做**提示**，不禁止。

### 工作原理

Windows 会话之间不能直接通信，主控端也无法跨会话启动进程，所以用「共享文件 + 登录自启代理」来搭桥。
**每条通道一套桥目录**，互不干扰：

```text
主控端（雪乃酱，账户 A）
  ├─ 1. 任务分组：channel_id 空 → 本地执行；= ch-xxx → 那条通道
  ├─ 2. 对每条通道各写一份 command.json，各自轮询自己的 status.json
  ├─ 3. 停止执行 / 紧急停止 → 给每条通道各写一份 stop.json
  └─ 4. 画面：每条通道各持一个内嵌客户端（一个客户端同时只能挂一块画面）

桥目录（每通道一份，按账户名隔离）
  ├─ 本机默认：%ProgramData%\YukinoChan\rdp\<账户名>\
  └─ 远程目标：你填的共享路径（两台机器都能读写）

目标会话（账户 B / 远程机器）
  ├─ 1. 账户登录后，公共启动目录里的快捷方式自动拉起 YukinoChan.exe --rdp-agent
  ├─ 2. 代理**常驻**：循环「等指令 → 执行 → 回等待」，等待期每秒写一次 idle 心跳
  ├─ 3. 读到 command.json 立刻删掉（防重复执行），用同一套 ScriptRunner 跑任务队列
  ├─ 4. 每完成一个任务：写回 status.json 的事件 + 在本会话弹通知
  └─ 5. 收到 stop.json → 停止本轮；全部结束后按该通道的收尾方式处理会话
```

**完成信号走事件通道，不看相位**：代理写下的 `done` 相位毫秒内就会被下一拍空闲心跳覆盖，
主控端 1Hz 轮询根本撞不上（历史上有过"任务跑完了但界面没反应"）。
所以完成统一认 **status.json 里的 `Finished` 事件** —— 空闲心跳不会碰事件列表。

**为什么远程主机要多填一个「指令桥目录」**：`%ProgramData%` 是本机路径，远程机器上的代理根本读不到。
所以目标是别的机器时，必须指定一个两边都能访问的位置（网上邻居共享、NAS、同步盘都行），
主控端写指令、代理读指令都走那里。部署代理时这个路径会自动写进快捷方式参数。

目标是本机另一个账户时这项**留空即可** —— 留空时按账户名自动分目录（`rdp\<账户名>\`），
一条通道一个目录，多条通道不会串数据。

### 前置条件

| 条件 | 说明 |
|---|---|
| 目标主机 | 默认 `127.0.0.1`（本机）。可填 IP / 主机名，支持 `IP:端口`；填 `rdp://xxx` 也会自动去掉前缀 |
| Windows 版本 | 专业版 / 企业版 / 工作站版。**家庭版不能作为 RDP 服务端**，程序会检测并明确提示（连远程主机时本机只是客户端，不受此限） |
| 远程桌面 | 目标机器上需开启。目标是本机时页面上有「开启远程桌面」按钮（改注册表 + 放行防火墙，需管理员） |
| 目标账户 | 必须已创建，且**必须设置过登录密码**（空密码账户无法通过 RDP 登录）。一条通道一个账户，**别让两条通道共用一个账户** |
| 系统会话数 | 本机想同时跑多条通道，需要系统允许多个交互式会话并存；普通 Windows 客户端版同一时刻只允许一个，多机或 Server 多会话才能真并行 |
| 通道数上限 | 一次最多 **8 条**通道并行（原生层上限）。超出的通道会被截断，开始执行时会弹一次说明，日志里也有警告 |
| 凭据 | 用通道编辑卡里的「保存凭据」存入 Windows 凭据管理器，**按「主机 + 账户」各存一条**，密码不写进 `config.json` |
| 会话代理 | 点「部署会话代理」放到所有用户公共启动目录（需管理员，一次性）。远程目标要在**对方机器**上部署 |
| 指令桥目录 | **仅远程主机需要**。填双方都能读写的 UNC 或盘符路径，例如 `\\192.168.1.20\YukinoBridge`；本机多账户留空即可 |

### 会话代理是怎么部署的

点「部署会话代理」时做两件事：

```text
1. 复制程序到公共目录
   源：当前运行的 YukinoChan.exe 所在目录（可能是 C:\Users\<你的账户>\... 下的开发产物）
   目标：C:\ProgramData\YukinoChan\agent\
   跳过：config.json / logs/ / runtime_stats/   ← 目标账户要自己生成这些

2. 建快捷方式
   C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\雪乃酱 RDP 会话代理.lnk
   TargetPath = C:\ProgramData\YukinoChan\agent\YukinoChan.exe
   Arguments  = --rdp-agent [--bridge:<桥目录>]
```

**为什么非要多复制这么一份**（早期版本踩过坑）：

第一版直接用主控端 exe 的路径写快捷方式。但主控端 exe 通常在 `C:\Users\<你的账户>\` 下，
而 `C:\Users\` 下的目录**不给其他普通用户读权限**。目标账户登录后启动快捷方式直接被拒绝，
而且 Windows 对启动项失败是**静默的** —— 表现为"远程桌面连上了，但任务就是不执行"，日志里什么都看不到。

复制到 `%ProgramData%` 就绕开了这个问题：这个目录默认对 `BUILTIN\Users` 开放读权限。

复制只在程序有更新时发生（比对 `YukinoChan.exe` 的写入时间），平时点「部署」是秒过的。
238 个文件约 142MB，本地 SSD 实测 600ms 左右。

> 如果你把雪乃酱装在 `%ProgramData%\YukinoChan\agent\` 下再点部署，程序会识别出"源就是目标"直接跳过复制。

「移除代理」会同时删掉快捷方式和那份程序副本。

**副本目录被占用时怎么办（三档策略）**

代理是常驻进程，它会一直持有副本目录里的 DLL —— 所以“覆盖部署”有时会被系统拦住。
遇到这种情况程序会按下面三档退让，**不会**把原因归到“你需要管理员权限”（那是误报）：

| 档 | 条件 | 做法 |
|---|---|---|
| ① | 副本目录未被占用 | 原地覆盖，路径不变（最常见） |
| ② | 被占用，且旧代理**空闲** | 先把空闲的旧代理送走，再原地覆盖 |
| ③ | 旧代理**正在跑任务**，或跨会话杀不掉 | 拒绝部署（跑任务时） / 改用备用目录 `agent_b` 并把快捷方式改指过去 |

> 旧代理**正在执行任务时不会硬来**（会先读目标账户那路桥的 `status.json`，相位是 `running` 就拒绝），
> 免得打断跑了一半的活。部署成功后旧代理已被结束，**要注销目标账户再重新连一次**，启动项才会拉起新代理。

> 曾试过“把旧副本目录整个改名挪开再重建”—— 实测走不通：只要目录内有文件被持有句柄，
> `Directory.Move` 一律返回 Access denied（`FileShare` 四种模式全试过）。别回头再试。

### 画面：内嵌 / 独立窗口 / 多画面同屏

**内嵌（默认）**：画面直接画在各「会话通道」页面里（FreeRDP + D3D11 交换链），不是 COM ActiveX 那一套。
鼠标键盘点上去就能操作远端；全屏时 `Win` / `Alt+Tab` 等系统组合键会转投远端，`F11` 或双击退出全屏。

> 早期确实走过 COM ActiveX + `SetParent` 的路（那个 WinForms 类库 `YukinoChan.RdpHost`），
> 在 WinUI 3 上要跟 XAML 抢 Z 序、还要处理 STA / 坐标换算 / 分辨率动态重协商，稳定性代价太大，已彻底删除。
> 现在用原生 FreeRDP 自绘画面，不涉及子窗口 Z 序问题。

**独立窗口（`mstsc`）**：通道管理页把「连接方式」改成「独立窗口」即可，
连接时打开系统自带的远程桌面客户端 —— 最稳、永远可用，但画面不在雪乃酱里。

| 项 | 内嵌 | 独立窗口（mstsc） |
|---|---|---|
| 画面位置 | 会话通道页面里（可多路并列） | 独立的远程桌面窗口 |
| 鼠标键盘 | 直接操作 | 直接操作 |
| 全屏 | 有（独立全屏窗口 + 键盘接管） | mstsc 自带 |
| 分辨率 | 跟随画面格子大小自动适配 | 按「远程桌面窗口尺寸」档位生成临时 `.rdp` |
| 多画面同屏 | 支持 | 不支持（每个窗口一套） |

**多画面同屏（M6）**：通道页顶部可在「单画面 / 多画面」之间切换。

- **单画面**：只摆本通道一格，占满整个画面区，保留「全屏」；
- **多画面**：2×2 四格并列显示全部活跃通道，每格标通道名与状态；
  通道多于 4 条时出现「上一页 / 下一页」（翻页，不是滚动）；
- 每格有「弹出窗口」→ 把这路画面弹成一个普通可缩放窗口，你可以自己并排摆放；
  关掉窗口后画面**自动回到**页面里那一格；
- 多画面模式下**不提供全屏**（会盖掉其它几路），双击某格 = 把它弹成独立窗口；
- 为什么每页 4 格而不是 8：8 路画面同时渲染的 CPU / GPU 开销不可接受；
  分页天然保证「同时渲染不超过 4 路」，用不到的画面格一律解挂（不占渲染）。

**渲染纪律**（排障时有用）：一个内嵌客户端同一时刻只能挂一块画面。
所以画面在「页面格子」和「弹出窗口」之间转移时，一定**先解挂再挂载**。
若某路画面黑屏或几路互相闪烁，先关掉那路的弹出窗口再重开一次。

### 自定义分辨率

分辨率**按通道**配置（通道编辑卡里的「远程桌面窗口尺寸」），写进 `config.json` 该通道的节点里：

| 档位 | 内嵌模式 | 独立窗口模式 |
|---|---|---|
| 自动（默认） | 跟随画面格子大小自动适配 | 不生成 `.rdp`，`mstsc /v:<主机>` 用系统默认尺寸 |
| 1280×720 / 1366×768 / 1600×900 / 1920×1080 / 2560×1440 | 按该尺寸向服务端申请桌面 | 按该尺寸生成临时 `.rdp` 连接 |

独立窗口模式下，选了具体档位会先在 `%LocalAppData%\YukinoChan\session-<通道id>.rdp` 生成一份配置，再 `mstsc <该文件>`：

```ini
full address:s:192.168.1.20
server port:i:3390          # 填了端口才有这行
screen mode id:i:1          # 必须写：不写会走全屏，宽高被忽略
desktopwidth:i:1920
desktopheight:i:1080
smart sizing:i:1            # 窗口缩小时整体等比缩放，不出现滚动条
dynamic resolution:i:0      # 必须写：:1 会跟随窗口而忽略上面两个尺寸
use multimon:i:0            # 必须写：否则可能继承上次设置而横跨多屏
prompt for credentials:i:0  # 凭据走凭据管理器，不弹输入框
```

后三个项（`screen mode id` / `dynamic resolution` / `use multimon`）是踩过坑才钉死的：
`.rdp` 里没写的项会拿「上次会话设置」补齐，少写一个就可能是全屏窗口或横跨多屏。

注意点：

- **改档位后要重新连接才生效**（内嵌连接也一样，要重连一次）；
- 独立窗口模式下 `.rdp` **按通道分文件**（`session-<通道id>.rdp`），多条通道并行时不会互相覆盖；
- 尺寸超出范围会被收敛到 `640×480` ~ `4096×4096`；
- `mstsc /w: /h:` 在给了 `.rdp` 文件时会被忽略，所以尺寸必须写进文件里。

`smart sizing:i:1` 是让体验好一点的关键：窗口拉小的时候画面整体缩小、比例不变，
而不是出现滚动条让你拖着看。

### 怎么用

**第一次配置（只需做一次）**

1. 打开左侧「**会话通道**」页，把总开关「在目标账户会话中执行任务」打开；
2. 点「**新建通道**」，在下面的编辑卡里填：通道名称（留空会按账户名自动起）、目标主机、目标账户；
3. 点「**保存凭据**」填一次该账户的密码（存进 Windows 凭据管理器，不写进 `config.json`）；
4. 点「**开启远程桌面**」（若目标机器尚未开启）；
5. 点「**部署会话代理**」（一次性，需要管理员；远程目标要在**对方机器**上做这一步）；
6. 点「**重新预检**」，确认那一行显示环境就绪、代理已部署。
   目标账户至少要**登录过一次**，代理才会被启动项拉起来并显示「在线」。

> 一套通道要配两个不同账户时：再点一次「新建通道」就行。
> 文本字段填完**切走焦点就会自动保存**（也有「保存通道设置」按钮兜底）。

**把任务分配给通道**

在「任务执行」页选中一条任务 → 详情「基础设置」里的「**执行通道**」下拉：

- **本地执行（当前会话）** —— 默认值，任务跑在雪乃酱自己所在的会话里；
- **<通道名>** —— 任务下发给那条通道，在目标账户里跑。

左侧任务列表每一条也会显示它归哪个通道，不用点开就知道。

**跑一轮**

1. 点顶栏「**开始执行**」。日志会先写一行「本轮启动 N 条会话通道并行执行：…」；
2. 左侧菜单「会话通道」下面出现分组「**执行中的通道**」，每条通道一个条目 ——
   点进去就是该通道的画面 + 进度 + 心跳 + 事件；
3. 主页的「会话通道」概览卡一眼看完各通道状态（点一行跳过去）；
4. 一条通道内部的任务按顺序跑；顶栏「停止执行」在远程 / 并行执行时**也是可用的**，
   会向**所有**通道下发 `stop.json`；
5. 全部结束后：日志出现 `[汇总] 共 N 块（本地 X / 通道 Y）：成功 …、异常 …。`，
   并弹**一条**汇总通知；看板娘在**整轮结束**时才从「工作」变「休息」。

**连另一台机器**

1. 通道编辑卡的目标主机填对方的 IP 或主机名；
2. 指令桥目录填双方都能读写的共享路径（本机多账户留空即可）；
3. 在**对方机器**上装雪乃酱，用同样的桥目录部署会话代理（这一步只能在对方机器上做）；
4. 点「重新预检」确认没有阻塞项，然后正常选通道、跑任务。

### 需要注意的限制

- **本机多通道并行取决于系统会话数。** 系统允许多个交互式会话并存时可真并行；普通 Windows 客户端版同一时刻只允许一个，第二条本机通道会把前一个桌面顶掉 —— 这种环境下多通道建议分散到不同机器。
- **一次最多 8 条通道并行**（原生层上限）。第 9 条及以后的通道**不会被启动**，开始执行时会弹一次说明；被截断的通道在日志里也会逐条写出原因。
- **同一账户不能开两条通道。** 它们抢的是同一个会话，没有意义；配成同一主机时管理页只提示，不阻止。
- **一条任务不会静默改跑地方。** `channel_id` 指向的通道被删掉 / 停用 / 没配账户时，该任务**本轮不执行**并给出原因（不会偷偷改跑本地 —— 那样可能把游戏重复拉起来）。
- **通知两边都发。** 系统通知只在发出它的那个会话里显示，所以任务完成时目标会话和主控端各发一次，你在哪边都看得到。主控端是轮询 `status.json` 后补发的；锁屏期间的通知会落暂存，回前台补发。
- **代理里的自动关机会直接执行。** 代理没有交互界面，如果开启了自动关机，会按配置排定倒计时关机（切过去可以 `shutdown /a` 取消）。
- **目标账户要能写日志。** 日志与统计仍写到程序目录，若目标账户对该目录没有写权限，任务照常执行但日志会缺失。
- **远程目标无法在本机枚举会话。** WTS API 只能查本机会话，所以连远程主机时不会等"会话就绪"，直接以对方回传的 `status.json` 为准。同样地，「断开 / 注销」只对本机目标生效，远程目标请用任务结束后的收尾方式。
- **远程目标的预检只能查一半。** 对方账户是否存在、远程桌面是否开启、代理有没有部署，本机都查不到，需要你在对方机器上确认。
- ⚠️ **从旧版本升级必须重新部署一次代理** —— 桥目录规则改成了按账户名分目录，详见下面的「升级注意」。

### 排障

| 现象 | 排查 |
|---|---|
| 提示"等待目标会话超时" | 凭据是否正确；账户是否有密码；远程桌面是否真的开启；手动开一次 `mstsc` 看能否登录 |
| 一直停在"等待代理接管" | 检查代理是否已部署、是否显示「在线」；打开**该通道的**桥目录看 `status.json` 有没有被更新 |
| 无法写入桥目录 | 本机默认 `<ProgramData>\YukinoChan\rdp\<账户名>` 需要写权限，用管理员运行一次即可；远程目录要确认对方也开了写权限 |
| 家庭版提示 | 该版本不支持作为 RDP 服务端，需要升级到专业版以上（连远程主机时本机只当客户端，不受此限） |
| 能连上远程主机但任务不动 | 90% 是桥目录没配或对方读不到：确认两边指向同一个共享路径，且对方代理的启动参数里带了 `--bridge:` |
| **连上了但任务完全不执行** | 先看 `%ProgramData%\YukinoChan\rdp\<账户名>\status.json`：如果 `AgentUser` 是空的、时间停在"下发那一刻"，说明目标会话里的代理没起来 —— **重新点一次「部署会话代理」**（会把程序复制到公共目录），再注销目标账户重新连 |
| 升级后"代理明明在跑却显示不在线" | 桥目录规则变了（`rdp\` → `rdp\<账户名>\`）。**重新部署一次代理**，并注销目标账户再重连 |
| 提示需要配置「指令桥目录」 | 目标是远程主机时的正常拦截；只想切本机账户的话，把远程主机改回 `127.0.0.1`（本机多账户留空即可） |
| 填了 IPv6 地址识别不对 | 裸 IPv6（如 `::1`）会被整体当作地址；带端口要写 `[::1]:3390` |
| 远程桌面窗口没弹出来 | 日志里会写 `mstsc` 的启动结果。多半是路径被策略拦了，手动跑一次 `mstsc` 验证系统可用 |
| 日志说"登录失败：账户名或密码不对" | 密码没交到 mstsc 手上。重新填一次密码点「保存凭据」（写进 Windows 凭据管理器，重启程序也不用重填） |
| 保存凭据后提示"读不回来" | 凭据管理器被组策略限制了。可以手动在「远程桌面」窗口里输入一次密码，或联系管理员放开凭据管理器 |
| 分辨率不是我想要的 | 改档位后**重新连接一次**（已连接的会话不能动态改尺寸）；选「自动」则跟随画面格子大小 / 系统默认值 |
| 窗口拉小后出现滚动条 | `.rdp` 里已写 `smart sizing:i:1`。若出现说明文件没生效，删掉 `%LocalAppData%\YukinoChan\session-<通道id>.rdp` 后重连一次 |
| 提示"环境未就绪" | 按「重新预检」看具体缺哪一项（远程桌面未开 / 服务未启动 / 代理未部署） |
| **某路画面黑屏 / 几路互相闪烁** | 一个客户端同时只能挂一块画面。先关掉那路的「弹出窗口」再重开；多画面翻页时会重排格位，属正常 |
| **只跑了部分通道** | 看日志里的「本轮启动 N 条」与被跳过通道的原因：常见是超 8 条上限、通道被停用、没配账户、或 `channel_id` 指向了已删除的通道 |
| **"停止执行"看起来没反应** | 停止是向每条通道各写一份 `stop.json`，代理下一拍才读到。若长时间不停，看该通道 `status.json` 的心跳时间是不是还在走 —— 不走说明代理已经不在了 |

### 升级注意（从旧版本升级必读）

新版桥目录规则变了：

```text
旧：<ProgramData>\YukinoChan\rdp\
新：<ProgramData>\YukinoChan\rdp\<账户名>\
```

**已部署过会话代理的机器，升级后必须重新点一次「部署会话代理」**，
否则新版主控端在新目录里读写、旧代理还在旧目录里转 —— 表现就是"代理明明在跑，却一直显示不在线"。
重新部署后还要**注销目标账户再重连**，新代理才会被启动项拉起。

其他升级注意：

- 旧配置里没有 `channels` 时会自动迁移出一个默认通道（继承原来的目标账户 / 桥目录 / 分辨率 / 收尾方式），
  **任务会自动指向它**，不需要手工重配；
- 一个通道内的任务仍然串行，只有**通道之间**才是并行 —— 如果原来靠 `concurrent_group` 做并发，行为不变；
- 内嵌画面改用原生 FreeRDP 实现，`src/YukinoChan.RdpHost/`（旧 COM ActiveX 方案）已删除；
  旧配置里的 `rdp.embed_remote_desktop` 键仅为兼容而保留，读进来恒为 `false`。

---

## 八、与 Python 版的关系

| | Python 版（仓库根） | WinUI 3 版（`winui3/`） |
|---|---|---|
| 状态 | legacy，仅修 bug | 主线 |
| 配置文件 | `config.json` | **同一个 `config.json`** |
| 日志目录 | `logs/` | **同一个 `logs/`** |
| 统计目录 | `runtime_stats/` | **同一个 `runtime_stats/`** |
| 看板娘素材 | `assets/mascot/` | **同一个 `assets/mascot/`** |

两个版本共享同一份配置与数据目录，可以来回切换，**不需要重新配置**。

---

## 九、已知限制

- **真机 GUI 验证尚未全覆盖**：构建环境在沙箱中无法启动 GUI，自动化部分靠 `dotnet build` + `_smoke`（632 项断言）；
  多通道并行、内嵌画面、多画面同屏等交互需要在真机上逐项验收（清单见计划书 §10）。
- **无 MSIX 打包**：unpackaged 模式直接跑 exe，如果需要商店分发要另建打包工程。
- **主题降级**：原 6 套 Qt 皮肤不保留，统一为系统 / 浅色 / 深色。
- **截图依赖 GDI+**：`System.Drawing.Common` 在 Windows 上可用，非 Windows 平台不支持（本工程本来就是 Windows-only）。
- **多画面每页最多 4 路**：8 路画面同时渲染的 CPU / GPU 开销不可接受，所以网格按 2×2 分页（翻页，不是滚动）。
- **弹出窗口没有独立入口**：画面弹成独立窗口后，要回去看它只能通过主窗口菜单里的「执行中的通道」；
  窗口本身只有关闭按钮（关闭后画面自动回页面里那一格）。
- **一次最多 8 条通道并行**（原生 `YCN_MAX_SESSIONS`）；超出的通道会被截断并明示原因。
- **真断线告警最迟 ~90 秒**：心跳判定把启动宽限期上限放在 90 秒（避免把正常的登录 + 代理拉起误报成失联），
  所以 90 秒内真掉线要到宽限期结束才会报。这是刻意的折中。
- **桥的 `command.json` / `status.json` 仍是 PascalCase**：与 `config.json` 的全 snake_case 约定不一致（历史遗留，待统一）。

---

## 十、后续建议

1. **真机验收多会话通道**：按计划书 §10 的清单逐项过（并行、停止、失联隔离、多画面、并发上限），
   重点是「系统是否支持多会话」与「多通道能否真正同时推进」。
2. 补齐 `docs/images/` 下 WinUI 3 版截图（尤其是会话通道页与多画面网格）。
3. 视情况加 MSIX 打包工程（`WindowsPackageType=MSIX`）。
4. 待办清理：桥的 `command.json` / `status.json` 改 snake_case；设置页主题切换重复弹对话框的异常；
   设置页加「重置窗口位置」；无人使用的 P/Invoke 清理。
5. 尚未做：内嵌会话的**剪贴板互通**（需专门的 FreeRDP 通道支持）。
6. Python 版在 README 中明确标注 legacy。
