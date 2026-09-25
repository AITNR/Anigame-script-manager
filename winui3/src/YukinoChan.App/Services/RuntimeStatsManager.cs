// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using YukinoChan.Helpers;

namespace YukinoChan.Services;

public sealed class RuntimeRecord
{
    public string SessionId { get; set; } = string.Empty;

    public string Time { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public long ElapsedSeconds { get; set; }

    public string ElapsedText { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string WaitMode { get; set; } = string.Empty;
}

public sealed class RuntimeHistoryItem
{
    public int Count { get; set; }

    public long TotalSeconds { get; set; }

    public double AverageSeconds { get; set; }

    public string AverageText { get; set; } = string.Empty;

    public long LastSeconds { get; set; }

    public string LastText { get; set; } = string.Empty;

    public string LastTime { get; set; } = string.Empty;
}

/// <summary>记录每次执行耗时，并按脚本名称维护历史平均值。</summary>
public sealed class RuntimeStatsManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly List<RuntimeRecord> _records = new();

    public RuntimeStatsManager(string statsDir)
    {
        StatsDir = statsDir;
        Directory.CreateDirectory(StatsDir);
        HistoryPath = Path.Combine(StatsDir, "runtime_history.json");
        SummaryPath = Path.Combine(StatsDir, "runtime_summary.json");
        SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    }

    public string StatsDir { get; }

    public string HistoryPath { get; }

    public string SummaryPath { get; }

    public string SessionId { get; }

    public void AddRecord(string taskName, long elapsedSeconds, string status, string waitMode)
    {
        var elapsed = Math.Max(0, elapsedSeconds);
        lock (_lock)
        {
            _records.Add(new RuntimeRecord
            {
                SessionId = SessionId,
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                TaskName = taskName,
                ElapsedSeconds = elapsed,
                ElapsedText = FormatHelper.FormatSeconds(elapsed),
                Status = status,
                WaitMode = waitMode,
            });
        }
    }

    /// <summary>保存本次会话的 json / csv，并更新历史平均值。</summary>
    public List<string> SaveSession()
    {
        List<RuntimeRecord> records;
        lock (_lock)
        {
            records = new List<RuntimeRecord>(_records);
        }

        if (records.Count == 0)
        {
            return new List<string>();
        }

        var sessionJson = Path.Combine(StatsDir, $"session_{SessionId}.json");
        var sessionCsv = Path.Combine(StatsDir, $"session_{SessionId}.csv");

        File.WriteAllText(sessionJson, JsonSerializer.Serialize(records, Options), new UTF8Encoding(false));

        var csv = new StringBuilder();
        csv.AppendLine("session_id,time,task_name,elapsed_seconds,elapsed_text,status,wait_mode");
        foreach (var record in records)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Escape(record.SessionId),
                Escape(record.Time),
                Escape(record.TaskName),
                record.ElapsedSeconds.ToString(CultureInfo.InvariantCulture),
                Escape(record.ElapsedText),
                Escape(record.Status),
                Escape(record.WaitMode),
            }));
        }

        File.WriteAllText(sessionCsv, csv.ToString(), new UTF8Encoding(true));

        var history = LoadHistory();
        foreach (var record in records)
        {
            var name = string.IsNullOrWhiteSpace(record.TaskName) ? "未命名" : record.TaskName;
            if (!history.TryGetValue(name, out var item))
            {
                item = new RuntimeHistoryItem();
            }

            item.Count += 1;
            item.TotalSeconds += record.ElapsedSeconds;
            item.AverageSeconds = Math.Round(item.TotalSeconds / (double)Math.Max(1, item.Count), 2);
            item.AverageText = FormatHelper.FormatSeconds((long)item.AverageSeconds);
            item.LastSeconds = record.ElapsedSeconds;
            item.LastText = FormatHelper.FormatSeconds(record.ElapsedSeconds);
            item.LastTime = record.Time;
            history[name] = item;
        }

        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, Options), new UTF8Encoding(false));
        File.WriteAllText(SummaryPath, JsonSerializer.Serialize(history, Options), new UTF8Encoding(false));

        return new List<string> { sessionJson, sessionCsv, HistoryPath, SummaryPath };
    }

    public Dictionary<string, RuntimeHistoryItem> LoadHistory()
    {
        if (!File.Exists(HistoryPath))
        {
            return new Dictionary<string, RuntimeHistoryItem>();
        }

        try
        {
            var text = File.ReadAllText(HistoryPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, RuntimeHistoryItem>>(text, Options)
                   ?? new Dictionary<string, RuntimeHistoryItem>();
        }
        catch
        {
            return new Dictionary<string, RuntimeHistoryItem>();
        }
    }

    private static string Escape(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
