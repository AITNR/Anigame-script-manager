# 主控端整窗假死：诊断、修复与验收

- **版本**：v1（2026-10-12）
- **状态**：🔧 **第一轮已实施**（第 1 层看门狗 + 2.1 / 2.2 / 3.2 已落地并编译通过，看门狗已端到端实测），**未提交**、**未部署**、**未在真机长时间复现验证**
- **适用范围**：`winui3/src/YukinoChan.App`（WinUI 3 主工程）
- **起因**：AITNR 反馈「现在又卡死了，查查看为什么」—— 主控端 GUI 整窗假死，进程还活着但点不动。
- **相关**：`rdp-embed-freerdp-plan.md`（内嵌渲染架构）、`known-limits-and-roadmap.md`（既有已知限制）

---

## 0. 一句话结论

**这不是崩溃，是 UI 线程被同步等待卡住导致消息泵停摆**（假死）。已确认卡死时刻在
**14:03:17–14:03:19**（正在开始内嵌画面 D3D 初始化时）；已确认这是**同族问题的新触发点**
（`0c8fc6f` 修过一次偶发假死，本次修复已在安装版本里）。

**根因层有两个结构性缺陷**，让这类卡死必然发生且必然无法自证：

1. **所有「能发现卡死」的机制都跑在 UI 线程上** → UI 线程一卡，全部静默失效；
2. **UI 线程上直接做阻塞式原生 / GPU / 进程调用**。

本次修的是这两点。**精确的等待对象（到底卡在哪个 API）仍未取得**，原因见 §5。

---

## 1. 现场证据（2026-10-12，PID 59404）

冻结实例：`C:\Program Files\YukinoChan\YukinoChan.exe`，14:03:14 启动，Session 1。

### 1.1 进程状态：确认假死

| 证据 | 结果 |
| --- | --- |
| `Responding` | **False** |
| `IsHungAppWindow(mainHwnd)` | **True** |
| `SendMessageTimeout(WM_NULL, 5000ms)` | 超时（返回 0，err=1460 ERROR_TIMEOUT） |
| UI 线程 tid=52888 ctxSw | `4023 → 4023`（**20 秒零增长**） |
| UI 线程 CPU | `968ms → 968ms`（零增长） |
| 65 个线程状态 | **全部 Waiting**（62 个 `Wait/UserRequest`），无任何线程在跑 |
| 对照组 explorer 主线程 | 同时段 ctxSw `4049461 → 4063061`（+13600） |
| 两个 FreeRDP 原生线程（60340/66116） | 20 秒 +1326 / +1322 ctxSw，**仍在转** |

→ 不是整个进程死掉，是 **UI 线程单点卡死**；CPU 零增长说明是**同步等待**，不是自旋。

### 1.2 卡死时刻：日志断点 + 一次缺失的日志

应用日志 `C:\Program Files\YukinoChan\logs\2026-10-12(2).log` 全文 21 行，**14:03:19 之后再无一行**：

```
[14:03:17] [通道：zdh] 已下发远程执行指令 f994de7d2f66 …
[14:03:17] [通道：zdh] [内嵌] 连接建立：桌面 1920×1080，画面已嵌入窗口。
[14:03:19] [通道：zdh] 会话代理会随目标账户登录自动启动并常驻等待指令，正在确认它是否在线…
```

三条独立旁证都指向 UI 线程在 **14:03:19 – 14:04:54** 之间彻底停摆：

| 旁证 | 正常时应出现 | 本次 |
| --- | --- | --- |
| `[embed] D3D 渲染器初始化成功` | 10-11 成功运行：连接建立后 2 秒出现 | **缺失** |
| `已确认会话代理在线` | 代理 14:03:48 上线（`agent_boot.log`），应由续体写回 | **缺失** |
| `仍在运行，已耗时 …` | 10-11 成功运行：**176 条**（每分钟一条，来自 1Hz 轮询） | **0 条** |

### 1.3 操作系统侧的独立佐证

```
14:10:47 / 14:11:04  DriverFrameworks-UserMode 10121 关键
                      在用户模式驱动程序 RdpIdd.dll 中发生运行时故障，宿主进程已终止
14:10:47 / 14:11:04  10111 关键 设备 Microsoft Remote Display Adapter 已脱线
                      WER: WUDFVerifierFailure / FxVerifierDriverReportedBugcheck / P6: RdpIdd.dll / P8: 2.35.0
14:10:49 / 14:11:04  LocalSessionManager: 会话 2(zdh)、会话 3(bh3) 断开（reason code 17）
```

`RdpIdd.dll` 是 Microsoft 的 RDP 间接显示驱动（IddCx 2.35.0，签名正常）。它崩了两次，
把 Remote Display Adapter 拉下线。**这与「GPU/显示驱动栈当时处于异常状态」是同时期现象**，
但是否为因果上游，**未证实**。

### 1.4 环境事实（仅供参考，**未**认定为根因）

本机 DriverStore 堆了**十多个第三方虚拟显示驱动**：`xvdd` ×11、GameViewer、AskLink、
Glidex、virtualdisplaydriver、usbcciddriver…，时间戳全部集中在 **2026-10-03**。
`EnumDisplayDevices` 显示主屏是 NVIDIA RTX 4060 Laptop（`DISPLAY2`，PRIMARY）。

---

## 2. 根因分析（分层）

### 2.1 已证实 vs 未证实（重要，接手先读这段）

**已证实（硬证据）**

- UI 线程死于 14:03:19–14:04:54 之间（§1.2 三条旁证）
- 进程整体没死，原生 RDP 线程仍在跑（§1.1）
- UI 线程彻底不走队列（§1.1）
- 显示驱动栈同时出事了（§1.3）

**⚠️ 已撤回的推断（重要教训）**

最初推断「缺 `[embed] D3D 渲染器初始化成功` 这行 = 卡在 D3D 初始化」。
**这是错的**。查 10-11 成功日志后发现：那行只在**有人真的点进通道页、把画面挂上 `RdpView` 之后**
才出现；而本次日志里没有任何导航记录。
所以「没这行」既可能是卡在 D3D，**也可能是当时压根没人挂画面**——不能当证据。

**未取得的关键证据**：**精确的等待对象 / 线程栈**。原因见 §5。

### 2.2 结构性缺陷 ①：所有看门狗都在 UI 线程上

```powershell
# 枚举全部定时器/看门狗，没有一个是后台线程：
MainViewModel.cs:1666, 1715, 3395, 3515, 4681, 5023   ← 全是 _dispatcher.CreateTimer()
RdpView.xaml.cs:207                                    ← 16ms 呈现节拍器
RdpChannelPage.xaml.cs:164 / HomePage / MainWindow ...  ← 全是 DispatcherQueue.CreateTimer()
```

UI 线程一卡，**心跳轮询、黑屏看门狗、1Hz 刷新、呈现节拍器全部静默失效**。于是：

- 没有失败日志（`AppendLog` 的队列没人消费）
- `crash.log` 干净（不是崩溃；`AppendLog` 走 XAML 未处理异常那条路，这次压根没抛异常）
- 没有自动恢复（重连逻辑本身也在 UI 线程上）

**这个 App 设计上就无法报告自己卡死。**

### 2.3 结构性缺陷 ②：UI 线程上直接做阻塞式原生 / GPU / 进程调用

| 位置 | 阻塞内容 | 风险 |
| --- | --- | --- |
| `MainViewModel.cs:3395–3412`（黑屏看门狗） | `TryCopyFrame` → `ycn_rdp_copy_frame` 在原生 `g_lock` 里对 1920×1080×4 ≈ **8.3MB** 整帧 memcpy；RDP 线程 `ycn_end_paint` 持同一把锁写新帧 | UI 线程在锁上等 = **CPU 零增长的静默冻结**，与本次现象吻合 |
| `ycn_rdp.c:2204` | `WaitForSingleObject(thread, 5000)` —— `ycn_rdp_disconnect` join 原生线程 | 最长阻塞 5 秒 |
| `RdpSessionService.cs:1889/1938` | `process.WaitForExit(15000/20000)` —— netsh / cmdkey / powershell | 最长 15–20 秒 |
| `RdpSessionService.cs:1896/1945` | `GetAwaiter().GetResult()` | 同步等 |

### 2.4 因果判断（按可能性排序）

1. **最可能**：UI 线程卡在首次内嵌 D3D11 初始化里，与 GPU/驱动内部互锁。
   症状与 `0c8fc6f`（「偶发整窗假死：D3D 驱动内部互锁 → 进程活着、Responding=False、
   UI 线程卡在等待、CPU 零增长」）**完全同族**；该修复**已在**当前安装版本里
   （`git merge-base --is-ancestor 0c8fc6f c9fc87a` = 是），所以这是**同族的新触发点**，不是旧 bug 回归。
2. **次可能**：D3D 初始化本身没卡，但 UI 线程在别的同步等待上卡住
   （如 `ISwapChainPanelNative.SetSwapChain` 进 XAML 组合器）。观察数据无法区分这两种。
3. **可以排除**：不是 FreeRDP 依赖 DLL 缺失 —— 安装目录八件套
   （`ycn_rdp` / `freerdp3` / `freerdp-client3` / `winpr3` / `z` / `cjson` / `libssl-3-x64` / `libcrypto-3-x64`）
   **全部就位**，不是上次那个 `0x8007007E`。

### 2.5 关于 `RdpD3DRenderer.cs` 注释里的一处谎言

`RdpD3DRenderer.cs:17` 写着「回归防线见 `_smoke/RenderThreadAffinityCheck.cs`」——
**这个文件不存在**（`winui3` 下只有一个 csproj）。那个声称的回归测试从没落地。
改动前别信这条注释。

---

## 3. 已实施的修改（第一轮）

> 提交前状态：**编译通过（0 错误），看门狗端到端实测通过，未提交、未部署。**

### 3.1 第 1 层：UI 线程无响应看门狗 ★核心

**新增** `Services/UiHangWatchdog.cs`，挂在 `App.xaml.cs`（`OnLaunched` 末尾，
`ViewModel.Initialize()` 之后 —— 必须等窗口建好才拿得到 HWND）。

```
Services.UiHangWatchdog.Install(Helpers.WindowHelper.GetHwnd(MainWindow));
```

**关键设计**：跑在**自己的后台线程**上（`new Thread{IsBackground=true}`），
用 `SendMessageTimeout(SMTO_ABORTIFHUNG, 3000ms)` 探测 —— 与目标消息泵无关，
目标卡死时它一定超时（而 DispatcherQueueTimer 会跟着一起静默）。

探测到卡死后落三样东西（全部进 `CrashLog.WriteNote`，不新建日志文件）：

1. 卡死事实：开始时刻、持续时长、连续超时次数；
2. **本进程全部线程的快照**（tid / 状态 / 等待原因 / ctxSw / CPU）——
   取它只需一次 `NtQuerySystemInformation(SystemProcessInformation)`，这是定位「卡在哪个等待」的第一手依据；
3. 恢复时刻（若后来自己好了）。

**参数**：探测间隔 5s、单次超时 3s、连续 2 次失败才报（防抖）、卡死中每 60s 补报一次、
单次最多列 128 个线程。

**设计约束（改前必读）**：

- 本类**永不**触碰任何 XAML / DispatcherQueue / WinRT 对象，只写文件；
- 所有异常都在内部吞掉 —— 看门狗自己绝不能成为启动失败或再次卡死的原因；
- 只对主控端启用（代理模式没有 UI 线程）。

### 3.2 第 2.1 层：黑屏看门狗的抓帧移到工作线程

`StartEmbedWatchdog` 原来整段 Tick 在 UI 线程上。现在拆两段：

- 抓帧 + 亮度采样 → `Task.Run(...)`（工作线程，等 `g_lock` 不再冻界面）
- 停表 / 重连 / 写日志 → `ContinueWith(..., TaskScheduler.FromCurrentSynchronizationContext())` 回 UI 线程

⚠️ **`TryCopyFrame` 返回的 `pixels` 是 client 内部的复用缓冲**，所以亮度采样必须在
同一个 `Task.Run` 里**同步做完**，不能把数组带出去再用。

### 3.3 第 2.2 层：原生断开改为异步

`DisposeSurface` 里 `client.Dispose()` → `ycn_rdp_disconnect` 会 join 原生线程（最坏 5 秒）。
现在 UI 侧先完成解绑与状态复位（立刻生效、界面马上松开），`Dispose` 丢到 `Task.Run`。

### 3.4 第 3.2 层：渲染后端变成配置项 + UI 开关

- `Models/RdpModels.cs`：新增 `RenderBackends`（`auto` / `software`）+ `RdpConfig.RenderBackend`
  （落盘键 `render_backend`，`Sanitize()` 里归一化）
- `Views/ChannelsPage.xaml`：「执行与通知（全局）」里新增「内嵌画面渲染后端」下拉框
- `Services/RdpD3DRenderer.cs`：新增 `PushRenderBackend(bool)` / `ShouldUseSoftware`
- `Views/RdpView.xaml.cs`：`Connect` / `AttachClient` 入口同步配置；两处判断从
  `DisableD3D` 改为 `ShouldUseSoftware`

**命令行 `--embed-nod3d` 仍然有效，且优先级更高**（等价于 `software`）。
用户遇到假死可自行切软渲染自救，不用等新版本。

---

## 4. 踩过的坑（务必读，别再犯）

### 4.1 看门狗第一版完全静默 —— 判据 bug

第一版 `Probe()` 只靠 `GetLastError()` 判断，实测**一声不响**。真实现象：

```
同一个卡死窗口：第一次探测  rc=0  err=1460 (ERROR_TIMEOUT)
               之后几次      rc=0  err=0        ← 被别的线程的成功 Win32 调用清掉了
```

`GetLastError` 只在**本次调用真的失败时**才被写，进程里其它线程的成功调用会清掉它。
只看 err 会把后续卡死**全判成"活着"**。

**正确判据**：

```csharp
if (rc != 0) return true;              // 一定成功
if (err == ErrorTimeout) return false; // 明确超时
return !IsHungAppWindow(_hwnd);        // err 不可信时兜底；窗口已销毁 = 正在关，不报
```

> 这个 bug 只有靠**端到端实测**才能发现。看代码完全看不出问题。

### 4.2 线程状态解析的偏移量（x64）

`NtQuerySystemInformation(SystemProcessInformation)`：

- 进程块内线程数组起始偏移 = **256**
- `SYSTEM_THREAD_INFORMATION` 大小 = **80**
- 字段偏移：KernelTime 0 / UserTime 8 / CreateTime 16 / WaitTime 24 / StartAddress 32 /
  ClientId 40 / Priority 48 / **ContextSwitches 64 / ThreadState 68 / WaitReason 72**

**校验方法**：拿已知状态的线程对跑一遍（`ManualResetEvent.WaitOne`、`Thread.Sleep`、
持锁线程），对不上就是偏移错了。别信第一直觉。

### 4.3 `GetThreadWaitChain` 在本机不可用

对**跨进程的提权目标**一律返回 `err=87 (ERROR_INVALID_PARAMETER)`；对自己进程在
nodeCount ≤ 16 时可用（`WCT_MAX_NODE_COUNT = 16`，传 32/64 会直接 err=87）。
所以别依赖它取等待链，用 `NtQuerySystemInformation` 的线程表更稳。

### 4.4 提权进程拿不到诊断信息

主控端 manifest 是 `requireAdministrator`，非提权 shell 下：

- `dotnet-dump collect -p <pid>` → `拒绝访问 (5)`
- 诊断管道 `dotnet-diagnostic-<pid>` 连接 → `Access denied`
- `Stop-Process` / `taskkill` → `拒绝访问`

**下次要做栈级取证，必须用管理员 PowerShell。**

### 4.5 PowerShell  heredoc 会吃掉中文

用 `@"..."@` 写含中文的 C# 补丁会被编码破坏。**改用 Python 脚本落盘**（`io.open(..., encoding="utf-8")`），
并且写回时显式 `newline=""` 以免把 CRLF 改成 LF。

> 本仓库源码是 **CRLF**。改完务必核对：`git diff --stat` 应只有预期的增删行数，
> 若出现整文件重写就是行尾被改了。

---

## 5. 仍未解决 / 待验证

1. **精确等待对象未取得**（最关键）。下次复现时用**管理员** PowerShell 抓：
   ```powershell
   # 提权 shell
   & "C:\Users\AITNR\.workbuddy\binaries\node\versions\22.22.2-6\.dotnet\tools\dotnet-dump.exe" collect -p <pid> -o hang.dmp
   ```
   或装 ProcDump 抓内核态栈。**这一项才能最终区分 §2.4 的 1 与 2。**
2. **看门狗尚未在真机长时间运行验证**（只在 harness 里验证过，见 §6）。
3. **卡死后能否自恢复**：看门狗只负责"留下证据"，不做任何恢复动作。
   若希望自动恢复（如自动切软渲染 / 自动断开重连），需要额外设计，且要非常克制 ——
   在 UI 线程卡死时从后台线程做重连，风险尚未评估。
4. **RdpIdd.dll 崩溃是否上游诱因**未证实。若反复出现，需单独查驱动侧。
5. **`RenderThreadAffinityCheck` 回归测试仍不存在**（§2.5）。

---

## 6. 如何验收

### 6.1 看门狗自身（可用 harness 复验，不依赖真机复现卡死）

建一个独立工程，`<Compile Include="...UiHangWatchdog.cs" />` **直接链接仓库里那一份源文件**
（测的不是副本），再桩一个 `CrashLog.WriteNote`。用一个真窗口 + 阻塞其消息泵来制造卡死：

```csharp
// UI 线程上自旋占住，不进消息循环
var until = DateTime.UtcNow.AddSeconds(30);
while (DateTime.UtcNow < until) { Thread.SpinWait(1000); Thread.Sleep(1); }
```

> ⚠️ **`Thread.Sleep` 造不出真卡死**：实测 WinForms 线程 `Thread.Sleep` 时
> `IsHungAppWindow=False`、SMTO 直接成功。必须自旋占住线程。

预期结果（已实测通过）：

| 场景 | 预期 |
| --- | --- |
| UI 线程阻塞 30 秒 | ~10 秒内检出（2 次探测），写卡死事件 + 全部线程状态表 |
| 阻塞后恢复 | 补写「已恢复（本次持续 0分20秒）」 |
| UI 线程仅阻塞 3 秒（短于 2×5s 间隔） | 零输出，不误报 |

### 6.2 改动整体（真机）

跑一轮：2 通道 + 内嵌 1920×1080 + 双任务，≥30 分钟。验收点：

- `crash.log` 里**不应**出现看门狗记录（正常运行时它静默）；
- 每分钟「仍在运行」日志连续无缺行；
- `Responding` 始终 True；
- 点「断开」后界面**立刻**松开（2.2 的效果，原来最坏卡 5 秒）。

### 6.3 渲染后端开关（3.2，同时是 D3D 因果链的对照组）

把「内嵌画面渲染后端」切到**软渲染**跑一轮：

- 若软渲染下不再卡死 → **坐实 D3D 在因果链上**，接着做 §7 的 3.1；
- 若仍卡死 → 说明问题不在 D3D 初始化，重点转向 §2.3 的其它阻塞点与驱动侧。

---

## 7. 下一步（按优先级）

| # | 内容 | 风险 | 说明 |
| --- | --- | --- | --- |
| 1 | **提权复现 + 抓栈** | 无 | 唯一能定论的手段。见 §5.1 |
| 2 | 2.3：`RunHidden` / `RunPowerShell` 的同步等待移出 UI 线程 | 中 | 要改调用方签名（`CheckReadiness` 在 UI 线程上调，`MainViewModel.cs:4376`） |
| 3 | 3.1：拆分 `RdpD3DRenderer.InitializeLocked` | 中 | `D3D11CreateDevice` / `CreateSwapChainForComposition` / `CreateStaging` 不需要 UI 线程；只有 `SetSwapChain`（L142）必须。把设备创建移出 UI 线程，**UI 线程上只剩一次 SetSwapChain** |
| 4 | 3.3：D3D 初始化加超时 | 中 | `SetSwapChain` 放独立线程 + 超时兜底，超时即回退软渲染（现在无限等） |
| 5 | 补 `RenderThreadAffinityCheck` 回归测试 | 低 | `RdpD3DRenderer.cs` 注释里承诺过，见 §2.5 |
| 6 | 评估「卡死自动恢复」 | 高 | 见 §5.3，需谨慎设计 |

**第 1 项应优先做** —— 它决定 2/3/4 是否值得投入。

---

## 8. 关键代码位置速查

| 功能 | 位置 |
| --- | --- |
| UI 看门狗（新增） | `Services/UiHangWatchdog.cs` |
| 看门狗挂载 | `App.xaml.cs` `OnLaunched`（`ViewModel.Initialize()` 之后） |
| 黑屏看门狗（已改） | `ViewModels/MainViewModel.cs` `StartEmbedWatchdog` |
| 异步断开（已改） | `ViewModels/MainViewModel.cs` `DisposeSurface` |
| 渲染后端配置 | `Models/RdpModels.cs` `RenderBackends` / `RdpConfig.RenderBackend` |
| 渲染后端开关 | `Views/ChannelsPage.xaml`「内嵌画面渲染后端」下拉框 |
| 后端判定 | `Services/RdpD3DRenderer.cs` `ShouldUseSoftware` / `PushRenderBackend` |
| 入口同步配置 | `Views/RdpView.xaml.cs` `Connect` / `AttachClient` |
| 帧拷贝（阻塞点） | `Services/RdpEmbeddedClient.cs` `TryCopyFrame` → `YukinoChan.RdpNative/ycn_rdp.c` `ycn_rdp_copy_frame` |
| 原生断开 join（阻塞点） | `YukinoChan.RdpNative/ycn_rdp.c:2204` |
| 进程等待（阻塞点） | `Services/RdpSessionService.cs:1889/1938` |

---

## 9. 本次现场的数据备忘

| 项 | 值 |
| --- | --- |
| 冻结实例 PID | 59404（14:03:14 启动，Session 1，已由用户手动结束） |
| UI 线程 tid | 52888 |
| 原生 RDP 线程 | 60340 / 66116（冻结期间仍在转） |
| 应用日志 | `C:\Program Files\YukinoChan\logs\2026-10-12(2).log`（21 行，止于 14:03:19） |
| 成功对照日志 | `2026-10-11(4).log`（608998 字节，176 条「仍在运行」） |
| 崩溃日志 | `C:\Program Files\YukinoChan\logs\crash.log`（干净，无本次记录） |
| RdpIdd.dll 崩溃 | 14:10:47 / 14:11:04，`RdpIdd.dll` 2.35.0 |
| 安装版本 | `2.0.0-dev.35+c9fc87a8312bf2aa2c7affdebc64eba62e4b2381`（含 `0c8fc6f`） |
---

## 10. 第二次故障（2026-10-15）：又是假死，但看门狗漏报了

### 10.1 现场

| 项 | 值 |
| --- | --- |
| 冻结实例 PID | **104756**（08:47:28 启动，Session 1） |
| UI 线程 tid | 122604 |
| UI 线程等待原因 | **`WrLpcReply`** |
| 117 个线程 | UI 1 个 `WrLpcReply`，**其余 116 个全部 `Suspended`** |
| UI 线程 ctxSw | `71423 → 71423`（10 秒零增长），CPU 零增长 |
| `SendMessageTimeout(5000ms)` | 超时 5013ms |
| 渲染后端 | **software**（日志确认 `SoftwareBitmap（主动选择的软渲染）`） |
| 结局 | 假死约 1 分钟后**崩溃**：`Microsoft.UI.Xaml.dll` 3.1.8 / `0xC0000005` @ `0x28c9f0` |

`WrLpcReply` + 116 线程全 Suspended + CPU 零增长 —— **与 `0c8fc6f` 修的「D3D 驱动内部
互锁 → 整窗假死」是同一签名**。但本次走的是**软渲染**，说明根因**不在渲染后端**。

### 10.2 看门狗为什么没报（第三次判据 bug）

现场实测：`rc=0`（失败）但 **`err=0`**，且 **`IsHungAppWindow=False`**。
而当时的判据是：

```csharp
if (rc != 0) return true;
if (err == ErrorTimeout) return false;   // err=0，不成立
return !IsHungAppWindow(_hwnd);          // false → 返回 true（判定「活着」）
```

→ 看门狗沉默。

### 10.3 三场景实测，确立唯一可靠判据

用 WinForms + STA 线程自旋占住，实测三种场景：

| 场景 | `rc` | `err` | `IsWindow` |
| --- | --- | --- | --- |
| A 窗口活着、UI 卡死 | **0** | 1460 **或** 0 | **True** |
| B 窗口已销毁 | 0 | 1400 | **False** |
| C 健康 | **1** | 0 | True |

**结论**：

- `rc` 区分「响应了没」—— 可靠；
- `IsWindow` 区分「窗口还在没」—— 可靠；
- `err` **完全不可靠**：同一卡死过程中会出现 1460 / 0 / **1400** 三种值，毫无规律；
- `IsHungAppWindow` **不可靠**：UI 线程卡在 `WrLpcReply` 时它返回 false。

### 10.4 最终判据

```csharp
if (rc != 0) return true;        // 响应了 = 活着
return !IsWindow(_hwnd);         // 没响应：窗口还在 = 卡死；窗口没了 = 正在关，不报
```

### 10.5 端到端复验（本次已做）

| 场景 | 结果 |
| --- | --- |
| UI 阻塞 25 秒 | ✅ ~10 秒检出（2 次探测），写卡死事件 + 14 个线程状态表 |
| 阻塞后恢复 | ✅ 补写「已恢复（本次持续 0分15秒）」 |
| UI 仅阻塞 3 秒 | ✅ 零输出，不误报 |

harness 做法：独立工程 `<Compile Include="...UiHangWatchdog.cs" />` 直接链接仓库源文件，
桩一个 `CrashLog.WriteNote`；UI 线程用 `Thread.SpinWait` + `Thread.Sleep(1)` 自旋占住
（**`Thread.Sleep` 造不出真卡死**，见 §4.5）。

### 10.6 本次未能取得的关键证据

**活体 dump 没抓到。** 冻结进程在 08:52 前已被手动结束；WER 报告目录
（`AppCrash_YukinoChan.exe_f7a03f53...`）拒绝访问，本机**未配置 `WER\LocalDumps`**，
`C:\Windows\Minidump` 无当天记录。

→ 所以「到底哪个 LPC 调用」仍未定位。

### 10.7 下一步

1. **配置 WER LocalDumps**（一次性，之后每次崩溃都自动落盘，不再依赖人工抓）：
   ```powershell
   # 管理员
   New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps' -Force | Out-Null
   New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps' `
     -Name DumpFolder -Value 'D:\dumps' -PropertyType ExpandString -Force | Out-Null
   New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps' `
     -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null   # 2 = full dump
   New-Item -ItemType Directory -Path 'D:\dumps' -Force | Out-Null
   ```
2. 复现后用 `dotnet-dump analyze` 看 UI 线程栈（提权 shell）。
3. 若确认仍是 D3D/合成器互锁，再做 §7 的 3.1（拆分 `InitializeLocked`）与 3.3（初始化超时）。

### 10.8 与第一次故障的关系

两次都是「UI 线程同步等待 → 消息泵停摆」，但：

- 10-12：D3D 路线，**先假死后崩溃**（`coreclr.dll` / `0xC0000005` @ `0xe8cce`）；
- 10-15：**软渲染**路线，同样先假死后崩溃（`Microsoft.UI.Xaml.dll` / `0xC0000005` @ `0x28c9f0`）。

**渲染后端不是根因**，存在共性的上层原因。日志两次都停在「等会话代理上线」阶段附近，
下一步应重点查那条链路（会话建立 / 等代理 / RDP 轮询）上有没有 UI 线程上的同步等待。

---

---

## 11. 第三次故障（2026-10-16）：dump 抓到真凶 —— 软渲染每帧 new SoftwareBitmap

### 11.1 现场

| 项 | 值 |
| --- | --- |
| 冻结实例 PID | **93904**（10:26:48 启动） |
| UI 线程 tid | 120156 |
| 渲染后端 | **software**（软渲染） |
| 画面 | **先正常 70 秒**（10:27:03–10:28:08，呈现 18–26 帧/秒），随后彻底静默 |
| 76 个线程 | 68 个 `Waiting/UserRequest`（含 UI 线程，全静止）+ 5 个 `WrQueue`（在动） |
| `Responding` | False |

### 11.2 抓到 dump（本次最大进展）

用提权 `Start-Process -Verb RunAs` 跑 `dotnet-dump collect`，得到 1.46GB 全量 dump
（`D:\dumps\hang-1016.dmp`）。此前三次都因为进程被提前结束 / WER 目录拒绝访问 /
未配 LocalDumps 而一无所获。

### 11.3 根因：UI 线程堵死在 `GC.AddMemoryPressure`

`clrstack` 顶层帧：

```
Program.Main
 -> Microsoft.UI.Xaml.Application.Start
  -> DispatcherQueueTimer.Tick              (16ms 呈现节拍器)
   -> RdpView.RenderFrameAsync
    -> SoftwareBitmap.CreateCopyFromBuffer   (RdpView.xaml.cs @ 656)
     -> SoftwareBitmap..ctor
      -> System.GC._AddMemoryPressure        <-- 卡死在这里
```

内存证据（`eeheap -gc` + `dumpheap`）：

- **LOH 16.7MB，只含 2 个 `System.Byte[]`，各 8,294,424 字节**
  —— 正是两路内嵌通道各自的 `_frameBuffer`（1920×1080×4）；
- GC 总堆仅 19.3MB，**LOH 占 86%**。

### 11.4 机理

1. `RenderFrameAsync` 每帧 `SoftwareBitmap.CreateCopyFromBuffer(...)`，**每秒新建约 24 个 8.3MB WinRT 对象**；
2. .NET 的 WinRT 投影在 `SoftwareBitmap` 构造时调 `GC.AddMemoryPressure(8.3MB)`
   （这块内存不受 GC 直接管理，只能靠压力通知催它回收）；
3. 于是每秒约 **200MB** 内存压力涌进 GC，累积到阈值后 `AddMemoryPressure` 内部强制完整 GC；
4. GC 又要处理 LOH 上的帧缓冲与大量 WinRT 包装对象；
5. **UI 线程堵在 `AddMemoryPressure` 里出不来** → 消息泵停摆 → 68 个线程一起冻结。

这也解释了此前所有想不通的现象：

- 为什么画面**先正常 70 秒**再卡（压力累积到临界点）；
- 为什么**软渲染和 D3D 都卡**（共性在呈现路径，不在具体后端）；
- 为什么**看门狗没报**（它不是不想报 —— 低优先级后台线程被同一个冻结拖住，跑不起来）；
- 为什么**无崩溃日志**（没抛异常，是纯阻塞）。

### 11.5 修复：复用 SoftwareBitmap，不要每帧 new

`RdpView.RenderFrameAsync` 改为：bitmap 尺寸不变就复用同一实例，每帧只
`CopyFromBuffer` 原地更新内容；仅尺寸变化（远端改分辨率）才重建。
内存压力从「每帧 8.3MB」降到「每连接一次 8.3MB」。

同时在 `DetachClient` / `Disconnect` 两处补上 `_softBitmap.Dispose()`
（它持 8.3MB 非 GC 内存，不复用就泄漏）。

### 11.6 修复有效性（已量化验证）

独立 harness 复现两种写法，各跑 10 秒 / 60Hz：

| 写法 | 帧数 | gen0 | gen1 | gen2 |
| --- | --- | --- | --- | --- |
| 旧（每帧 new） | 319 | **79** | **79** | **79** |
| 新（复用 bitmap） | 314 | **0** | **0** | **0** |

旧写法每帧都触发一次**完整 GC**（gen0+gen1+gen2 同步 +1），10 秒 79 次 ≈ 每秒 8 次；
新写法零 GC。这就是 UI 线程堵死的直接原因。

### 11.7 仍未解决

1. **看门狗在真实冻结下仍然不会报**（§10.5 的 harness 只覆盖了「UI 卡但进程健康」，
   真实场景是 68 个线程一起卡，低优先级后台看门狗线程得不到调度）。
   → 要可靠告警必须把看门狗**移到进程外**（独立 exe）。尚未实现。
2. D3D 路线那次（10-15）崩在 `coreclr.dll` / `Microsoft.UI.Xaml.dll`，
   根因未定位；但本次说明呈现路径有共性问题，D3D 那条也可能与内存/对象生命周期相关。
3. **真机验证未做**：修复只通过了「GC 次数对比」的间接验证，
   尚未在实际内嵌软渲染长跑中确认假死消失。

---

---

## 12. 外置看门狗（2026-10-16）：进程内监视不可靠，改成独立进程

### 12.1 为什么必须外置

§10.5 的 harness 验证曾让进程内看门狗「看起来能用」，但 §11 的真实故障证明了它的局限：

> 真实冻结是 **68 个线程一起卡住**，连 `ThreadPriority.BelowNormal` 的后台看门狗线程
> 都得不到调度 —— 看门狗自己也跑不起来，于是什么都没记下。

harness 只覆盖了「UI 卡但进程健康」，真实场景是整进程冻结。**这是两类不同的情况**，
前者能过不代表后者能过。三次漏报（10-12 / 10-15 / 10-16）都是这个原因。

### 12.2 新工程

`winui3/src/YukinoChan.Watchdog/` —— 独立进程，自带 `asInvoker` manifest。

**为什么必须是独立工程，而不是主程序的 `--watchdog` 参数入口**：
主程序 `app.manifest` 是 `requireAdministrator`（编译期静态、没法按参数豁免），
共用同一个 exe 会让看门狗每次启动都弹 UAC。agent 当年是靠 `mt.exe` 就地改写
副本 manifest 才绕开的，成本高且只此一例。

组成：

| 文件 | 作用 |
| --- | --- |
| `HangProbe.cs` | 探测 + 线程快照（`NtQuerySystemInformation`） |
| `Program.cs` | 常驻循环、日志、抓 dump、目标查找 |
| `YukinoChan.Watchdog.csproj` | 自包含 + `win-x64` |
| `app.manifest` | `asInvoker` |

用法：

```powershell
YukinoChan.Watchdog.exe                # 常驻监视
YukinoChan.Watchdog.exe --once         # 自检：探测一次并打印
YukinoChan.Watchdog.exe --pid 12345    # 只监视指定 PID（调试）
```

参数：探测间隔 3s、单次超时 3s、连续 2 次判定、假死 90s 后抓 dump、dump 冷却 10 分钟。

### 12.3 探测判据

与进程内版最终一致（§10.4）：`rc != 0` 即活着；否则看 `IsWindow` ——
窗口还在就是假死，窗口没了说明应用正在关。
**不看 `GetLastError`（会出现 1460/0/1400，无规律）、不看 `IsHungAppWindow`（WrLpcReply 时返回 false）。**

### 12.4 抓 dump：不用 createdump，用 MiniDumpWriteDump

第一版用 `createdump.exe`（.NET 运行时自带），实测失败：

```
[createdump] The pid argument is no longer supported
```

新版 .NET 的 createdump **只能抓自己**，传 pid 直接被拒。改用 Win32 `MiniDumpWriteDump`，
能抓任意进程。代价是 dump 里没有托管堆，但**线程栈、模块、句把都在** ——
足以判断「UI 线程卡在哪个调用」，那正是本看门狗要的证据。
若还需托管堆（如看对象分配），用提权的 `dotnet-dump collect` 补抓（它走诊断管道，需要权限）。

### 12.5 又一个实测才发现的坑：目录定位不能按进程名

`ResolveTargetDir()` 最初用 `Process.GetProcessesByName("YukinoChan")` 取第一个，
结果 dump 写进了 `C:\ProgramData\YukinoChan\agent\crashdumps` ——
机器上同时有主控端和每个目标账户一个的 `--rdp-agent`，进程名都一样，
而 agent 的 PID 往往更小。已改为**必须按被监视的 PID** 取目录。

### 12.6 端到端验证（本次已做）

用一个进程名同样叫 `YukinoChan` 的假死目标（真窗口 + STA 线程自旋占住）：

| 用例 | 结果 |
| --- | --- |
| `--once` 探测健康主控端 | ✅ `Alive`，exit=0 |
| `--once --pid <假死>` | ✅ `Hung` + 11 个线程快照，exit=1 |
| 常驻模式 | ✅ 6 秒检出 → 写事件 + 线程表 → 60 秒补报 → 恢复有记录 |
| 抓 dump | ✅ 154MB 落在被监视进程的 `crashdumps/` |

常驻模式实测日志：

```
11:42:25  外置 UI 看门狗启动（pid=114808，间隔 3s，超时 3000ms，连续 2 次判定，假死 90s 后抓 dump）
11:42:31  UI 线程无响应（假死）：连续 2 次探测失败，pid=77200。…
11:42:32    进程 11 个线程：tid=126772 Waiting DelayExecution ctxSw=1092 …
11:42:45    已抓 dump（线程栈级）：…crashdumps\hang-20261016-114244-77200.dmp（147 MB）
11:43:21  UI 线程无响应已恢复（本次持续 0分49秒，pid=77200）
```

### 12.7 已知限制

1. **主控端是管理员进程，看门狗默认 asInvoker，`OpenProcess` 可能被拒**（err=5）。
   此时 dump 抓不到，但日志与线程快照仍会写（那才是主要价值）。
   要 dump 就把看门狗也跑成管理员，或手动用提权 `dotnet-dump collect` 补抓。
   日志里会写明是哪一种。
2. **未做开机自启/常驻部署**：当前只构建出 exe，需要你决定怎么跑
   （计划任务 / 开机启动项 / 手动）。
3. **未在真实主控端上验证**：本次用的是假死目标。真机上主控端是提权进程，
   `OpenProcess` 的行为与 dump 能否落地都还没实测。

---
