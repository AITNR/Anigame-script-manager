// -*- coding: utf-8 -*-
using System.Collections.Generic;

namespace YukinoChan.Helpers;

/// <summary>
/// 目标进程关键词 / 游戏窗口关键词 的解析工具。
/// 兼容两种写法：列表 ["A.exe","B"] 或字符串 "A.exe; B，B"。
/// 分隔符兼容中英文逗号（, ，）与中英文分号（; ；）以及换行。
/// （相对 Python 版 normalize_keywords 额外支持全角分号「；」，中文输入法下更容易误输入）
/// </summary>
public static class KeywordHelper
{
    public static List<string> Normalize(object? value)
    {
        var result = new List<string>();
        if (value is null)
        {
            return result;
        }

        IEnumerable<string> rawItems;
        if (value is IEnumerable<string> list)
        {
            rawItems = list;
        }
        else
        {
            var text = value.ToString() ?? string.Empty;
            rawItems = text
                .Replace('\n', ';')
                .Replace('\r', ';')
                .Replace('，', ';')
                .Replace(',', ';')
                .Replace('；', ';')
                .Split(';');
        }

        foreach (var item in rawItems)
        {
            var keyword = (item ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                continue;
            }

            var exists = false;
            foreach (var known in result)
            {
                if (string.Equals(known, keyword, System.StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                result.Add(keyword);
            }
        }

        return result;
    }

    public static string ToText(object? value) => string.Join("; ", Normalize(value));
}
