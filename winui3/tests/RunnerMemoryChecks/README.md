# RunnerMemoryChecks

这是一个不依赖 WinUI、也不启动真实脚本的 `net8.0-windows` 控制台回归工程。它链接真实 `ScriptRunner` 与其任务调度所需的模型、日志、统计、进程适配和路径源码；仅对截图服务提供不触发的测试 stub，因为所有测试都关闭超时截图。

运行：

```powershell
dotnet run --project .\winui3\tests\RunnerMemoryChecks\RunnerMemoryChecks.csproj --no-restore
```

覆盖真实行为：

- 并发调用 `RunAsync` 返回同一个执行任务；
- `WaitFirst` 并发组中无效脚本能完成，不因首任务等待卡住；
- `TaskCompleted` 事件抛异常后 worker 不挂死，后续完整并发组仍可运行；
- 延迟 Enter 使用可取消的 awaitable，停止后及时结束；
- 真实 cap16 槽位等待路径在停止竞态下归还刚取得的 slot。

测试不会启动真实脚本进程，也不会构建主应用。