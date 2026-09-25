// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>
/// 主控端与目标会话 Agent 之间的通信桥。
/// 两个 Windows 用户会话之间不能直接通信，因此统一走 ProgramData 下的共享目录：
///   command.json  主控端写、Agent 读（要执行的任务快照）
///   status.json   Agent 写、主控端读（进度 + 事件流）
/// 写入一律走"临时文件 + 原子替换"，读取容忍并发 IOException 并重试，
/// 避免主控端正在读时 Agent 正好重写导致解析到半个文件。
/// </summary>
public static class RdpBridge
{
    private const int MaxEvents = 500;
    private const int ReadRetry = 5;

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

    /// <summary>
    /// 当前生效的共享目录。默认是本机 ProgramData 下（够用于本机多用户）；
    /// 目标是远程主机时，两端都要指向同一个双方可访问的位置（UNC 网络共享或映射盘符）。
    /// </summary>
    public static string BridgeDir { get; private set; } = DefaultBridgeDir();

    /// <summary>默认共享目录：C:\ProgramData\YukinoChan\rdp（所有用户可读，只有管理员/创建者可写）。</summary>
    public static string DefaultBridgeDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YukinoChan", "rdp");

    /// <summary>是否用了自定义的桥目录（非本机 ProgramData）。</summary>
    public static bool IsCustomized { get; private set; }

    /// <summary>
    /// 设置桥目录。传空表示回到默认目录。
    /// 主控端在连接前调用；Agent 端由 --bridge: 参数在启动时调用。
    /// </summary>
    public static void Configure(string? customPath)
    {
        var text = (customPath ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            BridgeDir = DefaultBridgeDir();
            IsCustomized = false;
            return;
        }

        BridgeDir = text;
        IsCustomized = true;
    }

    public static string CommandPath => Path.Combine(BridgeDir, "command.json");

    public static string StatusPath => Path.Combine(BridgeDir, "status.json");

    /// <summary>最近一次写入失败的原因，用于界面提示与排障。</summary>
    public static string LastError { get; private set; } = string.Empty;

    public static void EnsureDirectory()
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

    // ---------------- 主控端：下发指令 ----------------

    public static bool WriteCommand(RdpCommand command)
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
    public static void ResetStatus(string commandId)
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

    // ---------------- Agent：回写状态 ----------------

    /// <summary>Agent 侧读改写状态。Agent 是 status.json 的唯一写者，无需加锁。</summary>
    public static bool UpdateStatus(Func<RdpStatus, RdpStatus> mutator)
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

    public static bool TryReadCommand(out RdpCommand? command)
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

    public static RdpStatus? TryReadStatus()
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

    // ---------------- 内部实现 ----------------

    /// <summary>
    /// 写文件。按可靠性从高到低尝试三种方式：
    ///   1. 临时文件 + 原子替换（读取方永远看不到半截内容）
    ///   2. 删掉目标再重命名（目标文件不允许被覆盖时）
    ///   3. 直接覆写（前面都不行时的兜底）
    /// 跨账户场景下目标文件是另一个账户创建的，ACL 常常不允许删除，
    /// 所以必须有兜底 —— 写不进去会让整个任务链路断掉，比"可能读到半截"严重得多。
    /// </summary>
    private static bool WriteAtomic(string path, string content)
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
    private static bool OverwriteDirectly(string path, string content)
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

    /// <summary>在资源管理器里打开共享目录（排障用）。</summary>
    public static void OpenBridgeFolder()
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
}
