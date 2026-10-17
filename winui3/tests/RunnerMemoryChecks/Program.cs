using System.Diagnostics;
using System.Reflection;
using YukinoChan.Models;
using YukinoChan.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("RunAsync concurrent callers share one task", TestRunAsyncIsIdempotent),
    ("WaitFirst invalid scripts finish without hanging", TestWaitFirstInvalidScripts),
    ("TaskCompleted event exceptions do not hang and release workers", TestTaskCompletedExceptionReleasesSlots),
    ("Delayed Enter is cancellable without a dedicated sleeper", TestDelayedEnterCancellation),
    ("Task slot cap and stop-time slot return", TestTaskSlotCapAndStopReturn),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} test groups passed.");
if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

return 0;

static async Task TestRunAsyncIsIdempotent()
{
    using var fixture = new RunnerFixture();
    var runner = fixture.CreateRunner(Array.Empty<TaskConfig>());
    var first = runner.RunAsync();
    var second = runner.RunAsync();
    Assert(ReferenceEquals(first, second), "concurrent RunAsync calls return the same Task instance");
    await first.WaitAsync(TimeSpan.FromSeconds(5));
}

static async Task TestWaitFirstInvalidScripts()
{
    using var fixture = new RunnerFixture();
    var tasks = Enumerable.Range(0, 2).Select(index => new TaskConfig
    {
        Name = $"wait-first-{index}",
        Order = index + 1,
        ScriptPath = Path.Combine(fixture.Path, $"missing-{index}.cmd"),
        ConcurrentGroup = "wait-first",
        ConcurrentPolicy = ConcurrentPolicies.WaitFirst,
    }).ToArray();
    var completed = 0;
    var runner = fixture.CreateRunner(tasks);
    runner.TaskCompleted += (_, _) => Interlocked.Increment(ref completed);

    await runner.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Equal(2, completed, "WaitFirst group reports every invalid script without waiting forever");
}

static async Task TestTaskCompletedExceptionReleasesSlots()
{
    using var fixture = new RunnerFixture();
    var tasks = MakeInvalidConcurrentTasks(fixture.Path, 17, "throwing-events");
    var runner = fixture.CreateRunner(tasks);
    runner.TaskCompleted += (_, _) => throw new InvalidOperationException("test event failure");

    await runner.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

    // A second full group proves worker finally blocks returned every acquired slot.
    var followUp = fixture.CreateRunner(MakeInvalidConcurrentTasks(fixture.Path, 17, "after-throw"));
    await followUp.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
}

static async Task TestDelayedEnterCancellation()
{
    using var fixture = new RunnerFixture();
    var runner = fixture.CreateRunner(Array.Empty<TaskConfig>());
    var method = typeof(ScriptRunner).GetMethod("SchedulePressEnterAsync", BindingFlags.Instance | BindingFlags.NonPublic);
    Assert(method is not null, "delayed Enter scheduler exists");

    var started = Stopwatch.StartNew();
    var scheduled = (Task?)method!.Invoke(runner, new object[] { 30, "cancelled-enter" });
    Assert(scheduled is not null, "delayed Enter returns an awaitable task");
    Assert(!scheduled!.IsCompleted, "delayed Enter remains pending before its delay expires");
    Assert(started.Elapsed < TimeSpan.FromSeconds(1), "scheduling delayed Enter returns promptly");

    runner.RequestStop();
    await scheduled.WaitAsync(TimeSpan.FromSeconds(2));
}

static async Task TestTaskSlotCapAndStopReturn()
{
    using var fixture = new RunnerFixture();
    var runner = fixture.CreateRunner(Array.Empty<TaskConfig>());
    var slotsField = typeof(ScriptRunner).GetField("s_taskThreadSlots", BindingFlags.Static | BindingFlags.NonPublic);
    var waitMethod = typeof(ScriptRunner).GetMethod("WaitForTaskThreadSlot", BindingFlags.Instance | BindingFlags.NonPublic);
    Assert(slotsField is not null && waitMethod is not null, "runner exposes its real slot wait path for this memory check");
    var slots = (SemaphoreSlim)slotsField!.GetValue(null)!;

    for (var i = 0; i < AppConfig.MaxActiveTaskThreads; i++)
    {
        var acquired = (bool)waitMethod!.Invoke(runner, null)!;
        Assert(acquired, $"slot {i + 1} can be acquired");
    }
    Equal(0, slots.CurrentCount, "cap16 means no seventeenth slot is available");

    var waiter = Task.Run(() => (bool)waitMethod!.Invoke(runner, null)!);
    await Task.Delay(150);
    Assert(!waiter.IsCompleted, "the seventeenth slot waits instead of exceeding cap16");

    runner.RequestStop();
    slots.Release();
    var returned = await waiter.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(!returned, "a slot acquired after stop is immediately returned and rejected");
    Equal(1, slots.CurrentCount, "stop path returned the slot it had just acquired");

    slots.Release(AppConfig.MaxActiveTaskThreads - 1);
    Equal(AppConfig.MaxActiveTaskThreads, slots.CurrentCount, "all manually held slots are restored for fixture cleanup");
}

static TaskConfig[] MakeInvalidConcurrentTasks(string root, int count, string group)
{
    return Enumerable.Range(0, count).Select(index => new TaskConfig
    {
        Name = $"{group}-{index}",
        Order = index + 1,
        ScriptPath = Path.Combine(root, $"missing-{group}-{index}.cmd"),
        ConcurrentGroup = group,
        ConcurrentPolicy = ConcurrentPolicies.WaitAll,
    }).ToArray();
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}; expected '{expected}', actual '{actual}'");
    }
}

sealed class RunnerFixture : IDisposable
{
    public RunnerFixture()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YukinoChan.RunnerMemoryChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        LogDir = System.IO.Path.Combine(Path, "logs");
        StatsDir = System.IO.Path.Combine(Path, "stats");
    }

    public string Path { get; }
    public string LogDir { get; }
    public string StatsDir { get; }

    public ScriptRunner CreateRunner(IEnumerable<TaskConfig> tasks) => new(
        tasks,
        shutdownAfterDone: false,
        shutdownDelaySeconds: 0,
        logger: new FileLogger(LogDir),
        stats: new RuntimeStatsManager(StatsDir),
        enableTimeoutScreenshot: false);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { }
    }
}