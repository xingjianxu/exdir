using System;
using System.Globalization;

namespace Exdir.Helpers;

/// <summary>字节数的紧凑人类可读格式。</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>格式化字节数，例如 <c>1.23 MB</c>。目录返回空字符串。</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return string.Empty;
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return string.Create(CultureInfo.InvariantCulture, $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}");
    }
}

/// <summary>时间戳的紧凑显示格式。</summary>
public static class TimeFormatter
{
    public static string FormatListColumn(DateTimeOffset value)
    {
        if (value == default)
        {
            return string.Empty;
        }

        var local = value.ToLocalTime();
        var now = DateTimeOffset.Now;

        // 今天只显示时间，同年省略年份 —— 与资源管理器一致的紧凑风格
        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        if (local.Year == now.Year)
        {
            return local.ToString("M/d HH:mm", CultureInfo.CurrentCulture);
        }

        return local.ToString("yyyy/M/d HH:mm", CultureInfo.CurrentCulture);
    }
}
