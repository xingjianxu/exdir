namespace Exdir.Models;

/// <summary>按哪一列排序。</summary>
public enum FileSortColumn
{
    Name = 0,
    LastWriteTime,
    Type,
    Size,

    /// <summary>云同步状态（只有云目录里才显示这一列）。</summary>
    SyncState,
}

/// <summary>布局形态。目前只实现 Details，其余为后续扩展预留。</summary>
public enum ViewLayout
{
    Details = 0,
    Icons,
    Compact,
    Thumbnails,
}

/// <summary>快捷命令（title 栏右侧下拉菜单中的一项）。</summary>
public sealed class QuickCommand
{
    public required string Name { get; init; }

    /// <summary>Segoe Fluent Icons 字形。</summary>
    public string Glyph { get; init; } = "\uE8B7";

    /// <summary>命令行；<c>{path}</c> 会被替换为当前目录，<c>{selection}</c> 替换为选中项（以空格分隔并加引号）。</summary>
    public string CommandLine { get; init; } = string.Empty;

    public bool RunAsAdministrator { get; init; }

    /// <summary>用户自定义项（可删除）；内置项为 false。</summary>
    public bool IsUserDefined { get; init; }
}
