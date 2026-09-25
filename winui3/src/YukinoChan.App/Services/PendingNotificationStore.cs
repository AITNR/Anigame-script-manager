// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YukinoChan.Services;

/// <summary>
/// 「错过」的远程完成通知暂存。
///
/// 为什么需要它：本地多用户场景下 mstsc 接管控制台，主控端所在会话处于锁定状态，
/// Windows 对锁定会话【不弹 Toast 横幅】—— 通知只是进了操作中心，切回来也不会补弹。
/// 因此主控端发完通知后把内容落一份在这里，等窗口重新激活（用户切回来 / 重启应用）
/// 时补发一次，保证用户一定能看到。参见 MainWindow 的 Activated 处理。
///
/// 与 NotificationService 同一原则：通知属于锦上添花，任何失败都不影响主流程。
/// </summary>
public static class PendingNotificationStore
{
    /// <summary>超过这个时限的暂存通知直接作废，避免几天后突然弹一条旧的。</summary>
    private const double MaxAgeHours = 24;

    private static string FilePath => Path.Combine(AppPaths.BaseDir, "pending_notification.json");

    private sealed class Payload
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("o");
    }

    /// <summary>保存一条待补发的通知（覆盖旧条目 —— 只保留最近一条即可）。</summary>
    public static void Save(string title, string body)
    {
        try
        {
            var payload = new Payload { Title = title, Body = body };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload));
        }
        catch
        {
            // 写不进去就算了，通知仍在操作中心
        }
    }

    /// <summary>
    /// 取出并删除暂存的通知。没有、内容损坏或超过时限都返回 null（文件一律清掉）。
    /// </summary>
    public static (string Title, string Body)? TryConsume()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath);
            File.Delete(FilePath);

            var payload = JsonSerializer.Deserialize<Payload>(json);
            if (payload is null || string.IsNullOrEmpty(payload.Title))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(payload.CreatedAt, out var created) ||
                DateTimeOffset.Now - created > TimeSpan.FromHours(MaxAgeHours))
            {
                return null;
            }

            return (payload.Title, payload.Body);
        }
        catch
        {
            return null;
        }
    }
}
