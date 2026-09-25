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

    public static string ConfigPath => Path.Combine(BaseDir, ConfigFileName);
    public static string LogDir => Path.Combine(BaseDir, LogDirName);
    public static string StatsDir => Path.Combine(BaseDir, "runtime_stats");
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
