using System;

namespace Exdir.Helpers;

/// <summary>
/// 「最新访问」虚拟视图的位置。
///
/// 它不是一个真实目录：<see cref="Path" /> 只是标签页 <c>CurrentPath</c> 里的一个哨兵值，
/// 由 <c>FileSystemService</c> 认出来后按「最近访问过的目录与文件（按访问时间倒序）」枚举。
/// 这样做的好处是面包屑 / 后退 / 会话恢复 / 状态栏这些按“路径字符串”干活的地方一行都不用改
/// （与压缩包虚拟路径、远程路径同一种思路）。
/// </summary>
public static class RecentView
{
    /// <summary>虚拟位置字符串。协议头故意用 <c>exdir://</c>：真实路径不可能是这个形状。</summary>
    public const string Path = "exdir://recent";

    /// <summary>显示名（标签页标题 / 面包屑那一段）。</summary>
    public const string DisplayName = "最新访问";

    /// <summary>是不是「最新访问」视图的位置。</summary>
    public static bool IsRecentViewPath(string? path)
        => string.Equals(path, Path, StringComparison.OrdinalIgnoreCase);
}
