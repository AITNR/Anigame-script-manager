# 内嵌 RDP 卡死与崩溃：诊断、修复与遗留（2026-10-16 会话）

- **版本**：v1（2026-10-16）
- **状态**：3 个提交已推送 `main`；**崩溃根因未获直接证据**、**真机复现路径未验证**
- **适用范围**：`winui3/src/Yomori.RdpNative`（原生桥）与 `winui3/src/Yomori.App`（渲染层）
- **起因**：AITNR 反馈「又卡死了」→「现在还崩溃了」→「修好了，把那些抓取的日志都可以删除了」
- **相关**：`ui-hang-watchdog.md`（主控端整窗假死专题）、`rdp-embed-freerdp-plan.md`（内嵌渲染架构）

---

## 0. 一句话结论

本次会话把同一天里两条**互相独立**的问题线分开处理：

| 问题线 | 结论 | 证据强度 |
| --- | --- | --- |
| **卡死**（UI 线程抢锁被 588MB 同步写盘堵住） | 根因**已由实测数据确认**，已修 | 强（写入量、锁竞争、时间线三者互证） |
| **崩溃**（`0xC0000374` 堆损坏 + 一串 `0xC0000005`） | 只做到**加固 + 排除一个错误假设**；根因**未获直接证据** | 弱（见 §4） |

> ⚠️ 阅读提醒：本文件**明确区分**"实测所见"与"推断"。§4 的因果链是推断，不是从 dump 栈看到的。

---

## 1. 现场证据

### 1.1 卡死与崩溃是同一批进程

`C:\ProgramData\Yomori\watchdog\watchdog.log` 里的「UI 线程无响应（假死）」记录，
与 Windows 事件日志里 `Application Error` 的出错进程 ID **一一对应**：

| 看门狗记录 | 事件日志崩溃 | 关系 |
| --- | --- | --- |
| 17:30:07 pid=29432 | 17:29:56（0x72F8=29432） | 同一进程 |
| 17:34:23 pid=7428 | 17:34:12（0x1D04=7428） | 同一进程 |
| 18:22:58 pid=64820 | 18:22:48（0xFD34=64820） | 同一进程 |
| 18:24:10 pid=43628 | 18:24:01（0xAA6C=43628） | 同一进程 |

看门狗比崩溃晚约 10 秒 —— 因为进程已 AV、正在被 WER 收集转储，窗口还在。
**用户侧看到的是"卡死"，实际是进程正在死。** 全天共 5 次假死记录。

### 1.2 当日崩溃类型汇总（事件日志 `Application Error`）

| 次数 | 出错模块 | 异常代码 |
| --- | --- | --- |
| 5 | `coreclr.dll` | `0xc0000005` |
| 3 | `CoreMessagingXP.dll` | `0xc000027b` |
| 1 | `dcompi.dll` | `0xc0000005` |
| 1 | `ntdll.dll` | **`0xc0000374`（STATUS_HEAP_CORRUPTION）** |

其中 `coreclr.dll` 偏移 `0x1d45dc` 与 **10-12 那次完全相同** → 老问题复发，非新引入。

托管栈顶多次落在 `ToolTipService.SetToolTip`（`RdpSurfaceCell.ApplyMuteChrome` /
`ApplyLockChrome` ← `UpdateChrome` ← `RdpMultiViewPage.LayoutSurfaces` ← 1Hz
`DispatcherQueueTimer`），另有一次落在 `ycn_rdp_set_muted`。
**栈顶在托管层的表现不指向托管代码本身**（堆已被污染）。

### 1.3 关键物证：588 MB 诊断文件

`%TEMP%\ycn_gfx_debug.txt` 在崩溃时已达 **616,813,741 字节（588 MB）**，
共 **5,443,156 行**，其中 **5,116,697 行（94%）是 `mute-snap#` 快照**。

按内容归纳（去掉数字后分组）：

| 行数 | 内容 |
| --- | --- |
| 5,116,697 | `mute-snap## slot# dync=# att=# open=# ...` |
| 139,657 + 108,736 + 65,853 + 8,274 | 同族 `mute-snap#` 变体 |
| 1,010 | `mute-dvc: item# p=# dev=#` |
| 294 | `mute: slot# HOOKED dev=# play=# playEx=#` |
| 129 | `ResetGraphics-AV(#x#) swallowed` |

同文件里还有这三行（**本会话最重要的原始观察之一**）：

```
mute: collect slot0 (静态 rdpsnd)  → dync=0  att=1 open=3  onopen=0 isopen=0 nfmt=0   ✅ 合理
mute: collect slot1 (DVC rdpsnd)   → dync=1  att=1 open=0  onopen=1 isopen=0 nfmt=0   ✅ 合理
mute: collect slot2 src='dvc'      → dync=3670068 att=2949219 open=-849258240 nfmt=55 ❌ 全是垃圾
mute: slot2 HOOKED  play=0000000400000000 playEx=015E000074686777                     ❌ 非函数指针
```

`0x74686777` = ASCII `"gwht"` → **读到的确实不是函数指针**。

---

## 2. 卡死：根因已确认并修复（commit `358893e`）

### 2.1 机制

1. `ycn_debug_dump_tag()` 是 `fopen_s` / `fprintf` / `fclose` **同步**写盘；
2. 它的调用点有一大批在 **`g_lock` 内**；
3. 频率是**每帧级**，不是注释里以为的 500ms 一拍 ——
   `event_loop` 的 `WaitForMultipleObjects(..., 500)` 在帧流活跃时**每次事件唤醒都会返回**，
   于是 `ycn_rdpsnd_hook` 跟着每帧跑一次，快照条件 `tick % 120` 每几秒就命中；
4. UI 线程点「静音」/建连走 `ycn_rdp_set_muted`，要抢**同一把 `g_lock`**
   → 写盘那一刻它就等在那里 → **界面卡死**。

这解释了 588 MB 的成因，也解释了 §1.1 的"卡死与崩溃同源"。

### 2.2 修复

- 新增 `ycn_bc()`：64 槽 × 256 B **内存环形缓冲**，`Interlocked` 递增索引，
  无分配 / 无 I/O / 不抢锁，可在 `g_lock` 内安全调用；
- 新增 `ycn_dump_breadcrumbs()`：**只在崩溃路径**（SEH 吞下 AV 时）才落盘 ——
  "崩溃前最后几行即元凶"的诊断价值完整保留；
- 删除已无调用者的 `ycn_debug_dump_tag()`；
- 热路径写盘调用全部改走 `ycn_bc()`；
- 纪律写进注释固化：**锁内绝不 I/O，热路径绝不 I/O**。

### 2.3 已验证

| 项 | 结果 |
| --- | --- |
| 编译 | 通过（唯一警告 C4081 来自 winpr 头） |
| `ycn_rdp_selftest_mute()` | rc = `0x1F` 全位通过（静音闸未破坏） |
| 稳态 I/O | 连续 1000 次调用后诊断文件**根本未被创建** |
| 导出表 | 15 个，与改动前完全一致（C ABI 无变化） |

---

## 3. 崩溃：加固与一个被排除的假设（commit `25c9058`）

### 3.1 排除的错误假设：硬编码偏移失配

代码里有一整套按硬编码偏移读 FreeRDP 私有结构的地方。曾怀疑装机版 FreeRDP
与探针时用的版本不同导致偏移位移 —— **此假设已排除**。

用 FreeRDP 3.32.0 源码（vcpkg buildtrees）编译 `offsetof` 实测：

| 字段 | 代码里写的 | 实测 | |
| --- | --- | --- | --- |
| `OpenHandle` | 184 | 184 | ✓ |
| `NumberOfClientFormats` | 232 | 232 | ✓ |
| `attached` | 236 | 236 | ✓ |
| `dynamic` | 240 | 240 | ✓ |
| `isOpen` | 268 | 268 | ✓ |
| `device` | 312 | 312 | ✓ |
| `applyVolume` | 404 | 404 | ✓ |
| `OnOpenCalled` | 416 | 416 | ✓ |
| `async` | 420 | 420 | ✓ |
| `sizeof(rdpsnd_plugin)` | （`fakePlugin[432]`） | 432 | ✓ |

**全部一致**。`rdpsndDevicePlugin` 布局（同法实测）：
`rdpsnd`=0 / `FormatSupported`=8 / `Open`=16 / `GetVolume`=24 / `SetVolume`=32 /
`Play`=40 / `Start`=48 / `Close`=56 / `Free`=64 / `DefaultFormat`=72 /
`ServerFormatAnnounce`=80 / `PlayEx`=88，`sizeof`=96。

### 3.2 另一条已澄清的源码事实

`dvcman->plugins` 是**异构列表** —— 所有 DVC 插件都往里注册
（`audin` / `rdpecam` / `urbdrc` / `video` / `rdpsnd` …，见各自
`RegisterPlugin(pEntryPoints, NAME, &x->iface)`）。名字与指针分两个**平行**数组存
（`drdynvc_main.c:153`）。

其中 `rdpsnd` 注册的 `iface = &rdpsnd->iface`，而 `IWTSPlugin iface;` 是
`rdpsnd_plugin` 的**首成员** → `&rdpsnd->iface == rdpsnd`，所以 rdpsnd 的那一项
**确实是**合法 `rdpsnd_plugin*`。

### 3.3 改动

1. `ycn_is_freerdp_ptr()`：新判据 —— 函数指针必须落在 FreeRDP 自己的模块
   （`freerdp3` / `freerdp-client3` / `winpr3`）内。
   旧判据只做「不是本模块的钩子就放行」，等价于放行任意可读地址。
2. `ycn_plugin_fields_sane()`：新判据 —— 按结构体字段取值判"像不像" `rdpsndPlugin`
   （5 个 BOOL 字段必须落在 {0,1}）。不依赖模块名，换 FreeRDP 版本也不失效。
3. `probe` 结尾两道都要过；`hook_slot` **写入前**再加同一道闸
   （device 取自跨拍槽位缓存，probe 这拍返回 NULL 时缓存不会更新）。
4. 自检新增 `0x20` / `0x40` 断言（见 3.4）。
5. 修掉一处自己引入的缺陷：判据里的 `dev->SetVolume` 原先在 `__try`
   **之外**解引用，等于把第 2 层解引用漏出 SEH 护栏（文件里 1109 行的注释
   正是这个警告）。改为在 SEH 内缓存到 `setvol`，出护栏只用缓存值。

### 3.4 已验证

`ycn_rdp_selftest_mute()` 返回位掩码扩展到 `0x7F`：

| 位 | 含义 |
| --- | --- |
| `0x01` | 拿到可用会话槽位 |
| `0x02` | 未静音时收到原始数据 |
| `0x04` | 静音时收到全零数据 |
| `0x08` | 静音时 Play 仍被调用 |
| `0x10` | 取消静音后恢复原始数据 |
| `0x20` | **伪插件被拒绝**（两形态：字段非法 / SetVolume 不在 FreeRDP 模块内） |
| `0x40` | **真设备形态被接受**（防判据过严 → 静音静默失效） |

- 修复后：rc = **`0x7F`** 全通过。
- **反向对照**：把同一组断言接到**修复前**的弱判据上 → `0x20` **失败**（rc = `0x1F`）。
  这证明这两条断言不是空摆设，确实能抓到旧行为。

---

## 4. 崩溃根因：**未获直接证据**（重要）

### 4.1 推断的因果链

```
dvcman->plugins 里的某项不是 rdpsnd_plugin
  → 但代码按 +312 当 device 读出来
  → probe 的弱判据（只要不是本模块钩子就放行）让它通过
  → hook_slot 往这块内存写 SetVolume / Play / PlayEx 三个函数指针
  → 破坏堆（0xC0000374）
  → 下游一串 0xC0000005 / 0xC000027B
```

`slot2` 的垃圾字段（§1.3）与 `play=0000000400000000` 支持"这一项不是合法设备"。
`hook_slot` 确实是本文件里唯一对 FreeRDP 结构体**写**的地方。

### 4.2 但这条链是**推断**，不是观察

必须明确：

- **从未观察到"写坏了谁的内存"。** 没有 dump 栈、没有写前/写后的内存对比。
- 实际观察到的只有两条间接现象：诊断文件里 `play=` / `playEx=` 读出的值像垃圾；
  以及一个 `ntdll 0xC0000374` 事件码。
- **"被写的那块内存属于谁"从未验证。** 存在另一种同样合理的解释：
  该 plugin 曾是真的、但 device 已被 FreeRDP `free` 掉（**use-after-free**）。
  这两种情况的证据与修法都不同。
- ⚠️ 另需注意：SEH **挡不住** 3.1 节这条链 —— SEH 只能抓"踩到不可访问页"，
  而"写进一块合法但不属于我们的内存"不抛异常，只静默毁堆。

### 4.3 因此

`25c9058` 是**按该推断做的加固**，不保证它就是根因。真机上是否真存在这种对象、
加固后崩溃是否消失，都需要在**真机复现路径（连通道 → 点静音）**上确认。

---

## 5. 渲染层：另一条独立隐患（commit `dcf2998`）

`OnPresentTick` 用 `_ = RenderFrameAsync(client.Session)` 即发即弃，
而 `_softRendering` **只防"重入"不防"堆积"** —— 没有任何取消机制。两处后果：

1. **use-after-dispose**：`DetachClient()` / `Disconnect()` 会
   `_softBitmap?.Dispose()` 并把 `_bitmapSource` 置空/换新，但在飞的任务过了
   `await` 后仍会回到 `_softBitmap.CopyFromBuffer(...)` / `_bitmapSource.SetBitmapAsync(...)`。
2. **活锁**：`_softRendering` 是实例字段，旧连接留下的任务一旦卡在 `await` 不返回，
   它一直是 `true` → 新连接每一拍都在 `if (_softRendering) return;` 被挡掉
   → **画面永久不更新**。`DetachClient` 里那句 `_softRendering = false`
   只复位标志，并未终止旧任务。

**修复**：新增 `_renderGeneration`，`DetachClient` / `Disconnect` 时自增；
`RenderFrameAsync` 带 generation 参数，在 `await` **前**与 **后**各校验一次，
不匹配即 return。

**验证**：`dotnet build -c Release -p:Platform=x64` 通过，0 错误
（4 个警告都在改动之外的文件：`RdpEmbeddedClient.cs:123`、`RdpD3DRenderer.cs:295`）。

**未验证**：没有实测「切页面/弹窗后画面是否恢复」。
原注释引用的 dump 数据（"36 个 `AsyncStateMachineBox<RenderFrameAsync>`"、
"卡死现场 32 个"）**未能复现核对**，本提交不为那组数字背书。

---

## 6. 构建产物差异（重要运维事实）

**CI 产出与本地构建不是同一个二进制**，即使源码 commit 与 vcpkg commit 都相同。

实测（同一 commit `25c9058`、同一 vcpkg commit `10541e31…`）：

| 文件 | CI 产出 | 本地构建 | 一致 |
| --- | --- | --- | --- |
| `ycn_rdp.dll` | 40,448 | 37,888 | ✗ |
| `freerdp3.dll` | 2,038,272 | 1,969,152 | ✗ |
| `winpr3.dll` | 733,696 | 723,456 | ✗ |
| `freerdp-client3.dll` | 559,104 | 538,112 | ✗ |
| `z.dll` / `cjson.dll` / `libssl-3-x64.dll` / `libcrypto-3-x64.dll` | — | — | ✗ 全不同 |

结论：**验证必须装在 CI 的包上**，本地自编的 DLL 不能作为验证依据。
（本次会话中 `25c9058` 的 CI 包为 `dev.45` / `build-45`，`ycn_rdp.dll` sha16
=`6B58045BDB19099F`，与装机一致。）

---

## 7. 提交清单

| 提交 | 时间 | 内容 | 文件 |
| --- | --- | --- | --- |
| `358893e` | 19:19 | 修卡死 —— 诊断落盘移出 `g_lock`，改内存环形缓冲 | `ycn_rdp.c`（+70/−19） |
| `25c9058` | 23:19 | rdpsnd 设备判据补双向校验 + 自检加 `0x20`/`0x40` 断言 | `ycn_rdp.c`、`ycn_rdp.h`（+193/−4） |
| `dcf2998` | 23:35 | 软渲染加渲染世代号，堵 use-after-dispose 与活锁 | `RdpView.xaml.cs`（+43/−2） |

三个提交均已推送 `main`。

---

## 8. 遗留与建议

1. **崩溃根因待真机确认**（最高优先）。在 CI 包上跑：连通道 → 点静音 → 观察是否
   仍出现 `0xC0000374` / `0xC0000005`。若消失，§4.1 推断得到支持；若仍在，
   推断被推翻，需另起炉灶（下一步应考虑在 `hook_slot` 写入前后加内存校验，
   或对 `dvcman->plugins` 里的项做**按名字**筛选而非按形态探测）。
2. **判据白名单需真机验证**：`ycn_is_freerdp_ptr()` 假设真设备的方法指针落在
   `freerdp3` / `freerdp-client3` / `winpr3` 之内（`waveOut*` 后端在
   `freerdp-client3.dll`，已由 import 表佐证）。但真机上静音是否仍生效，
   必须实测 —— 判据过严会导致静音**静默失效**（`0x40` 位只能证明合成用例）。
3. **`_renderInFlight` 是死字段**：只增只减、全仓库无读取点，对运行时行为无影响。
   建议删除，或补上实际诊断输出。
4. **CI 与本地构建差异未定位**：同源码同 vcpkg 却逐字节不同（§6），
   团队若长期在本地做验证会引入误导，建议统一到 CI 产物。
5. **诊断残留在 `%TEMP%`**：本次已清理 588 MB 诊断文件与转储（D 盘回收 9.5 GB）。
   注意 `ycn_debug_dump_tag` 虽已删除，但 `ycn_dump_breadcrumbs()` 在崩溃时仍会写盘
   —— 这是**刻意保留**的，不要当成残留物删除。