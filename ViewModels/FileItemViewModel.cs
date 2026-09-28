using System;
using System.IO;
using Exdir.Helpers;
using Exdir.Models;

namespace Exdir.ViewModels;

/// <summary>文件列表中的一行。不可变，切换目录/排序时整体重建。</summary>
public sealed class FileItemViewModel
{
    private readonly string _displayName;

    public FileItemViewModel(FileSystemEntry entry, bool showExtensions)
    {
        Entry = entry;

        _displayName = !entry.IsDirectory && !showExtensions && entry.Name.LastIndexOf('.') is var dot && dot > 0
            ? entry.Name[..dot]
            : entry.Name;
    }

    public FileSystemEntry Entry { get; }

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

    public string Tooltip => Entry.IsDirectory
        ? Entry.FullPath
        : $"{Entry.FullPath}\n{SizeFormatter.Format(Entry.Size)}";

    public string Extension => Entry.IsDirectory ? string.Empty : Path.GetExtension(Entry.Name);
}
