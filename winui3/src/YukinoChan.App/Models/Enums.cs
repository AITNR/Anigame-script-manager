// -*- coding: utf-8 -*-
using System.Collections.Generic;

namespace YukinoChan.Models;

/// <summary>等待 / 完成判断模式（配置值保持与 Python 版一致的 snake_case）。</summary>
public static class WaitModes
{
    public const string DirectProcess = "direct_process";
    public const string ProcessName = "process_name";
    public const string FireAndContinue = "fire_and_continue";
    public const string WindowTitle = "window_title";
    public const string CmdlineKeyword = "cmdline_keyword";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(DirectProcess, "等待直接启动进程"),
        new(ProcessName, "等待指定进程名"),
        new(FireAndContinue, "启动后不等待"),
        new(WindowTitle, "等待窗口标题出现后消失"),
        new(CmdlineKeyword, "等待命令行出现后消失"),
    };

    public static string Normalize(string? value) =>
        value is DirectProcess or ProcessName or FireAndContinue or WindowTitle or CmdlineKeyword
            ? value
            : DirectProcess;

    public static string Label(string? value)
    {
        foreach (var item in Items)
        {
            if (item.Key == value)
            {
                return item.Value;
            }
        }

        return Items[0].Value;
    }
}

/// <summary>超时处理方式。</summary>
public static class TimeoutActions
{
    public const string KillAndContinue = "kill_and_continue";
    public const string SkipAndContinue = "skip_and_continue";
    public const string StopAll = "stop_all";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(KillAndContinue, "强制结束并继续"),
        new(SkipAndContinue, "不结束，跳过并继续"),
        new(StopAll, "停止全部任务"),
    };

    public static string Normalize(string? value) =>
        value is KillAndContinue or SkipAndContinue or StopAll ? value : KillAndContinue;

    public static string Label(string? value)
    {
        foreach (var item in Items)
        {
            if (item.Key == value)
            {
                return item.Value;
            }
        }

        return Items[0].Value;
    }
}

/// <summary>并发组完成策略。</summary>
public static class ConcurrentPolicies
{
    public const string WaitAll = "wait_all";
    public const string WaitFirst = "wait_first";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(WaitAll, "等待本组全部完成"),
        new(WaitFirst, "只等本组第一个完成"),
    };

    public static string Normalize(string? value) => value is WaitAll or WaitFirst ? value : WaitAll;

    public static string Label(string? value)
    {
        foreach (var item in Items)
        {
            if (item.Key == value)
            {
                return item.Value;
            }
        }

        return Items[0].Value;
    }
}

/// <summary>看板娘状态机。</summary>
public static class MascotStates
{
    public const string Idle = "idle";
    public const string Work = "work";
    public const string Rest = "rest";
    public const string Error = "error";
}

/// <summary>监控目标类型（由 V31.17.1 关键词优先级推导）。</summary>
public static class MonitorKinds
{
    public const string Direct = "direct";
    public const string Process = "process";
    public const string Window = "window";
    public const string Cmdline = "cmdline";
}
