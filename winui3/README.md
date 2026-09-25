# YukinoChan WinUI 3 版（雪乃酱 · 二游脚本统合管理器）

> 本目录是雪乃酱的 **C# / WinUI 3 重写版**，与仓库根目录的 Python + PySide6 版本（`main.py` / `core/` / `ui/`）功能等价。
> Python 版进入 **legacy 维护状态**，不再新增功能；后续开发以本目录为准。

开发基线：**v2.0 WinUI 3**（原 Python 版基线 v31.17.1）

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
├─ src/YukinoChan.RdpHost/          # 【已停用】早期内嵌远程桌面的 WinForms 类库，不再被主工程引用
│  ├─ YukinoChan.RdpHost.csproj     #   保留代码以备将来需要，但已从解决方案中移除
│  ├─ RemoteDesktopHost.cs          #   隐藏 Form 承载 AxMsRdpClient9NotSafeForScripting
│  ├─ RdpErrorText.cs               #   RDP 错误码 → 中文说明（冒烟测试仍在用）
│  └─ RdpNative.cs                  #   SetParent / MoveWindow / 窗口样式
└─ src/YukinoChan.App/
   ├─ YukinoChan.App.csproj
   ├─ app.manifest                 # asInvoker + PerMonitorV2 + UTF-8
   ├─ App.xaml / App.xaml.cs       # 应用入口、全局资源、异常兜底
   ├─ Themes/Styles.xaml           # 雪乃酱雾蓝主题（YukinoAccent 等 Brush / 卡片 / 气泡样式）
   ├─ Models/
   │  ├─ Enums.cs                  # WaitModes / TimeoutActions / ConcurrentPolicies / MascotStates
   │  ├─ RdpModels.cs              # RdpConfig / RdpResolutions / RdpTargets / RdpCommand / RdpStatus / RdpTaskEvent
   │  ├─ TaskConfig.cs             # 单条任务配置（键名与 config.json 完全一致）
   │  ├─ AppConfig.cs              # 全局配置
   │  └─ MonitorSpec.cs            # 监控目标描述
   ├─ Services/
   │  ├─ AppPaths.cs               # 配置/日志/统计/素材路径统一入口
   │  ├─ RdpBridge.cs              # 跨会话通信桥（ProgramData 下的 command.json / status.json）
   │  ├─ RdpSessionService.cs      # 本机环回 RDP：预检、凭据、连接（mstsc）、会话查询、断开/注销、代理部署
   │  ├─ RdpAgentRunner.cs         # 目标会话里的执行代理（--rdp-agent 模式）
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
   │  └─ MainViewModel.cs          # 单一主 ViewModel，串联所有服务
   └─ Views/
      ├─ MainWindow.xaml(.cs)      # NavigationView + 自定义标题栏 + 控制栏
      ├─ RdpPage.xaml(.cs)         # 远程会话：账户凭据 / 环境预检 / 连接控制 / 执行设置 / 连接与进度
      ├─ MascotPanel.xaml(.cs)     # 右侧看板娘 + 状态网格 + 发言气泡
      ├─ HomePage.xaml(.cs)        # 总览
      ├─ TasksPage.xaml(.cs)       # 任务管理（列表 + 分组表单）
      ├─ StatsPage.xaml(.cs)       # 耗时统计
      ├─ LogsPage.xaml(.cs)        # 运行日志
      ├─ SettingsPage.xaml(.cs)    # 设置
      └─ ShutdownCountdownDialog.xaml(.cs)  # 关机倒计时
```

代码量约 **10000 行 C#**（含 XAML 与后台代码，不含 obj 下的自动生成文件），
全部在 `YukinoChan.App`。

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

## 七、远程会话执行（RDP 多用户 / 远程主机）

### 能做什么

通过远程桌面登录另一个 Windows 会话，让任务队列在目标账户里自动跑完，
每个任务完成都会弹系统通知，全部跑完后按你选的方式处理会话（保持 / 断开 / 注销）。

**远程桌面用系统自带的 mstsc 打开独立窗口**：连接时会弹出 Windows 远程桌面客户端，
登录成功后会话代理自动接管任务。主界面这边持续接收进度、弹系统通知。

> 早期版本试过把远程桌面画面嵌进雪乃酱窗口（ActiveX + SetParent），
> 但在 WinUI 3 上要跟内容桥抢 Z 序、还要处理 STA / 坐标换算 / WinForms 布局覆盖，
> 稳定性代价太大，已弃用。相关代码保留在 `src/YukinoChan.RdpHost/`，不再被主工程引用。

**「连接」与「开始执行」是分开的两件事**：

1. 在「远程会话」页点**「连接目标账户」** → 打开远程桌面窗口，你先确认登录成功；
2. 回到任意页面点**「开始执行」** → 再下发任务（如果还没连，会先自动连接再下发）。

「断开目标会话」用于主动断开目标账户的会话。

**目标主机可以自己填**，两种用法：

| 用法 | 目标主机填什么 | 典型场景 |
|---|---|---|
| 切本机另一个账户（默认） | `127.0.0.1` | 游戏或脚本装在另一个账户下、需要另一套用户环境、想把挂机环境和日常环境隔开 |
| 连另一台机器 | 局域网 IP / 主机名 / 公网地址，可带端口（如 `192.168.1.20:3390`） | 主机和挂机机分开、多台机器统一由一台下发任务 |

### 工作原理

Windows 会话之间不能直接通信，主控端也无法跨会话启动进程，所以用"共享文件 + 登录自启代理"来搭桥：

```text
主控端（账户 A）
  ├─ 1. 把任务快照写进共享目录的 command.json
  │      默认 %ProgramData%\YukinoChan\rdp（够用于本机多用户）
  │      目标是远程主机时，填一个双方都能读写的 UNC 路径
  ├─ 2. 用系统自带的远程桌面（mstsc）打开到 <目标主机> 的独立窗口（凭据来自内存或 Windows 凭据管理器）
  ├─ 3. 轮询 status.json，实时显示进度
  └─ 4. 每收到一条"任务完成"事件就弹一次通知

目标会话（账户 B / 远程机器）
  ├─ 1. 登录后，公共启动目录里的快捷方式自动拉起 YukinoChan.exe --rdp-agent [--bridge:<共享目录>]
  ├─ 2. Agent 隐藏窗口运行，读完 command.json 后立刻删掉（防重复执行）
  ├─ 3. 用同一套 ScriptRunner 执行任务队列（语义与本地执行完全一致）
  ├─ 4. 每完成一个任务：写回 status.json + 在本会话弹通知
  └─ 5. 全部结束后按指令处理会话（保持 / 断开 / 注销），然后退出
```

**为什么远程主机要多填一个"指令桥目录"**：`%ProgramData%` 是本机路径，远程机器上的 Agent 根本读不到。
所以目标是别的机器时，必须指定一个两边都能访问的位置（网上邻居共享、NAS、同步盘都行），
主控端写指令、Agent 读指令都走那里。部署 Agent 时这个路径会自动写进快捷方式参数。

目标是本机另一个账户时这项**留空即可**，行为和之前完全一样。

### 前置条件

| 条件 | 说明 |
|---|---|
| 目标主机 | 默认 `127.0.0.1`（本机）。可填 IP / 主机名，支持 `IP:端口`；填 `rdp://xxx` 也会自动去掉前缀 |
| Windows 版本 | 专业版 / 企业版 / 工作站版。**家庭版不能作为 RDP 服务端**，程序会检测并明确提示（连远程主机时本机只是客户端，不受此限） |
| 远程桌面 | 目标机器上需开启。目标是本机时页面上有「开启远程桌面」按钮（改注册表 + 放行防火墙，需管理员） |
| 目标账户 | 必须已创建，且**必须设置过登录密码**（空密码账户无法通过 RDP 登录） |
| 凭据 | 用页面上的「保存凭据」存入 Windows 凭据管理器，密码**不写进 config.json** |
| 会话代理 | 点「部署会话代理」放到所有用户公共启动目录（需管理员，一次性）。远程目标要在**对方机器**上部署 |
| 指令桥目录 | **仅远程主机需要**。填双方都能读写的 UNC 或盘符路径，例如 `\\192.168.1.20\YukinoBridge` |

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

### 关于"内嵌画面"（已弃用）

早期版本试过把远程桌面画面嵌进雪乃酱主窗口，做法是拿 `MsRdpClient` 这个 COM ActiveX 控件
塞进 WinUI 3 —— 因为 WinUI 没有 `WindowsFormsHost`，还专门开了一个 WinForms 类库 `YukinoChan.RdpHost` 做宿主。

**这条路最后放弃了**，原因很实在：

- ActiveX 控件渲染在 WinUI 3 的原生子窗口里，和 XAML 内容的 Z 序、布局、缩放都不一路，
  实测很容易出现"控件创建成功、连接成功，但画面被盖住/不刷新"。
- 分辨率要跟内嵌区域联动，得走 `UpdateSessionDisplaySettings` 动态重协商，边界情况多。
- 维护成本远高于收益。

所以现在**统一用系统自带的远程桌面（`mstsc`）开独立窗口**：

| 项 | 做法 |
|---|---|
| 打开连接 | 调 `mstsc`，目标主机取「远程主机」设置项，凭据走 Windows 凭据管理器 |
| 分辨率 | 按下面「自定义分辨率」的档位生成临时 `.rdp`，让 mstsc 按指定尺寸开窗口 |
| 断开 | 「断开目标会话」调 WTS API 断开会话，不是关窗口 |

`YukinoChan.RdpHost` 的代码**保留在仓库里备查**（`repo/winui3/src/YukinoChan.RdpHost/`），
但已从解决方案和主工程引用中摘除，不参与编译产物。
`config.json` 的 `rdp.embed_remote_desktop` 键也保留着，只为兼容旧配置文件，读进来恒为 `false`。

### 自定义分辨率

「远程桌面连接」卡片里的**「远程桌面窗口尺寸」**下拉框控制远程桌面的分辨率：

| 档位 | 效果 |
|---|---|
| 自动（默认） | 不生成 `.rdp`，直接 `mstsc /v:<主机>`，窗口尺寸用系统默认值 |
| 1280 × 720 / 1366 × 768 / 1600 × 900 / 1920 × 1080 / 2560 × 1440 | 按这个尺寸连接 |

实现方式是：选了具体档位时，先在 `%LocalAppData%\YukinoChan\session.rdp` 生成一份配置，再 `mstsc <该文件>`：

```ini
full address:s:192.168.1.20
server port:i:3390          # 填了端口才有这行
desktopwidth:i:1920
desktopheight:i:1080
smart sizing:i:1            # 窗口缩小时整体等比缩放，不出现滚动条
dynamic resolution:i:1
prompt for credentials:i:0  # 凭据走凭据管理器，不弹输入框
```

两个注意点：

- **改档位后要重新连接才生效** —— 远程桌面客户端不支持在已连接的窗口上动态改尺寸。
- 尺寸写进 `config.json` 的 `rdp.desktop_width` / `rdp.desktop_height`，`0` 表示自动。
  超出范围的值会被收敛到 `640×480` ~ `4096×4096`。

`smart sizing:i:1` 是让体验好一点的关键：窗口拉小的时候画面整体缩小、比例不变，
而不是出现滚动条让你拖着看。

### 怎么用

**切本机另一个账户：**

1. 打开「远程会话」页面，确认远程主机是 `127.0.0.1`（不确定就点右边的「用本机」）。
2. 填目标账户名与密码，点**保存凭据**。
3. 点**开启远程桌面**（若尚未开启）。
4. 点**部署会话代理**（一次性，会提示需要管理员）。
5. 点**重新检查**，确认显示"环境就绪，可以连接"。
6. 选好会话收尾方式，打开总开关，回到主界面点**开始执行**。

**连另一台机器：**

1. 在远程主机栏填上对方的 IP 或主机名。
2. 在指令桥目录栏填上双方都能读写的共享路径。
3. 填对方账户名与密码，点**保存凭据**。
4. 在**对方机器**上装雪乃酱，用同样的桥目录部署会话代理（这一步只能在对方机器上做）。
5. 点**重新检查**确认没有阻塞项，然后**连接并执行**。

### 需要注意的限制

- **连接后当前桌面会被锁定。** Windows 客户端版同时只允许一个交互式会话，连到目标账户后你当前的桌面会锁屏。这对夜间挂机场景没有影响，但不适合"边用电脑边跑"。
- **通知两边都发。** 系统通知只在发出它的那个会话里显示，所以任务完成时目标会话和主控端各发一次，你在哪边都看得到。主控端是轮询 `status.json` 后补发的。
- **Agent 里的自动关机会直接执行。** 代理没有交互界面，如果开启了自动关机，会按配置排定倒计时关机（切过去可以 `shutdown /a` 取消）。
- **目标账户要能写日志。** 日志与统计仍写到程序目录，若目标账户对该目录没有写权限，任务照常执行但日志会缺失。
- **远程目标无法在本机枚举会话。** WTS API 只能查本机会话，所以连远程主机时不会等"会话就绪"，直接以对方回传的 `status.json` 为准。同样地，「断开 / 注销」按钮只对本机目标生效，远程目标请用任务结束后的收尾方式。
- **远程目标的预检只能查一半。** 对方账户是否存在、远程桌面是否开启、Agent 有没有部署，本机都查不到，需要你在对方机器上确认。

### 排障

| 现象 | 排查 |
|---|---|
| 提示"等待目标会话超时" | 凭据是否正确；账户是否有密码；远程桌面是否真的开启；手动开一次 `mstsc` 看能否登录 |
| 一直停在"等待代理接管" | 检查代理是否已部署；打开共享目录看 `status.json` 有没有被更新 |
| 无法写入共享目录 | 本机默认 `C:\ProgramData\YukinoChan\rdp` 需要写权限，用管理员运行一次即可；远程目录要确认对方也开了写权限 |
| 家庭版提示 | 该版本不支持，无解，需要升级到专业版以上（连远程主机不受影响，本机只当客户端） |
| 能连上远程主机但任务不动 | 90% 是桥目录没配或对方读不到：确认两边指向同一个共享路径，且对方 Agent 的启动参数里带了 `--bridge:` |
| **连上了但任务完全不执行** | 先看 `%ProgramData%\YukinoChan\rdp\status.json`：如果 `AgentUser` 是空的、时间停在"下发那一刻"，说明目标会话里的代理没起来。常见原因是快捷方式指向了一个别的账户读不到的程序路径 —— **重新点一次「部署会话代理」**（新版会把程序复制到公共目录） |
| 提示需要配置「指令桥目录」 | 目标是远程主机时的正常拦截；只想切本机账户的话，把远程主机改回 `127.0.0.1` |
| 填了 IPv6 地址识别不对 | 裸 IPv6（如 `::1`）会被整体当作地址；带端口要写 `[::1]:3390` |
| 远程桌面窗口没弹出来 | 日志里会写 `mstsc` 的启动结果。多半是路径被策略拦了，手动跑一次 `mstsc` 验证系统可用 |
| 远程桌面能开，但日志说"登录失败：账户名或密码不对" | 密码没交到 mstsc 手上。重新填一次密码点「保存凭据」（保存时写进 Windows 凭据管理器，重启程序也不用重填） |
| 保存凭据后提示"读不回来" | 凭据管理器被组策略限制了。可以手动在「远程桌面」窗口里输入一次密码，或联系管理员放开凭据管理器 |
| 远程桌面窗口分辨率不是我想要的 | 选一个固定档位后**重新点「连接目标账户」**（已连接的窗口不能动态改尺寸）；选「自动」则用系统默认值 |
| 远程桌面窗口拉小后出现滚动条 | 正常配置里开了 `smart sizing`，不会出现。若出现说明 `.rdp` 没生效，删掉 `%LocalAppData%\YukinoChan\session.rdp` 后重连一次 |
| 提示"环境未就绪" | 按页面上的「重新检查」看具体缺哪一项（远程桌面未开 / 服务未启动 / 代理未部署） |

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

- **未做真机 GUI 冒烟**：当前构建环境在沙箱中，无法启动 GUI 进程。已通过 `dotnet build` 0 错误 0 警告 + 产物校验，但界面交互仍需在真机上跑一遍。
- **无 MSIX 打包**：unpackaged 模式直接跑 exe，如果需要商店分发要另建打包工程。
- **主题降级**：原 6 套 Qt 皮肤不保留，统一为系统 / 浅色 / 深色。
- **截图依赖 GDI+**：`System.Drawing.Common` 在 Windows 上可用，非 Windows 平台不支持（本工程本来就是 Windows-only）。

---

## 十、后续建议

1. 真机跑一遍完整任务链（建议先用单任务测试）。
2. 补齐 `docs/images/` 下 WinUI 3 版截图。
3. 视情况加 MSIX 打包工程（`WindowsPackageType=MSIX`）。
4. Python 版在 README 中明确标注 legacy。
