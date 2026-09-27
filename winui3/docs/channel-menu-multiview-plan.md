# 雪乃酱 · 会话通道菜单重构 计划书（父项 = 多画面，子项 = 单通道画面）

- **版本**：v1 + 实施记录（2026-09-27）
- **状态**：✅ 已实施（M1–M5 全部落地，编译 + 751 项冒烟断言通过）；**剩真机点击手感验收**（§7）
- **适用范围**：`repo/winui3/src/YukinoChan.App/`（纯界面层重构，未动执行/停止/连接链路）
- **触发**：2026-09-27 真机反馈 —— 多画面入口藏在某条通道页里的「单画面 / 多画面」切换按钮后面，且通道项与「会话通道」平级挂在菜单中部，与直觉不符。

## 0. 实施结果（2026-09-27）

| 项 | 落地情况 |
| --- | --- |
| 菜层级 | 「会话通道」变为**父项**（XAML `ChannelsNavItem` + `<NavigationViewItem.MenuItems>`），子项运行时重建：`多画面（全部通道）` → 各通道 → 分隔线 → `通道管理…`；旧「执行中的通道」分组标题逻辑整体删除 |
| 导航 | tag 语义重排：`channels` = 多画面、`multiview` = 多画面（子项别名）、`channel-mgmt` = 通道管理、`ch:<id>` = 单通道；新增 `ItemInvoked` 处理（带子项的父项点击不改变选中态） |
| 新页面 | `Views/RdpMultiViewPage.xaml(.cs)`：2×2 网格 / 每页 4 格 / 分页 / 逐格连接 / 弹出窗口 / **不给全屏**，空态给「去通道管理」 |
| 通道页 | `RdpChannelPage` 删掉单/多画面开关与网格分支，固定本通道一格（保留全屏、弹窗、事件卡折叠） |
| 通道管理页 | 文案改「通道管理」，提示卡改「打开多画面 / 打开当前通道画面」；`--embed-vm` 自检路径改跳 `channel-mgmt` |
| VM | `SurfaceGridChannelIds` 扩为「配置里启用的通道 → 本轮会话 → 活画面」；看板娘新增 `multiview` / `channel-mgmt` 键 |
| 冒烟 | 新增 12 条 `[M8]` 源码断言，并改写随重构失效的 3 条 M6 断言；全量 751 项通过 |

> 真机未验证项：① 点父项主体是否真的触发 `ItemInvoked` 并跳到多画面（若只展开，用子项「多画面」也达意）；
> ② 父项被选中时的高亮回显；③ 多画面页 4 路同时渲染的开销。

---

## 1. 现状与问题

当前（M4–M6 落地后的形态）：

```
左侧菜单                                现状行为
─────────────────────────────────────
总览
任务执行
耗时统计
运行日志
会话通道        ← 点击 = 通道配置/预检页（ChannelsPage）
─────────────
执行中的通道    ← 运行后动态插入的分组标题
  ⛓ zdh        ← 点击 = RdpChannelPage（默认单画面）
  ⛓ bh3        ← 页内「单画面/多画面」切换 → 多画面=2×2 网格显示**全部**通道
设置
```

问题点：

1. **「多画面」入口错位**：它是某一条通道页面里的一个视图模式，用户却在"这一条通道"里找"全部通道"的画面 —— 概念上就拧着。
2. **通道项与「会话通道」平级**：菜单中部插一个「执行中的通道」分组标题 + 兄弟项，层级关系不直观。
3. 通道页里的「单画面」模式对多通道场景是多余的入口（每条通道页都摆着一组 单画面/多画面 按钮，实际多画面只有一份）。

## 2. 目标状态（拍板方向）

```
左侧菜单                                目标行为
─────────────────────────────────────
总览
任务执行
耗时统计
运行日志
会话通道 ▾       ← 点击父项本身 = **多画面页**（全部通道 2×2 网格 + 分页）
    ⛓ zdh       ← 子项 = 该通道独立画面页（RdpChannelPage，纯单画面）
    ⛓ bh3
    ⚙ 通道管理…  ← 子项 = 现在的配置/预检页（ChannelsPage）
设置
```

- 点「会话通道」= 一眼看到所有通道的多窗口。
- 父项下的**子菜单**才是各通道单独的画面。
- 通道页内的「单画面 / 多画面」切换按钮**删除**。

## 3. 待拍板决策（✅ = 实施时已采用）

| #   | 决策点           | 选项                                                                                                   | 结论                                                                                                     |
| --- | ---------------- | ------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------- |
| D1  | 父项点击行为     | a) 点父项 = 导航到多画面页（点箭头区域才展开）<br>b) 父项下第一个子项「多画面」                          | ✅ **a + b 都做**：`ItemInvoked` 接父项导航（WinUI 有子项时点击不改变选中态），同时给一个子项「多画面（全部通道）」兜底 |
| D2  | 子项来源         | a) 仅本轮执行中的通道<br>b) 已配置且启用的通道**常驻**                                                    | ✅ **b**：运行前也能点进通道页手动「连接本通道」看画面；本轮在跑的通道（含已删/已停用的）一定在列            |
| D3  | 配置/预检页入口  | a) 子菜单末项「通道管理…」<br>b) 挪进「设置」页                                                          | ✅ **a**，tag 由 `channels` 改为 `channel-mgmt`                                                            |
| D4  | 多画面页形态     | a) 新建 `RdpMultiViewPage`，网格/分页逻辑整体迁出<br>b) RdpChannelPage 泛化成"单/多通用"                  | ✅ **a**：迁移后 RdpChannelPage 只剩单画面，代码更简单                                                     |
| D5  | 「执行中的通道」 | 删除分组标题（层级已由父子结构表达）                                                                     | ✅ 已删除                                                                                                  |

## 4. 文件级改动清单

### 4.1 `Views/MainWindow.xaml(.cs)` — 菜单结构与导航

- XAML：`Tag="channels"` 项改为**带子项的父项**（`Content="会话通道"`，子项运行时填充，静态先放一个「通道管理…」占位）。
- `RebuildChannelMenuItems()`：
  - 不再向 `NavView.MenuItems` 平级插入 header + 通道项；改为**清空并填充父项的 `.MenuItems`**（子项顺序：各通道 → 分隔线 →「通道管理…」）。
  - D2 选 b 时：数据源改为 `VM.Config.Rdp.Channels`（启用项）∪ 本轮 `VM.Sessions`（补运行状态角标 `Glyph`/`InfoBadge`），通道被删时同步移除。
- 导航 tag 语义变更：

| tag               | 页面                                     |
| ----------------- | ---------------------------------------- |
| `channels`        | **RdpMultiViewPage**（新，多画面）       |
| `ch:<id>`         | RdpChannelPage（不变，单画面）           |
| `channel-mgmt`    | ChannelsPage（原 `channels` 的内容）     |

- `OnNavigationSelectionChanged` → 增加/改用 `ItemInvoked`：父项点击也要导航（`args.InvokedItem`/`InvokedItemContainer` 取 tag）；`SyncNavigationSelection` 兼容父项与子项的选中回显。
- 自检模式（`--embed-vm`）的 `NavigateTo("channels")` 改为 `channel-mgmt`（该流程依赖 ChannelsPage 的自动连接逻辑，不动它）。
- `VM.UpdateMascotForPage` 增加 `"multiview"` 分支（沿用 channel 的看板娘文案或新增）。

### 4.2 新增 `Views/RdpMultiViewPage.xaml(.cs)`

- 从 `RdpChannelPage` **整体迁出**：`_cells` 四格、`LayoutSurfaces()`、`PlaceCell()`、分页条、空态提示、「弹出窗口」、多画面下双击=弹窗。
- 数据源固定 `VM.SurfaceGridChannelIds`（多画面语义），无 `_multiView` 分支、无单画面按钮。
- **不给全屏**（既定决策）：无 `RdpFullScreenCoordinator`。
- 空态：一条可显示通道都没有时，显示引导文案 + 「去通道管理」按钮（`Navigate("channel-mgmt")`）。
- 生命周期照抄现有契约：`OnNavigatedFrom` 只解挂各格（`SetClient(null)`），不断会话。

### 4.3 `Views/RdpChannelPage.xaml(.cs)` — 简化

- 删除：单画面/多画面切换按钮组、`_multiView`/`_gridPage`/`ResolveSurfaceIds` 的多画面分支、`PlaceCell` 多格布局、分页条。
- 画面区固定为单格（Cell0 跨满，保留 `Height="*"` 画面优先 + 事件卡折叠）。
- 保留：全屏（`RdpFullScreenCoordinator`，F11/双击）、连接/断开、弹出窗口入口、心跳/进度/事件。
- `OnCellFullScreen` 里的"多画面下弹窗"分支删除。

### 4.4 `Views/ChannelsPage.xaml(.cs)`

- 删除「画面在会话通道页面里」提示卡，改为「打开多画面」（`Navigate("channels")`）。
- 其余（通道 CRUD、凭据、部署代理、预检）不动；自检模式 `--embed-vm` 路径不动。

### 4.5 `ViewModels/MainViewModel.cs`

- `Navigate()` 支持 `channel-mgmt` tag。
- D2 选 b 时：暴露已启用通道的只读集合供菜单重建（沿用 `ChannelDisplayName` 等现有 helper）。
- `UpdateMascotForPage("multiview")`。

### 4.6 冒烟（`_smoke/`）

- 新增源码级断言（`TaskScopeCheck.cs` 同目录可加一例）：
  - `MainWindow` 源里不再出现 `执行中的通道` 平级插入逻辑；
  - `RdpChannelPage` 源不再包含 `MultiViewButton` / `GridPageSize`；
  - tag `channel-mgmt` 与 `RdpMultiViewPage` 的导航映射存在。
- ⚠️ 断言挑**调用写法/标识符**，不做整文件否定式扫描（注释里提到就误报——老规矩）。

## 5. 风险与边界

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| 父项可点性 | WinUI NavigationView 有子项时，点击主体默认只**展开**不触发 `SelectionChanged` | 改监听 `ItemInvoked`（父/子都会触发），导航逻辑只认 tag；真机验证点击手感和选中回显 |
| 选中态回显 | 父项被"导航"后 `SelectedItem` 指向父项是否正常高亮需真机确认 | `SyncNavigationSelection` 里直接赋 `SelectedItem`；异常时降级为高亮子项「多画面」占位 |
| 双渲染器抢占 | 多画面页与弹出窗口/通道页同时挂同一 client | 沿用 `IsSurfacePoppedOut` 让位 + `SetClient` 同实例短路，迁移时原样搬运不改逻辑 |
| 通道数量 | 子菜单 8 通道（上限）高度可接受 | 不做折叠/滚动，超限再说 |
| 自检流程 | `--embed-vm` 依赖 ChannelsPage 自动连接 | 只改导航目标字符串，页面逻辑零改动 |
| ⚠️ XAML 陷阱（实施时踩到） | 父项的子项若直接写成子元素，会被当成 `Content` 赋值 → **WMC0035 Duplication assignment to the 'Content' property** | 子项必须放进 `<NavigationViewItem.MenuItems>` 属性元素；代码侧用 `parent.MenuItems.Add(...)` |

## 6. 实施步骤

- **M1**：MainWindow 菜单父子化 + tag 重排 + `ItemInvoked` 导航（含自检路径、看板娘分支）。
- **M2**：新建 `RdpMultiViewPage`，从 RdpChannelPage 迁出网格/分页/弹窗逻辑。
- **M3**：RdpChannelPage 删多画面分支，简化为纯单画面。
- **M4**：ChannelsPage 提示卡改「打开多画面」；MainViewModel tag/看板娘跟进。
- **M5**：冒烟断言 + `dotnet build -c Release -p:Platform=x64` + `_smoke`；真机验收（见 §7）。

## 7. 验收清单

1. 不执行任务时：左侧「会话通道」可展开，子项列出已启用通道（未连接态）+「通道管理…」。
2. 点父项「会话通道」→ 多画面页：所有可显示通道 2×2 网格，>4 路翻页，空态有引导。
3. 点子项 zdh → 只见 zdh 的画面/进度/心跳/事件，无「单画面/多画面」按钮；可全屏（F11/双击）、可弹窗。
4. 开始执行后：子项出现运行状态角标；多画面页实时反映各路画面；切页画面流不断（DetachClient 契约）。
5. 停止/结束后：通道子项保留可回看；「执行中的通道」分组标题不再出现。
6. 「通道管理…」进出正常，部署代理/预检等功能不受影响；`--embed-vm` 自检仍走通。
7. 回归：主页通道概览、看板娘面板、停止链路（F8 / Ctrl+Alt+F8）不受影响。
