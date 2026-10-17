using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using YukinoChan.Models;
using YukinoChan.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length > 0 && string.Equals(args[0], "--write-log", StringComparison.OrdinalIgnoreCase))
{
    return WriteSyntheticLog(args);
}

if (args.Length > 0 && string.Equals(args[0], "--observe", StringComparison.OrdinalIgnoreCase))
{
    return ObserveBridge(args);
}

var tests = new (string Name, Action Run)[]
{
    ("RdpBridge live-log NDJSON protocol", TestRdpBridgeLiveLogs),
    ("RdpChannelSession isolation and bounded collections", TestRdpChannelSession),
    ("ExternalLogFilter normalization and classification", TestExternalLogFilter),
    ("ExternalLogTail incremental, rotation, partial lines and encoding", TestExternalLogTail),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
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

static int WriteSyntheticLog(string[] args)
{
    if (args.Length != 3 || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2]))
    {
        Console.Error.WriteLine("Usage: --write-log <path> <label>");
        return 2;
    }

    var path = Path.GetFullPath(args[1]);
    var label = args[2];
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    using var writer = new StreamWriter(
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
        new UTF8Encoding(false));
    for (var i = 1; i <= 15; i++)
    {
        writer.WriteLine($"[INFO] {label}-{i} 中文正文");
        writer.Flush();
        Thread.Sleep(200);
    }

    writer.WriteLine($"[INFO] {label}-FINAL");
    writer.Flush();
    Console.WriteLine($"WROTE path={path} label={label} rows=16");
    return 0;
}

static int ObserveBridge(string[] args)
{
    if (args.Length != 4 || !int.TryParse(args[3], out var expectedTaskCount) || expectedTaskCount < 0)
    {
        Console.Error.WriteLine("Usage: --observe <bridgeDir> <commandId> <expected task count>");
        return 2;
    }

    var bridge = new RdpBridge(args[1]);
    var session = new RdpChannelSession(null, bridge);
    session.Begin(args[2], expectedTaskCount, DateTimeOffset.Now);
    var observed = new List<RdpLiveLog>();
    var taskDoneCount = 0;
    var finishedCount = 0;
    var extLogEventCount = 0;
    var seenEventSeq = new HashSet<long>();
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
    var completed = false;

    while (DateTime.UtcNow < deadline)
    {
        var status = bridge.TryReadStatus();
        if (status is not null && string.Equals(status.CommandId, args[2], StringComparison.Ordinal))
        {
            var tick = session.Tick(status, DateTimeOffset.Now, 90);
            foreach (var ev in tick.NewEvents)
            {
                if (ev.Kind == RdpEventKinds.TaskDone)
                {
                    taskDoneCount++;
                }
                else if (ev.Kind == RdpEventKinds.Finished)
                {
                    finishedCount++;
                }
            }

            foreach (var ev in status.Events)
            {
                if (ev.Kind == RdpEventKinds.ExternalLog && seenEventSeq.Add(ev.Seq))
                {
                    extLogEventCount++;
                }
            }

            if (tick.SawFinished || status.Phase is "done" or "stopped" or "error")
            {
                completed = true;
            }
        }

        observed.AddRange(session.ConsumeLiveLogs());
        if (completed)
        {
            break;
        }

        Thread.Sleep(200);
    }

    var finalSeen = observed.Any(log => log.Message.Contains("FINAL", StringComparison.Ordinal));
    var ordinaryEventsOk = taskDoneCount == expectedTaskCount && finishedCount == 1;
    var success = completed && observed.Count > 0 && finalSeen && ordinaryEventsOk && extLogEventCount == 0;
    Console.WriteLine(
        $"OBSERVE completed={completed} observed={observed.Count} final={finalSeen} " +
        $"task_done={taskDoneCount}/{expectedTaskCount} finished={finishedCount}/1 ext_log={extLogEventCount} " +
        $"result={(success ? "PASS" : "FAIL")}");
    return success ? 0 : 1;
}

static void TestRdpBridgeLiveLogs()
{
    using var temp = new TempDirectory();
    var bridge = new RdpBridge(temp.Path);
    const string commandId = "cmd-中文-1";
    var first = new RdpLiveLog
    {
        Seq = 1,
        Time = "12:00:01",
        TaskName = "BetterGI",
        Message = "中文第一行\n第二行",
        Level = "info",
    };
    var second = new RdpLiveLog
    {
        Seq = 2,
        Time = "12:00:02",
        TaskName = "MaaEnd",
        Message = "任务完成",
        Level = "warn",
        IsAbnormal = true,
    };

    Assert(bridge.AppendLiveLogBatch(commandId, new[] { first, second }), "append initial batch");
    var offset = 0L;
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var read), "read initial batch");
    Equal(2, read.Count, "initial row count");
    Equal(commandId, read[0].CommandId, "command id is assigned on append");
    Equal(first.Message, read[0].Message, "newline-containing message survives JSON round trip");
    Equal(second.Message, read[1].Message, "Chinese message survives JSON round trip");

    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var duplicateRead), "repeat read");
    Equal(0, duplicateRead.Count, "incremental read does not duplicate rows");

    var path = bridge.LiveLogPath(commandId);
    var half = new RdpLiveLog { CommandId = commandId, Seq = 3, Message = "半行恢复" };
    var halfJson = JsonSerializer.Serialize(half);
    File.AppendAllText(path, halfJson[..(halfJson.Length / 2)], new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var incomplete), "read incomplete row");
    Equal(0, incomplete.Count, "incomplete JSON row is held at the byte offset");
    File.AppendAllText(path, halfJson[(halfJson.Length / 2)..] + "\n", new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var recovered), "read completed row");
    Equal(1, recovered.Count, "completed half row is recovered");
    Equal("半行恢复", recovered[0].Message, "recovered message");

    File.AppendAllText(path, "{not-json}\n", new UTF8Encoding(false));
    var wrongCommand = JsonSerializer.Serialize(new RdpLiveLog
    {
        CommandId = "other-command",
        Seq = 99,
        Message = "不应出现",
    });
    File.AppendAllText(path, wrongCommand + "\n", new UTF8Encoding(false));
    File.AppendAllText(path, JsonSerializer.Serialize(new RdpLiveLog
    {
        CommandId = commandId,
        Seq = 4,
        Message = "坏行之后仍可读",
    }) + "\n", new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var afterBadRows), "read after malformed rows");
    Equal(1, afterBadRows.Count, "malformed and wrong-command rows are filtered without blocking");
    Equal("坏行之后仍可读", afterBadRows[0].Message, "valid row after malformed rows");

    File.WriteAllText(path, JsonSerializer.Serialize(new RdpLiveLog
    {
        CommandId = commandId,
        Seq = 5,
        Message = "截断后重新开始",
    }) + "\n", new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var afterTruncate), "read after truncation");
    Equal(1, afterTruncate.Count, "file truncation resets the byte cursor");
    Equal("截断后重新开始", afterTruncate[0].Message, "row after truncation");

    Assert(!bridge.TryReadLiveLogs("missing", ref offset, out var missing), "missing command file reports unavailable input");
    Equal(0, missing.Count, "missing command file yields no rows");
}

static void TestRdpChannelSession()
{
    using var temp = new TempDirectory();
    var bridge = new RdpBridge(temp.Path);
    var session = new RdpChannelSession(null, bridge);
    session.Begin("session-a", total: 1, DateTimeOffset.UtcNow);
    session.SetEventLineLimit(20);

    for (var i = 1; i <= RdpChannelSession.MaxExternalLogLines + 20; i++)
    {
        Assert(bridge.AppendLiveLogBatch("session-a", new[]
        {
            new RdpLiveLog { CommandId = "session-a", Seq = i, Message = $"日志-{i}" },
        }), $"append live row {i}");
    }

    var ordinaryEvents = new List<RdpTaskEvent>();
    for (var i = 1; i <= 23; i++)
    {
        ordinaryEvents.Add(new RdpTaskEvent
        {
            Seq = i,
            Kind = RdpEventKinds.Log,
            Message = $"ordinary-{i}",
        });
    }

    ordinaryEvents.Add(new RdpTaskEvent { Seq = 24, Kind = RdpEventKinds.TaskDone, Message = "task_done" });
    ordinaryEvents.Add(new RdpTaskEvent { Seq = 25, Kind = RdpEventKinds.Finished, Message = "finished" });
    var ordinary = new RdpStatus
    {
        CommandId = "session-a",
        Phase = "done",
        UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
        Events = ordinaryEvents,
    };
    var tick = session.Tick(ordinary, DateTimeOffset.UtcNow, 30);
    var consumed = session.ConsumeLiveLogs();
    Equal(RdpChannelSession.MaxExternalLogLines, session.ExternalLogs.Count, "live-log collection is bounded");
    Equal(20, session.Events.Count, "ordinary event collection has its independent capacity");
    Equal(25, tick.NewEvents.Count, "ordinary events remain independent of live-log volume");
    Equal(RdpEventKinds.TaskDone, tick.NewEvents[^2].Kind, "task_done remains an ordinary event");
    Equal(RdpEventKinds.Finished, tick.NewEvents[^1].Kind, "finished event survives live-log volume");
    Equal(220, consumed.Count, "all new live rows are consumed in one pass");
    Equal("日志-21", session.ExternalLogs[0].Message, "old live rows are evicted only from live collection");
    Equal("日志-220", session.ExternalLogs[^1].Message, "newest live row is retained");
    Equal(0, session.ConsumeLiveLogs().Count, "session incremental consumption does not duplicate rows");

    session.Begin("session-b", total: 1, DateTimeOffset.UtcNow);
    Assert(bridge.AppendLiveLogBatch("session-a", new[]
    {
        new RdpLiveLog { CommandId = "session-a", Seq = 220, Message = "旧指令日志" },
    }), "append old command after reset");
    Assert(bridge.AppendLiveLogBatch("session-b", new[]
    {
        new RdpLiveLog { CommandId = "session-b", Seq = 1, Message = "新指令日志" },
    }), "append new command after reset");
    var isolated = session.ConsumeLiveLogs();
    Equal(1, isolated.Count, "new command consumes only its own stream");
    Equal("新指令日志", isolated[0].Message, "command reset prevents old stream leakage");
}

static void TestExternalLogFilter()
{
    var parsed = ExternalLogFilter.Parse("2026-10-17 12:00:00 [ERROR] 中文\u001b[31m故障\u001b[0m", new[] { "故障" });
    Equal("2026-10-17 12:00:00 [ERROR] 中文故障", parsed.Text, "ANSI/control characters are removed");
    Equal(ExternalLogLevel.Error, parsed.Level, "bracket error level is classified");
    Assert(parsed.KeywordHit, "keyword hit is detected");

    var warning = ExternalLogFilter.Parse("12:00:01 | WARN | 连接超时", Array.Empty<string>());
    Equal(ExternalLogLevel.Warn, warning.Level, "pipe warning level is classified");
    var info = ExternalLogFilter.Parse("2026-10-17 12:00:02 INFO ready", Array.Empty<string>());
    Equal(ExternalLogLevel.Info, info.Level, "timestamp bare info level is classified");
    var bodyOnlyError = ExternalLogFilter.Parse("normal message: error-like text", Array.Empty<string>());
    Equal(ExternalLogLevel.Unknown, bodyOnlyError.Level, "body text does not become a level");
    Assert(ExternalLogFilter.NormalizeLine("a\n\r  b") == "a b", "line breaks and whitespace are normalized");
}

static void TestExternalLogTail()
{
    using var temp = new TempDirectory();
    var failures = new List<string>();

    void Check(string name, Action assertion)
    {
        try
        {
            assertion();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }

    var tailDirectory = Path.Combine(temp.Path, "tail");
    Directory.CreateDirectory(tailDirectory);
    var file = Path.Combine(tailDirectory, "current.log");
    File.WriteAllText(file, "旧内容\n", new UTF8Encoding(false));

    using var tail = new ExternalLogTail(tailDirectory, "*.log", ExternalLogEncodings.Utf8, followTail: true);
    Check("follow-tail starts at current end", () => Equal(0, tail.Poll(Array.Empty<string>()).Count, "follow-tail starts at current end"));

    AppendUtf8InChunks(file, "第一条中文\n第二条\n", chunkSize: 2);
    Check("UTF-8 byte chunks produce complete incremental lines", () =>
    {
        var first = tail.Poll(Array.Empty<string>());
        Equal(2, first.Count, "UTF-8 byte chunks produce complete incremental lines");
        Equal("第一条中文", first[0].Text, "UTF-8 Chinese text is decoded");
    });

    var bytewiseFile = Path.Combine(temp.Path, "bytewise.log");
    File.WriteAllText(bytewiseFile, string.Empty, new UTF8Encoding(false));
    using var bytewiseTail = new ExternalLogTail(bytewiseFile, "*.log", ExternalLogEncodings.Utf8, followTail: false);
    var bytewiseText = Encoding.UTF8.GetBytes("跨Poll中文\n");
    for (var i = 0; i < bytewiseText.Length; i++)
    {
        using (var stream = new FileStream(bytewiseFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.WriteByte(bytewiseText[i]);
            stream.Flush();
        }

        var rows = bytewiseTail.Poll(Array.Empty<string>());
        if (i < bytewiseText.Length - 1)
        {
            Equal(0, rows.Count, $"bytewise UTF-8 poll {i + 1} waits for newline");
        }
        else
        {
            Equal(1, rows.Count, "bytewise UTF-8 polling emits one complete line");
            Equal("跨Poll中文", rows[0].Text, "UTF-8 decoder preserves characters across Poll calls");
        }
    }

    var halfFile = Path.Combine(temp.Path, "half.log");
    File.WriteAllText(halfFile, "半行", new UTF8Encoding(false));
    using var halfTail = new ExternalLogTail(halfFile, "*.log", ExternalLogEncodings.Utf8, followTail: false);
    Check("tail waits for a complete line", () => Equal(0, halfTail.Poll(Array.Empty<string>()).Count, "tail waits for a complete line"));
    File.AppendAllText(halfFile, "完成\n", new UTF8Encoding(false));
    Check("half line is emitted after newline arrives", () =>
    {
        var completed = halfTail.Poll(Array.Empty<string>());
        Equal(1, completed.Count, "half line is emitted after newline arrives");
        Equal("半行完成", completed[0].Text, "half-line text is preserved");
    });

    var rotationDir = Path.Combine(temp.Path, "rotation");
    Directory.CreateDirectory(rotationDir);
    var oldRotated = Path.Combine(rotationDir, "old.log");
    File.WriteAllText(oldRotated, "旧轮转内容\n", new UTF8Encoding(false));
    using var rotationTail = new ExternalLogTail(rotationDir, "*.log", ExternalLogEncodings.Utf8, followTail: true);
    var rotated = Path.Combine(rotationDir, "rotated.log");
    File.WriteAllText(rotated, "轮转后\n", new UTF8Encoding(false));
    Touch(rotated, DateTime.UtcNow.AddMinutes(1));
    Check("newest matching file is followed after rotation", () =>
    {
        var afterRotation = rotationTail.Poll(Array.Empty<string>());
        Equal(1, afterRotation.Count, "newest matching file is followed after rotation");
        Equal("轮转后", afterRotation[0].Text, "rotated file content is read");
    });

    File.WriteAllText(rotated, "重写\n", new UTF8Encoding(false));
    Touch(rotated, DateTime.UtcNow.AddMinutes(2));
    Check("truncated/recreated content is reread", () =>
    {
        var afterRewrite = rotationTail.Poll(Array.Empty<string>());
        Equal(1, afterRewrite.Count, "truncated/recreated content is reread");
        Equal("重写", afterRewrite[0].Text, "rewritten file content is read");
    });

    var gbkFile = Path.Combine(temp.Path, "gbk.log");
    File.WriteAllText(gbkFile, "启动中文\n", Encoding.GetEncoding("GBK"));
    using var gbkTail = new ExternalLogTail(gbkFile, "*.log", ExternalLogEncodings.Gbk, followTail: false);
    Check("explicit GBK file is read", () =>
    {
        var gbk = gbkTail.Poll(Array.Empty<string>());
        Equal(1, gbk.Count, "explicit GBK file is read");
        Equal("启动中文", gbk[0].Text, "GBK Chinese text is decoded");
    });

    if (failures.Count > 0)
    {
        throw new InvalidOperationException(string.Join(" | ", failures));
    }
}

static void AppendUtf8InChunks(string path, string text, int chunkSize)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    for (var offset = 0; offset < bytes.Length; offset += chunkSize)
    {
        var count = Math.Min(chunkSize, bytes.Length - offset);
        stream.Write(bytes, offset, count);
        stream.Flush();
    }
}

static void Touch(string path, DateTime utc)
{
    File.SetLastWriteTimeUtc(path, utc);
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

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YukinoChan.LogChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Keep test cleanup best-effort; assertion failures remain the signal.
        }
    }
}
