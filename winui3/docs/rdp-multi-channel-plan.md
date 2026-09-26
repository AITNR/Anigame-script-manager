# 雪乃酱 · 多会话通道并行执行 计划书

- **版本**：草案 v1（2026-09-26）
- **状态**：⚠️ 仅为规划文档，未动工。所有代码改动以本计划书评审通过为前提。
- **适用范围**：`repo/winui3/` 下的 WinUI 3 工程（`YukinoChan.App`），含原生工程 `YukinoChan.RdpNative`
- **前置**：内嵌 RDP（FreeRDP）已完成并真机可用，见 `rdp-embed-freerdp-plan.md`

### 已拍板的四项决策（2026-09-26 与 AITNR 确认）

| #   | 决策点        | 结论                                                            |
| --- | ----------- | ------------------------------------------------------------- |
| D1  | 并行形态       | **混合**：每个通道自填「主机 + 账户」，多机 / 多账户由用户自己组合，不预设形态            |
| D2  | 用户配置方式     | **新增「会话通道」列表**：通道里配主机/账户/凭据/分辨率/收尾，任务详情里选通道                |
| D3  | 菜单项生命周期    | **开始执行后新增，结束后保留**（可切回去看画面与结果）；停止执行、未启用任务的通道移除              |
| D4  | 原「远程会话」页   | **改造为「会话通道」管理/预检页**（通道 CRUD、凭据、部署代理、预检），画面搬到各通道自己的页面        |
| D5  | 并发上限        | **直接把原生 `YCN_MAX_SESSIONS` 从 4 提到 8**，M0 内改宏 + 重编 `ycn_rdp.dll`         |
| D6  | 未分配通道的任务   | **保留「本地执行」**，且把它做成任务页下拉里的**显式选项**（并作为新建任务的默认值）                |
| D7  | 同主机多通道（R1） | **不做额外限制**：不禁止、不排队，只在预检里留一行提示                                  |
| D8  | 多画面同屏       | **要做**，列入 M6（菜单切换之后的增强）：网格并列 + 弹出独立窗口                            |

---

## 1. 目标与非目标

### 1.1 目标

1. **通道化**：把「一个全局 RDP 目标」拆成 N 个「会话通道」，每个通道 = 主机 + 账户 + 凭据 + 桥目录 + 收尾策略。
2. **任务绑定通道**：任务详情里选通道（或选「本地执行」），开始执行时按通道分组下发，**跨通道并行、通道内串行**。
3. **菜单化**：开始执行后左侧菜单为每个在跑的通道新增一项，点进去是该通道自己的内嵌 RDP 画面 + 进度/心跳/事件；原「远程会话」页不再承载画面。
4. **并行可用**：同屏切换查看，互不干扰；每个通道独立完成判定、通知、收尾各算各的。

### 1.2 非目标

- 本轮核心是「菜单切换 + 单画面」；**多画面同屏已拍板要做（D8）**，列为 M6，不再是"非目标"。
- 不做协议层/执行层改动：剪贴板重定向、磁盘重定向等 FreeRDP 通道能力另开一轮。
- 不改 Agent 的任务执行语义（超时、看门狗、并发组、统计口径一律不动）。
- 不动 Python 原版 `repo/ui`（已由 WinUI 版取代）。
- 不破解 Windows 客户端版的单会话限制（见 §3.3 R1）。

---

## 2. 现状盘点：要动的单点

改动之所以不小，是因为当前整条 RDP 链路是**全局单例**设计的。逐个点名：

| 位置                                        | 现状                                                                                    | 本轮改造                                        |
| ----------------------------------------- | ------------------------------------------------------------------------------------- | ------------------------------------------- |
| `Services/RdpBridge.cs`                   | **静态类**：`BridgeDir` / `CommandPath` / `StatusPath` / `LastError` 全是 static，全局只有一座桥 | **实例类**：每通道一个 `RdpBridge`（§5）               |
| `Services/RdpCredentialStore.cs`          | 凭据键 = `YukinoChan/RDP/<host>`，同一主机多账户**必然互相覆盖**                                      | 键改为 `host + "\|" + user`，旧键兼容读取（§6）         |
| `ViewModels/MainViewModel.cs`             | 单通道字段：`_embedClient`、`_rdpCurrentCommandId`、`_lastRdpEventSeq`、`RdpProgress`… 一堆    | 下沉为每通道一个 `RdpChannelSession`（§7）            |
| `MainViewModel.PollRdpStatus`             | 一个 1Hz 定时器读一座桥                                                                        | 每通道一个定时器（或统一节拍循环遍历所有活跃通道）                   |
| `MainViewModel.ConnectAndRunAsync` (1995) | 拿全局 `RdpSettings` 走一遍：写指令 → 连接 → 等会话 → 等代理 → 开轮询                                     | 抽成 `RdpChannelSession.RunAsync()`，N 个并行跑     |
| `RdpSessionService.WriteSessionFile`      | `.rdp` 落在**固定路径** `SessionFilePath`，并发会互相覆盖                                          | 按通道 id 生成不同文件名（§3.3 R3）                     |
| `RdpSessionService.Connect`               | mstsc 模式，窗口会接管控制台 → 当前桌面被锁                                                           | 并行通道**强制内嵌**（embedded）；mstsc 保留给单通道调试        |
| `Views/RdpPage.xaml(.cs)`                 | 一个页面：预检 + 凭据 + 部署 + 内嵌画面，全混在一起                                                       | 拆成 `ChannelsPage`（管理/预检）+ `RdpChannelPage`（画面） |
| `Views/MainWindow.xaml`                   | 菜单是**写死**的 5 项（`home`/`tasks`/`stats`/`logs`/`rdp`）                                   | 运行时按活跃通道动态插入（§8.1）                          |
| `Models/RdpModels.cs` `RdpConfig`         | 只有一组 `target_host` / `target_user` / `bridge_path`                                    | 新增 `Channels` 列表；全局那组降级为「默认模板 + 旧配置迁移源」      |
| `Models/TaskConfig.cs`                    | 没有「用哪个用户」的概念                                                                          | 新增 `channel_id`（§4.1）                        |
| `MainViewModel.Stop/EmergencyStop` (588)  | 只调 `_runner?.RequestStop()`，**远程通道根本没有停止手段**（点了没反应）                                    | 新增桥文件 `stop.json` 让 Agent 响应停止（§7.4）         |

**可复用的既有资产（不动）**：`RdpHeartbeat.Evaluate`（三段式判定，纯函数）、`RdpAgentRunner`（事件写入/收尾）、`RdpEventKinds` 完成事件契约、`RdpSessionService` 的会话枚举/注销/`WaitForAgentOnline`、`RdpEmbeddedClient`（**每实例一条会话**）。

---

## 3. 核心概念：会话通道（Channel）

### 3.1 定义

> **会话通道** = 一条「主控端 ↔ 目标会话」的完整通道：目标主机 + 目标账户 + 凭据 + 指令桥目录 + 画面连接实例 + 运行状态。

```text
                      ┌──────────── 主控端（雪乃酱，账户 A）────────────┐
                      │                                              │
  任务列表 ──分组──►  │  Ch-1  主机 127.0.0.1 / Player2 ──内嵌画面──┐  │
                      │  Ch-2  主机 192.168.1.20 / GamePC\P1 ─────┐ │  │
                      │  Ch-3  主机 10.0.0.7 / Server\Bot3 ─────┐ │ │  │
                      └─────────────────────────────────────────┼─┼─┼──┘
                        command.json / status.json / stop.json  │ │ │
                        （每通道各一份，目录隔离）                  │ │ │
                                                                ▼ ▼ ▼
                                                        各自的目标会话 + 常驻代理
```

### 3.2 执行语义

- **通道内**：按任务 `order` 串行 —— 与现状 `ScriptRunner` 完全一致，零改动。
- **通道间**：并行 —— 每条通道各自下发 `command.json`、各自等代理、各自轮询。
- **未分配通道的任务**（`channel_id` 为空）：走原来的**本地执行**路径（当前会话的 `ScriptRunner`），与远程通道并存。
- **引用了不存在的通道 id**：本轮**不执行**并给出原因（宁可不跑，也不猜着跑到别处）—— 比静默改跑本地更安全。
- **RDP 总开关关闭**（`rdp.enabled = false`）：全部本地执行，行为与今天一致。

### 3.3 硬限制（必须先讲清，否则"并行"是空话）

| 编号 | 限制                                                                                                                     | 对策                                                                                                          |
| -- | ---------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| R1 | **Windows 客户端版同一台机器同时只允许一个交互式会话**：本机开第二个账户的远程连接会顶掉/锁掉第一个（这是服务端 TermService 的限制，换 FreeRDP 客户端也绕不过） | 本机多账户并行在**普通客户端版**上不可行；系统允许多会话（Server / 已放开多会话）时可真并行。按 D7 **不做额外限制**：不禁止、不排队、不自动排队串行，只在预检里留一行提示"同一主机多账户可能互相顶掉"，出问题由用户自己判断。<br>⚠️ **2026-09-27 用户确认：其系统支持多用户多会话** —— R1 不再是本项目的天花板，文档与界面文案均按「取决于系统是否支持多会话」表述 |
| R2 | **原生 DLL 并发上限 `YCN_MAX_SESSIONS = 4`**（`ycn_rdp.c:45`，已是多会话设计）                                                        | **按 D5 直接提到 8**：M0 改宏 + 重编 DLL；C# 侧上限常量同步为 8（`RdpNativeLimits.MaxSessions`），`PlanLaunches` 超过 8 才截断并提示                |
| R3 | mstsc 模式：窗口接管控制台（当前桌面被锁）+ `.rdp` 文件固定路径                                                                              | 并行通道一律走 **embedded**；mstsc 仅保留给「只跑一个通道」的调试场景，且 `.rdp` 文件名带通道 id 避免覆盖                                      |
| R4 | **跨账户文件权限**：桥目录要被主控端账户与目标账户读写                                                                                        | 沿用 `RdpBridge` 现有的 `BUILTIN\Users` 显式放行逻辑（每通道目录建好就放行一次）                                                  |
| R5 | 代理只在**目标账户登录那一刻**由启动项拉起（公共启动目录快捷方式，对所有用户生效但只触发一次）                                                                 | 现状不变；多通道只要求**每个账户各自登录过一次**。桥目录靠用户名自动区分（§5.3），所以一个快捷方式服务所有账户                                        |

---

## 4. 配置模型

### 4.1 config.json schema（全 snake_case，与既有约定一致）

```jsonc
{
  "rdp": {
    "enabled": true,
    "client_mode": "embedded",        // 全局默认连接方式
    "audio_enabled": true,
    "gfx_enabled": false,
    "connect_timeout_seconds": 120,
    "notify_on_task_done": true,
    "notify_on_all_done": true,

    // ↓ 旧字段保留（迁移源 + 新建通道时的默认值）：target_host / target_user /
    //   bridge_path / credential_saved / session_finish / desktop_width / desktop_height

    "channels": [
      {
        "id": "ch-7f3a",              // 稳定 id，任务引用它
        "name": "1 号机 · Player2",    // 菜单项文案
        "host": "127.0.0.1",
        "user": "Player2",
        "bridge_path": "",            // 留空 = <ProgramData>\YukinoChan\rdp\<user>
        "credential_saved": true,
        "session_finish": "keep",     // keep / disconnect / logoff（按通道）
        "desktop_width": 1280,
        "desktop_height": 720,
        "enabled": true
      }
    ]
  },
  "tasks": [
    { "name": "原神", "channel_id": "ch-7f3a", "order": 1, "...": "…" },
    { "name": "星铁", "channel_id": "",        "order": 2 }   // 空 = 本地执行
  ]
}
```

### 4.2 迁移策略（旧配置不能丢）

`AppConfig.Sanitize()` 里加一次**幂等迁移**：

1. `rdp.channels` 缺失或为空，且旧 `rdp.target_user` 非空 → 用旧字段生成**一个默认通道**（`id` 取 `default`、name 取 `目标账户 target_user`），`credential_saved` / `session_finish` / 分辨率一并继承。
2. 所有任务的 `channel_id` 为空且 RDP 总开关开着 → 指向该默认通道（保住"原来能跑"的行为）。
3. 旧桥目录 `<ProgramData>\YukinoChan\rdp\` 下若已有 `status.json`，迁移到 `rdp\<user>\`（并提示"代理需重新部署一次"，因为旧快捷方式的默认目录规则变了 —— 见 §5.3）。
4. 迁移只写内存 + 下次保存落盘，不做破坏性删除。

### 4.3 可单测的纯函数（进 `_smoke`）

新增 `Models/RdpChannelPlanner.cs`（不引 WinUI，与 `WindowPlacement` 同规格）：

```csharp
public static class RdpChannelPlanner
{
    // 分组 + 启动计划一次算完：channel_id 为空 → 本地；找不到 / 通道停用 / 没配账户 / 超上限
    //                      → ToLaunch 之外的 Skipped，并带原因（绝不静默改跑本地）
    public static ExecutionPlan Plan(
        IEnumerable<TaskConfig> tasks, IReadOnlyList<RdpChannel> channels,
        bool rdpEnabled, int maxParallel = RdpNativeLimits.MaxSessions);
}
```

新增 `Models/RdpChannelPaths.cs`：

```csharp
public static class RdpChannelPaths
{
    public static string Slug(string? userName);                       // 用户名 → 目录名（去非法字符、小写）
    public static string BridgeDirFor(RdpChannel ch, string baseDir);  // 显式 bridge_path 优先，否则 base/<slug(user)>
    public static string AgentBridgeDir(string baseDir, string userName); // Agent 侧：base/<slug(自己)>
}
```

冒烟用例（跟 `_smoke/WindowPlacementCheck.cs` 一个路子）：分组/空组/未知 channel_id/并发截断/用户名 sanitize/往返序列化/迁移幂等。

---

## 5. 指令桥多实例化（本轮最大的一处重构）

### 5.1 现状

`RdpBridge` 是静态类，`BridgeDir` 一个进程内只有一份。多通道必须每通道一份。

### 5.2 新形态

```csharp
public sealed class RdpBridge
{
    public RdpBridge(string dir) { BridgeDir = dir; }
    public string BridgeDir { get; }
    public string CommandPath => Path.Combine(BridgeDir, "command.json");
    public string StatusPath  => Path.Combine(BridgeDir, "status.json");
    public string StopPath    => Path.Combine(BridgeDir, "stop.json");   // §7.4 新增
    public bool WriteCommand(RdpCommand c);
    public void  ResetStatus(string commandId);
    public RdpStatus? TryReadStatus();
    public bool WriteStop(string commandId, bool emergency);
    public static RdpBridge For(RdpChannel ch);   // 带缓存，按通道 id
}
```

- 全部 static 成员改成实例成员（写入原子替换、读取重试逻辑**原样搬**，不动语义）。
- `RdpSessionService` / `MainViewModel` 里所有 `RdpBridge.Xxx()` 调用点改成 `bridge.Xxx()`（调用点共约 10 处，逐处核对）。
- **权限**：`EnsureDirectory()` 里对每通道目录做一次 `BUILTIN\Users` 放行（沿用现有 `ApplyUsersAcl` 逻辑）。

### 5.3 Agent 侧目录规则：按用户名派生（关键设计）

公共启动目录的快捷方式**只有一个**、对所有用户共用，参数固定 —— 所以不可能靠快捷方式给每个账户传不同的 `--bridge:`。

**规则**：桥目录默认 = `<ProgramData>\YukinoChan\rdp\<用户名 slug>\`
- Agent 启动时：`RdpBridge.Configure(ExtractBridgePath(args) ?? AgentBridgeDir(base, Environment.UserName))`；
- 主控端：`RdpChannelPaths.BridgeDirFor(channel, base)` 用同一个 `user` 算 —— **两边天然对齐，零配置**；
- 远程场景（对方是另一台机器）：通道里显式填 UNC 共享目录，走 `--bridge:` 老路径，规则不变；
- 改动点：`RdpBridge.DefaultBridgeDir()`（现在没有用户段）+ `App.xaml.cs:49` 的 Agent 启动配置。

> 迁移副作用：旧部署的 Agent（旧规则 = 无用户段的根目录）与新主控端会**读不到同一个目录** → 升级后提示「重新部署一次会话代理」，一键按钮现成（`DeployAgent`）。

---

## 6. 凭据：按「主机 + 用户」存

`RdpCredentialStore` 的 target 从 `YukinoChan/RDP/<host>` 改为 `YukinoChan/RDP/<host>|<user>`：

- `Write(host, user, pwd)` / `Read(host, user)` / `Delete(host, user)` / `Exists(host, user)`；
- **兼容**：新 key 读不到时回落到旧 key（旧条目只记 host），读到即算命中，下次保存时写新 key 并删旧 key；
- 调用点：`RdpSessionService.SaveCredential/TryLoadPassword/HasCredential/DeleteCredential`（385/425/434）、`MainViewModel.TryStartEmbeddedConnect`（1362）、`RdpPage` 自检通道（104）。

通道页为每个通道单独提供「保存凭据 / 清除」按钮 —— 预检状态也按通道算。

---

## 7. 运行时：RdpChannelSession

### 7.1 职责

把 `MainViewModel` 里那一堆单通道字段，整体搬进一个新类 `Services/RdpChannelSession.cs`：

```csharp
public sealed class RdpChannelSession : IDisposable
{
    public RdpChannel Channel { get; }
    public RdpBridge Bridge { get; }
    public RdpEmbeddedClient? EmbedClient { get; }        // 内嵌画面实例（每通道一个）

    public string Phase { get; }                          // idle/connecting/running/done/error
    public int Progress, Total, ElapsedSeconds;
    public string CurrentTask, StatusText, HeartbeatText;
    public bool IsStale, IsCompleted;
    public ObservableCollection<RdpTaskEvent> Events { get; }

    public Task RunAsync(IReadOnlyList<TaskConfig> tasks, CancellationToken ct);  // 下发→连接→等代理→轮询
    public void RequestStop(bool emergency);              // §7.4
    public void Disconnect();                             // 断开画面（会话保留）
    public void Logoff();                                 // 注销目标账户（带运行中确认）
    public event Action? EmbedClientChanged;              // 页面接管/交还画面
    public event Action<RdpChannelSession>? Completed;    // 供 VM 汇总
}
```

**从 `MainViewModel` 迁进去的字段**（逐个核对，别漏）：`_embedClient`、`_embedLastHost`、`_embedRetryCount`、`_embedAutoReconnects`、`_embedUserDisconnect`、`_embedWatchdog`、`_rdpCurrentCommandId`、`_rdpCommandIssuedAt`、`_rdpLastHeartbeat`、`_rdpStaleReported`、`_rdpAwaitingAgentLogged`、`_rdpCompletionReported`、`_lastRdpEventSeq`、`RdpPhase/RdpProgress/...`。

`MainViewModel` 保留：**一个** `ObservableCollection<RdpChannelSession> ActiveSessions` + 汇总属性（总进度、是否全部完成）+ 菜单同步。

### 7.2 并行启动流程（`Start()` 改造）

```csharp
public void Start()
{
    SaveConfig();
    if (!RdpSettings.Enabled) { /* 本地执行，现状不变 */ return; }

    var plan = RdpChannelPlanner.GroupByChannel(Tasks, RdpSettings.Channels, rdpEnabled: true);
    foreach (var g in plan.Where(g => g.IsLocal)) { /* 本地执行那部分，走现有 _runner */ }

    var launch = RdpChannelPlanner.PlanLaunches(plan.Remote, RdpNativeLimits.MaxSessions /* =8，与原生 YCN_MAX_SESSIONS 对齐 */);
    if (launch.Skipped.Count > 0) AppendLog($"以下通道本轮未启动（{原因}）：…");

    foreach (var g in launch.ToLaunch)
    {
        var session = new RdpChannelSession(g.Channel);
        ActiveSessions.Add(session);          // → 触发菜单项新增（§8.1）
        _ = session.RunAsync(g.Tasks, _cts.Token);   // 并行，不 await
    }
}
```

- 预检（凭据 / 账户存在 / 远程桌面开启 / 版本支持 / 远程桥目录）**按通道各做一遍**，阻塞项汇总成一条对话框一次性告知，而不是弹 N 次。
- 内嵌连接：`TryStartEmbeddedConnect` 逻辑（含黑屏自愈重试 + 看门狗）原样搬进 session，按通道独立计数。

### 7.3 轮询 / 心跳 / 完成判定

- 保留**一个** 1Hz 定时器，循环遍历所有活跃 session 调各自的 `Poll()` —— 比每通道一个定时器更好控（暂停/停止/退出一处收口）。
- **完成判定契约必须原样继承**（这是踩过坑的地方）：`sawFinished`（本指令的 `Finished` 事件）**OR** 终态相位，循环外单点 `ReportCompletion`，`_rdpCompletionReported` 保证只收尾一次；`ResetStatus` 每次下发都写全新 `RdpStatus` 且清空 `Events` + `_lastRdpEventSeq = 0`。
- 心跳：`RdpHeartbeat.Evaluate` 是纯函数，按 session 各算一次，`expectedCommandId` 用**本 session** 的 command id（不要跨通道混用，否则归属校验会失效）。

### 7.4 停止 / 紧急停止（补全现状缺口）

现状 `Stop()` 只作用于本地 `ScriptRunner`，远程通道点了没反应。新增：

- 桥目录新增 `stop.json`：`{"command_id":"…","emergency":false}`（原子写）。
- Agent 侧（`RdpAgentRunner`）：在每秒的 elapsed/progress 回调点检查一次该文件，命中且 `command_id` 匹配 → `runner.RequestStop()` / `RequestEmergencyStop()`，随后删掉 `stop.json` 防止重复触发。
- 主控端：`Stop()` → 所有活跃 session 写 stop + 移除菜单项；`EmergencyStop()` → 同上且 `emergency=true`。
- 兼容性：老 Agent（不认识 stop.json）跑着的场景，写文件无害，只是不响应 —— 日志里明说"目标端代理版本不支持远程停止"。

### 7.5 收尾

`session_finish` 按通道生效（keep / disconnect / logoff），由 Agent 按指令执行（现状如此，只是从全局改成通道字段）。
全部通道完成后：汇总通知（列出每条通道的结果）、刷新统计、看板娘状态、日志；菜单项**保留**，页面显示"已结束 + 耗时 + 断开/注销"按钮。

---

## 8. UI

### 8.1 动态菜单（`MainWindow`）

- 菜单结构：`总览 / 任务执行 / 耗时统计 / 运行日志 / 会话通道 / 设置`
  - 静态项「会话通道」（`Tag = "channels"`，原 `rdp`）进去是**通道管理 / 预检页**；
  - 该静态项之后动态插入一个 `NavigationViewItemHeader`「**执行中的通道**」，其下按 `VM.Sessions` 动态增删 `NavigationViewItem`；
  - `Tag = "ch:<id>"`，`Content = 通道名`，`Icon = People`。（分组标题不叫「会话通道」以免与静态项重名，见附录 B 偏差 8。）
- `NavigateTo(tag)`：`ch:` 前缀 → `ContentFrame.Navigate(typeof(Views.RdpChannelPage), channelId)`。
- 页面用 `OnNavigatedTo(e)` 取参数 → 绑定对应 session（**连接归 session 持有，页面只 attach/detach 画面**，沿用 RdpPage 已验证的"切页不断连"做法）。
- 移除时机：停止执行 / 紧急停止 / 该通道被禁用 / 窗口关闭。移除前先 detach 画面；`Closed` 里遍历释放全部 session。
- `SyncNavigationSelection` 现有实现遍历 `MenuItems` / `FooterMenuItems`，动态项在 `MenuItems` 内，无需改；但要注意**先插项再选中**。

### 8.2 `Views/RdpChannelPage.xaml(.cs)`（新）

从 `RdpPage` 拆出「画面 + 该通道状态」：

- 复用 `RdpView` 画面控件（全屏、键盘接管、帧率状态栏原样搬）；
- 顶部：通道名 / 目标（主机 · 账户）/ 桥目录 / 连接状态；
- 进度区：当前任务、n/N、耗时、**心跳**（失联告警）、事件列表（该通道自己的）；
- 按钮：「连接预览」（无会话时文案为「连接到这条通道」，复用 `ConnectChannelSurface`）/「打开桥目录」；
  画面右上角浮层：「全屏」/「断开画面」（断开只释放内嵌连接，目标会话与代理保留）。
- **布局：画面优先** —— 画面卡占满窗口剩余空间，事件卡固定底部、可折叠。
- 全屏：`RdpView` 只负责发 `FullScreenToggleRequested`（双击画面 / F11），
  真正的编排（全屏窗口 + `WH_KEYBOARD_LL` 键盘接管 + 宿主渲染挂起）收在
  `RdpFullScreenCoordinator`，**通道页与管理页共用同一份**。
- 自检参数 `--embed-vm host user pwd` 保留，自动导航到「会话通道」页并在那里挂画面（见 `MainWindow` 的 `EmbedVmArgs` 分支）。

### 8.3 `Views/ChannelsPage.xaml(.cs)`（原 RdpPage 改造，D4）

**保留**：远程桌面开关、连接方式、音频/GFX、通知开关、连接超时、**部署/移除会话代理、打开桥目录、预检信息条**（这些是"管理/预检"职能）。
**搬走**：内嵌画面卡片 → `RdpChannelPage`。
**新增**：通道列表（增/删/改名）+ 通道编辑（主机、账户、桥目录、分辨率、收尾方式、保存/清除凭据）+ 每通道的预检结果。
菜单项文案改「会话通道」，`tag` 由 `rdp` 改 `channels`（`MainViewModel.RequestNavigation` 同步改）。

### 8.4 任务页（D2）

`TasksPage.xaml`「基础设置」里加一行：

```xml
<ComboBox x:Name="ChannelBox" Header="执行通道" ItemsSource="{x:Bind VM.ChannelChoices}"
          SelectedValuePath="Id" DisplayMemberPath="Name"
          SelectedValue="{Binding ChannelId, Mode=TwoWay}" />
```

`VM.ChannelChoices` = `[ChannelChoice("", "本地执行（当前会话）")] + 各通道 ChannelChoice(id, name)`。
（落地时用 `Models/RdpChannel.cs` 里的 `ChannelChoice { Id, Name }` 类而非 `KeyValuePair` —— `x:Bind` 对泛型元组成员的绑定不友好，且 `Id` 空串天然表达"本地执行"。）

**D6 明确的两条语义**：

- `channel_id = ""` → 该任务跑在**主控端当前会话**的 `ScriptRunner` 里（现状行为），不与任何通道交互；
- 「本地执行」是下拉里的**显式第一项**，且是**新建任务的默认值** —— 避免"忘了选通道就悄悄跑远程"；
- 左侧任务列表项模板里也显示通道名（一眼看出任务归谁）。

### 8.5 其它（M5 已落地）

- `HomePage`：**已加**「会话通道」概览卡 —— 一行一条通道（状态符号 / 名称 / 目标 / 进度 / 状态 / 心跳），
  跑过任务后才出现（`VM.HasChannelOverview`），点一行跳到那条通道的画面页。
  数据源是 `VM.ChannelOverview`（`ChannelOverviewItem` 快照集合），页面按 1Hz 重建 ——
  `RdpChannelSession` 的属性不是可观察的，重建快照最省事又足够便宜（最多 8 条）。
- 通知：**已实现**按通道分别弹「任务完成」；全部结束后**一条汇总**
  （`RunSummary.Title` + `ComposeHeadline`，正文形如 `共 3 块（本地 1 / 通道 2）：成功 2、异常 1。`）。
  单块（只有本地、或只有一条通道）**不发汇总** —— 与按块反馈完全重复。
- 看板娘：**取最差**由 `FinalizeRunSummary()` 在**整轮结束**时统一决定 ——
  各块收尾只往 `_runParts` 登记结果。异常在块收尾时**立刻**报（Error 是粘性状态，
  后续正常块不会顶掉它），正常则保持 Work 直到整轮结束。
  这条修正很重要：原先各块完成就 `SetMascotState(Rest, force: true)`，
  两条通道并行时 A 跑完就会让看板娘"休息"（而 B 还在跑）。
- 日志：**已统一**走 `AppendChannelLog(session, msg)` → `[通道：<展示名>] …`（VM 里 24 处）；
  整轮汇总另有 `[汇总] ` 前缀的行。
- 同主机多通道提示：通道预检里给出 `ChannelSameHostHint`（D5：只提示不禁用）。
- 并发上限截断：`Start()` 里对 `Reason` 含"并发上限"的跳过项单独写一条醒目日志 + 弹一次说明对话框
  （这是唯一一种"看起来该跑、其实没跑"的截断，且用户自己能解决）。
- 状态面板（看板娘浮动卡 `MascotPanel` + 主页状态卡）：**已统一**绑 `VM.Panel*` 四个计算属性，
  口径见附录 B 第 13 条的偏差说明 —— 远程与并行执行下面板必须有值，不能只有本地执行能显示。

---

## 9. 里程碑

| 里程碑  | 内容                                                                                | 产出/验收                                                   |
| ---- | --------------------------------------------------------------------------------- | -------------------------------------------------------- |
| M0   | **原生并发上限 4→8**（`YCN_MAX_SESSIONS` 改宏 + 重编 DLL，先探工具链）+ 配置模型：`RdpChannel`、`TaskConfig.channel_id`、`AppConfig.Sanitize` 迁移；纯函数 `RdpChannelPlanner` / `RdpChannelPaths` | `dotnet build` 通过；`_smoke` 新增 `RdpChannelCheck.cs`（分组/截断/目录/迁移）全绿；重编 DLL 同步到 App 输出目录与代理副本目录 |
| M1   | 桥实例化：`RdpBridge` 改实例类 + 全部调用点改造 + Agent 侧按用户名取目录；`stop.json` 读写与 Agent 响应          | 编译通过；旧单通道流程行为不变（回归）                                      |
| M2   | 凭据按 host+user（含旧键兼容）；`RdpSessionService` 内嵌/mstsc 调用支持多通道（`.rdp` 文件名带 id）          | 两个不同账户凭据互不覆盖（凭据管理器里能看到两条）                                |
| M3   | `RdpChannelSession` 落地：把 VM 的单通道字段整体搬进去；`Start()` 按通道并行启动、`Stop/EmergencyStop` 全覆盖 | 编译通过；单通道回归行为不变                                           |
| M4   | UI：动态菜单 + `RdpChannelPage` + `ChannelsPage` 改造 + 任务页通道下拉（含「本地执行」显式项）              | 开始执行后菜单出现 N 项；切换页面画面不断连；停止后菜单项移除                        |
| M5   | 收尾/通知/日志/看板娘/主页概览；并发上限（8）截断提示与"同主机多通道"提示行                                     | 各通道独立收尾；汇总通知列出各通道结果                                      |
| M6   | **多画面同屏（D8）**：通道页「单画面 / 多画面」切换（网格 2×2，每页 4 格、超出分页）+ 通道「弹出独立窗口」按钮      | 两个及以上通道画面可同时看到、都能操作                                      |
| M7   | 真机验收（≥2 台机器或本机多账户多会话）+ 文档更新（`README` / `USER_GUIDE` / `CHANGELOG`）              | 见 §10；**文档部分 ✅ 已完成（2026-09-27），真机验收待用户执行** |

### M6 多画面同屏的实现要点（D8）✅ 已落地

- **网格模式**：`RdpChannelPage` 顶部加「单画面 / 多画面」切换；多画面时把全部活跃通道的 `RdpView` 并列成网格，每格标通道名与状态。
- **渲染纪律**：本页用不到的格位一律解挂（不占渲染）；每页**最多 4 格**（2×2）—— 4 路是有意的同时渲染上限，再多 CPU/GPU 扛不住（计划书本身的 §M6 风险项），超出分页。
- **弹出独立窗口**：每格一个「弹出窗口」按钮，复用 `RdpFullScreenWindow` 已验证的「新窗口新建 `RdpView` 接管同一 client」机制（去掉全屏与键盘钩子即普通窗口），用户自己并排摆放 —— 零新机制、最稳的同屏路径。
- 两者共用一条约束：**一个 client 同一时刻只挂一个 `RdpView`**（attach/detach）。

**实施记录（与上面要点的差异，均为有意为之）**

1. **前提是「内嵌连接按通道分家」**：原本 VM 只持有一个 `_embedClient`（单例），两条通道共用会互相打断画面。M6 把它改成按通道 id 索引的 `Dictionary<string, EmbedConnection>`：client + 上次连接参数 + 黑屏自愈重试计数 + 看门狗 + 自动重连计数 + 「主动断开」抑制位，**每通道一份**。Key 用空串表示设置页那个不属于任何通道的入口。
   - 对外新增：`GetSurfaceClient(channelId)` / `HasSurface` / `SurfaceChannelIds` / `DisposeSurface(channelId)`；`DisposeEmbedClient()` 保留为「断开全部」。
   - 日志统一走 `AppendSurfaceLog(key, msg)` —— 通道内嵌带 `[通道：X]` 前缀，设置页入口只有 `[内嵌]`。
2. **`RdpSurfaceCell` 独立成控件**：一度画面 = `RdpView` + 顶部「通道名 / 状态 / 全屏·弹出·断开」条 + 无画面时的占位。通道页只做**摆放**（1 格或 2×2），不再自己管画面。好处是每格的按钮与占位文案只写一遍。
3. **每页 4 格而非 8 格**：原生并发上限是 8，但"8 路同时渲染"是不可接受的开销。分页（每页 4 格）天然满足"不多于 4 路同时渲染"，比"仅活动通道渲染"更好懂、也不用猜哪一路是活动的。
4. **画面弹出期间页面内该格让位**：顺序是「先让页面 `SetClient(null)` 解挂 → 再让新窗口 `AttachClient`」，关窗时反向归还。反了会出现两个渲染器挂同一条会话（画面互抢、CPU 翻倍）。
5. **多画面模式下不给「全屏」**：全屏会盖掉别的路，语义冲突；多画面里双击某格 = 把这路弹成独立窗口。单画面模式保留全屏（键盘接管那套）。
6. **`ConnectChannelSurface` 支持"还没跑过任务"**：`_sessions` 在开跑前是空的，原来会报「找不到通道」；现在回退到 `Config.Rdp.Channels` 查配置，所以通道页上「连接本通道」在开跑之前也能用。

---

## 10. 验收清单

**沙箱内（可自动化）**
1. `dotnet build -c Release -p:Platform=x64` 零警告零错误；
2. `cd _smoke && dotnet run -c Release` 全绿（含新增 `RdpChannelCheck`）；
3. 旧 config.json（无 `channels`）加载后生成默认通道、任务自动指向它、再保存再加载结果一致（幂等）。

**真机（需 AITNR 操作）**
4. **并行（已确认支持多会话的环境）**：本机两个不同账户各配一条通道（或两台机器各一条），任务分两组 → 开始执行后菜单出现两项，画面分别可见，进度各自推进；
5. 切换菜单项时画面**不断连**；全屏 + 键盘接管在通道页内正常；
6. 停止执行 → 两端脚本都停下（验证 `stop.json`）；紧急停止同理；
7. 一端会话被注销 → **只有那一个通道**报失联，另一通道不受影响；
8. 全部完成 → 汇总通知列出两条通道结果，菜单项保留且显示"已结束"；
9. **多画面（M6）**：开 3 个通道 → 网格模式下三路画面同时可见、都能操作；「弹出独立窗口」后并排摆放正常；
10. **并发上限 8（M0）**：重编后的 `ycn_rdp.dll` 能同时建立多条内嵌会话不报错（真机至少验 3 路，8 路尽力）。

---

## 11. 风险与对策

| 风险                                                    | 影响                       | 对策                                                                |
| ----------------------------------------------------- | ------------------------ | ----------------------------------------------------------------- |
| **R1 单会话限制**：普通客户端版上本机多账户并行跑不起来                      | 功能在"本机多账户"场景不成立（仅限普通客户端版）         | 按 D7 不额外限制：预检留一行提示 + 文档写明；系统支持多会话时本机即可并行（**用户环境已确认支持**），否则用多机。不阻断启动               |
| **R2 并发上限 / 重编原生 DLL**                                | 通道多了启动不了；重编产物没同步会"连不上" | 上限按 D5 提到 8；重编后必须把新 `ycn_rdp.dll` 同步到 **App 输出目录 + 会话代理副本目录**（ProgramData 下那份），否则两端版本不一致；M0 加一条"DLL 路径/版本自检日志"    |
| **桥实例化的改造面**：`RdpBridge` 静态成员被 ~10 处调用                | 漏改一处 → 通道串数据（最危险的 bug）  | M1 逐个调用点核对；冒烟里加"两个 bridge 实例路径不同"断言；日志打印每通道桥目录                   |
| **Agent 升级不同步**：旧 Agent 用旧目录规则                       | 看起来"代理不上线"              | 预检里比对 Agent 版本/心跳；提示一键重新部署                                        |
| **多 `RdpView` 实例**（每页面一个 D3D 交换链）                     | 渲染资源/显存占用               | 一次只挂载一个（Frame 单页面），切页 detach；若发现问题可改为**共享单个 RdpView 控件**按页面挂载      |
| **`stop.json` 老 Agent 不响应**                           | 停止按钮看起来无效               | 日志明示版本不支持；文档要求两端同时升级                                              |
| 菜单动态项与 `NavigationView` 选中态竞态                         | 选中错项/页面不切换              | 统一 `NavigateTo(tag)` 单一出口；插项与选中顺序固定（先插后选）                         |
| **多画面渲染开销（M6）**                                      | N 路 D3D 交换链同时渲染 → CPU/GPU 翻倍、掉帧 | 不可见画面一律 `SuspendRendering` / 解挂；网格每页 4 格（分页）保证同时渲染 ≤4 路；多画面下不提供全屏；提供一键退回单画面 |

---

## 12. 待确认 / 开放问题

1. ~~并发上限是否提到 8？~~ → **已拍板 D5：M0 直接改宏 + 重编。**
2. ~~未分配通道的任务怎么算？~~ → **已拍板 D6：本地执行，且做成下拉里的显式选项 + 新建任务默认值。**
3. ~~同主机多通道要不要禁止？~~ → **已拍板 D7：不禁止、不排队，只留一行预检提示。**
4. ~~多画面同屏要不要做？~~ → **已拍板 D8：做，列 M6（网格并列 + 弹出独立窗口）。**
5. **仍未定**：`config.json` 目前仍随代码提交（历史遗留）；通道配置属于运行状态，是否顺手 `git rm --cached` + ignore？
6. ~~重编 DLL 需要工具链，环境具备吗？~~ → **已探明具备**（2026-09-26 实查本机）：
   - VS 2022 装在 `C:\Program Files (x86)\Microsoft Visual Studio\2022`；
   - CMake 用 VS 自带那份：`C:\Program Files (x86)\Microsoft Visual Studio\2022\<SKU>\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`；
   - `YukinoChan.RdpNative/build/CMakeFiles/3.31.6-msvc6` 表明此前正是用它编的（CMake 3.31.6 + MSVC）。
   
   → **M0 直接改宏重编即可**，无需降级方案。重编后记得把 `ycn_rdp.dll` 同步到 App 输出目录与会话代理副本目录（见 §11 R2）。

---

## 附录 A：本轮新增/改动文件清单（✅ 表示已落地）

```text
新增
  Models/RdpChannel.cs              通道模型（含 Sanitize / Clone / ChannelChoice）✅
  Models/RdpChannelPlanner.cs       分组 + 启动计划（纯函数，可单测）✅
  Models/RdpChannelPaths.cs         桥目录规则（纯函数，可单测）✅
  Models/RunSummary.cs              整轮汇总与「取最差」（纯函数，可单测）✅
  Models/ChannelOverviewItem.cs     主页通道概览的快照行 ✅
  Services/RdpChannelSession.cs     每通道运行时（指令归属/进度/事件/心跳/收尾幂等）✅
  Views/RdpChannelPage.xaml(.cs)    通道画面页（单/多画面切换 + 2×2 网格 + 事件）✅
  Views/RdpSurfaceCell.xaml(.cs)    一格画面（RdpView + 通道名/状态 + 全屏·弹出·断开）✅
  Views/RdpFullScreenCoordinator.cs 全屏编排（全屏窗口 + 键盘接管 + 宿主挂起），两页共用 ✅
  Views/RdpChannelWindow.cs         M6：通道独立窗口（复用「新窗口接管同一 client」，去全屏与钩子）✅
  Views/ChannelsPage.xaml(.cs)      通道管理/预检页（替代原 RdpPage，D4）✅
  _smoke/RdpChannelCheck.cs         冒烟断言 ✅
  _smoke/RdpChannelSessionCheck.cs  冒烟断言（通道判定链，35 项）✅
  _smoke/AgentDeployCheck.cs        冒烟断言（部署占用探测，8 项）✅
  _smoke/RunSummaryCheck.cs         冒烟断言（整轮汇总 / 取最差，24 项）✅
  _smoke/RdpChannelSessionCheck.cs  含「本地组镜像」段（面板在远程/并行下有值）✅

改造
  YukinoChan.RdpNative/ycn_rdp.c    YCN_MAX_SESSIONS 4 → 8（M0，需重编并同步 DLL）✅
  Services/RdpBridge.cs             静态 → 实例 + stop.json ✅
  Services/RdpCredentialStore.cs    键改为 host|user（旧键兼容）✅
  Services/RdpSessionService.cs     调用点传 bridge 实例；.rdp 文件名带通道 id；部署三档策略 ✅
  Services/RdpAgentRunner.cs        响应 stop.json ✅
  Models/TaskConfig.cs              + channel_id、ChannelDisplay（[JsonIgnore]）✅
  Models/RdpModels.cs               RdpConfig + Channels ✅
  Models/AppConfig.cs               迁移 + Sanitize ✅
  ViewModels/MainViewModel.cs       单通道字段 → RdpChannelSession 集合；Start/Stop 并行化；通道 CRUD ✅
  Views/MainWindow.xaml(.cs)        动态菜单 + ch: 前缀导航；rdp → channels ✅
  Views/RdpPage.xaml(.cs)           ❌ 已删除（拆成 ChannelsPage + RdpChannelPage）✅
  Views/TasksPage.xaml              执行通道下拉 + 列表通道名 ✅
  Views/HomePage.xaml               通道概览卡（1Hz 快照重建 + 点击跳转）；状态卡改绑 Panel* ✅
  Views/MascotPanel.xaml            看板娘浮动卡四项改绑 Panel*（原先只跟本地执行走）✅
  App.xaml.cs                       Agent 侧默认桥目录按用户名 ✅
```

---

## 附录 B：实施进度

### 已完成（2026-09-26，全部构建通过 + 冒烟全绿）

| 里程碑 | 状态 | 落地内容与关键决定 |
| --- | --- | --- |
| M0 | ✅ | ① 原生 `YCN_MAX_SESSIONS` 4 → **8**（`ycn_rdp.c`），用 VS 自带 CMake 重编 `ycn_rdp.dll`，由 csproj 的 `CopyToOutputDirectory` 自动带进输出目录；② `Models/RdpChannel.cs`（通道模型）、`Models/RdpChannelPaths.cs`（桥目录规则）、`Models/RdpChannelPlanner.cs`（分组 + 启动计划 + `RdpNativeLimits.MaxSessions=8`）；③ `TaskConfig.channel_id`、`RdpConfig.channels`、`AppConfig.MigrateLegacyChannel()` 幂等迁移；④ `_smoke/RdpChannelCheck.cs` 34 项断言 |
| M1 | ✅ | `RdpBridge` 静态类 → **实例类**（`For(dir)` / `ForChannel(channel)` / `Agent` 缓存工厂）；默认桥目录 = `RdpChannelPaths.AgentBridgeDir(Environment.UserName)`；全部调用点改造（`MainViewModel` / `RdpSessionService` / `RdpAgentRunner` / `App.xaml.cs` / `_smoke`） |
| M1.5 | ✅ | `stop.json` 停止通道：`RdpBridge.WriteStop/TryReadStop/IsStopRequested/ClearStop`、`RdpStopRequest` 模型；Agent 在每秒计时回调里检查并把请求转成 `ScriptRunner.RequestStop/RequestEmergencyStop`；主控端 `Stop()` / `EmergencyStop()` 同时下发 |
| M2 | ✅ | 凭据键 `YukinoChan/RDP/<host>` → **`<host>\|<账户>`**（账户名去域前缀 + 小写归一化）；旧键回落读取时校验条目里的账户名，不匹配就不认；`SaveCredential/DeleteCredential/TryLoadPassword/HasCredential/ResolvePassword` 全部带上账户参数；冒烟新增"同主机两账户互不覆盖 / 按账户删不影响另一个"断言 |
| M3 | ✅ | ① **`Services/RdpChannelSession.cs`（新，纯逻辑可单测）**：把 VM 里那套单通道字段（指令归属 / 事件序号 / 进度 / 心跳 / 收尾幂等标志）整体下沉成**每通道一份**，判定链收进 `Tick()`，返回 `RdpChannelTick`（新事件 / 完成信号 / 异常 / 失联边沿）由 VM 落成日志与通知；② `Start()` 改为 **`RdpChannelPlanner.Plan()` 驱动**：本地组 + 各通道并行启动（`RunChannelsAsync` → `Task.WhenAll`），未启动的逐条说明原因；③ `FinishRunPart()` 用 `_pendingRuns` 计数（本地算一块、每条通道各算一块），**整轮结束**才停轮询 / 刷统计 / 放行"完成后自动退出"；④ `ConnectAndRunAsync` 收敛为"用设置页配置合成伪通道 → 走同一条 `RunChannelAsync`"，单通道行为回归不变；⑤ `PollRdpStatus` 逐通道读取判定，`Stop/EmergencyStop` 向**全部**通道下发 stop.json；⑥ `.rdp` 文件名按通道 id 分隔（`session-<id>.rdp`），并发不再互相覆盖；⑦ 冒烟新增 `_smoke/RdpChannelSessionCheck.cs`（35 项：归属 / 事件去重 / 完成 / 失联恢复 / Begin 复位 / 多通道隔离 / 停止归属） |
| M4 | ✅ | ① **`Views/ChannelsPage.xaml(.cs)`（新，替代原 `RdpPage`，D4）**：保留全局开关 / 连接方式 / 分辨率 / 音频 / GFX / 超时 / 部署代理 / 移除代理 / 打开桥目录 / 预检条，新增**通道列表**（`ListView` 绑 `VM.Channels`，选中回写 `SelectedChannel`）+ **通道编辑卡**（名称 / 主机 / 账户 / 桥目录 / 分辨率 / 收尾方式 / 启用 / 保存·清除凭据）+ 每通道预检结果；文本字段**失焦即落盘**（`OnChannelHostLostFocus` → `PersistChannelEdits`），下拉类改动 `OnChannelFieldCommitted` 即时落盘；② **`Views/RdpChannelPage.xaml(.cs)`**：通道头（名称 / 目标 / 桥目录 + 连接预览 / 打开桥目录）+ 该通道进度条与心跳 + `RdpView` 画面（或占位说明）+ 该通道事件列表；`OnNavigatedTo` 按参数绑定 session，`SyncEmbedView()` 只在"本通道是内嵌宿主"且未挂同一实例时 `AttachClient`，`OnNavigatedFrom` 只 `DetachClient`（切页不断会话）；③ **`MainWindow` 动态菜单**：静态项文案「远程会话」→「会话通道」、`tag` `rdp` → `channels`；`SessionsChanged` 事件驱动 `RebuildChannelMenuItems()`，在 `channels` 项后插 `NavigationViewItemHeader`「执行中的通道」+ 各 `ch:<id>` 项；`NavigateTo` 加 `ch:` 分支 → `RdpChannelPage(channelId)`；④ **任务页**：详情「基础设置」加「执行通道」`ComboBox`（`SelectedValuePath="Id"`），左侧任务项模板加 `{x:Bind ChannelDisplay}` 行；⑤ **VM**：`Channels` 镜像集合 / `SelectedChannel` / `HasSelectedChannel` / `ChannelReadinessText` / `ReloadChannels()` / `AddChannel·DuplicateChannel·RemoveChannel` / `SaveChannelCredential(password)` / `ClearChannelCredential` / `DeployChannelAgent` / `OpenChannelBridgeFolder` / `PersistChannelEdits`；`SessionsChanged` 事件 + `RaiseSessionsChanged()`；⑥ **模型**：新增 `ChannelChoice`，`TaskConfig.ChannelDisplay`（`[JsonIgnore]`，运行期由 `RefreshChannelChoices()` 反查填充）；⑦ 冒烟新增 8 项 `[M4]` 源码级断言（菜单 tag / 导航分支 / 通道列表与增删复制 / 每通道预检 / 失焦落盘 / 通道页按通道绑定 / 管理页不抢画面 / 任务页下拉与列表通道名） |
| M5 | ✅ | ① **`Models/RunSummary.cs`（新，纯函数可单测）**：`RunPartResult`（一块执行的收尾结果）+ `HasAnyAbnormal` / `SuccessCount` / `AbnormalCount` / `ComposeHeadline`（一行式，形如 `共 3 块（本地 1 / 通道 2）：成功 2、异常 1。`）/ `ComposeLines`（多行明细，**异常块排前面**）/ `ComposeMascotText`；② **看板娘"取最差"改成整轮结束才定**：`ReportSessionCompletion` 与 `OnRunnerFinished` 改为只往 `_runParts` 登记结果（异常仍立刻报 Error —— 粘性状态天然挡住后续正常块），`FinishRunPart` 归零时调 `FinalizeRunSummary()` 一次性定终态；③ **整轮汇总**：`[汇总]` 日志（标题行 + 逐块明细）+ 一条汇总通知（`NotifyOnAllDone` 时才发，走与按块通知同一套"锁屏落暂存、激活补发"），**单块不发**（与按块反馈重复）；④ **日志前缀统一**：新增 `AppendChannelLog(session, msg)`，VM 里 24 处 `[{tag}]` / `[{session.DisplayName}]` 全部归并成 `[通道：<展示名>]`；⑤ **主页通道概览**：`Models/ChannelOverviewItem.cs`（快照 record，含 `NavTag` / `StateGlyph`）+ `VM.ChannelOverview`（`ObservableCollection`）+ `VM.RefreshChannelOverview()` + `VM.HasChannelOverview`；`HomePage` 加概览卡（跑过任务才出现），页面 1Hz 重建快照、离开页面停表，点一行走 `ch:<id>` 跳转；⑥ **同主机多通道提示**（D5）：`DescribeSameHostConflict` → `VM.ChannelSameHostHint` / `HasSameHostHint`，管理页预检区一个 Warning InfoBar；按 **id** 比对（配置对象在 `ReloadChannels` 里会整体重建，引用比对会失效）；⑦ **并发上限截断显式点名**：`Start()` 对 `Reason` 含"并发上限"的跳过项单独写醒目日志 + 弹一次说明对话框；⑧ 冒烟新增 `_smoke/RunSummaryCheck.cs`（24 项）+ 12 项 `[M5]` 源码级断言 |
| M6 | ✅ | ① **内嵌连接按通道分家**（多画面的前提）：VM 的 `_embedClient` 单例 → `Dictionary<string, EmbedConnection>`（每通道一份：client + 上次连接参数 + 黑屏自愈重试 + 看门狗 + 自动重连计数 + 主动断开抑制位 + 「已弹出窗口」标记）；新增 `GetSurfaceClient` / `HasSurface` / `SurfaceChannelIds` / `DisposeSurface`；`DisposeEmbedClient()` 退化为"断开全部"；② **`Views/RdpSurfaceCell.xaml(.cs)`（新）**：一格画面 = `RdpView` + 顶部「通道名 / 状态 / 全屏·弹出窗口·断开」条 + 无画面占位（失焦式文案区分"未连接 / 已在独立窗口 / 非内嵌模式"）；③ **`Views/RdpChannelWindow.cs`（新）**：通道独立普通窗口（复用 `RdpFullScreenWindow` 的"新窗口新建 `RdpView` 接管同一 client"，去掉全屏与键盘钩子），默认 1024×640 可自由缩放并排；④ **`RdpChannelPage` 网格化**：顶部「单画面 / 多画面」切换，画面区固定 2×2 四格，单画面 = 本通道一格占满（保留全屏 + 键盘接管），多画面 = `VM.SurfaceGridChannelIds` 分页铺（每页 4 格，>4 出「上一页/下一页」），每格可弹成独立窗口；⑤ **渲染纪律**：本页用不到的格位一律 `SetClient(null)` 解挂；弹出窗口占用期间页面内该格让位（先解挂再交窗口，关窗反向归还）；⑥ `ConnectChannelSurface` 支持"还没跑过任务"（`_sessions` 空时回退到 `Config.Rdp.Channels` 查配置）；⑦ 冒烟新增 6 项 `[M6]` 源码级断言 |

**实现期与计划书的偏差（有意为之）**：

1. `channel_id` 指向**不存在的通道**时：不静默改跑本地，而是归入"本轮未启动"并给出原因 —— 静默本地执行可能把游戏重复拉起来，比不跑更糟。
2. M1 阶段 `MainViewModel` 还没有通道概念，用一个 `Bridge` 属性按「配置的桥目录 + 目标账户」解析；M3 已换成 `RdpBridge.ForChannel(session.Channel)`。
3. `RdpBridge` 的纯工具成员（`MakeEvent` / `TrimEvents` / `AppendEvent` / `DefaultBridgeDir`）保持静态 —— 它们无状态。
4. **M3 把判定链从 VM 抽进 `RdpChannelSession`（纯逻辑）**，于是原先那批"只能源码级断言"的回归锁（`PollRdpStatus` 是私有方法、依赖 WinUI 设施）**升级成了真正的运行期单测**；源码级断言保留，但改成锁"VM 与 Session 接得对不对"。
5. **顺带修掉一个真缺口**：改造前远程模式全程 `IsRunning = false`，顶栏「停止执行」是**灰的** —— M1.5 加的 `stop.json` 其实点不到。M3 起远程执行也置 `IsRunning`，停止按钮真正可用。
6. **内嵌画面已按通道分家（M6 完成）**：一个 `RdpEmbeddedClient` 同一时刻只能挂一个渲染视图，所以 M4 阶段只让第一条通道用内嵌、其余走独立窗口。M6 把 VM 的 `_embedClient` 单例改成 `Dictionary<string, EmbedConnection>`（每通道一份 client + 重试 + 看门狗 + 自动重连），多路画面才得以同时存在；同时删掉了「一次只支持一条通道」的提示与限制。
7. **M4 直接新建 `ChannelsPage` 并删掉旧 `RdpPage`**（而非原地改造改名）：`RdpPage.xaml(.cs)` 里画面/通道/全局设置三块内容混在一起，改造期间新旧两套配置界面并存会互相抢渲染视图，索性一次拆净；冒烟里 3 处 `RdpPage.xaml` 路径断言同步改指 `ChannelsPage.xaml`，并新增"旧 `RdpPage` 已不存在"的断言防回退。
8. **M4 动态分组标题用「执行中的通道」而非「会话通道」**：静态菜单项文案已改叫「会话通道」，分组若同名会出现两个重复条目，无法区分"配置入口"与"运行中的画面"。
9. **M4 通道编辑卡同时支持两种落盘时机**：文本框（名称/主机/账户/桥目录）走**失焦落盘**（`OnChannelHostLostFocus`），下拉/开关（分辨率/收尾/启用）走**即时落盘**（`OnChannelFieldCommitted`），另留一个「保存通道配置」按钮兜底 —— 因为没有"选中即保存"的天然时机，只靠按钮容易漏。
10. **M4 `ReloadChannels()` 重建 `Channels` 集合后按 `id` 复原选中项**：集合被 `Clear()` 重建后原引用已失效，直接沿用旧引用会让编辑卡显示空白。
11. **M4 补丁：全屏编排从管理页抽进 `RdpFullScreenCoordinator`，两页共用**。计划书 §8.3 只说了"管理页保留部署/预检、画面搬到通道页"，但**全屏那段逻辑跟着画面一起留在了管理页** —— 结果正常模式下画面在通道页，而处理器在管理页，双击 / F11 / 按钮**都没有入口能触发全屏**（用户实测："没有全屏选项，无法操作内部"）。抽成共用类后两边都能进，并加 `s_active` 静态守卫：两条通道页先后点全屏时先收掉前一个窗口，否则两个窗口会抢同一个 client（一个 client 只能挂一块画面）。
12. **M4 补丁：通道页改「画面优先」布局**。原事件卡写死 `Height="180"`，窗口稍矮就把画面挤成一条横带（用户实测："显示区域太小，什么都看不到"）。现在事件卡 `Height="Auto"` + ListView 固定 140 且可折叠（收起后画面多拿 140px），画面卡吃满窗口剩余空间，`RowDefinition` 保底 `MinHeight=220`。
13. **M6 每页 4 格而非 8 格**：原生并发上限是 8，但"8 路 D3D 交换链同时渲染"是不可接受的 CPU/GPU 开销。用分页（每页 4 格）替代计划书里的">4 路降级为仅活动通道渲染"—— 前者天然满足"同时渲染不多于 4 路"，也比"哪一路算活动的"更好懂。计划书 §8 表格里的"最多 8 路"据此改成"网格 2×2，每页 4 格"。
14. **M6 多画面模式下不提供「全屏」**：全屏会盖掉其它几路，与"同屏"语义冲突。多画面里双击某格 = 把这路弹成独立窗口；单画面模式保留全屏（含键盘接管）。
15. **M6 弹出窗口期间页面内该格让位**：顺序必须是「先让页面 `SetClient(null)` 解挂 → 再让新窗口 `AttachClient`」，关窗时反向归还。反了会出现两个渲染器挂同一条会话（画面互抢、CPU 翻倍）。
13. **M5 补丁：状态面板（看板娘浮动卡 + 主页状态卡）统一数据源**。原面板绑 `VM.StatusText` / `CurrentTaskName` / `ElapsedText` / `ProgressText` —— 这四个**只有本地执行的 `ScriptRunner` 回调会写**；M3 把远程通道的进度全下沉进 `RdpChannelSession` 后，它们再没被回灌过。于是"跑远程任务时面板一直显示 空闲 / 00:00 / - / 0 / 0"（用户实测反馈）。
    - VM 新增计算属性 `PanelStatusText` / `PanelCurrentTask` / `PanelElapsedText` / `PanelProgressText`，口径按"本轮有几块"取值：**单块 = 那一块**（观感与改造前一致）；**多块 = 整轮聚合**（状态 `执行中（N 块并行）`、耗时取整轮墙钟 `_runStartedAt`、进度取各块之和）；未跑过则回落到本地那套。
    - `RdpChannelSession.UpdateLocal(...)`：本地没有桥，进度由 `ScriptRunner` 回调灌进会话，让**面板 / 主页概览 / 整轮汇总只有一个数据源**。
    - 计算属性推断不出字段变化 → 新增 `RaisePanelChanged()` 显式通知四个属性名，在镜像、runner 回调、`IsRunning` 变化、整轮收尾处调用。
    - 顺带修掉：**只有远程通道（无本地任务）时看板娘整轮停在"待命中"** —— 原代码只在连接流程的中后段才 `SetMascotState(Work)`，没有任何一处覆盖"刚点开始"的那一段。

### ⚠️ 升级注意（真机必读）

桥目录规则变了：`<ProgramData>\YukinoChan\rdp\` → `<ProgramData>\YukinoChan\rdp\<账户名>\`。
**已部署会话代理的机器需要重新部署一次代理**（主控端的「部署会话代理」按钮），
否则新版主控端在新目录里读写，而旧代理还在旧目录里转 —— 表现就是"代理明明在跑，却一直显示不在线"。

### 部署会话代理的坑（2026-09-26 真机修复）

真机点「部署会话代理」失败，界面提示"需要管理员权限"—— **那是误报**。日志里的真实原因是：

```
复制会话代理程序失败：The process cannot access the file
'C:\ProgramData\YukinoChan\agent\CoreMessagingXP.dll' because it is being used by another process.
```

会话代理是**常驻进程**，持有着副本目录里的 DLL，`File.Copy(overwrite: true)` 直接被拒；
而 `MainViewModel` 把**任何**失败都追加一句"请以管理员身份重新启动雪乃酱"，把归因带偏。

**试过但走不通的方案**：把旧副本目录整体改名挪开再重建。
实测结论（已固化成冒烟断言）：**只要目录内有文件被持有句柄，`Directory.Move` 就返回
Access denied** —— `FileShare` 的 None / Read / Read+Delete / ReadWrite+Delete 四种模式全部被拒。
所以"挪开旧目录"这条路是死的，别回头再试。

**最终做法**（`RdpSessionService.EnsureAgentPayload`）：

1. 副本目录**没被占用** → 原地覆盖，路径不变；
2. 被占用 → 先 `StopAgentProcess` 把常驻旧代理送走，再原地覆盖。
   **只在它空闲时动手**：先读目标账户那路桥的 `status.json`，`Phase == "running"` 就直接拒绝部署，
   免得打断跑了一半的任务；
3. 送不走（跨会话杀不掉 / 权限不足）→ 退到**备用副本目录** `agent_b`，快捷方式改指新路径。
   旧代理继续用旧那份，两边互不干扰。

配套改动：

| 改动 | 作用 |
| --- | --- |
| `IsPayloadLocked(dir)` | 拿主程序 exe 试开一次独占写句柄探测占用（只探一个文件，对运行中代理干扰最小） |
| `IsElevated` / `IsOtherInstanceRunning()` | 失败时**准确归因**：是权限问题、还是旧实例还占着 |
| `RunPowerShell(script, out detail)` | 原来静默返回 false，失败原因全丢；现在带出 PowerShell 的 stderr / 退出码 |
| `DeployAgent(..., string? targetUser)` | VM 传 `RdpSettings.TargetUser`，用于判断代理是否在跑任务 |
| `_smoke/AgentDeployCheck.cs`（8 项） | 两槽不同、占用探测与现实一致（"探测说占用了"⇒"复制真的失败"）、空目录不误判 |

⚠️ 部署成功后旧代理已被结束，**要注销目标账户再重新连接**，启动项才会拉起新代理。

### M7 文档部分（2026-09-27 完成）

| 产物 | 内容 |
| --- | --- |
| `README.md` | 第七节整体重写为「会话通道」：能做什么 / 目标主机 / 工作原理（多通道桥 + 常驻代理 + 完成信号走事件）/ 前置条件 / 代理部署（含三档策略与误报归因）/ 画面（内嵌 · 独立窗口 · 多画面同屏）/ 自定义分辨率（按通道、`.rdp` 三个必写项）/ 怎么用 / 限制 / 排障（新增多通道条目）/ 升级注意；目录结构与代码量（≈19000 行 / 76 文件）同步 |
| `USER_GUIDE.md`（新） | 面向用户的分步手册：两个概念 / 五分钟上手 / 界面地图 / 配置通道 / 部署代理 / 分配任务 / 执行监控停止 / 画面三种模式 / 典型配置（单机多账户 · 多机 · 混合）/ config.json 片段 / 升级注意 / 速查表 |
| `CHANGELOG.md`（新） | v2.0（多会话通道）Added / Changed / Fixed / Upgrade notes / Known limitations；另含 v2.0-dev（WinUI 3 重写版） |
| `ChannelsPage.xaml` | 多会话提示文案改为条件式（「取决于系统是否支持多用户多会话」），不再断言"客户端版只能一个" |
| 计划书 | §3.3 R1 / §11 风险表 / §10 验收清单：注明用户环境已确认支持多会话，R1 不再是天花板 |

### 待办

- **M7 剩余**：真机验收（≥2 个账户 / ≥2 台机器）—— 清单见 §10 与「真机验证要点」。
- 悬置未定：`config.json` 是否 `git rm --cached` + ignore。
- 已知局限（真机验收时留意）：`RdpChannelWindow` 弹出后只有主窗口菜单里的「执行中的通道」还能导航，弹出窗口本身没有关闭按钮以外的手动入口；多画面网格里通道多于一页时需要翻页（不是滚动）。

### 真机验证 M3 + M4 的要点（并行开关在这一步）

**M3（并行与停止）**

1. 「会话通道」页加两个通道（不同账户 / 不同主机），任务页把任务分给不同通道；
2. 点「开始执行」→ 日志应出现「本轮启动 N 条会话通道并行执行：…」，两条通道各自下发指令；
3. 执行期间顶栏「停止执行」**应当是可用状态**（改造前远程模式下是灰的）；
4. 点「停止执行」→ 每个通道的 `stop.json` 都应出现，代理随即收尾；
5. 单通道（全部任务走一个通道）行为应与改造前完全一致。

**M4（界面）**

6. 「会话通道」菜单项进去是**通道管理/预检页**（全局开关 + 通道列表 + 编辑卡 + 每通道预检），
   **不再有内嵌画面**；点「新建通道」能加、能改名、能复制、能删除，文本字段填完切走再回来**不丢**；
7. 点「开始执行」→ 左侧菜单在「会话通道」下方出现分组「执行中的通道」+ 各通道项（`ch:<id>`）；
   点进去是**该通道自己的画面与事件**，切到别的页面再切回来**画面不断连**；
8. 任务详情「基础设置」的下拉里，「本地执行（当前会话）」是**第一项**；新建任务的默认值也是它；
9. 单通道内嵌时画面只出现在**该通道页**（管理页不抢渲染画面）；多条通道各自有画面时互不打断
   （M6 起内嵌连接按通道分家，不再"只第一条能内嵌"）；
10. 通道页的画面区应当**明显占满**窗口（窗口矮时事件卡会挤画面 → 点「收起」把事件卡折起来）；
11. 画面右上角点「全屏」→ 出现独立全屏窗口、画面填满屏幕；
    此时按 `Win` / `Alt+Tab` 应当**打到远端**而不是切本地窗口；
    按 `F11` 或双击画面退出，**本地键盘立刻恢复**（钩子已摘）；
12. 全屏状态下再从菜单进另一条通道页点「全屏」→ 应当只有**一个**全屏窗口（前一个被自动收掉）。

**M5（收尾汇总与概览）**

13. 两条通道并行跑完 → 看板娘**只在全部结束时**才从"工作"变"休息"
    （A 通道先跑完的那一刻，它必须还在工作态 —— 否则用户会以为整轮结束了）；
14. 全部结束 → 日志出现 `[汇总] 共 N 块（…）：成功 X、异常 Y。` + 逐块明细（异常块排在最前），
    并且只收到**一条**汇总通知（不是每块一条）；
15. 让其中一条通道故意失败 → 看板娘停在 Error 提示、汇总点数对得上、明细里异常那条在最上面；
16. 主页出现「会话通道」概览卡（一行一条，含状态符号 / 进度 / 心跳），点一行能跳到对应通道页；
17. 把两条通道配成同一个 host → 通道管理页出现橙色「同一主机有多条通道」提示（**只是提示，不应禁用**）；
18. 配 9 条以上通道（上限 8）→ 开始执行时弹一次「并发上限」说明，日志里也有对应的警告行。
19. **面板四项在远程 / 并行执行时必须有值**：一开跑（还没连上代理那几秒）就应当是「正在连接目标会话…」而非「空闲」；
    连上后耗时逐秒走、当前任务与进度跟着代理回传刷新；多块并行时状态显示 `执行中（N 块并行）`、进度是各块之和。
20. 只跑远程通道（任务全部分配给通道、本地无任务）→ 看板娘**立刻**进入"工作"态，不该整轮停在"待命中～"。

**M6（多画面同屏）**

21. 开 2～3 条通道并各自连上画面 → 通道页点「多画面」：网格里**同时**看到 2～3 路画面，每格左上角有通道名与状态；
    点其中一格画面 → 只有该路收到鼠标/键盘（不会串到别路）；
22. 点某格「弹出窗口」→ 出现一个普通可缩放窗口显示那一路；页面里对应格变成"正在独立窗口里显示"的占位；
    把这个窗口拖到主窗口旁边，两路画面**能同时操作**；关掉该窗口 → 画面**自动回到**页面里的那一格；
23. 从单画面切多画面、再切回单画面 → 画面**不闪断**（只在切换时重排格位，不重连）；
24. 单画面模式点「全屏」→ 仍是独立全屏窗口 + 键盘接管（Win / Alt+Tab 打到远端）；
    多画面模式下双击某格 → 弹成独立窗口（**不**开全屏）；
25. 配 5 条以上通道（都有画面）→ 多画面出现「上一页 / 下一页」，翻页时只渲染当前页 4 路；
    停留在一页时 CPU 占用应与只有 4 路画面时相当（隐藏格位不参与渲染）。
26. 还没跑过任务时，在通道页点「连接本通道」→ 应当能建连（不再报"找不到通道"）。

### 构建与验证基线（每轮改动后都要跑）

```bash
dotnet build -c Release -p:Platform=x64          # 主工程，0 错误
cd _smoke && dotnet run -c Release               # 冒烟，0 FAIL（"===== 全部通过 ====="）
```
