using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>
/// 设置窗口（<c>Views/SettingsWindow</c>）的编辑模型：界面上每一项都绑到这里，
/// 任何一项被改动都会触发 <see cref="Changed" />，由窗口转交
/// <see cref="MainViewModel.ApplySettings" /> —— 设置是**即时生效并立即落盘**的
/// （Windows 11 设置的做法），没有“保存 / 取消”按钮。
///
/// 为什么不直接绑 <see cref="AppSettings" />：那份是不带变更通知的 POCO，
/// 而这里的属性要在改动时统一发通知（顺带夹取取值范围）；真正写回 <see cref="AppSettings" />
/// 只发生在 <see cref="MainViewModel.ApplySettings" /> 一处。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IShellContextMenuService _contextMenu;

    /// <summary>构建时被关掉的那些系统菜单项（用来给出初始开关状态）。</summary>
    private readonly HashSet<string> _shellMenuDisabled;

    private SettingsCategoryViewModel _selectedCategory;
    private bool _showHiddenFiles;
    private bool _showExtensions;
    private bool _foldersFirst;
    private bool _enableListAnimations;
    private bool _squareTabCorners;
    private bool _columnAutoFit;
    private bool _showToolbar;
    private bool _showSidebar;
    private bool _dualPane;
    private bool _startWithWindows;
    private double _rowHeight;
    private string _compressionOutputDirectory;
    private bool _useBuiltInContextMenu;
    private int _themeIndex;
    private bool _sidebarShowHome;
    private bool _sidebarShowFavorites;
    private bool _sidebarShowCloud;
    private bool _sidebarShowComputer;
    private bool _sidebarShowRemote;
    private bool _sidebarShowRecent;
    private bool _sidebarHomeDesktop;
    private bool _sidebarHomeDocuments;
    private bool _sidebarHomeDownloads;
    private bool _sidebarHomePictures;
    private bool _sidebarHomeMusic;
    private bool _sidebarHomeVideos;

    /// <summary>按当前设置生成一份编辑模型。</summary>
    public SettingsViewModel(AppSettings settings, IShellContextMenuService contextMenu)
    {
        _contextMenu = contextMenu;
        // 顺序即左侧导航的显示顺序
        Categories = new List<SettingsCategoryViewModel>
        {
            new(SettingsCategory.FileList, "文件列表"),
            new(SettingsCategory.Appearance, "外观"),
            new(SettingsCategory.Layout, "布局"),
            new(SettingsCategory.Startup, "启动"),
            new(SettingsCategory.Sidebar, "侧边栏"),
            new(SettingsCategory.Remote, "远程"),
            new(SettingsCategory.ShellMenu, "右键菜单"),
        };

        // 默认停在第一个分类，保证右侧永远有一页是可见的
        _selectedCategory = Categories[0];

        _shellMenuDisabled = new HashSet<string>(settings.ShellMenuDisabledItems, StringComparer.Ordinal);

        // 先用已经记下来的清单把页面填上（不碰 COM），再在窗口 Loaded 时用样本目标枚举一次补全
        RebuildShellMenuItems(settings.ShellMenuKnownItems);

        _showHiddenFiles = settings.ShowHiddenFiles;
        _showExtensions = settings.ShowExtensions;
        _foldersFirst = settings.FoldersFirst;
        _enableListAnimations = settings.EnableListAnimations;
        _squareTabCorners = settings.SquareTabCorners;
        _columnAutoFit = settings.ColumnAutoFit;
        _showToolbar = settings.ShowToolbar;
        _showSidebar = settings.IsSidebarVisible;
        _dualPane = settings.IsDualPane;
        _sidebarShowHome = settings.SidebarShowHome;
        _sidebarShowFavorites = settings.SidebarShowFavorites;
        _sidebarShowCloud = settings.SidebarShowCloud;
        _sidebarShowComputer = settings.SidebarShowComputer;
        _sidebarHomeDesktop = settings.SidebarHomeDesktop;
        _sidebarHomeDocuments = settings.SidebarHomeDocuments;
        _sidebarHomeDownloads = settings.SidebarHomeDownloads;
        _sidebarHomePictures = settings.SidebarHomePictures;
        _sidebarHomeMusic = settings.SidebarHomeMusic;
        _sidebarHomeVideos = settings.SidebarHomeVideos;
        _useBuiltInContextMenu = settings.UseBuiltInContextMenu;
        _startWithWindows = settings.StartWithWindows;
        _sidebarShowRemote = settings.SidebarShowRemote;
        _sidebarShowRecent = settings.SidebarShowRecent;

        // 远程位置：编辑的是一份**深拷贝**（设置窗口自己不改 config.json）
        foreach (var location in settings.RemoteLocations)
        {
            RemoteLocationItems.Add(new RemoteLocationItemViewModel(location.Clone()));
        }

        // 主题下拉框：0 = 跟随系统 / 1 = 浅色 / 2 = 深色（见 ThemeHelper，与 SettingsView.xaml 里的项顺序一致）
        _themeIndex = ThemeHelper.ToIndex(settings.Theme);

        // 行高：config.json 可能被手改过，夹进可用区间再填给滑条
        _rowHeight = ColumnLayout.NormalizeRowHeight(settings.RowHeight);

        // 压缩输出目录：空 = 用「下载」文件夹
        _compressionOutputDirectory = settings.CompressionOutputDirectory ?? string.Empty;
    }

    /// <summary>
    /// 任何一项设置被用户改动时触发（选中左侧分类不算）。
    /// 设置窗口订阅它来即时应用 + 落盘。
    /// </summary>
    public event EventHandler? Changed;

    // ------------------------------------------------------------------ 分类导航

    /// <summary>左侧导航的配置大类（顺序即显示顺序）。</summary>
    public IReadOnlyList<SettingsCategoryViewModel> Categories { get; }

    /// <summary>
    /// 当前选中的配置大类，右侧只显示它对应的那一页。
    /// 由窗口在 <c>NavigationView.ItemInvoked</c> 里赋值；给 null 直接忽略，
    /// 保证永远有一项是选中的。
    /// </summary>
    public SettingsCategoryViewModel SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value))
            {
                return;
            }

            // 五个页面各自绑一个 bool（比让 XAML 去比枚举省事，也不用给每个页面写转换器参数）
            OnPropertyChanged(nameof(IsFileListPageVisible));
            OnPropertyChanged(nameof(IsAppearancePageVisible));
            OnPropertyChanged(nameof(IsLayoutPageVisible));
            OnPropertyChanged(nameof(IsStartupPageVisible));
            OnPropertyChanged(nameof(IsSidebarPageVisible));
            OnPropertyChanged(nameof(IsRemotePageVisible));
            OnPropertyChanged(nameof(IsShellMenuPageVisible));
        }
    }

    /// <summary>右侧是否显示「文件列表」页。</summary>
    public bool IsFileListPageVisible => _selectedCategory.Key == SettingsCategory.FileList;

    /// <summary>右侧是否显示「外观」页。</summary>
    public bool IsAppearancePageVisible => _selectedCategory.Key == SettingsCategory.Appearance;

    /// <summary>右侧是否显示「布局」页。</summary>
    public bool IsLayoutPageVisible => _selectedCategory.Key == SettingsCategory.Layout;

    /// <summary>右侧是否显示「启动」页。</summary>
    public bool IsStartupPageVisible => _selectedCategory.Key == SettingsCategory.Startup;

    /// <summary>右侧是否显示「侧边栏」页。</summary>
    public bool IsSidebarPageVisible => _selectedCategory.Key == SettingsCategory.Sidebar;

    /// <summary>右侧是否显示「远程」页。</summary>
    public bool IsRemotePageVisible => _selectedCategory.Key == SettingsCategory.Remote;

    /// <summary>右侧是否显示「右键菜单」页。</summary>
    public bool IsShellMenuPageVisible => _selectedCategory.Key == SettingsCategory.ShellMenu;

    // ------------------------------------------------------------------ 右键菜单

    /// <summary>
    /// 用 exdir 自己构建的轻量右键菜单（弹出快，只含 exdir 自己的命令），
    /// 关掉则回到系统外壳菜单（内容完整、含第三方扩展，但弹出慢）。
    /// </summary>
    public bool UseBuiltInContextMenu
    {
        get => _useBuiltInContextMenu;
        set => SetAndNotify(ref _useBuiltInContextMenu, value);
    }

    /// <summary>
    /// 系统右键菜单项清单（含第三方扩展）。顺序：顶级菜单在前，然后按“子菜单路径 + 文本”排。
    /// 每项的开关就是“是否保留在右键菜单里”，默认全开。
    /// </summary>
    public ObservableCollection<ShellMenuItemViewModel> ShellMenuItems { get; } = new();

    /// <summary>清单是不是空的（第一次运行时枚举还没回来），用来显示“正在读取…”。</summary>
    public bool HasShellMenuItems => ShellMenuItems.Count > 0;

    /// <summary>
    /// 用样本目标（文本文件 / 文件夹 / 目录背景）现枚举一次系统菜单，并把结果并进页面。
    /// 由窗口在 Loaded 之后调用：枚举要建 COM 对象、耗时几十到几百毫秒，不能在构造里做。
    /// </summary>
    public void RefreshShellMenuItems()
    {
        try
        {
            RebuildShellMenuItems(_contextMenu.GetCatalog());
        }
        catch (Exception)
        {
            // 枚举失败就保留构造时用已记下来的那份清单，不影响设置窗口
            return;
        }

        // 现枚举出来的项要留下来（否则下次打开又得重新枚举一次），所以这次也算“改动了设置”
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重建清单：已有的项保留用户刚拨过的开关（Refresh 时不会把用户的改动冲掉），
    /// 没见过的项按“设置里被关掉的集合”给默认值。
    /// </summary>
    private void RebuildShellMenuItems(IEnumerable<ShellMenuItem> items)
    {
        // 用索引器而不是 ToDictionary：万一 config.json 被手改出重复 key，也不能把窗口开崩
        var previous = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var existing in ShellMenuItems)
        {
            previous[existing.Item.Key] = existing.IsEnabled;
            existing.PropertyChanged -= OnShellMenuItemChanged;
        }

        var usedTitles = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        ShellMenuItems.Clear();

        foreach (var item in items)
        {
            var enabled = previous.TryGetValue(item.Key, out var current)
                ? current
                : !_shellMenuDisabled.Contains(item.Key);

            var row = new ShellMenuItemViewModel(item, MakeUniqueTitle(item, usedTitles), enabled);
            // 开关的改动要能被窗口看到（ShellMenuItemViewModel 是清单里的行，不是本类的属性）
            row.PropertyChanged += OnShellMenuItemChanged;
            ShellMenuItems.Add(row);
        }

        OnPropertyChanged(nameof(HasShellMenuItems));
    }

    private void OnShellMenuItemChanged(object? sender, PropertyChangedEventArgs e)
        => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 同一个名字可能出现多次（同一个扩展在不同上下文、或子菜单里也有同名项），
    /// 而开关的 UIA 名字就是标题 —— 重名会让自动化脚本和屏幕阅读器都分不清，所以补上区分后缀。
    /// </summary>
    private static string MakeUniqueTitle(ShellMenuItem item, HashSet<string> usedTitles)
    {
        if (usedTitles.Add(item.Text))
        {
            return item.Text;
        }

        var suffix = !string.IsNullOrEmpty(item.MenuPath)
            ? item.MenuPath
            : string.Join("/", item.Scopes);

        var index = 2;
        var title = $"{item.Text}（{suffix}）";

        while (!usedTitles.Add(title))
        {
            title = $"{item.Text}（{suffix} {index++}）";
        }

        return title;
    }

    // ------------------------------------------------------------------ 文件列表

    /// <summary>显示隐藏文件与系统文件。</summary>
    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        set => SetAndNotify(ref _showHiddenFiles, value);
    }

    /// <summary>显示文件扩展名。</summary>
    public bool ShowExtensions
    {
        get => _showExtensions;
        set => SetAndNotify(ref _showExtensions, value);
    }

    /// <summary>文件夹排在文件前面。</summary>
    public bool FoldersFirst
    {
        get => _foldersFirst;
        set => SetAndNotify(ref _foldersFirst, value);
    }

    /// <summary>文件列表的行高（DIP），对应「文件列表」页里的滑块。</summary>
    public double RowHeight
    {
        get => _rowHeight;
        set => SetAndNotify(ref _rowHeight, ColumnLayout.NormalizeRowHeight(value));
    }

    /// <summary>行高滑块的上下限与步进（与 <see cref="ColumnLayout" /> 里的常量同源）。</summary>
    public double RowHeightMinimum => ColumnLayout.MinRowHeight;

    public double RowHeightMaximum => ColumnLayout.MaxRowHeight;

    public double RowHeightStep => ColumnLayout.RowHeightStep;

    /// <summary>
    /// 右键「压缩」生成的 zip 保存到哪个目录；空 = 用户的「下载」文件夹。
    /// 界面上是一个可直接编辑的路径框（旁边有「浏览…」按钮，由视图负责弹文件夹选择器 ——
    /// 这里只管收值，改动还是走同一个 <see cref="Changed" /> 即时落盘）。
    /// </summary>
    public string CompressionOutputDirectory
    {
        get => _compressionOutputDirectory;
        set => SetAndNotify(ref _compressionOutputDirectory, value ?? string.Empty);
    }

    // ------------------------------------------------------------------ 外观

    /// <summary>
    /// 主题下拉框的选中下标：0 = 跟随系统、1 = 浅色、2 = 深色。
    /// 用下标而不是枚举，是因为 XAML 里 ComboBox 的项就是按这个顺序写死的。
    /// </summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetAndNotify(ref _themeIndex, ThemeHelper.ToIndex(ThemeHelper.FromIndex(value)));
    }

    /// <summary>
    /// 把下拉框同步到外部改动的主题（用户点了标题栏那个太阳 / 月亮开关）。
    /// 故意不发 <see cref="Changed" />：那会反过来再走一轮 ApplySettings，纯属白做。
    /// </summary>
    public void SyncTheme(AppTheme theme)
    {
        var index = ThemeHelper.ToIndex(theme);
        if (_themeIndex == index)
        {
            return;
        }

        _themeIndex = index;
        OnPropertyChanged(nameof(ThemeIndex));
    }

    /// <summary>列表过渡动画（换目录入场 / 插行重排）。</summary>
    public bool EnableListAnimations
    {
        get => _enableListAnimations;
        set => SetAndNotify(ref _enableListAnimations, value);
    }

    /// <summary>标签页顶部两个角用直角（关掉 = WinUI 默认的圆角）。</summary>
    public bool SquareTabCorners
    {
        get => _squareTabCorners;
        set => SetAndNotify(ref _squareTabCorners, value);
    }

    // ------------------------------------------------------------------ 布局

    /// <summary>列宽自动适应窗格宽度（关掉 = 固定列宽 + 横向滚动）。</summary>
    public bool ColumnAutoFit
    {
        get => _columnAutoFit;
        set => SetAndNotify(ref _columnAutoFit, value);
    }

    /// <summary>显示工具条。</summary>
    public bool ShowToolbar
    {
        get => _showToolbar;
        set => SetAndNotify(ref _showToolbar, value);
    }

    /// <summary>显示侧边栏文件夹树。</summary>
    public bool ShowSidebar
    {
        get => _showSidebar;
        set => SetAndNotify(ref _showSidebar, value);
    }

    /// <summary>双窗格模式。</summary>
    public bool DualPane
    {
        get => _dualPane;
        set => SetAndNotify(ref _dualPane, value);
    }

    // ------------------------------------------------------------------ 启动

    /// <summary>
    /// 开机自启：登录时在后台启动 exdir（不显示主窗口，只恢复上次的会话与首屏图标），
    /// 这样之后双击 exe / 点托盘图标几乎是瞬时的。
    /// 实际写注册表在 <see cref="MainViewModel.ApplySettings" />（设置应用只有那一个入口）。
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetAndNotify(ref _startWithWindows, value);
    }

    // ------------------------------------------------------------------ 侧边栏

    /// <summary>显示「主目录」分组（主目录入口 + 桌面 / 文档 / 下载等标准文件夹）。</summary>
    public bool SidebarShowHome
    {
        get => _sidebarShowHome;
        set => SetAndNotify(ref _sidebarShowHome, value);
    }

    /// <summary>显示「收藏夹」分组（工具条固定目录的镜像，也是拖拽收藏的落点）。</summary>
    public bool SidebarShowFavorites
    {
        get => _sidebarShowFavorites;
        set => SetAndNotify(ref _sidebarShowFavorites, value);
    }

    /// <summary>显示「云存储」分组（OneDrive / WPS 等同步根）。</summary>
    public bool SidebarShowCloud
    {
        get => _sidebarShowCloud;
        set => SetAndNotify(ref _sidebarShowCloud, value);
    }

    /// <summary>显示「此电脑」分组（各磁盘）。</summary>
    public bool SidebarShowComputer
    {
        get => _sidebarShowComputer;
        set => SetAndNotify(ref _sidebarShowComputer, value);
    }

    /// <summary>显示「远程」分组（SFTP / FTP 位置）。</summary>
    public bool SidebarShowRemote
    {
        get => _sidebarShowRemote;
        set => SetAndNotify(ref _sidebarShowRemote, value);
    }

    /// <summary>显示「最新访问」分组（最近导航过的目录，排在最上面）。</summary>
    public bool SidebarShowRecent
    {
        get => _sidebarShowRecent;
        set => SetAndNotify(ref _sidebarShowRecent, value);
    }

    // ------------------------------------------------------------------ 远程位置（SFTP / FTP）

    /// <summary>
    /// 配置好的远程位置（设置窗口「远程」页里那几行）。
    /// 每行点「编辑…」弹对话框改的就是这份副本；改完立即发一次 <see cref="Changed" />。
    /// </summary>
    public ObservableCollection<RemoteLocationItemViewModel> RemoteLocationItems { get; } = new();

    /// <summary>一行都没有时显示“还没有远程位置”的提示。</summary>
    public bool HasRemoteLocations => RemoteLocationItems.Count > 0;

    /// <summary>
    /// 交给 <c>MainViewModel.ApplySettings</c> 的清单快照（都是副本，改这份不会动设置里的对象）。
    /// </summary>
    public IReadOnlyList<RemoteLocation> RemoteLocations
        => RemoteLocationItems.Select(static item => item.Location.Clone()).ToList();

    /// <summary>新增一个位置（对话框确认后调）。</summary>
    public void AddRemoteLocation(RemoteLocation location)
    {
        RemoteLocationItems.Add(new RemoteLocationItemViewModel(location));
        AfterRemoteLocationsChanged();
    }

    /// <summary>替换一行（编辑对话框确认后调）；行不在集合里时什么都不做。</summary>
    public void UpdateRemoteLocation(RemoteLocationItemViewModel item, RemoteLocation location)
    {
        var index = RemoteLocationItems.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        item.Update(location);

        // 名字 / 起始目录变了可能影响排序，但用户自己排的顺序更该尊重 —— 原位替换
        RemoteLocationItems[index] = item;
        AfterRemoteLocationsChanged();
    }

    /// <summary>删掉一行。</summary>
    public void RemoveRemoteLocation(RemoteLocationItemViewModel item)
    {
        if (RemoteLocationItems.Remove(item))
        {
            AfterRemoteLocationsChanged();
        }
    }

    private void AfterRemoteLocationsChanged()
    {
        OnPropertyChanged(nameof(RemoteLocations));
        OnPropertyChanged(nameof(HasRemoteLocations));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ 侧边栏：主目录里的标准文件夹
    // 侧边栏「主目录」分组里显示哪几个标准文件夹（默认只开桌面与下载）。
    // 按 UserFolderKey 匹配，不依赖显示名（见 SidebarViewModel.ApplyHomeFolders）。

    /// <summary>「主目录」里显示“桌面”。</summary>
    public bool SidebarHomeDesktop
    {
        get => _sidebarHomeDesktop;
        set => SetAndNotify(ref _sidebarHomeDesktop, value);
    }

    /// <summary>「主目录」里显示“文档”。</summary>
    public bool SidebarHomeDocuments
    {
        get => _sidebarHomeDocuments;
        set => SetAndNotify(ref _sidebarHomeDocuments, value);
    }

    /// <summary>「主目录」里显示“下载”。</summary>
    public bool SidebarHomeDownloads
    {
        get => _sidebarHomeDownloads;
        set => SetAndNotify(ref _sidebarHomeDownloads, value);
    }

    /// <summary>「主目录」里显示“图片”。</summary>
    public bool SidebarHomePictures
    {
        get => _sidebarHomePictures;
        set => SetAndNotify(ref _sidebarHomePictures, value);
    }

    /// <summary>「主目录」里显示“音乐”。</summary>
    public bool SidebarHomeMusic
    {
        get => _sidebarHomeMusic;
        set => SetAndNotify(ref _sidebarHomeMusic, value);
    }

    /// <summary>「主目录」里显示“视频”。</summary>
    public bool SidebarHomeVideos
    {
        get => _sidebarHomeVideos;
        set => SetAndNotify(ref _sidebarHomeVideos, value);
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>值真的变了才通知界面、并广播一次 <see cref="Changed" />。</summary>
    private bool SetAndNotify<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
