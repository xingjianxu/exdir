using System;
using Exdir.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Exdir.Converters;

/// <summary>bool → Visibility。</summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>为 true 时把 true 映射成 Collapsed（用于反向绑定）。</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && (v == Visibility.Visible) != Invert;
}

/// <summary>非空字符串 → Visible。</summary>
public sealed partial class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// <see cref="CloudSyncState"/> → Visible：ConverterParameter 写状态名（Synced / CloudOnly …），相等才显示。
/// 状态列里每种状态各有一个字形，靠它只显示当前那一个。
/// </summary>
public sealed partial class CloudSyncStateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var expected = parameter as string;

        return value is CloudSyncState state
            && !string.IsNullOrEmpty(expected)
            && string.Equals(state.ToString(), expected, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>集合/数量 → Visible（0 时 Collapsed）。</summary>
public sealed partial class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            null => 0,
            _ => 1,
        };

        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
