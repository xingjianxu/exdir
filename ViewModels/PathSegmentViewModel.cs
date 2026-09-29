namespace Exdir.ViewModels;

/// <summary>地址栏面包屑里的一段路径。</summary>
public sealed class PathSegmentViewModel
{
    public PathSegmentViewModel(string displayName, string fullPath, bool isFirst, bool isCurrent)
    {
        DisplayName = displayName;
        FullPath = fullPath;
        IsFirst = isFirst;
        IsCurrent = isCurrent;
    }

    /// <summary>分段上显示的文字：根段是 `C:` 或 `\\server\share`，其余是目录名。</summary>
    public string DisplayName { get; }

    /// <summary>这一段的完整路径（点击后导航的目标）。</summary>
    public string FullPath { get; }

    /// <summary>第一段左侧没有 chevron 分隔符。</summary>
    public bool IsFirst { get; }

    /// <summary>当前目录所在段：点击它进入地址栏编辑态而不是导航（与资源管理器一致）。</summary>
    public bool IsCurrent { get; }

    /// <summary>悬停提示用完整路径；“此电脑”段没有路径，退化成显示名。</summary>
    public string Tooltip => string.IsNullOrEmpty(FullPath) ? DisplayName : FullPath;
}
