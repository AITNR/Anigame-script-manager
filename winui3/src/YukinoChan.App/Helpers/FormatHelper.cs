// -*- coding: utf-8 -*-
using System;
using System.Globalization;

namespace YukinoChan.Helpers;

public static class FormatHelper
{
    public static string FormatSeconds(long seconds)
    {
        var total = Math.Max(0, seconds);
        var h = total / 3600;
        var m = (total % 3600) / 60;
        var s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", m, s);
    }

    public static string NowTimeText() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string TodayText() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static int SafeInt(string? value, int fallback, int minValue = int.MinValue)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(minValue, parsed);
        }

        return Math.Max(minValue, fallback);
    }
}
