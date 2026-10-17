# YukinoChan.LogChecks

这是一个不依赖 WinUI 的 `net8.0-windows` 控制台回归工程。生产源文件通过链接方式编译，测试工程不修改生产代码，也不需要加入解决方案。

## 普通回归

在仓库根目录执行：

```powershell
dotnet restore .\winui3\tests\YukinoChan.LogChecks\YukinoChan.LogChecks.csproj
dotnet run --project .\winui3\tests\YukinoChan.LogChecks\YukinoChan.LogChecks.csproj --no-restore
```

普通模式覆盖：

- `RdpBridge.For` 的规范化路径实例复用；`TryReadStatus` 的 UTF-8 BOM 兼容与 1 MiB 大小拒绝；`AppendLiveLogBatch` / `TryReadLiveLogs` 的 NDJSON、中文、换行、增量、半 JSON、坏行、错误 command 和截断；
- `RdpChannelSession` 的 live-log 容量、普通 `task_done` / `finished` 隔离和 command 重置；
- `ExternalLogFilter` 的规整、级别和关键字；
- `ExternalLogTail` 的增量、轮转、半行、UTF-8 分块、GBK，以及 4000+ 行积压跨多次 Poll 的完整有序消费。

tail 的半行断言用于锁定生产 tail 的完整行语义；若失败应作为回归失败报告，不要通过修改本测试绕过。

## 合成日志写入器

为 AgentHost + 真实 YukinoChan 代理 E2E 生成持续增长的 UTF-8 日志文件：

```powershell
dotnet run --project .\winui3\tests\YukinoChan.LogChecks\YukinoChan.LogChecks.csproj --no-restore -- --write-log <log-path> <label>
```

它写入 15 条 `[INFO] <label>-序号 中文正文`，每条写入后 flush 并等待 200ms，最后追加 `[INFO] <label>-FINAL`，成功返回 `0`。

## 桥观察器

在主控端向独立桥目录下发 command、由真实 `YukinoChan.exe --rdp-agent` 执行合成任务后，另开终端运行：

```powershell
dotnet run --project .\winui3\tests\YukinoChan.LogChecks\YukinoChan.LogChecks.csproj --no-restore -- --observe <bridgeDir> <commandId> <expected-task-count>
```

观察器每 200ms 读取真实 `RdpBridge` 状态和 live-log 流，最长等待 90 秒。成功条件是：收到本 command 的 `finished`、观察到 `FINAL`、`task_done` 数量等于期望任务数、`finished` 恰好一条，并且普通 `status.Events` 中没有 `ext_log`。输出包含 `observed` 总采集条数和所有检查项；成功返回 `0`，超时或任一条件失败返回 `1`。
