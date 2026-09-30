using Exdir.Models;
using Microsoft.UI.Xaml;

namespace Exdir.Helpers;

/// <summary>
/// 三态主题（跟随系统 / 浅色 / 深色）与 WinUI <see cref="ElementTheme" /> 之间的换算，
/// 以及设置窗口「主题」下拉框的下标映射（两处的顺序必须一致）。
///
/// 实际“换主题”的动作只发生在一处：<c>MainWindow.ApplyTheme</c>——给根元素设
/// <c>RequestedTheme</c>。设置窗口是另一个 <c>Window</c>，不继承主窗口的主题，由它自己再推一次。
/// </summary>
public static class ThemeHelper
{
    /// <summary>settings.json 被手改坏时的兜底：认不出来的值一律当「跟随系统」。</summary>
    public static AppTheme Normalize(AppTheme theme) => theme switch
    {
        AppTheme.Light or AppTheme.Dark => theme,
        _ => AppTheme.System,
    };

    /// <summary>下拉框 / 索引的取值（0 = 跟随系统、1 = 浅色、2 = 深色），与 SettingsView 里的项顺序一致。</summary>
    public static int ToIndex(AppTheme theme) => Normalize(theme) switch
    {
        AppTheme.Light => 1,
        AppTheme.Dark => 2,
        _ => 0,
    };

    /// <summary>索引 → 主题（越界或手改坏都落回「跟随系统」）。</summary>
    public static AppTheme FromIndex(int index) => index switch
    {
        1 => AppTheme.Light,
        2 => AppTheme.Dark,
        _ => AppTheme.System,
    };

    /// <summary>「跟随系统」用 <see cref="ElementTheme.Default" />，由 WinUI 自己按系统应用主题解析。</summary>
    public static ElementTheme ToElementTheme(AppTheme theme) => Normalize(theme) switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>中文名（日志与界面文案）。</summary>
    public static string ToDisplayName(AppTheme theme) => Normalize(theme) switch
    {
        AppTheme.Light => "浅色",
        AppTheme.Dark => "深色",
        _ => "跟随系统",
    };
}
