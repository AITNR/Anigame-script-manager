// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;

namespace YukinoChan.Services;

/// <summary>
/// 统一路径入口。配置、日志、统计、看板娘素材都以程序根目录为准，
/// 与 Python 版 core.app_dir() 的行为保持一致。
/// </summary>
public static class AppPaths
{
    public const string ConfigFileName = "config.json";
    public const string LogDirName = "logs";

    public static string BaseDir { get; } = ResolveBaseDir();

    /// <summary>
    /// 代理模式标记（<c>--rdp-agent</c> 启动时由 App.OnLaunched 置位）。
    ///
    /// 为什么要分家：所有目标账户共用同一份代理副本，如果日志和统计都落在副本目录下，
    /// 固定文件名（last_abnormal_report.json / runtime_history.json）和整秒生成的
    /// session 文件名会被别的账户先占用（CREATOR OWNER 机制下别人改不了），
    /// 结果是"任务跑完了却因为写不了统计被判异常"。所以代理模式下按账户各自分开。
    /// </summary>
    public static bool AgentMode { get; set; }

    /// <summary>代理模式下的数据子目录名（当前账户名，去掉路径非法字符）。</summary>
    private static string AccountTag
    {
        get
        {
            var raw = Environment.UserName?.Trim() ?? string.Empty;
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder();
            foreach (var ch in raw)
            {
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            var tag = builder.ToString().Trim().TrimEnd('.');
            return tag.Length == 0 ? "agent" : tag;
        }
    }

    public static string ConfigPath => Path.Combine(BaseDir, ConfigFileName);

    /// <summary>日志目录。代理模式下落到 <c>logs/&lt;账户名&gt;</c>，主控端不受影响。</summary>
    public static string LogDir => AgentMode
        ? Path.Combine(BaseDir, LogDirName, AccountTag)
        : Path.Combine(BaseDir, LogDirName);

    /// <summary>耗时统计目录。代理模式下同样按账户分家。</summary>
    public static string StatsDir => AgentMode
        ? Path.Combine(BaseDir, "runtime_stats", AccountTag)
        : Path.Combine(BaseDir, "runtime_stats");
    public static string AssetsDir => Path.Combine(BaseDir, "assets");
    public static string MascotDir => Path.Combine(AssetsDir, "mascot");

    private static string ResolveBaseDir()
    {
        // 单文件 / 自包含发布时，AppContext.BaseDirectory 仍指向 exe 所在目录。
        var dir = AppContext.BaseDirectory;

        // 开发期（bin/Debug/net8.0-windows.../win-x64/）向上回溯到仓库根目录，
        // 让 assets/ 与 config.json 能落在源码树里，便于调试。
        var probe = new DirectoryInfo(dir);
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            if (File.Exists(Path.Combine(probe.FullName, "config.json")) ||
                Directory.Exists(Path.Combine(probe.FullName, "assets")))
            {
                return probe.FullName;
            }

            probe = probe.Parent;
        }

        return dir;
    }

    public static void WriteStartupError(Exception? exc)
    {
        try
        {
            var path = Path.Combine(BaseDir, "startup_error.log");
            var builder = new StringBuilder();
            builder.AppendLine()
                   .AppendLine(new string('=', 80))
                   .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                   .AppendLine("Runtime: " + Environment.Version)
                   .AppendLine("BaseDir: " + BaseDir)
                   .AppendLine(exc?.ToString() ?? "(unknown exception)");

            File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 忽略：写入失败不应引发二次崩溃
        }
    }
}
