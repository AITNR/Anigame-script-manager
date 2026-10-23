# 内存占用审计（2026-10-17）

> **修正说明**：初版把 `docs/runtime-memory-optimization.md` 里「两路 1.84–1.91 GiB / 1060 线程」
> 当成了当前基线，据此推论「双路存在非线性累积」。**那个数字是 `7c7350c` 这次优化前用来对比的
> 检查基线，不是现状**。实际双路实测 639 MB / 116 线程 —— 优化已经生效，且当前完全线性、无泄漏。
> 下面全部按实测重写。

范围：只读审计 + 外部采集。**未改动任何生产代码。**

数据来源：
- 安装版 `C:\Program Files\Yomori\logs\*.log` 里程序自己每分钟打的 `[memory]` 行；
- `winui3/tools/watch_memory.ps1` 外部采样（2026-10-17 20:12–20:16，真实双路连接）；
- 源码静态审查（`Yomori.App` / `Yomori.RdpNative`）。

---

## 一、结论

1. **上一轮内存优化（`7c7350c`）确实生效了**：双路 1080p 从 1.84–1.91 GiB / 1060 线程
   降到 **639 MB / 116 线程**，Private 降约 65%，线程降约 89%。
2. **当前没有泄漏。** 4 分钟连续采样：Private 只涨 0.5 MB（噪声），线程从 147 **降到** 116，
   句柄降 71，GPU 本地内存降 14.5 MB —— 资源在收敛，不是在累积。
3. **成本结构是线性的、每路固定约 205 MB**，不是几何增长。空闲 229 MB，
   单路约 435 MB，双路约 640 MB。
4. **托管堆不是瓶颈**（双路时 19 MB，占总内存 3%），优化 C# 侧收益接近零。
5. **还能省，但空间只有约 100–200 MB（15%–30%）**，不是 1 GB。剩下的大头是
   WinUI3 空载基线（197–229 MB）和每路 FreeRDP 的 ~180 MB，两者都不容易动。
6. **显存只有 97 MB，同样不是瓶颈**，且已到下限：纹理固定 3 张（2 backbuffer + 1 staging），
   与帧率无关；`FlipSequential` 不允许低于 2 张。见 §3.1。

---

## 二、实测数据

### 2.1 各状态稳态值

| 状态 | Private | WorkingSet | 线程 | 句柄 | 托管堆 | GPU 本地 | 来源 |
|---|---|---|---|---|---|---|---|
| 空闲稳态 | 197 MB | 206 MB | 56–66 | 1250–1263 | 2 MB | — | 18:42–18:45（4 分钟稳定） |
| 刚启动 1 分钟 | 229 MB | 242 MB | 63 | 1278 | 3 MB | — | 20:10:41 |
| 单路 1080p | 437 MB | 444 MB | 121 | 1528 | 10 MB | — | 19:50:40 |
| **双路 1080p（当前）** | **638–639 MB** | **631–633 MB** | **113–147** | **1714–1792** | **19 MB** | **97–112 MB** | 20:11–20:16 |

按每路增量：**约 205 MB / 42 线程 / 253 句柄**。

### 2.2 4 分钟连续曲线（真实双路，未断开）

| 时间 | Private | WS | 线程 | 句柄 | GPU |
|---|---|---|---|---|---|
| 20:12:18 | 638.6 | 631.2 | 147 | 1792 | 111.8 |
| 20:12:50 | 638.7 | 631.8 | 146 | 1786 | 111.8 |
| 20:13:06 | 638.1 | 631.8 | 118 | 1728 | 111.8 |
| 20:13:54 | 638.4 | 632.2 | 117 | 1726 | 98.0 |
| 20:14:42 | 639.1 | 633.4 | 117 | 1724 | 98.0 |
| 20:15:30 | 639.0 | 633.4 | 115 | 1714 | 97.3 |
| 20:16:03 | 639.1 | 633.5 | 116 | 1721 | 97.3 |

判读：

- Private `δ = +0.5 MB / 4 min`，属于测量噪声，**不是泄漏斜率**；
- 线程 `147 → 116`、句柄 `1792 → 1721`、GPU `111.8 → 97.3`，三项同向**下降**，
  说明连接建立后的临时分配被正常回收；
- 会话计数也干净：`live_sessions=2 total_created=2 total_released=0`（**零重连**）。

### 2.3 与优化前对比

| 指标 | 优化前（`7c7350c` 前，双路） | 现在（双路） | 变化 |
|---|---|---|---|
| Private | 1.84–1.91 GiB | 639 MB | **−65%** |
| 线程 | 1060–1080 | 116 | **−89%** |

---

## 三、639 MB 花在哪

| 组成 | 估算 | 依据 |
|---|---|---|
| WinUI3 / XAML 空载基线 | 197–229 MB | 空闲实测 |
| 每路 D3D 渲染 | 23.7 MB × 2 = 47 MB | swapchain 2 张 + staging 1 张，1920×1080×4 = 7.91 MiB/张 |
| 每路 FreeRDP native | ~180 MB × 2 = 360 MB | (639 − 229 − 47) ÷ 2 |
| 托管堆 | 19 MB | `GC.GetTotalMemory` |

GPU 本地内存另计 97 MB（双路 swapchain 只有 31.6 MB，其余是合成器、字体图集、纹理）。

**关键判断**：每路 ~180 MB 的 FreeRDP 是最大单项，但它属于解码器与表面缓存的内部行为。
当前管线是「GFX 通道已接入 GDI（全帧模式）」（`gfx_state=2`、`codecs_null=0`），
即**没有启用 H264/AVC 硬件解码分支**，所以没有「换解码器」这种便宜可捡。

### 3.1 显存（GPU 本地内存）

实测双路 1080p：**97.3 MB**（连接建立后瞬时 111.8 MB，之后回落到 97.3 并保持平稳）。

| 组成 | 大小 | 依据 |
|---|---|---|
| swapchain backbuffer（7.91 MB × 2 张 × 2 路） | 31.6 MB | `BufferCount = 2`，`RdpD3DRenderer.CreateStaging` |
| D3D11 设备 × 2 + 驱动内部分配 | 约 10–20 MB | 每路 `D3D11CreateDevice` 独立创建设备 |
| DWM 合成器为两个 `SwapChainPanel` 生成的合成表面 | 约 45–55 MB | 97.3 − 31.6 − 设备开销 |
| XAML 字体图集 / 图标纹理 | 数 MB | — |

- staging 纹理（7.91 MB × 2）是 `Usage = Staging`、CPU 可写，通常落在非本地内存，
  **不计入 Local Usage**；所以真正的显存只有 swapchain 那 31.6 MB。
- **显存占用与帧率无关**：纹理固定 3 张，不管帧率是 1 fps 还是 60 fps 都不变。
  日志里 `呈现 ≈1.0 帧/秒`（远程桌面空闲不送帧）同样只是这 3 张纹理。
- 本机是 RTX 4060 Laptop（8 GB），系统已用专用显存 4810 MB，
  Yomori 的 97 MB 约占 **2%**，不是瓶颈。
- **可压缩空间接近零**：`FlipSequential` 要求至少 2 张 backbuffer，已是下限；
  唯一还能动的是「每路一个 `ID3D11Device`」（4 路可省 3 份设备开销），但收益只有十几 MB，
  且设备生命周期风险高，不值得。
- **反直觉结论：切到软渲染不会更省显存。** `SoftwareBitmap` 在 CPU 内存持有一份 7.91 MB，
  `SoftwareBitmapSource` 还要再上传一份给合成器 —— 反而可能两份都占，
  所以 `render_backend = software` 不能当省显存的手段。


---

## 四、还能优化什么（按 ROI 排序）

### 4.1 零代码，立即生效

| 项 | 当前 | 影响 |
|---|---|---|
| `audio_enabled: true` | 两条通道各有 rdpsnd + AUDIO_PLAYBACK_DVC | 不需要远端声音时关掉，少一条通道的线程与音频缓冲。日志里 `dvc=6`、`names` 中两个 `AUDIO_PLAYBACK_DVC` |
| `desktop_width/height: 1920×1080` | 帧面积 100% | 降到 1280×720 后帧面积 44%，D3D 省约 13 MB/路，native 位图同比降 |

⚠️ 远程跑的是游戏脚本（BetterGI / MAA），**降分辨率可能影响脚本的图像识别**，改前先确认脚本不依赖特定分辨率。

### 4.2 代码，中等收益

1. **空载基线 197–229 MB** —— 这是除每路成本外最大的一块。WinUI3 的 `Frame` 会缓存已导航过的
   页面实例，切过的页面不会被释放；`MainViewModel` 是单例，日志集合上限 4000 行
   （`MaxLogLines`，`_logBacklog` 与 `LogLines` 各有界）。要往下压得先量化各页面实例占用，
   属于「先诊断再改」的活，不要盲改。
2. **`RdpSurfaceCell.UpdateChrome()` 的 1Hz 强制重设** —— 每拍无条件写 5 个 `IsEnabled`、
   3 处前景色、2 次 `ToolTipService.SetToolTip`（每次新建 ToolTip 对象）、1 次 `StateText.Text`。
   4 格全开时每秒 8 次 ToolTip 重建。
   **对总内存影响很小**（属于分配压力/GC churn，不是泄漏），但它同时是那个已定位到
   `RdpMultiViewPage.xaml.cs:88 → :148 → ToolTipService.SetToolTip` 崩溃链路的触发点。
   **值得改，但理由是稳定性，不是省内存。**

### 4.3 已排除（不用再花时间）

| 方向 | 结论 |
|---|---|
| 托管侧优化（LINQ / 字符串 / 集合 / VM 拆分） | 托管堆 19 MB，占 3%，改了看不出 |
| D3D 缓冲下调 | 已是 `BufferCount=2` + 1 staging 的最小配置，`FlipSequential` 要求至少 2 |
| 共享跨路 D3D device | 省的是 GPU/驱动内部分配，不进 Private Bytes；且设备生命周期风险高 |
| 色深 32→16 | `RdpEmbeddedClient.cs` 里 `ColorDepth = 32` 是**硬编码**、配置不生效；但 GFX 不支持 16bpp，且明显掉画质 |
| 日志集合无界 | 已限制 4000 行，两处集合都受限 |
| `_embeds` 字典泄漏 | key 是 channelId，通道数有限，不增长 |
| Mascot 图片 | 已限 `DecodePixelWidth = 512` |

---

## 五、还需验证的两件事

1. **4 格多画面**：按每路 205 MB 外推约 197 + 4×205 ≈ **1 GB**。
   如果这个场景也线性，说明成本已完全可控；若明显超标，再查。
2. **完全断开后能否回到 idle 水位**：已有部分答案 —— 20:18:19 断开一路后
   （`total_released` 由 0 变 1，`live_sessions=1`），内存从 **639 MB → 494 MB**、
   线程 **116 → 89**，说明 teardown **确实在回收**，但只释放了 145 MB（每路成本 205 MB 的 71%）。
   剩下的约 60 MB 没回到 OS，两种解释：native/CLR 分配器保留已释放的堆块（常见且正常），
   或有少量残留。要区分，得再断开最后一路、等 1–2 分钟看是否降到 197 MB 附近。
   判据是**趋势而不是瞬时值**：只要不随时间单调上升，就不算泄漏。

```powershell
# 断开连接的瞬间看回落情况
powershell -ExecutionPolicy Bypass -File winui3\tools\watch_memory.ps1 -DurationMinutes 3 -IntervalSeconds 5
```

---

## 附：采集工具

`winui3/tools/watch_memory.ps1` —— 外部采样，不注入进程、不改程序。

```powershell
powershell -ExecutionPolicy Bypass -File winui3\tools\watch_memory.ps1 -DurationMinutes 30
```

输出 CSV（UTF-8 BOM，Excel 可直接打开）+ 终端摘要，含 Private / WorkingSet / 线程 / 句柄 /
GPU 本地内存的首末差与每小时斜率。

判读要点：

- 线程数随时间单调上升 → 线程泄漏；**当前双路是下降的，健康**；
- Private 台阶状上升 → 一次性分配大缓冲，正常；平滑上升 → 累积；
- 断开全部连接后不回落 → teardown 没释放。

限制：主控是 `requireAdministrator`，非提权 shell 有时读不到 GPU 计数器，该列留空，
**不要把空值当 0**。

---

# 附：会话代理进程（2026-10-17 21:00–21:15 实测）

主控端的内存查完，同一台机器上还常驻着**每个目标账户一个**的会话代理。代理是纯后台进程
（跑脚本 / 等指令 / 回状态），没有窗口，所以它的内存曲线跟主控是两回事，单独记在这里。

## 1. 实测（三组对照）

| 对象 | Private | WorkingSet | 线程 | 句柄 | 备注 |
|---|---|---|---|---|---|
| 生产代理 A（zdh，PID 65496，4.5h） | 34.4 MB | 40.8–42.5 MB | 8 | 331 | 跑过 1h25m 任务后 |
| 生产代理 B（bh3，PID 75644，4.7h） | 35.4–36.3 MB | 45.0–46.4 MB | 8 | 329–341 | 同上 |
| 旧构建空载探针（1Hz 心跳） | 14.2 → 17.2（5min）→ 23.4（12min）→ **27.9（31min 后走平）** | 60 → 70 MB | 8–15 | 340–349 | ProgramData 那份 exe，`--rdp-agent` + 临时桥 |
| 新构建空载探针（5s 心跳、无消息泵） | 10.9 → 12.6（5min）→ **15.8（7.5min 后走平）** | 43 → 50 MB | 8–12 | 288–294 | 本次改动后的 Release 构建 |
| 空白 .NET 8 控制台（基线） | 7.0–8.0 MB | 22–26 MB | 7–8 | — | 说明 CLR 本身就要 8 MB |

两个生产代理在 20:25 / 20:37 / 21:11 三次采样**一个字节都没涨**（34.4 / 35.4），
而新起的探针会在半小时内爬到自己的平衡点 —— 所以代理侧**不是泄漏**，
是"空载平衡点"高低的问题。

## 2. 那 20 多 MB 是什么（两个来源，都跟 GUI 无关）

1. **1 秒一拍的心跳是"读-改-写整个 status.json"**
   `AgentHost` 空闲时每秒调一次 `RdpBridge.UpdateStatus`：读回文件 → `JsonSerializer`
   反序列化 → 改字段 → 缩进序列化 → 写 `.tmp` → **两次 `GrantUsersFullControl`** → 覆盖。
   生产桥里的 status.json 有 **20–23 KB**（带着上一轮最多 500 条 Events），
   按 1 秒一拍 ≈ 每天 2 GB 写盘、上百 GB 临时对象。
   实测探针托管堆从 1.2 MB（15 秒）线性涨到 6.8 MB（6 分钟），即 **~23 KB/s** 的垃圾 ——
   正好对应"每次心跳几 KB × 每秒一次"，踩在 Gen0 上直到平衡点。
2. **一个从来没建成过的 WinRT 消息泵**
   上一版用 `DispatcherQueueController.CreateOnCurrentThread()` 维持进程存活，
   但真实代理里**每一次都抛 `COMException 0x80040111`（CLASS_E_CLASSNOTAVAILABLE）** ——
   `%ProgramData%\Yomori\agent\startup_error.log` 里躺着 21 条同样的记录，
   从 10-12 到 10-17，每个启动批次一条。也就是说生产环境**一直在跑兜底分支**，
   而这个失败的尝试每次仍先把 WinRT 投影那套 DLL 拉进进程：
   `Microsoft.Windows.SDK.NET.dll`（24 MB）、`Microsoft.WinUI.dll`（7 MB）、
   `Microsoft.WindowsAppRuntime.dll`、`CoreMessaging.dll` 等（旧探针共加载 94 个模块）。

代理没有 GUI，上面两样都不该付。

## 3. 本次改动（3 个文件）

| 文件 | 改动 |
|---|---|
| `Services/AgentHost.cs` | 删掉 `DispatcherQueueController` 与自建 Win32 消息循环；主线程改为 `WaitLoop(loop)` 同步等常驻循环（**这正是生产一直在走的分支**，行为不变）；空闲心跳从 1s 放宽到 5s（查指令仍是 1s） |
| `Services/NativeMethods.cs` | 删掉随之无用的 `PeekMessageW`/`TranslateMessage`/`DispatchMessageW`/`PostQuitMessage` 与 `Message` 结构 |
| `Program.cs` | 只改注释：代理宿主现在是纯后台进程，不再提消息泵 |

`_smoke` 同步更新：原先"必须有 PeekMessageW"这类**跟着实现走**的断言已换成不变量
（"代理宿主不 using 任何 Microsoft.UI/WinRT""不激活 Dispatcher""靠 WaitLoop 存活"），
并补上 `Smoke.csproj` 漏链的 4 个文件 —— 7c7350c 引入 `ExternalLogTail` 之后冒烟工程本来编不过，
这与本次改动无关，是拉代码后没同步的欠账。

## 4. 验证（不是"看代码觉得对"）

- 空载探针 12 分钟：status.json `UpdatedAt` 新鲜度 **4.6s → 1.1s**（≤5s，主控 `RdpHeartbeat`
  超时 20s，留 4 倍余量），`Phase=idle` 正常 → 心跳没被改坏；
- 内存：30s 10.9 MB → 5min 12.6 MB → 7.5min **15.8 MB 走平**（旧构建同口径 17.2 / 27.9）；
- `repo/startup_error.log` 最后一条仍是 10-04，**没有新的 0x80040111**；
- 冒烟：代理宿主相关 7 条断言全 PASS（其余 9 条失败在 XAML/原生裸指针等**本次没碰的文件**上，
  是 7c7350c 拉代码后的既有欠账，先记账不动）。

**还没验证的**（必须补）：把新代理部署到 `%ProgramData%\Yomori\agent` 后在真实 RDP 会话里跑一轮，
再触发一次真实任务、看任务结束回到 idle 的水位 —— 这次只验证了空载，没有跑真任务。

## 5. 结论

- 代理侧**没有泄漏**，之前的 34–36 MB 是一个"空载平衡点"，成因是 1Hz 全量重写 status.json
  + 一个 100% 失败但照样加载 WinRT 的消息泵；
- 改动后空载平衡点从 **27.9 MB → 15.8 MB**（探针口径），按生产 status.json 更大来估，
  真实代理应从 34–36 MB 降到 **20 MB 上下**（待真机复测，别把估算当验收）；
- 剩下能省的已经不多：CLR 基线 8 MB + 少量程序集，再去掉也没意义。
