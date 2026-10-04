// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using YukinoChan.Models;

namespace YukinoChan.Services;

/// <summary>config.json 读写。兼容 Python 版键名与历史遗留字段。</summary>
public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ConfigManager(string configPath)
    {
        ConfigPath = configPath;
    }

    public string ConfigPath { get; }

    public AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var created = new AppConfig();
            Save(created);
            return created;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var normalized = NormalizeLegacyKeys(json);
            var config = JsonSerializer.Deserialize<AppConfig>(normalized, DeserializeOptions);
            if (config is null)
            {
                throw new InvalidDataException("config.json 顶层结构不是对象");
            }

            config.Sanitize();
            return config;
        }
        catch (Exception)
        {
            try
            {
                var backup = Path.ChangeExtension(ConfigPath, ".broken.json");
                File.Copy(ConfigPath, backup, overwrite: true);
            }
            catch
            {
                // 忽略备份失败
            }

            var fallback = new AppConfig();
            Save(fallback);
            return fallback;
        }
    }

    public void Save(AppConfig config)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        config.Sanitize();

        // 最后一道保险：明文密码只允许出现在**导出文件**里。
        // 万一运行期的配置对象上还挂着 ExportPassword（比如刚从导出文件反序列化过来
        // 就被 Save 了），绝不能让它进 config.json —— 那会把"密码只存在凭据管理器"
        // 这条底线破掉。导入流程已经在 SaveImportedCredentials 里清过一遍，
        // 这里再兜一次底，代价只是两个字符串赋值。
        StripExportSecrets(config);

        var json = JsonSerializer.Serialize(config, SerializeOptions);
        File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
    }

    /// <summary>剥掉只属于导出产物的字段（当前就是明文密码与导出说明块）。</summary>
    public static void StripExportSecrets(AppConfig config)
    {
        if (config.Rdp is { } rdp)
        {
            rdp.ExportPassword = string.Empty;
            foreach (var channel in rdp.Channels)
            {
                channel.ExportPassword = string.Empty;
            }
        }

        config.ExportOptions = null;
    }

    /// <summary>把历史配置键迁移到当前键名，保证旧配置文件可直接沿用。</summary>
    private static string NormalizeLegacyKeys(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is not JsonObject obj)
            {
                return json;
            }

            if (obj["tasks"] is JsonArray tasks)
            {
                foreach (var node in tasks)
                {
                    if (node is not JsonObject task)
                    {
                        continue;
                    }

                    if (task["window_keywords"] is null && task["game_window_keywords"] is not null)
                    {
                        task["window_keywords"] = task["game_window_keywords"]!.DeepClone();
                    }

                    if (task["launcher_process"] is null && task["start_process"] is not null)
                    {
                        task["launcher_process"] = task["start_process"]!.DeepClone();
                    }
                }
            }

            return root.ToJsonString();
        }
        catch
        {
            return json;
        }
    }
}
