using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>
/// 外壳主 ViewModel：磁盘条、固定目录、快捷菜单、侧边栏、窗格集合与全局命令。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>
    /// 工具条上最多固定多少个目录。工具条的固定目录区不滚动，太多了会把左边的磁盘区挤没，
    /// 所以拖放/菜单新增时统一卡在这个上限（已有的配置项不会被删除）。
    /// </summary>
    private const int MaxPinnedFolders = 12;

    private readonly ISettingsService _settings;
    private readonly IDriveService _driveService;
    private readonly IKnownFolderService _knownFolders;
    private readonly IFileSystemService _fileSystem;
    private readonly IShellService _shell;
    private readonly IShellIconService _icons;
    private readonly IShellContextMenuService _contextMenu;
    private readonly IFileOperationService _fileOperations;

    private PanelViewModel _activePane = null!;
    private bool _isDualPane;
    private bool _isToolbarVisible = true;
    private bool _isSidebarVisible = true;

    public MainViewModel(
        ISettingsService settings,
        IDriveService driveService,
        INetworkLocationService networkLocations,
        IKnownFolderService knownFolders,
        IFileSystemService fileSystem,
        IShellService shell,
        IShellIconService icons,
        IShellContextMenuService contextMenu,
        IClipboardService clipboard,
        IFileOperationService fileOperations)
    {
        _settings = settings;
        _driveService = driveService;
        _knownFolders = knownFolders;
        _fileSystem = fileSystem;
        _shell = shell;
        _icons = icons;
        _contextMenu = contextMenu;
        _fileOperations = fileOperations;

        // 复制 / 移动完成后要让受影响的目录重新枚举（可能是另一个窗格、另一个标签页）
        _fileOperations.Completed += OnFileOperationCompleted;

        Sidebar = new SidebarViewModel(fileSystem, knownFolders, driveService, networkLocations);
        Sidebar.NavigateRequested += OnSidebarNavigateRequested;
        Sidebar.PinRequested += OnSidebarPinRequested;
        Sidebar.UnpinRequested += OnSidebarUnpinRequested;

        // 侧边栏的「收藏夹」分组是工具条固定目录的镜像：增删、拖拽排序都立刻同步过去
        PinnedFolders.CollectionChanged += (_, _) => Sidebar.SyncFavorites(PinnedFolders);

        PrimaryPane = new PanelViewModel("primary", fileSystem, shell, settings, icons, contextMenu, clipboard, fileOperations);
        SecondaryPane = new PanelViewModel("secondary", fileSystem, shell, settings, icons, contextMenu, clipboard, fileOperations);

        PrimaryPane.Navigated += OnPaneNavigated;
        SecondaryPane.Navigated += OnPaneNavigated;

        PrimaryPane.ActivateRequested += OnPaneActivateRequested;
        SecondaryPane.ActivateRequested += OnPaneActivateRequested;

        ActivePane = PrimaryPane;

        // 状态栏要读活动窗格 / 活动标签页，因此必须等 ActivePane 就位后再建
        StatusBar = new StatusBarViewModel(this, driveService);

        ReloadDrives();
        LoadPinnedFolders();
        LoadQuickCommands();

        // 侧边栏四个分组的显示开关来自设置，必须在窗口首次渲染前生效（InitializeAsync 在 Loaded 里，
        // 那时窗口已经可见，不在这里先应用的话会先闪一下全部四个分组）
        ApplySidebarGroups();
    }

    // ------------------------------------------------------------------ 集合

    /// <summary>title 栏左侧的磁盘按钮。</summary>
    public ObservableCollection<DriveModel> Drives { get; } = new();

    /// <summary>title 栏右侧固定的常用目录。</summary>
    public ObservableCollection<PinnedFolderViewModel> PinnedFolders { get; } = new();

    /// <summary>title 栏“快捷菜单”中的命令（当前为空，由设置提供）。</summary>
    public ObservableCollection<QuickCommand> QuickCommands { get; } = new();

    public SidebarViewModel Sidebar { get; }

    public PanelViewModel PrimaryPane { get; }

    public PanelViewModel SecondaryPane { get; }

    /// <summary>文件列表区底部状态栏的数据源（全窗口一条，始终跟随活动窗格）。</summary>
    public StatusBarViewModel StatusBar { get; }

    // ------------------------------------------------------------------ 布局状态

    /// <summary>当前获得焦点的窗格，导航命令作用于它。</summary>
    public PanelViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (value is null || ReferenceEquals(_activePane, value))
            {
                return;
            }

            _activePane = value;
            PrimaryPane.IsActive = ReferenceEquals(value, PrimaryPane);
            SecondaryPane.IsActive = ReferenceEquals(value, SecondaryPane);

            OnPropertyChanged();
            RaiseActivePaneDependent();
        }
    }

    public bool IsDualPane
    {
        get => _isDualPane;
        set
        {
            if (!SetProperty(ref _isDualPane, value))
            {
                return;
            }

            _settings.Current.IsDualPane = value;

            if (value)
            {
                _ = EnsureSecondaryPaneReadyAsync();
            }
            else if (ReferenceEquals(ActivePane, SecondaryPane))
            {
                ActivePane = PrimaryPane;
            }
        }
    }

    public bool IsToolbarVisible
    {
        get => _isToolbarVisible;
        set
        {
            if (SetProperty(ref _isToolbarVisible, value))
            {
                _settings.Current.ShowToolbar = value;
            }
        }
    }

    public bool IsSidebarVisible
    {
        get => _isSidebarVisible;
        set
        {
            if (SetProperty(ref _isSidebarVisible, value))
            {
                _settings.Current.IsSidebarVisible = value;
            }
        }
    }

    public bool ShowHiddenFiles
    {
        get => _settings.Current.ShowHiddenFiles;
        set
        {
            if (_settings.Current.ShowHiddenFiles == value)
            {
                return;
            }

            _settings.Current.ShowHiddenFiles = value;
            OnPropertyChanged();
            _ = ApplyViewSettingsToAllTabsAsync();
        }
    }

    public bool ShowExtensions
    {
        get => _settings.Current.ShowExtensions;
        set
        {
            if (_settings.Current.ShowExtensions == value)
            {
                return;
            }

            _settings.Current.ShowExtensions = value;
            OnPropertyChanged();
            _ = ApplyViewSettingsToAllTabsAsync();
        }
    }

    /// <summary>文件列表是否播放过渡动画（配置菜单 → 文件列表 → 过渡动画）。</summary>
    public bool EnableListAnimations
    {
        get => _settings.Current.EnableListAnimations;
        set
        {
            if (_settings.Current.EnableListAnimations == value)
            {
                return;
            }

            _settings.Current.EnableListAnimations = value;
            OnPropertyChanged();

            // 动画开关只改变视图行为，不必重新枚举目录
            foreach (var pane in new[] { PrimaryPane, SecondaryPane })
            {
                foreach (var tab in pane.Tabs)
                {
                    tab.ApplyAnimationSettings();
                }
            }
        }
    }

    // ------------------------------------------------------------------ 主题

    /// <summary>
    /// 主题：跟随系统 / 浅色 / 深色（设置窗口「外观 → 主题」，也是标题栏那个太阳 / 月亮开关）。
    /// ViewModel 只负责“值”与落盘，真正换主题是 <c>MainWindow.ApplyTheme</c>
    /// （给根元素设 RequestedTheme；<c>Application.RequestedTheme</c> 启动后不允许再改）。
    /// </summary>
    public AppTheme Theme
    {
        get => _settings.Current.Theme;
        set
        {
            var theme = ThemeHelper.Normalize(value);
            if (_settings.Current.Theme == theme)
            {
                return;
            }

            _settings.Current.Theme = theme;
            OnPropertyChanged();

            // 换主题是个“明确动作”（拨开关 / 选下拉框），当场落盘，不必等到退出
            _settings.Save();
            Log.Write($"主题：{ThemeHelper.ToDisplayName(theme)}");
        }
    }

    /// <summary>
    /// 标题栏那个太阳 / 月亮开关：true = 深色。
    /// 不管当前是不是「跟随系统」，拨一下就固定成显式的浅色 / 深色（与市面上大多数应用的开关一致）。
    /// </summary>
    public void SetDarkMode(bool dark) => Theme = dark ? AppTheme.Dark : AppTheme.Light;

    // ------------------------------------------------------------------ 标题

    /// <summary>窗口标题栏中间显示的当前目录名。</summary>
    public string ActiveDirectoryName => ActivePane.ActiveTab?.CurrentDirectoryName ?? "此电脑";

    /// <summary>窗口标题栏中间的完整路径（悬停提示 / 标题）。</summary>
    public string ActiveDirectoryPath => ActivePane.ActiveTab?.CurrentPath ?? string.Empty;

    public string WindowTitleText => string.IsNullOrEmpty(ActiveDirectoryPath)
        ? "exdir"
        : $"{ActiveDirectoryName} - {ActiveDirectoryPath} - exdir";

    // ------------------------------------------------------------------ 命令

    [RelayCommand]
    private void ToggleDualPane() => IsDualPane = !IsDualPane;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleToolbar() => IsToolbarVisible = !IsToolbarVisible;

    [RelayCommand]
    private void ToggleHiddenFiles() => ShowHiddenFiles = !ShowHiddenFiles;

    [RelayCommand]
    private void ToggleShowExtensions() => ShowExtensions = !ShowExtensions;

    [RelayCommand]
    private void ToggleListAnimations() => EnableListAnimations = !EnableListAnimations;

    [RelayCommand]
    private async Task RefreshActivePaneAsync()
    {
        if (ActivePane.ActiveTab is { } tab)
        {
            await tab.RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (ActivePane.ActiveTab is { } tab && tab.GoBackCommand.CanExecute(null))
        {
            await tab.GoBackCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (ActivePane.ActiveTab is { } tab && tab.GoForwardCommand.CanExecute(null))
        {
            await tab.GoForwardCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (ActivePane.ActiveTab is { } tab && tab.GoUpCommand.CanExecute(null))
        {
            await tab.GoUpCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NewTabAsync()
    {
        await ActivePane.NewTabAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseActiveTab()
    {
        if (ActivePane.ActiveTab is { } tab)
        {
            ActivePane.RemoveTab(tab);
        }
    }

    [RelayCommand]
    private void OpenTerminal() => ActivePane.ActiveTab?.OpenTerminalCommand.Execute(null);

    [RelayCommand]
    private void OpenTerminalAsAdmin() => ActivePane.ActiveTab?.OpenTerminalAsAdminCommand.Execute(null);

    [RelayCommand]
    private void CopyCurrentPath() => ActivePane.ActiveTab?.CopyCurrentPathCommand.Execute(null);

    /// <summary>编辑菜单「复制」/ Ctrl+C：复制活动窗格里选中的项。</summary>
    [RelayCommand]
    private void CopySelection()
    {
        if (ActivePane.ActiveTab is { } tab && tab.CopySelectionCommand.CanExecute(null))
        {
            tab.CopySelectionCommand.Execute(null);
        }
    }

    /// <summary>编辑菜单「剪切」/ Ctrl+X。</summary>
    [RelayCommand]
    private void CutSelection()
    {
        if (ActivePane.ActiveTab is { } tab && tab.CutSelectionCommand.CanExecute(null))
        {
            tab.CutSelectionCommand.Execute(null);
        }
    }

    /// <summary>编辑菜单「粘贴」/ Ctrl+V：粘贴到活动窗格的当前目录。</summary>
    [RelayCommand]
    private async Task PasteAsync()
    {
        if (ActivePane.ActiveTab is { } tab)
        {
            await tab.PasteCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    /// <summary>编辑菜单「删除」/ Del：把活动窗格里选中的项丢进回收站。</summary>
    [RelayCommand]
    private async Task DeleteSelectionAsync()
    {
        if (ActivePane.ActiveTab is { } tab && tab.DeleteSelectionCommand.CanExecute(null))
        {
            await tab.DeleteSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void RevealInExplorer() => ActivePane.ActiveTab?.RevealInExplorerCommand.Execute(null);

    [RelayCommand]
    private void SwitchActivePane() => ActivePane = ReferenceEquals(ActivePane, PrimaryPane) ? SecondaryPane : PrimaryPane;

    /// <summary>把活动标签页的地址栏切到可编辑态（Ctrl+L / Alt+D）。</summary>
    [RelayCommand]
    private void EditActivePath() => ActivePane.ActiveTab?.BeginPathEdit();

    /// <summary>导航指定窗格（<c>primary</c> / <c>secondary</c>）到某路径。</summary>
    [RelayCommand]
    private async Task NavigateToAsync(string? path)
    {
        var target = path;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (ActivePane.ActiveTab is { } tab)
        {
            await tab.NavigateAsync(target).ConfigureAwait(true);
        }
    }

    /// <summary>把当前目录固定到 title 栏（若尚未固定）。</summary>
    [RelayCommand]
    private void PinCurrentFolder()
    {
        if (ActivePane.ActiveTab?.CurrentPath is not { } path)
        {
            return;
        }

        if (TryPinFolder(path))
        {
            PersistPinnedFolders(writeToDisk: true);
        }
    }

    [RelayCommand]
    private void UnpinFolder(PinnedFolderViewModel? folder)
    {
        if (folder is null)
        {
            return;
        }

        PinnedFolders.Remove(folder);
        PersistPinnedFolders(writeToDisk: true);
    }

    /// <summary>按路径取消固定（侧边栏收藏项的右键「取消收藏」）。</summary>
    public void UnpinFolderByPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var folder = PinnedFolders.FirstOrDefault(
            p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

        if (folder is not null)
        {
            UnpinFolder(folder);
        }
    }

    /// <summary>
    /// 工具条上的固定目录拖拽排序：把 <paramref name="path" /> 插到第 <paramref name="targetIndex" />
    /// 个之前（<c>0..Count</c>，<c>Count</c> 表示放到最后）。和增删一样立即落盘。
    /// </summary>
    public void MovePinnedFolder(string? path, int targetIndex)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var current = -1;
        for (var i = 0; i < PinnedFolders.Count; i++)
        {
            if (string.Equals(PinnedFolders[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                current = i;
                break;
            }
        }

        if (current < 0)
        {
            return;
        }

        // 目标下标是按“自己还在列表里”算出来的，往后拖要减一才是移除后的插入位置
        var target = Math.Clamp(targetIndex, 0, PinnedFolders.Count);
        if (target > current)
        {
            target--;
        }

        if (target == current)
        {
            return;
        }

        // 用移除 + 插入而不是 ObservableCollection.Move：ItemsControl 对 Move 通知的支持
        // 依版本而异，而这个集合只有十来个元素，重建容器的代价可以忽略
        var folder = PinnedFolders[current];
        PinnedFolders.RemoveAt(current);
        PinnedFolders.Insert(target, folder);

        PersistPinnedFolders(writeToDisk: true);
        Log.Write($"固定目录排序：{folder.Name} 移到第 {target + 1} 位");
    }

    /// <summary>
    /// 把一批路径固定到工具条（文件列表 / 侧边栏拖到工具条固定目录区）。
    /// 返回真正新增的条目数：文件、不存在的路径、已经固定的目录都会被跳过。
    /// </summary>
    public int PinFolders(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return 0;
        }

        var added = 0;
        foreach (var path in paths)
        {
            if (TryPinFolder(path))
            {
                added++;
            }
        }

        if (added > 0)
        {
            PersistPinnedFolders(writeToDisk: true);
        }

        return added;
    }

    /// <summary>固定单个目录；不是已存在的目录、或已经固定过（或工具条已满）时返回 false。</summary>
    public bool TryPinFolder(string? path)
    {
        // 先确认是目录：NormalizeDirectoryPath 对文件路径会返回它的上级目录，
        // 直接用它会把“拖了个文件上来”变成“固定了文件所在的目录”
        if (string.IsNullOrWhiteSpace(path) || !_fileSystem.DirectoryExists(path))
        {
            return false;
        }

        var normalized = _fileSystem.NormalizeDirectoryPath(path);
        if (normalized is null)
        {
            return false;
        }

        if (PinnedFolders.Count >= MaxPinnedFolders)
        {
            return false;
        }

        if (PinnedFolders.Any(p => string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        PinnedFolders.Add(new PinnedFolderViewModel(normalized));
        return true;
    }

    /// <summary>执行快捷菜单命令。</summary>
    [RelayCommand]
    private void RunQuickCommand(QuickCommand? command)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.CommandLine))
        {
            return;
        }

        var tab = ActivePane.ActiveTab;
        var workingDirectory = tab?.CurrentPath ?? _knownFolders.UserProfile;
        var selection = tab is null
            ? string.Empty
            : string.Join(' ', tab.Selection.Select(i => $"\"{i.FullPath}\""));

        var line = command.CommandLine
            .Replace("{path}", workingDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{selection}", selection, StringComparison.OrdinalIgnoreCase);

        _shell.RunCommand(line, workingDirectory, command.RunAsAdministrator);
    }

    // ------------------------------------------------------------------ 生命周期

    /// <summary>恢复上次会话。由主窗口 Loaded 事件调用。</summary>
    public async Task InitializeAsync()
    {
        ApplySettingsToState();

        await RestorePaneAsync(
            PrimaryPane,
            _settings.Current.PrimaryTabs,
            _settings.Current.PrimaryActiveTab,
            fallback: _knownFolders.UserProfile).ConfigureAwait(true);

        if (IsDualPane)
        {
            await RestorePaneAsync(
                SecondaryPane,
                _settings.Current.SecondaryTabs,
                _settings.Current.SecondaryActiveTab,
                fallback: _knownFolders.UserProfile).ConfigureAwait(true);
        }

        RaiseActivePaneDependent();
    }

    /// <summary>把当前布局与会话写入设置并落盘。由主窗口 Closed 事件调用。</summary>
    public void SaveSession()
    {
        _settings.Current.PrimaryTabs = PrimaryPane.Tabs
            .Select(t => t.CurrentPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        _settings.Current.PrimaryActiveTab = PrimaryPane.ActiveTab is null
            ? 0
            : Math.Max(0, PrimaryPane.Tabs.IndexOf(PrimaryPane.ActiveTab));

        _settings.Current.SecondaryTabs = SecondaryPane.Tabs
            .Select(t => t.CurrentPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        _settings.Current.SecondaryActiveTab = SecondaryPane.ActiveTab is null
            ? 0
            : Math.Max(0, SecondaryPane.Tabs.IndexOf(SecondaryPane.ActiveTab));

        PersistPinnedFolders();
        _settings.Current.QuickCommands = QuickCommands.ToList();
        _settings.Current.FoldersFirst = PrimaryPane.ActiveTab?.FoldersFirst ?? true;

        _settings.Save();
    }

    // ------------------------------------------------------------------ 设置窗口

    /// <summary>
    /// 给设置窗口做一份编辑模型。设置是即时生效的，所以它只有一份、窗口关闭就丢掉。
    /// </summary>
    public SettingsViewModel CreateSettingsEditor() => new(_settings.Current, _contextMenu);

    /// <summary>
    /// 应用设置窗口里改过的内容（用户每动一项就调一次，见 <see cref="SettingsViewModel.Changed" />）。
    /// 因为是明确动作，所以结束时立刻落盘，不等到退出。
    /// </summary>
    public void ApplySettings(SettingsViewModel edited)
    {
        ArgumentNullException.ThrowIfNull(edited);

        var settings = _settings.Current;

        // 先只改字段、最后统一刷新：隐藏文件与扩展名都会触发重新枚举目录，
        // 走属性设置器的话两项一起改就会把每个目录白枚举两遍。
        var reloadLists = settings.ShowHiddenFiles != edited.ShowHiddenFiles
                          || settings.ShowExtensions != edited.ShowExtensions;

        settings.ShowHiddenFiles = edited.ShowHiddenFiles;
        settings.ShowExtensions = edited.ShowExtensions;
        settings.FoldersFirst = edited.FoldersFirst;
        settings.ColumnAutoFit = edited.ColumnAutoFit;
        settings.RowHeight = ColumnLayout.NormalizeRowHeight(edited.RowHeight);
        settings.SquareTabCorners = edited.SquareTabCorners;
        settings.Theme = ThemeHelper.Normalize(ThemeHelper.FromIndex(edited.ThemeIndex));

        // 侧边栏四个分组的显示开关（设置窗口「侧边栏」页）
        settings.SidebarShowHome = edited.SidebarShowHome;
        settings.SidebarShowFavorites = edited.SidebarShowFavorites;
        settings.SidebarShowCloud = edited.SidebarShowCloud;
        settings.SidebarShowComputer = edited.SidebarShowComputer;

        // 「主目录」分组里显示哪几个标准文件夹
        settings.SidebarHomeDesktop = edited.SidebarHomeDesktop;
        settings.SidebarHomeDocuments = edited.SidebarHomeDocuments;
        settings.SidebarHomeDownloads = edited.SidebarHomeDownloads;
        settings.SidebarHomePictures = edited.SidebarHomePictures;
        settings.SidebarHomeMusic = edited.SidebarHomeMusic;
        settings.SidebarHomeVideos = edited.SidebarHomeVideos;

        OnPropertyChanged(nameof(ShowHiddenFiles));
        OnPropertyChanged(nameof(ShowExtensions));

        // 主题：主窗口与设置窗口都在听这个属性（重设同一个 RequestedTheme 是无害的，不必先比一遍）
        OnPropertyChanged(nameof(Theme));

        // 动画开关自带“应用到所有标签页”的逻辑，而且只改视图行为不重载目录，直接走属性设置器
        EnableListAnimations = edited.EnableListAnimations;

        foreach (var pane in new[] { PrimaryPane, SecondaryPane })
        {
            foreach (var tab in pane.Tabs)
            {
                // 赋值是幂等的：值没变时不会重排 / 不触发列宽回写
                tab.FoldersFirst = edited.FoldersFirst;
                tab.Columns.AutoFit = edited.ColumnAutoFit;
                tab.Columns.RowHeight = settings.RowHeight;
                tab.ApplyTabCornerSettings();
            }
        }

        if (reloadLists)
        {
            _ = ApplyViewSettingsToAllTabsAsync();
        }

        IsToolbarVisible = edited.ShowToolbar;
        IsSidebarVisible = edited.ShowSidebar;
        IsDualPane = edited.DualPane;

        ApplySidebarGroups();

        // 右键菜单：风格（系统 / 内置）与清单。
        // 风格是视图在每次右键时现读的（见 FolderTabViewModel.UseBuiltInContextMenu），
        // 所以这里只需写回设置，不需要通知任何界面。
        settings.UseBuiltInContextMenu = edited.UseBuiltInContextMenu;

        // 清单本身也存回去（设置页里新枚举出来的项要留下，否则下次打开又得重新枚举），
        // 被关掉的只存 Key，弹出菜单前据此把项从 HMENU 里删掉。
        settings.ShellMenuKnownItems = edited.ShellMenuItems.Select(i => i.Item).ToList();
        settings.ShellMenuDisabledItems = edited.ShellMenuItems
            .Where(i => !i.IsEnabled)
            .Select(i => i.Item.Key)
            .ToList();

        _settings.Save();

        Log.Write(
            $"设置已应用：隐藏文件={edited.ShowHiddenFiles} 扩展名={edited.ShowExtensions} "
            + $"文件夹优先={edited.FoldersFirst} 动画={edited.EnableListAnimations} 列宽自适应={edited.ColumnAutoFit} "
            + $"行高={settings.RowHeight:0} 标签页={(settings.SquareTabCorners ? "直角" : "圆角")} "
            + $"主题={ThemeHelper.ToDisplayName(settings.Theme)} "
            + $"工具条={edited.ShowToolbar} 侧边栏={edited.ShowSidebar} 双窗格={edited.DualPane} "
            + $"侧边栏分组（主目录/收藏夹/云存储/此电脑）="
            + $"{edited.SidebarShowHome}/{edited.SidebarShowFavorites}/{edited.SidebarShowCloud}/{edited.SidebarShowComputer} "
            + $"主目录文件夹（桌面/文档/下载/图片/音乐/视频）="
            + $"{edited.SidebarHomeDesktop}/{edited.SidebarHomeDocuments}/{edited.SidebarHomeDownloads}/"
            + $"{edited.SidebarHomePictures}/{edited.SidebarHomeMusic}/{edited.SidebarHomeVideos} "
            + $"右键菜单={(edited.UseBuiltInContextMenu ? "内置" : "系统")} "
            + $"系统菜单项={edited.ShellMenuItems.Count}（关闭 {settings.ShellMenuDisabledItems.Count}）");
    }

    /// <summary>
    /// 重新读一遍磁盘清单（U 盘插拔后由 <c>MainWindow</c> 安排刷新，也可以从「工具 → 重新扫描磁盘」手动触发）。
    /// 工具条左侧的磁盘区（<see cref="Drives" />）与侧边栏的「此电脑」分组都要跟着变，但都只做增量更新。
    /// </summary>
    [RelayCommand]
    public void RefreshDrives()
    {
        ReloadDrives();
        Sidebar.RefreshDrives();
    }

    // ------------------------------------------------------------------ 内部

    private void ApplySettingsToState()
    {
        _isDualPane = _settings.Current.IsDualPane;
        _isToolbarVisible = _settings.Current.ShowToolbar;
        _isSidebarVisible = _settings.Current.IsSidebarVisible;

        OnPropertyChanged(nameof(IsDualPane));
        OnPropertyChanged(nameof(IsToolbarVisible));
        OnPropertyChanged(nameof(IsSidebarVisible));

        ApplySidebarGroups();
    }

    /// <summary>
    /// 把设置里的侧边栏分组显示开关推给 <see cref="Sidebar" />（启动与设置改动时都走这里）。
    /// 侧边栏自己只负责“显示哪几个分组”，不读设置。
    /// </summary>
    private void ApplySidebarGroups()
    {
        Sidebar.ApplyGroupVisibility(
            _settings.Current.SidebarShowHome,
            _settings.Current.SidebarShowFavorites,
            _settings.Current.SidebarShowCloud,
            _settings.Current.SidebarShowComputer);

        // 「主目录」分组里显示哪几个标准文件夹（默认只开桌面与下载）
        Sidebar.ApplyHomeFolders(
            _settings.Current.SidebarHomeDesktop,
            _settings.Current.SidebarHomeDocuments,
            _settings.Current.SidebarHomeDownloads,
            _settings.Current.SidebarHomePictures,
            _settings.Current.SidebarHomeMusic,
            _settings.Current.SidebarHomeVideos);
    }

    private void ReloadDrives()
    {
        var drives = _driveService.GetDrives();

        // 清单没变就什么都不做：一次插拔会连发好几条 WM_DEVICECHANGE，
        // 每次都 Drives.Clear() 会把工具条上的磁盘按钮整体重建一遍（闪一下，还丢键盘焦点）
        if (Drives.Count == drives.Count)
        {
            var same = true;
            for (var i = 0; i < drives.Count; i++)
            {
                if (!SameDrive(Drives[i], drives[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        Drives.Clear();
        foreach (var drive in drives)
        {
            Drives.Add(drive);
        }
    }

    /// <summary>磁盘按钮上看得见的东西都一样就算同一个盘（盘符 / 卷标 / 类型 / 是否就绪）。</summary>
    private static bool SameDrive(DriveModel a, DriveModel b)
        => string.Equals(a.RootPath, b.RootPath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.ToolbarText, b.ToolbarText, StringComparison.Ordinal)
           && a.Kind == b.Kind
           && a.IsReady == b.IsReady;

    private void LoadPinnedFolders()
    {
        PinnedFolders.Clear();

        // 只在“从未配置过”时给默认值：用户把固定目录删光了，下次启动也不该又塞回来
        var configured = _settings.Current.PinnedFoldersInitialized
            ? _settings.Current.PinnedFolders
            : _knownFolders.GetDefaultPinnedFolders().ToList();

        foreach (var path in configured)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                PinnedFolders.Add(new PinnedFolderViewModel(path));
            }
        }

        PersistPinnedFolders();
    }

    /// <summary>
    /// 把固定目录写回设置。
    /// <paramref name="writeToDisk" /> 为 true 时立即落盘（增删固定目录是用户的明确动作，
    /// 不该等到退出时才保存——崩溃或强杀就丢了）。
    /// </summary>
    private void PersistPinnedFolders(bool writeToDisk = false)
    {
        _settings.Current.PinnedFolders = PinnedFolders.Select(p => p.Path).ToList();
        _settings.Current.PinnedFoldersInitialized = true;

        if (writeToDisk)
        {
            _settings.Save();
        }
    }

    private void LoadQuickCommands()
    {
        QuickCommands.Clear();
        foreach (var command in _settings.Current.QuickCommands)
        {
            QuickCommands.Add(command);
        }
    }

    private async Task RestorePaneAsync(
        PanelViewModel pane,
        IReadOnlyList<string> paths,
        int activeIndex,
        string fallback)
    {
        var restored = paths
            .Where(p => _fileSystem.DirectoryExists(p))
            .ToList();

        if (restored.Count == 0)
        {
            restored.Add(fallback);
        }

        foreach (var path in restored)
        {
            var tab = pane.CreateTab();
            await tab.NavigateAsync(path, pushHistory: false).ConfigureAwait(true);
        }

        if (pane.Tabs.Count > 0)
        {
            pane.ActiveTab = pane.Tabs[Math.Clamp(activeIndex, 0, pane.Tabs.Count - 1)];
        }
    }

    private async Task EnsureSecondaryPaneReadyAsync()
    {
        if (SecondaryPane.Tabs.Count > 0)
        {
            return;
        }

        var path = PrimaryPane.ActiveTab?.CurrentPath ?? _knownFolders.UserProfile;
        await SecondaryPane.InitializeAsync(path).ConfigureAwait(true);
    }

    private async Task ApplyViewSettingsToAllTabsAsync()
    {
        foreach (var pane in new[] { PrimaryPane, SecondaryPane })
        {
            foreach (var tab in pane.Tabs.ToList())
            {
                await tab.ApplyViewSettingsAsync().ConfigureAwait(true);
            }
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        _settings.Save();
        _shell.OpenWithDefaultApp(_settings.DataDirectory);
    }

    /// <summary>退出（由宿主窗口提供实际实现）。</summary>
    public event EventHandler? ExitRequested;

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>用户主目录（用于“转到”菜单的快捷项）。</summary>
    public string HomePath => _knownFolders.UserProfile;

    /// <summary>常用位置列表（供菜单/侧边栏构建）。</summary>
    public IReadOnlyList<SpecialFolderModel> SpecialFolders => _knownFolders.GetUserFolders();

    /// <summary>当前设置对象（窗口位置与尺寸由宿主窗口直接读写）。</summary>
    public AppSettings Settings => _settings.Current;

    /// <summary>配置文件完整路径（“关于”对话框展示用）。</summary>
    public string SettingsFilePath => Path.Combine(_settings.DataDirectory, "settings.json");

    /// <summary>侧边栏宽度（DIP）；由窗口拖动分隔条时写入。</summary>
    public double SidebarWidth
    {
        get => _settings.Current.SidebarWidth;
        set => _settings.Current.SidebarWidth = value;
    }

    /// <summary>双窗格时主窗格的像素宽度；为 null 表示平分。</summary>
    public double? PrimaryPaneWidth
    {
        get => _settings.Current.PrimaryPaneWidth;
        set => _settings.Current.PrimaryPaneWidth = value;
    }

    private void OnPaneActivateRequested(object? sender, EventArgs e)
    {
        if (sender is PanelViewModel pane)
        {
            ActivePane = pane;
        }
    }

    private void OnSidebarNavigateRequested(object? sender, string path)
    {
        if (ActivePane.ActiveTab is { } tab)
        {
            _ = tab.NavigateAsync(path);
        }
    }

    /// <summary>侧边栏里把目录拖到「收藏夹」分组上：和拖到工具条固定目录区是同一条链路。</summary>
    private void OnSidebarPinRequested(object? sender, IReadOnlyList<string> paths)
    {
        var added = PinFolders(paths);
        Log.Write($"侧边栏收藏：拖入 {paths.Count} 项，新增 {added} 项");
    }

    private void OnSidebarUnpinRequested(object? sender, string path) => UnpinFolderByPath(path);

    /// <summary>
    /// 一次复制 / 移动 / 删除完成后，把“源所在目录”与“目标目录”那几个标签页重新枚举一遍。
    /// 不按发起操作的标签页刷新，而是按路径找：拖到另一个窗格、粘到另一个标签页都要跟着变；
    /// 删除时如果某个标签页正开在被删掉的目录里，就退到上一级（刷新它只会得到一条错误）。
    /// </summary>
    private void OnFileOperationCompleted(object? sender, FileOperationCompletedEventArgs e)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(e.DestinationDirectory))
        {
            affected.Add(Normalize(e.DestinationDirectory));
        }

        foreach (var source in e.SourcePaths)
        {
            if (_fileSystem.GetParentDirectory(source) is { } parent)
            {
                affected.Add(Normalize(parent));
            }
        }

        foreach (var pane in new[] { PrimaryPane, SecondaryPane })
        {
            foreach (var tab in pane.Tabs.ToList())
            {
                var current = Normalize(tab.CurrentPath);

                if (e.IsDelete && DeletedAncestorOf(e.SourcePaths, current) is { } deleted)
                {
                    if (_fileSystem.GetParentDirectory(deleted) is { } parent)
                    {
                        _ = tab.NavigateAsync(parent);
                    }

                    continue;
                }

                if (affected.Contains(current))
                {
                    _ = tab.RefreshAsync();
                }
            }
        }
    }

    /// <summary>被删掉的那批里，有没有 <paramref name="current" /> 本身或它的祖先（返回那个被删路径）。</summary>
    private static string? DeletedAncestorOf(IReadOnlyList<string> deleted, string current)
    {
        foreach (var path in deleted)
        {
            var root = Normalize(path);
            if (current.Length >= root.Length
                && current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (current.Length == root.Length || current[root.Length] == '\\' || current[root.Length] == '/'))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>比路径是否同一个目录时忽略末尾分隔符与大小写（`C:\` 与 `C:\` 这种根路径也要相等）。</summary>
    private static string Normalize(string path) => path.TrimEnd('\\', '/');

    private void OnPaneNavigated(object? sender, string path)
    {
        if (ReferenceEquals(sender, ActivePane))
        {
            RaiseActivePaneDependent();
        }

        _ = path;
    }

    private void RaiseActivePaneDependent()
    {
        OnPropertyChanged(nameof(ActiveDirectoryName));
        OnPropertyChanged(nameof(ActiveDirectoryPath));
        OnPropertyChanged(nameof(WindowTitleText));
        OnPropertyChanged(nameof(ActivePane));
    }
}
