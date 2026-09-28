using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>侧边栏树节点的种类。</summary>
public enum SidebarNodeKind
{
    /// <summary>分组标题（主目录 / 云存储 / 此电脑），本身不可导航。</summary>
    Group,

    /// <summary>用户主目录。</summary>
    Home,

    /// <summary>用户标准文件夹（桌面、文档……）。</summary>
    UserFolder,

    /// <summary>云存储同步根目录。</summary>
    Cloud,

    /// <summary>驱动器。</summary>
    Drive,

    /// <summary>普通文件夹。</summary>
    Folder,
}

/// <summary>侧边栏树中的一个节点。子节点按需（展开时）加载。</summary>
public sealed partial class SidebarNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _hasUnrealizedChildren;

    public SidebarNodeViewModel(
        string name,
        string fullPath,
        string glyph,
        SidebarNodeKind kind,
        bool canExpand = true)
    {
        Name = name;
        FullPath = fullPath;
        Glyph = glyph;
        Kind = kind;
        HasUnrealizedChildren = canExpand && kind != SidebarNodeKind.Group;
    }

    public string Name { get; }

    public string FullPath { get; }

    public string Glyph { get; }

    public SidebarNodeKind Kind { get; }

    public ObservableCollection<SidebarNodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>为 true 时树控件会显示展开箭头；展开时由视图层触发子节点加载。</summary>
    public bool HasUnrealizedChildren
    {
        get => _hasUnrealizedChildren;
        set => SetProperty(ref _hasUnrealizedChildren, value);
    }

    /// <summary>子节点是否已经加载过（避免重复枚举）。</summary>
    public bool ChildrenLoaded { get; set; }

    /// <summary>分组标题节点不可导航。</summary>
    public bool IsNavigable => !string.IsNullOrEmpty(FullPath);

    public bool IsDrive => Kind == SidebarNodeKind.Drive;

    public string Tooltip => FullPath;
}

/// <summary>左侧文件夹树。</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    /// <summary>每次展开最多枚举的子目录数量，避免 C:\Windows 这类目录拖慢 UI。</summary>
    private const int MaxChildrenPerLevel = 300;

    private readonly IFileSystemService _fileSystem;
    private readonly IKnownFolderService _knownFolders;
    private readonly IDriveService _driveService;

    private readonly Dictionary<string, SidebarNodeViewModel> _index = new(StringComparer.OrdinalIgnoreCase);

    public SidebarViewModel(
        IFileSystemService fileSystem,
        IKnownFolderService knownFolders,
        IDriveService driveService)
    {
        _fileSystem = fileSystem;
        _knownFolders = knownFolders;
        _driveService = driveService;

        BuildTree();
    }

    /// <summary>用户点击树节点时请求导航。参数为目录路径。</summary>
    public event EventHandler<string>? NavigateRequested;

    public ObservableCollection<SidebarNodeViewModel> Roots { get; } = new();

    /// <summary>重新构建整棵树（磁盘热插拔后调用）。</summary>
    public void BuildTree()
    {
        Roots.Clear();
        _index.Clear();

        Roots.Add(BuildHomeGroup());
        Roots.Add(BuildCloudGroup());
        Roots.Add(BuildComputerGroup());

        // 顶层默认展开
        foreach (var root in Roots)
        {
            root.IsExpanded = true;
        }
    }

    /// <summary>展开节点时加载其子目录。</summary>
    public async Task ExpandAsync(SidebarNodeViewModel node)
    {
        if (node.ChildrenLoaded || node.Kind == SidebarNodeKind.Group)
        {
            return;
        }

        node.ChildrenLoaded = true;

        if (!_fileSystem.DirectoryExists(node.FullPath))
        {
            node.HasUnrealizedChildren = false;
            return;
        }

        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = await _fileSystem
                .EnumerateSubDirectoriesAsync(node.FullPath, MaxChildrenPerLevel)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            node.HasUnrealizedChildren = false;
            return;
        }

        node.Children.Clear();
        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var child = new SidebarNodeViewModel(
                entry.Name,
                entry.FullPath,
                FileTypeHelper.FolderGlyph,
                SidebarNodeKind.Folder);

            node.Children.Add(child);
            _index[entry.FullPath] = child;
        }

        node.HasUnrealizedChildren = node.Children.Count > 0;
    }

    public void RequestNavigate(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            NavigateRequested?.Invoke(this, path);
        }
    }

    /// <summary>尝试在已展开的节点中定位并选中路径（尽力而为）。</summary>
    public SidebarNodeViewModel? FindNode(string path)
        => _index.TryGetValue(path, out var node) ? node : null;

    // ------------------------------------------------------------------ 构建

    private SidebarNodeViewModel BuildHomeGroup()
    {
        // 分组节点自身就是“主目录”的入口（有路径 → 可点击导航），子节点为其中的标准文件夹
        var group = new SidebarNodeViewModel("主目录", _knownFolders.UserProfile, FileTypeHelper.HomeGlyph, SidebarNodeKind.Group);
        group.ChildrenLoaded = true;

        foreach (var folder in _knownFolders.GetUserFolders())
        {
            var node = new SidebarNodeViewModel(folder.Name, folder.Path, folder.Glyph, SidebarNodeKind.UserFolder);
            group.Children.Add(node);
            _index[folder.Path] = node;
        }

        _index[_knownFolders.UserProfile] = group;
        return group;
    }

    private SidebarNodeViewModel BuildCloudGroup()
    {
        var group = new SidebarNodeViewModel("云存储", string.Empty, FileTypeHelper.CloudGlyph, SidebarNodeKind.Group);
        group.ChildrenLoaded = true;

        foreach (var folder in _knownFolders.GetCloudFolders())
        {
            var node = new SidebarNodeViewModel(folder.Name, folder.Path, folder.Glyph, SidebarNodeKind.Cloud);
            group.Children.Add(node);
            _index[folder.Path] = node;
        }

        return group;
    }

    private SidebarNodeViewModel BuildComputerGroup()
    {
        var group = new SidebarNodeViewModel("此电脑", string.Empty, FileTypeHelper.DriveGlyph, SidebarNodeKind.Group);
        group.ChildrenLoaded = true;

        foreach (var drive in _driveService.GetDrives())
        {
            var name = string.IsNullOrEmpty(drive.VolumeLabel)
                ? drive.DisplayName
                : $"{drive.DisplayName}  {drive.VolumeLabel}";

            var node = new SidebarNodeViewModel(
                name,
                drive.RootPath,
                drive.Glyph,
                SidebarNodeKind.Drive,
                canExpand: drive.IsReady);

            group.Children.Add(node);
            _index[drive.RootPath] = node;
        }

        return group;
    }
}
