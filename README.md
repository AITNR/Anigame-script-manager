# 雪乃酱 · 二游脚本统合管理器

**让每日清体力变得更省心。**

Windows 桌面应用，用 C# + WinUI 3（Windows App SDK）写成。
把「一堆脚本按顺序跑一遍」这件事管起来：编排、超时、进程清理、远程多机并行、跑完自动关机。

- 当前版本：**v2.0 WinUI 3**
- 平台：Windows 10 1809（build 17763）及以上，推荐 Windows 11；x64
- 仓库：<https://github.com/AITNR/Anigame-script-manager>

---

## 它能做什么

| 能力 | 说明 |
|---|---|
| 任务编排 | 一串脚本（exe / bat / py …）按序执行，支持并发组（等全部完成 / 任一完成即推进）、超时动作、按窗口标题或进程名判定"跑完没" |
| 会话通道 | 把任务分给**不同账户、不同机器**并行执行 —— 通道之间并行、通道内部串行，最多 8 条 |
| 内嵌画面 | 远程桌面用原生 FreeRDP **内嵌**在窗口里，鼠标键盘直接操作；也可同屏并列多路（2×2 分页） |
| 运行反馈 | 看板娘状态面板、运行日志、脚本耗时统计（按来源区分是谁跑的） |
| 收尾 | 跑完自动关机（可取消的倒计时）、异常报告、开机自启动 |
| 快捷键 | `F8` 停止执行，`Ctrl+Alt+F8` 紧急停止 |

界面是原生 Fluent：Mica 背景、跟随系统主题、自定义标题栏、高 DPI 多显示器不糊。

---

## 快速开始

### 环境

| 项目 | 版本 |
|---|---|
| 操作系统 | Windows 10 1809 及以上，推荐 Windows 11 |
| .NET SDK | 8.0 |
| 目标框架 | `net8.0-windows10.0.19041.0` |
| 架构 | x64 |

### 编译

```bash
cd winui3
dotnet restore
dotnet build -c Release -p:Platform=x64
```

产物（`unpackaged` 模式，**双击即可运行**，不需要 MSIX 安装或侧载）：

```text
winui3/src/YukinoChan.App/bin/x64/Release/net8.0-windows10.0.19041.0/YukinoChan.exe
```

两个前提：

- **内嵌画面需要原生层 `ycn_rdp.dll`**（FreeRDP 桥接层）。它是构建产物、没有入库，全新克隆后要自己编一次，步骤见 [winui3/README.md](winui3/README.md) 第三节。
- **程序开着时别直接 `dotnet build`**：exe 被占用会导致资源包 `YukinoChan.pri` 没更新，启动后点页面会崩。先完全退出程序，或换输出目录构建，同样见 [winui3/README.md](winui3/README.md) 第三节。

---

## 文档

| 想看什么 | 去哪 |
|---|---|
| 完整说明：环境、编译、目录结构、会话通道详解 | [winui3/README.md](winui3/README.md) |
| 使用手册：界面地图、怎么配任务与通道、排障 | [winui3/USER_GUIDE.md](winui3/USER_GUIDE.md) |
| 变更记录 | [winui3/CHANGELOG.md](winui3/CHANGELOG.md) |
| 设计文档与计划书 | [winui3/docs/](winui3/docs/) |
| 从 Python / PySide6 版移植的记录 | [winui3/docs/migration-from-python.md](winui3/docs/migration-from-python.md) |
| 已知限制与后续建议 | [winui3/docs/known-limits-and-roadmap.md](winui3/docs/known-limits-and-roadmap.md) |

---

## 仓库结构

```text
winui3/       C# / WinUI 3 主工程（YukinoChan.sln、src/、docs/、tools/）
assets/       看板娘素材与图标 —— 同时是程序定位根目录的标记，不要删
config.json   运行时配置（任务、会话通道、窗口几何）
docs/         文档截图
```

数据目录（`config.json` / `logs/` / `runtime_stats/`）都在程序根目录下 ——
程序靠「同级有 `config.json` 或 `assets/`」向上回溯定位，所以 `assets/mascot/` 也在仓库里跟着走。

---

## 关于旧版本

仓库原先还有一套 Python + PySide6 实现（v1.0 RC1），已于 2026-09-28 移除，
现在 `winui3/` 是唯一主线。需要旧代码可从移除前的 Git 历史取回。

---

## 许可证

[MIT](LICENSE)
