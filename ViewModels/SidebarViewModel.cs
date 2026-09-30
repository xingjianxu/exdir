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

    /// <summary>「收藏夹」分组标题（内容镜像工具条上的固定目录，可折叠，本身不可导航）。</summary>
    FavoritesGroup,

    /// <summary>收藏夹里的一个目录（等价于工具条上的一个固定目录）。</summary>
    Favorite,

    /// <summary>普通文件夹。</summary>
    Folder,
}

/// <summary>侧边栏树中的一个节点。子节点按需（展开时）加载。</summary>
public sealed partial class SidebarNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _hasUnrealizedChildren;
    private bool _isDropTarget;

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

    /// <summary>
    /// 拖拽经过时的高亮标记：只在「收藏夹」分组及其子项上会被置为 true
    /// （这两个位置才是“收藏目录”的落点，见 <see cref="SidebarView" />）。
    /// </summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
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

    /// <summary>
    /// 四个分组的节点引用（每次 BuildTree 重建）。
    /// 分组本身始终存在，显示 / 隐藏只改它们在不不在 <see cref="Roots" /> 里，
    /// 这样重新打开一个分组时它之前展开的子节点与展开状态都还在。
    /// </summary>
    private SidebarNodeViewModel? _homeGroup;

    /// <summary>「收藏夹」分组的节点引用（每次 BuildTree 重建；内容由 <see cref="SyncFavorites" /> 填充）。</summary>
    private SidebarNodeViewModel? _favoritesGroup;

    private SidebarNodeViewModel? _cloudGroup;
    private SidebarNodeViewModel? _computerGroup;

    // 分组显示开关（由 MainViewModel 在启动与设置改动时推过来，见 ApplyGroupVisibility）
    private bool _showHome = true;
    private bool _showFavorites = true;
    private bool _showCloud = true;
    private bool _showComputer = true;

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

    /// <summary>
    /// 用户把目录拖到「收藏夹」分组（或其行上）时请求固定。参数是被拖入的目录路径。
    /// 侧边栏不认识工具条的固定目录集合，所以只发事件、由 MainViewModel 去真正固定并落盘。
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? PinRequested;

    /// <summary>用户右键收藏项选择「取消收藏」。参数为要移除的目录路径。</summary>
    public event EventHandler<string>? UnpinRequested;

    public ObservableCollection<SidebarNodeViewModel> Roots { get; } = new();

    /// <summary>重新构建整棵树（磁盘热插拔后调用）。</summary>
    public void BuildTree()
    {
        Roots.Clear();
        _index.Clear();

        _homeGroup = BuildHomeGroup();
        _favoritesGroup = BuildFavoritesGroup();
        _cloudGroup = BuildCloudGroup();
        _computerGroup = BuildComputerGroup();

        // 顶层默认展开
        foreach (var root in AllGroups())
        {
            root.IsExpanded = true;
        }

        RefreshRoots();
    }

    /// <summary>
    /// 磁盘热插拔后把「此电脑」分组对齐到当前磁盘清单（U 盘插上就出现、拔掉就消失）。
    ///
    /// 只动这一个分组：其它分组、用户在树里展开的目录、滚动位置都留着（整树重建会把这些全丢掉）。
    /// 已经存在且没有变化的盘节点**复用同一个节点对象**，所以它的容器不会重建、展开状态也还在；
    /// 只有卷标或“能不能展开”（未就绪 → 就绪，例如刚插上的光驱）变了才换新节点。
    /// </summary>
    public void RefreshDrives()
    {
        if (_computerGroup is null)
        {
            return;
        }

        var existing = new Dictionary<string, SidebarNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _computerGroup.Children)
        {
            existing[node.FullPath] = node;
        }

        var desired = new List<SidebarNodeViewModel>(existing.Count);
        foreach (var drive in _driveService.GetDrives())
        {
            if (existing.TryGetValue(drive.RootPath, out var node) && CanReuse(node, drive))
            {
                desired.Add(node);
            }
            else
            {
                desired.Add(CreateDriveNode(drive));
            }
        }

        // 拔掉的盘（以及上面被换掉的旧节点）先摘掉，顺手清索引
        for (var i = _computerGroup.Children.Count - 1; i >= 0; i--)
        {
            var node = _computerGroup.Children[i];
            if (!desired.Contains(node))
            {
                Unindex(node);
                _computerGroup.Children.RemoveAt(i);
            }
        }

        // 再按磁盘清单的顺序补齐 / 挪位（新插的 U 盘一般排在最后，但盘符顺序要以 DriveService 为准）
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _computerGroup.Children.Count && ReferenceEquals(_computerGroup.Children[i], desired[i]))
            {
                continue;
            }

            var at = _computerGroup.Children.IndexOf(desired[i]);
            if (at < 0)
            {
                _computerGroup.Children.Insert(i, desired[i]);
                _index[desired[i].FullPath] = desired[i];
            }
            else
            {
                // 不用 ObservableCollection.Move：ItemsControl 对 Move 通知的支持依版本而异
                _computerGroup.Children.RemoveAt(at);
                _computerGroup.Children.Insert(i, desired[i]);
            }
        }
    }

    /// <summary>
    /// 按设置显示 / 隐藏侧边栏的四个分组（设置窗口「侧边栏」页）。
    /// 启动时与设置改动时各调一次。
    /// </summary>
    public void ApplyGroupVisibility(bool home, bool favorites, bool cloud, bool computer)
    {
        _showHome = home;
        _showFavorites = favorites;
        _showCloud = cloud;
        _showComputer = computer;

        RefreshRoots();
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

    /// <summary>请求把一批目录加入收藏（由视图层的拖放处理调用）。</summary>
    public void RequestPin(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            PinRequested?.Invoke(this, paths);
        }
    }

    /// <summary>请求把某个目录移出收藏（右键收藏项的「取消收藏」）。</summary>
    public void RequestUnpin(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            UnpinRequested?.Invoke(this, path);
        }
    }

    /// <summary>
    /// 把工具条上的固定目录镜像到「收藏夹」分组。固定目录增删或重排时由 MainViewModel 调用。
    /// 只重建这一个分组的子节点，其它分组（以及用户在树里展开的状态）不受影响。
    /// </summary>
    public void SyncFavorites(IEnumerable<PinnedFolderViewModel> pinnedFolders)
    {
        if (_favoritesGroup is null)
        {
            return;
        }

        _favoritesGroup.Children.Clear();

        foreach (var folder in pinnedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.Path))
            {
                continue;
            }

            // 收藏项可以有子目录（展开时同样走 ExpandAsync 懒加载），因此 canExpand 保持默认 true
            _favoritesGroup.Children.Add(new SidebarNodeViewModel(
                folder.Name,
                folder.Path,
                FileTypeHelper.FavoriteGlyph,
                SidebarNodeKind.Favorite));
        }

        // 收藏夹的内容是完整的固定目录清单，不存在“还没枚举过的子项”
        _favoritesGroup.HasUnrealizedChildren = false;
    }

    /// <summary>尝试在已展开的节点中定位并选中路径（尽力而为）。</summary>
    public SidebarNodeViewModel? FindNode(string path)
        => _index.TryGetValue(path, out var node) ? node : null;

    // ------------------------------------------------------------------ 构建

    private IEnumerable<SidebarNodeViewModel> AllGroups()
    {
        if (_homeGroup is not null) { yield return _homeGroup; }
        if (_favoritesGroup is not null) { yield return _favoritesGroup; }
        if (_cloudGroup is not null) { yield return _cloudGroup; }
        if (_computerGroup is not null) { yield return _computerGroup; }
    }

    /// <summary>
    /// 按显示开关重组 <see cref="Roots" />：只增删差异项，不整表重建。
    /// 分组对象始终是同一批，所以折叠 / 展开状态与已懒加载的子节点都留着；
    /// 整表重建会让树控件重建容器，把已展开的分组全部折回去。
    /// </summary>
    private void RefreshRoots()
    {
        var desired = new List<SidebarNodeViewModel>(4);
        if (_showHome && _homeGroup is not null) { desired.Add(_homeGroup); }
        if (_showFavorites && _favoritesGroup is not null) { desired.Add(_favoritesGroup); }
        if (_showCloud && _cloudGroup is not null) { desired.Add(_cloudGroup); }
        if (_showComputer && _computerGroup is not null) { desired.Add(_computerGroup); }

        foreach (var node in Roots.ToList())
        {
            if (!desired.Contains(node))
            {
                Roots.Remove(node);
            }
        }

        // 剩下的已经是 desired 的子序列，按位置把缺的补回去即可保持分组顺序
        for (var i = 0; i < desired.Count; i++)
        {
            if (i >= Roots.Count)
            {
                Roots.Add(desired[i]);
            }
            else if (!ReferenceEquals(Roots[i], desired[i]))
            {
                Roots.Insert(i, desired[i]);
            }
        }
    }

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

    private SidebarNodeViewModel BuildFavoritesGroup()
    {
        // 即使一个收藏都没有，这个分组也要留着（它就是“拖到这里收藏”的落点）；
        // 它没有可枚举的子目录，所以先把 HasUnrealizedChildren 清掉，不画展开箭头。
        var group = new SidebarNodeViewModel(
            "收藏夹",
            string.Empty,
            FileTypeHelper.FavoriteGlyph,
            SidebarNodeKind.FavoritesGroup);
        group.HasUnrealizedChildren = false;
        group.ChildrenLoaded = true;
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
            var node = CreateDriveNode(drive);
            group.Children.Add(node);
            _index[drive.RootPath] = node;
        }

        return group;
    }

    private static SidebarNodeViewModel CreateDriveNode(DriveModel drive)
        => new(
            DriveDisplayName(drive),
            drive.RootPath,
            drive.Glyph,
            SidebarNodeKind.Drive,
            canExpand: drive.IsReady);

    private static string DriveDisplayName(DriveModel drive)
        => string.IsNullOrEmpty(drive.VolumeLabel)
            ? drive.DisplayName
            : $"{drive.DisplayName}  {drive.VolumeLabel}";

    /// <summary>节点是否还如实反映这个盘（卷标文字与“能不能展开”都对）。</summary>
    private static bool CanReuse(SidebarNodeViewModel node, DriveModel drive)
        => string.Equals(node.Name, DriveDisplayName(drive), StringComparison.Ordinal)
           && node.HasUnrealizedChildren == drive.IsReady;

    /// <summary>把节点从路径索引里移除（只在该路径确实指向它时）。</summary>
    private void Unindex(SidebarNodeViewModel node)
    {
        if (_index.TryGetValue(node.FullPath, out var indexed) && ReferenceEquals(indexed, node))
        {
            _index.Remove(node.FullPath);
        }
    }
}
