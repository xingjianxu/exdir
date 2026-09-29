using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Helpers;
using Exdir.Models;
namespace Exdir.ViewModels;

/// <summary>
/// 文件列表中的一行。
/// 除了展开状态，其余信息在切换目录/排序时整体重建；目录行可以就地展开，
/// 子节点由 <see cref="FolderTabViewModel"/> 懒加载后用 <see cref="SetChildren"/> 灌进来。
/// </summary>
public sealed class FileItemViewModel : ObservableObject
{
    /// <summary>每深一层缩进的像素宽度。</summary>
    public const double IndentPerLevel = 14;

    private readonly string _displayName;
    private readonly List<FileItemViewModel> _children = new();

    private bool _isExpanded;
    private bool _childrenLoaded;

    public FileItemViewModel(FileSystemEntry entry, bool showExtensions, ColumnLayout columns, int depth = 0)
    {
        Entry = entry;
        Columns = columns;
        Depth = depth;

        _displayName = !entry.IsDirectory && !showExtensions && entry.Name.LastIndexOf('.') is var dot && dot > 0
            ? entry.Name[..dot]
            : entry.Name;
    }

    public FileSystemEntry Entry { get; }

    /// <summary>列宽状态，由同一个标签页的所有行共享（见 <see cref="ColumnLayout"/>）。</summary>
    public ColumnLayout Columns { get; }

    /// <summary>在树中的层级；根目录下的条目为 0。</summary>
    public int Depth { get; }

    /// <summary>名称列左侧的缩进占位宽度。</summary>
    public double IndentWidth => Depth * IndentPerLevel;

    public string Name => Entry.Name;

    /// <summary>列表中显示的名称（可隐藏扩展名）。</summary>
    public string DisplayName => _displayName;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsHidden => Entry.IsHidden;

    public long Size => Entry.Size;

    public DateTimeOffset LastWriteTime => Entry.LastWriteTime;

    /// <summary>大小列文本；目录显示为空白。</summary>
    public string SizeText => Entry.IsDirectory ? string.Empty : SizeFormatter.Format(Entry.Size);

    public string ModifiedText => TimeFormatter.FormatListColumn(Entry.LastWriteTime);

    public string TypeName => Entry.TypeName;

    public string Glyph => FileTypeHelper.GetGlyph(Entry.FullPath, Entry.IsDirectory);

    // ------------------------------------------------------------------ 云同步状态

    /// <summary>同步状态；非云目录（或客户端不报状态）为 <see cref="CloudSyncState.None"/>。</summary>
    public CloudSyncState SyncState => Entry.SyncState;

    /// <summary>状态列里显示的字形（无状态时为空串，单元格也就看不见东西）。</summary>
    public string SyncStateGlyph => CloudSyncStateHelper.GetGlyph(Entry.SyncState);

    /// <summary>状态文案（无障碍名称）。</summary>
    public string SyncStateText => CloudSyncStateHelper.GetText(Entry.SyncState);

    /// <summary>同步状态提示（悬停）；无状态时为 null，避免弹出一个空提示框。</summary>
    public string? SyncStateTooltip => Entry.SyncState == CloudSyncState.None
        ? null
        : CloudSyncStateHelper.GetTooltip(Entry.SyncState);

    public string Tooltip => Entry.IsDirectory
        ? Entry.FullPath
        : $"{Entry.FullPath}\n{SizeFormatter.Format(Entry.Size)}";

    public string Extension => Entry.IsDirectory ? string.Empty : Path.GetExtension(Entry.Name);

    // ------------------------------------------------------------------ 树形展开

    /// <summary>已经加载过子项（哪怕为空）。</summary>
    public bool ChildrenLoaded => _childrenLoaded;

    /// <summary>已加载的子项（未加载时为空集合）。</summary>
    public IReadOnlyList<FileItemViewModel> Children => _children;

    /// <summary>是否显示展开箭头：目录，且（未加载过，或加载后确实有子项）。</summary>
    public bool CanExpand => IsDirectory && !(_childrenLoaded && _children.Count == 0);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpanderGlyph));
            }
        }
    }

    /// <summary>展开箭头字形；不可展开时为空串（占位由固定宽度的容器保证）。</summary>
    public string ExpanderGlyph => CanExpand
        ? (IsExpanded ? "\uE70D" : "\uE76C")
        : string.Empty;

    /// <summary>灌入子项（由 FolderTabViewModel 排序后调用）。</summary>
    public void SetChildren(IReadOnlyList<FileItemViewModel> children)
    {
        _children.Clear();
        _children.AddRange(children);
        _childrenLoaded = true;

        OnPropertyChanged(nameof(Children));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(ExpanderGlyph));
    }

    /// <summary>重新排序已加载的直接子项。</summary>
    public void SortChildren(Comparison<FileItemViewModel> comparison) => _children.Sort(comparison);
}
