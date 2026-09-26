// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// 主控端与目标会话 Agent 之间的通信桥（**每通道一个实例**）。
///
/// 两个 Windows 用户会话之间不能直接通信，因此统一走共享目录下的一组文件：
///   command.json  主控端写、Agent 读（要执行的任务快照）
///   status.json   Agent 写、主控端读（进度 + 事件流）
///   stop.json     主控端写、Agent 读（请求停止 / 紧急停止）
/// 写入一律走"临时文件 + 原子替换"，读取容忍并发 IOException 并重试，
/// 避免主控端正在读时 Agent 正好重写导致解析到半个文件。
///
/// 为什么改成实例类：多会话通道并行时，每个通道必须有自己的一座桥
/// （同一个 command.json 会被多个代理抢读，任务就串了）。
/// 目录由 <see cref="RdpChannelPaths"/> 按通道/用户名派生，两端各自推算出同一个路径。
/// </summary>
public sealed class RdpBridge
{
    private const int MaxEvents = 500;
    private const int ReadRetry = 5;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, RdpBridge> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>建一座指向指定目录的桥。目录选取请优先用 <see cref="For"/> / <see cref="ForChannel"/>。</summary>
    public RdpBridge(string directory)
    {
        var text = (directory ?? string.Empty).Trim();
        BridgeDir = text.Length == 0 ? DefaultBridgeDir() : text;
    }

    /// <summary>本座桥的共享目录。</summary>
    public string BridgeDir { get; }

    /// <summary>是否用了自定义目录（不是按当前账户派生的默认目录）。</summary>
    public bool IsCustomized =>
        !string.Equals(
            Path.GetFullPath(BridgeDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(DefaultBridgeDir()).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>最近一次写入失败的原因，用于界面提示与排障。</summary>
    public string LastError { get; private set; } = string.Empty;

    public string CommandPath => Path.Combine(BridgeDir, "command.json");

    public string StatusPath => Path.Combine(BridgeDir, "status.json");

    /// <summary>停止请求文件：主控端写、Agent 读。老版本 Agent 不认识它，写了也不会响应（无害）。</summary>
    public string StopPath => Path.Combine(BridgeDir, "stop.json");

    // ---------------- 桥实例的选取 ----------------

    /// <summary>
    /// 默认桥目录：<c>&lt;ProgramData&gt;\YukinoChan\rdp\&lt;当前账户&gt;</c>。
    /// 代理侧按"自己在哪个账户"算，主控端按"通道配的账户"算，两边自然对齐。
    /// </summary>
    public static string DefaultBridgeDir() => RdpChannelPaths.AgentBridgeDir(Environment.UserName);

    /// <summary>按目录取桥实例（同目录复用同一实例）。传空 = 默认目录。</summary>
    public static RdpBridge For(string? directory)
    {
        var text = (directory ?? string.Empty).Trim();
        var key = text.Length == 0 ? DefaultBridgeDir() : text;

        lock (CacheGate)
        {
            if (!Cache.TryGetValue(key, out var bridge))
            {
                bridge = new RdpBridge(key);
                Cache[key] = bridge;
            }

            return bridge;
        }
    }

    /// <summary>按通道取桥实例：配了 bridge_path 用它，否则按通道账户派生。</summary>
    public static RdpBridge ForChannel(RdpChannel? channel) =>
        For(RdpChannelPaths.BridgeDirFor(channel));

    /// <summary>
    /// Agent 进程用的桥。
    /// 启动时由 <see cref="ConfigureAgent"/> 设定（命令行 --bridge: 优先，否则按当前账户派生）。
    /// </summary>
    public static RdpBridge Agent { get; private set; } = For(null);

    /// <summary>Agent 启动时确定自己该读写哪座桥。</summary>
    public static void ConfigureAgent(string? customPath)
    {
        Agent = For(customPath);
    }

    // ---------------- 共享目录 ----------------

    public void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(BridgeDir);
        }
        catch
        {
            // 目录创建失败会在后续写入时明确报错，这里不阻断启动
        }
    }

    /// <summary>在资源管理器里打开共享目录（排障用）。</summary>
    public void OpenBridgeFolder()
    {
        EnsureDirectory();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"\"{BridgeDir}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------- 主控端：下发指令 ----------------

    public bool WriteCommand(RdpCommand command)
    {
        EnsureDirectory();
        try
        {
            LastError = string.Empty;
            return WriteAtomic(CommandPath, JsonSerializer.Serialize(command, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>清空上一次的状态，并写入一个 idle 占位，让主控端立刻知道 Agent 还没接手。</summary>
    public void ResetStatus(string commandId)
    {
        EnsureDirectory();
        var status = new RdpStatus
        {
            CommandId = commandId,
            Phase = "idle",
            StatusText = "等待目标会话中的雪乃酱接管…",
        };
        try
        {
            LastError = string.Empty;
            WriteAtomic(StatusPath, JsonSerializer.Serialize(status, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // ---------------- 主控端：请求停止 ----------------

    /// <summary>请求目标会话停止当前指令（Agent 每秒检查一次）。</summary>
    public bool WriteStop(string commandId, bool emergency)
    {
        EnsureDirectory();
        try
        {
            LastError = string.Empty;
            var request = new RdpStopRequest { CommandId = commandId ?? string.Empty, Emergency = emergency };
            return WriteAtomic(StopPath, JsonSerializer.Serialize(request, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>读停止请求（Agent 侧）。读不到或解析失败都返回 null。</summary>
    public RdpStopRequest? TryReadStop()
    {
        try
        {
            if (!File.Exists(StopPath))
            {
                return null;
            }

            var text = ReadTextWithRetry(StopPath);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize<RdpStopRequest>(text, ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Agent 侧判定：是否有人请求停止**当前这条指令**。
    /// 归属校验不可省 —— 桥里可能残留上一轮指令的 stop.json，
    /// 不校验的话新指令一启动就会被它立刻停掉。
    /// </summary>
    public bool IsStopRequested(string currentCommandId, out bool emergency)
    {
        emergency = false;
        var request = TryReadStop();
        if (request is null || string.IsNullOrEmpty(currentCommandId))
        {
            return false;
        }

        if (!string.Equals(request.CommandId, currentCommandId, StringComparison.Ordinal))
        {
            return false;
        }

        emergency = request.Emergency;
        return true;
    }

    /// <summary>处理完停止请求后删掉它，避免重复触发。</summary>
    public void ClearStop()
    {
        TryDeleteQuietly(StopPath);
    }

    // ---------------- Agent：回写状态 ----------------

    /// <summary>Agent 侧读改写状态。Agent 是 status.json 的唯一写者，无需加锁。</summary>
    public bool UpdateStatus(Func<RdpStatus, RdpStatus> mutator)
    {
        EnsureDirectory();
        try
        {
            LastError = string.Empty;
            RdpStatus status;
            try
            {
                var existing = ReadTextWithRetry(StatusPath);
                status = string.IsNullOrWhiteSpace(existing)
                    ? new RdpStatus()
                    : JsonSerializer.Deserialize<RdpStatus>(existing, ReadOptions) ?? new RdpStatus();
            }
            catch
            {
                status = new RdpStatus();
            }

            status = mutator(status);
            status.UpdatedAt = DateTimeOffset.Now.ToString("o");
            status.Events = TrimEvents(status.Events);

            return WriteAtomic(StatusPath, JsonSerializer.Serialize(status, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Agent 追加一条事件，序号自增。</summary>
    public static void AppendEvent(RdpStatus current, RdpTaskEvent ev)
    {
        current.Events.Add(ev);
    }

    /// <summary>把事件列表裁剪到上限，只保留最新的若干条（纯逻辑，不碰文件）。</summary>
    public static List<RdpTaskEvent> TrimEvents(List<RdpTaskEvent>? events)
    {
        if (events is null)
        {
            return new List<RdpTaskEvent>();
        }

        return events.Count <= MaxEvents
            ? events
            : events.GetRange(events.Count - MaxEvents, MaxEvents);
    }

    public static RdpTaskEvent MakeEvent(RdpStatus current, string kind, string message, string taskName = "",
        int index = 0, int total = 0, int elapsedSeconds = 0, bool abnormal = false)
    {
        var nextSeq = current.Events.Count == 0 ? 1 : current.Events[^1].Seq + 1;
        return new RdpTaskEvent
        {
            Seq = nextSeq,
            Kind = kind,
            Time = DateTime.Now.ToString("HH:mm:ss"),
            TaskName = taskName,
            Message = message,
            Index = index,
            Total = total,
            ElapsedSeconds = elapsedSeconds,
            IsAbnormal = abnormal,
        };
    }

    // ---------------- 双方通用读取 ----------------

    public bool TryReadCommand(out RdpCommand? command)
    {
        command = null;
        try
        {
            if (!File.Exists(CommandPath))
            {
                return false;
            }

            var text = ReadTextWithRetry(CommandPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            command = JsonSerializer.Deserialize<RdpCommand>(text, ReadOptions);
            return command is not null;
        }
        catch
        {
            return false;
        }
    }

    public RdpStatus? TryReadStatus()
    {
        try
        {
            if (!File.Exists(StatusPath))
            {
                return null;
            }

            var text = ReadTextWithRetry(StatusPath);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize<RdpStatus>(text, ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清掉指令文件（Agent 读到并接管后调用，防止代理被再次拉起时重复执行同一批任务）。
    ///
    /// 首选直接删。跨账户场景下删除可能被 ACL 拦（指令文件是主控端账户创建的），
    /// 这时退化为清空内容 —— 效果一样：读取方看到空内容会当作"没有指令"。
    /// </summary>
    public void ConsumeCommand()
    {
        var path = CommandPath;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 落到下面清空内容
        }
        catch (IOException)
        {
            // 文件被占用等情况，同样清空内容
        }

        try
        {
            if (File.Exists(path))
            {
                File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 彻底没辙就留着，但读取方会因为内容为空而当作"没有指令"
        }
    }

    // ---------------- 内部实现 ----------------

    /// <summary>
    /// 写文件。按可靠性从高到低尝试三种方式：
    ///   1. 临时文件 + 原子替换（读取方永远看不到半截内容）
    ///   2. 删掉目标再重命名（目标文件不允许被覆盖时）
    ///   3. 直接覆写（前面都不行时的兜底）
    /// 跨账户场景下目标文件是另一个账户创建的，ACL 常常不允许删除，
    /// 所以必须有兜底 —— 写不进去会让整个任务链路断掉，比"可能读到半截"严重得多。
    /// </summary>
    private bool WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先放开目标文件的权限。必须放在替换之前：
        // 否则别的账户创建的文件会一直挡住后续所有写入。
        GrantUsersFullControl(path);

        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            GrantUsersFullControl(temp);
        }
        catch
        {
            // 临时文件都写不出来，直接试覆写
            TryDeleteQuietly(temp);
            return OverwriteDirectly(path, content);
        }

        // 方式 1：原子替换
        try
        {
            File.Move(temp, path, overwrite: true);
            GrantUsersFullControl(path);
            return true;
        }
        catch
        {
            // 落到方式 2
        }

        // 方式 2：先删目标再重命名
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
            GrantUsersFullControl(path);
            return true;
        }
        catch
        {
            // 落到方式 3
        }

        // 方式 3：直接覆写
        TryDeleteQuietly(temp);
        return OverwriteDirectly(path, content);
    }

    /// <summary>兜底写入：直接覆写目标文件。失败时抛异常，由调用方记录 LastError。</summary>
    private bool OverwriteDirectly(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            GrantUsersFullControl(path);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    /// <summary>删文件，失败就算了 —— 只是清理临时文件，不该影响主流程。</summary>
    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 给文件授予 BUILTIN\Users 完全控制。
    /// 桥目录是跨账户通信用的，两个账户都要能读写对方的文件，默认 ACL 不够。
    /// 授权失败不抛异常 —— 同账户场景本来就不需要，不该因此阻断写入。
    /// </summary>
    private static void GrantUsersFullControl(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            new FileInfo(path).SetAccessControl(security);
        }
        catch
        {
            // 权限设置失败不影响函数返回值；跨账户场景会在状态回写时暴露出来
        }
    }

    private static string ReadTextWithRetry(string path)
    {
        for (var attempt = 0; attempt < ReadRetry; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < ReadRetry - 1)
            {
                System.Threading.Thread.Sleep(40);
            }
        }

        return string.Empty;
    }
}
