using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;
using Microsoft.UI.Xaml;

namespace Exdir.ViewModels;

/// <summary>
/// 一个文件标签页：维护当前目录、条目集合、选中项、前进/后退历史与排序状态。
/// </summary>
public sealed partial class FolderTabViewModel : ObservableObject
{
    private static readonly char[] Separators = { '\\', '/' };

    private readonly IFileSystemService _fileSystem;
    private readonly IShellService _shell;
    private readonly ISettingsService _settings;
    private readonly IShellIconService _icons;
    private readonly IShellContextMenuService _contextMenu;

    private readonly List<string> _backStack = new();
    private readonly List<string> _forwardStack = new();
    private CancellationTokenSource? _loadCts;

    private IReadOnlyList<FileSystemEntry> _entries = Array.Empty<FileSystemEntry>();

    /// <summary>当前目录的直接子项（树的根层）。</summary>
    private readonly List<FileItemViewModel> _rootNodes = new();

    /// <summary>处于展开状态的目录路径（刷新/重进目录后据此恢复展开）。</summary>
    private readonly HashSet<string> _expandedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>列表整体重建后需要恢复的选中项路径。</summary>
    private IReadOnlyList<string> _pendingSelection = Array.Empty<string>();

    private string _currentPath = string.Empty;
    private string _currentDirectoryName = string.Empty;
    private string _pathInput = string.Empty;
    private IReadOnlyList<PathSegmentViewModel> _pathSegments = Array.Empty<PathSegmentViewModel>();
    private bool _isPathEditing;
    private bool _isLoading;
    private string? _errorMessage;
    private ObservableCollection<FileItemViewModel> _items = new();
    private IReadOnlyList<FileItemViewModel> _selection = Array.Empty<FileItemViewModel>();
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortAscending = true;
    private bool _foldersFirst;
    private bool _showExtensions = true;
    private bool _enableListAnimations = true;
    private CornerRadius _tabCornerRadius;

    public FolderTabViewModel(
        IFileSystemService fileSystem,
        IShellService shell,
        ISettingsService settings,
        IShellIconService icons,
        IShellContextMenuService contextMenu)
    {
        _fileSystem = fileSystem;
        _shell = shell;
        _settings = settings;
        _icons = icons;
        _contextMenu = contextMenu;

        _foldersFirst = settings.Current.FoldersFirst;
        _showExtensions = settings.Current.ShowExtensions;
        _enableListAnimations = settings.Current.EnableListAnimations;
        _tabCornerRadius = CornerRadiusFor(settings.Current.SquareTabCorners);

        Columns = new ColumnLayout();
        Columns.Apply(
            settings.Current.ColumnWidths,
            settings.Current.ColumnAutoFillName,
            settings.Current.ColumnAutoFit);

        // 行高不是列宽的一部分，单独从设置里取（越界值会被 ColumnLayout 夹回来）
        Columns.RowHeight = settings.Current.RowHeight;
        Columns.RequestedChanged += OnColumnLayoutChanged;
    }

    /// <summary>列宽状态：本标签页的列头与所有行共享同一个实例。</summary>
    public ColumnLayout Columns { get; }

    /// <summary>
    /// 文件列表的右键菜单用哪一种（见 <see cref="AppSettings.UseBuiltInContextMenu" />）：
    /// true = exdir 自己用 WinUI <c>MenuFlyout</c> 现搭的轻量菜单（弹出瞬时），
    /// false = 系统外壳菜单（内容完整但慢）。
    /// 视图在**每次右键时现读**这个值，所以设置里改完立即生效、不需要任何通知。
    /// </summary>
    public bool UseBuiltInContextMenu => _settings.Current.UseBuiltInContextMenu;

    /// <summary>列表整体重建后需要恢复的选中项路径（视图在重建后读取）。</summary>
    public IReadOnlyList<string> PendingSelection => _pendingSelection;

    /// <summary>导航完成后触发（用于同步侧边栏与窗口标题）。</summary>
    public event EventHandler<string>? Navigated;

    // ------------------------------------------------------------------ 状态

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                PathSegments = BuildSegments(value);
                OnPropertyChanged(nameof(CurrentDirectoryName));
                OnPropertyChanged(nameof(TabHeader));
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    /// <summary>地址栏面包屑分段（随 <see cref="CurrentPath"/> 变化整体替换）。</summary>
    public IReadOnlyList<PathSegmentViewModel> PathSegments
    {
        get => _pathSegments;
        private set => SetProperty(ref _pathSegments, value);
    }

    /// <summary>当前目录名（无路径）。</summary>
    public string CurrentDirectoryName
    {
        get
        {
            if (string.IsNullOrEmpty(_currentPath))
            {
                return "此电脑";
            }

            try
            {
                var name = new DirectoryInfo(_currentPath).Name;
                return string.IsNullOrEmpty(name) ? _currentPath : name;
            }
            catch (Exception)
            {
                return _currentPath;
            }
        }
    }

    /// <summary>标签页标题。</summary>
    public string TabHeader => string.IsNullOrEmpty(_currentPath) ? "此电脑" : CurrentDirectoryName;

    public string TooltipText => _currentPath;

    /// <summary>路径栏中可编辑的文本。</summary>
    public string PathInput
    {
        get => _pathInput;
        set => SetProperty(ref _pathInput, value);
    }

    /// <summary>
    /// 地址栏是否处于“可直接输入路径”的编辑态。
    /// 放在 ViewModel 里而不是视图里，是为了让 Ctrl+L 这类外部入口也能把它切过来。
    /// </summary>
    public bool IsPathEditing
    {
        get => _isPathEditing;
        private set => SetProperty(ref _isPathEditing, value);
    }

    /// <summary>切到地址栏编辑态（点击地址栏空白处 / 点击当前目录段 / Ctrl+L）。</summary>
    public void BeginPathEdit()
    {
        // 每次都以当前目录为起点，避免上次没提交的输入残留
        PathInput = _currentPath;
        IsPathEditing = true;
    }

    /// <summary>退出地址栏编辑态并丢弃未提交的输入（Esc / 失焦）。</summary>
    public void CancelPathEdit()
    {
        PathInput = _currentPath;
        IsPathEditing = false;
    }

    public ObservableCollection<FileItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(ItemCount));
            }
        }
    }

    /// <summary>
    /// 当前目录的直接子项数（不含就地展开出来的孙子行）——状态栏的“N 项”。
    /// 在 <see cref="Items"/> 替换时一并通知：那时 <c>_entries</c> 已经是本次枚举的结果。
    /// </summary>
    public int ItemCount => _entries.Count;

    public IReadOnlyList<FileItemViewModel> Selection
    {
        get => _selection;
        private set
        {
            if (SetProperty(ref _selection, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OpenSelectionCommand.NotifyCanExecuteChanged();
                CopySelectionPathCommand.NotifyCanExecuteChanged();
                RevealInExplorerCommand.NotifyCanExecuteChanged();
                ShowPropertiesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => _selection.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public bool IsEmpty => _items.Count == 0;

    /// <summary>文件列表是否播放过渡动画：视图据此决定要不要清空 ListView 的过渡集合。</summary>
    public bool EnableListAnimations
    {
        get => _enableListAnimations;
        private set => SetProperty(ref _enableListAnimations, value);
    }

    /// <summary>
    /// 标签头的圆角：视图把它绑到 <c>TabViewItem.CornerRadius</c> 上。
    /// 为什么不直接在 XAML 里写死 / 用资源：它得能在设置改动后立即生效（每个标签页各自的属性，
    /// 赋同样的值不会重排），而 WinUI 模板里那个 <c>CornerRadius</c> 是 <c>TemplateBinding</c>，会跟着这个属性变。
    /// </summary>
    public CornerRadius TabCornerRadius
    {
        get => _tabCornerRadius;
        private set => SetProperty(ref _tabCornerRadius, value);
    }

    public bool CanGoBack => _backStack.Count > 0;

    public bool CanGoForward => _forwardStack.Count > 0;

    public bool CanGoUp => !string.IsNullOrEmpty(_currentPath) && _fileSystem.GetParentDirectory(_currentPath) is not null;

    // ------------------------------------------------------------------ 排序

    public FileSortColumn SortColumn
    {
        get => _sortColumn;
        private set
        {
            if (SetProperty(ref _sortColumn, value))
            {
                NotifySortGlyphs();
            }
        }
    }

    public bool SortAscending
    {
        get => _sortAscending;
        private set
        {
            if (SetProperty(ref _sortAscending, value))
            {
                NotifySortGlyphs();
            }
        }
    }

    public bool FoldersFirst
    {
        get => _foldersFirst;
        set
        {
            if (SetProperty(ref _foldersFirst, value))
            {
                _settings.Current.FoldersFirst = value;
                ResortItems();
            }
        }
    }

    public string NameSortGlyph => GlyphFor(FileSortColumn.Name);

    public string DateSortGlyph => GlyphFor(FileSortColumn.LastWriteTime);

    public string TypeSortGlyph => GlyphFor(FileSortColumn.Type);

    public string SizeSortGlyph => GlyphFor(FileSortColumn.Size);

    public string SyncStateSortGlyph => GlyphFor(FileSortColumn.SyncState);

    // ------------------------------------------------------------------ 导航

    /// <summary>加载指定目录。</summary>
    /// <param name="path">目标路径。</param>
    /// <param name="pushHistory">是否写入后退历史。</param>
    /// <param name="selectPath">导航完成后要选中的条目路径（用于返回上一级后定位）。</param>
    /// <param name="preserveSelection">是否保留当前选中项（刷新时用）。</param>
    public async Task NavigateAsync(
        string path,
        bool pushHistory = true,
        string? selectPath = null,
        bool preserveSelection = false)
    {
        var normalized = _fileSystem.NormalizeDirectoryPath(path);
        if (normalized is null)
        {
            ErrorMessage = $"无法打开：{path}";
            return;
        }

        // 路径合法才退出地址栏编辑态：输错了要留在框里让用户改
        IsPathEditing = false;

        var previous = _currentPath;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;
        ErrorMessage = null;

        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = await _fileSystem.EnumerateDirectoryAsync(
                normalized,
                _settings.Current.ShowHiddenFiles,
                cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            entries = Array.Empty<FileSystemEntry>();
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (pushHistory && !string.IsNullOrEmpty(previous) &&
            !string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _backStack.Add(previous);
            _forwardStack.Clear();
        }

        _entries = entries;
        CurrentPath = normalized;
        PathInput = normalized;

        // 只有云同步目录才有状态可显示（非云目录里整列隐藏）；离开云目录后“按状态排序”也就没意义了
        Columns.ShowSyncColumn = entries.Any(static entry => entry.SyncState != CloudSyncState.None);
        if (!Columns.ShowSyncColumn && SortColumn == FileSortColumn.SyncState)
        {
            SortColumn = FileSortColumn.Name;
            SortAscending = true;
        }

        // 换了目录：上一个目录的展开状态没有意义
        if (!string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _expandedPaths.Clear();
        }

        _pendingSelection = !string.IsNullOrEmpty(selectPath)
            ? new[] { selectPath }
            : preserveSelection
                ? _selection.Select(i => i.FullPath).ToList()
                : Array.Empty<string>();

        Selection = Array.Empty<FileItemViewModel>();
        BuildRootNodes();
        RebuildFlatList();
        await RestoreExpansionAsync().ConfigureAwait(true);

        NotifyNavigationState();
        Navigated?.Invoke(this, normalized);
    }

    /// <summary>重新枚举当前目录（保留展开状态与选中项）。</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_currentPath))
        {
            return;
        }

        await NavigateAsync(_currentPath, pushHistory: false, preserveSelection: true).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        var target = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);

        if (!string.IsNullOrEmpty(_currentPath))
        {
            _forwardStack.Add(_currentPath);
        }

        await NavigateAsync(target, pushHistory: false).ConfigureAwait(true);
        NotifyNavigationState();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForwardAsync()
    {
        if (_forwardStack.Count == 0)
        {
            return;
        }

        var target = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);

        if (!string.IsNullOrEmpty(_currentPath))
        {
            _backStack.Add(_currentPath);
        }

        await NavigateAsync(target, pushHistory: false).ConfigureAwait(true);
        NotifyNavigationState();
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task GoUpAsync()
    {
        var parent = _fileSystem.GetParentDirectory(_currentPath);
        if (parent is null)
        {
            return;
        }

        await NavigateAsync(parent, pushHistory: true, selectPath: _currentPath).ConfigureAwait(true);
    }

    /// <summary>把路径栏里的文本当作目标路径导航。</summary>
    [RelayCommand]
    private async Task NavigatePathAsync()
    {
        var text = PathInput?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            CancelPathEdit();
            return;
        }

        var normalized = _fileSystem.NormalizeDirectoryPath(text);
        if (normalized is null)
        {
            // 可能是文件路径：直接交给 shell 打开
            if (_fileSystem.FileExists(text))
            {
                _shell.OpenWithDefaultApp(text);
                CancelPathEdit();
                return;
            }

            // 保留用户输入（编辑态也不退出），改错就行
            ErrorMessage = $"路径不存在：{text}";
            return;
        }

        await NavigateAsync(normalized).ConfigureAwait(true);
    }

    /// <summary>点击面包屑分段时导航到该段（<see cref="PathSegments"/>）。</summary>
    [RelayCommand]
    private async Task NavigateToSegmentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await NavigateAsync(path).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ 打开

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelection()
    {
        var item = _selection.FirstOrDefault();
        if (item is null)
        {
            return;
        }

        // 只打开第一项：目录进入，文件交给默认程序
        OpenItem(item);
    }

    // ------------------------------------------------------------------ 图标

    /// <summary>
    /// 行进入可视区时请求它的真实外壳图标（由 <c>DetailsView</c> 的
    /// <c>ContainerContentChanging</c> 调用，所以只为真正显示出来的行付出代价）。
    /// 提取在后台线程，回填到 VM 时已经回到 UI 线程（图像源必须在 UI 线程建）。
    /// </summary>
    public async Task EnsureIconAsync(FileItemViewModel item)
    {
        if (item.IconRequested)
        {
            return;
        }

        // 先置位再 await：容器反复回收重建时同一行不会重复排队
        item.IconRequested = true;

        try
        {
            var bitmap = await _icons.GetIconAsync(item.FullPath, item.IsDirectory).ConfigureAwait(true);
            if (bitmap is not null)
            {
                item.SetIcon(IconImageHelper.ToImageSource(bitmap));
            }
        }
        catch (Exception ex)
        {
            // 图标只是锦上添花：失败了就继续用字形，不能影响列表
            Log.Exception($"设置图标 @ {item.FullPath}", ex);
        }
    }

    /// <summary>双击 / 回车打开某一项。</summary>
    public void OpenItem(FileItemViewModel item)
    {
        if (item.IsDirectory)
        {
            _ = NavigateAsync(item.FullPath);
        }
        else
        {
            _shell.OpenWithDefaultApp(item.FullPath);
        }
    }

    public void OpenSelectionWithDefaultApp()
    {
        foreach (var item in _selection)
        {
            _shell.OpenWithDefaultApp(item.FullPath);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopySelectionPath()
    {
        _shell.CopyTextToClipboard(string.Join(Environment.NewLine, _selection.Select(i => i.FullPath)));
    }

    [RelayCommand]
    private void CopyCurrentPath() => _shell.CopyTextToClipboard(_currentPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RevealInExplorer()
    {
        var item = _selection.FirstOrDefault();
        if (item is not null)
        {
            _shell.RevealInFileExplorer(item.FullPath);
        }
    }

    /// <summary>弹出选中项的“属性”对话框（内置右键菜单用；多选时只取第一项）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowProperties()
    {
        var item = _selection.FirstOrDefault();
        if (item is not null)
        {
            _shell.ShowProperties(item.FullPath);
        }
    }

    /// <summary>
    /// 在当前目录新建一个“新建文件夹”（重名时自动加 (2)(3)…），建完立刻选中它。
    /// 内置右键菜单的空白处菜单用它 —— 系统菜单的新建走外壳，exdir 自己这条不依赖 IContextMenu。
    /// </summary>
    [RelayCommand]
    private async Task CreateNewFolderAsync()
    {
        if (string.IsNullOrEmpty(_currentPath))
        {
            return;
        }

        var directory = _currentPath;
        var candidate = Path.Combine(directory, "新建文件夹");
        var index = 2;

        while (_fileSystem.DirectoryExists(candidate) || _fileSystem.FileExists(candidate))
        {
            candidate = Path.Combine(directory, $"新建文件夹 ({index++})");
        }

        try
        {
            Directory.CreateDirectory(candidate);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法新建文件夹：{ex.Message}";
            Log.Write($"新建文件夹失败：{candidate}（{ex.Message}）");
            return;
        }

        Log.Write($"新建文件夹：{candidate}");

        // 重新枚举一次（而不是往列表里插一行）：新建后目录的排序位置不一定在末尾，
        // 而且选中项要靠 NavigateAsync 的 selectPath 在新集合里找回
        await NavigateAsync(directory, pushHistory: false, selectPath: candidate).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenTerminal() => _shell.OpenTerminal(_currentPath);

    [RelayCommand]
    private void OpenTerminalAsAdmin() => _shell.OpenTerminal(_currentPath, asAdministrator: true);

    /// <summary>
    /// 在指定屏幕位置弹出系统右键菜单（由视图层在 <c>ContextRequested</c> 里调用）。
    /// </summary>
    /// <param name="paths">选中项路径；<paramref name="isBackground" /> 为 true 时只取第一个，代表当前目录的空白处。</param>
    /// <param name="isBackground">true = 文件列表空白处（目录背景菜单）。</param>
    /// <param name="screenX">弹出位置 X（屏幕物理像素，由视图用 <c>DpiHelper.ToScreenPoint</c> 换算）。</param>
    /// <param name="screenY">弹出位置 Y（屏幕物理像素）。</param>
    public void ShowShellContextMenu(IReadOnlyList<string> paths, bool isBackground, int screenX, int screenY)
    {
        try
        {
            _contextMenu.Show(paths, isBackground, screenX, screenY);
        }
        catch (Exception ex)
        {
            // 外壳扩展千奇百怪，弹菜单失败不能把整个应用带走
            Log.Exception("系统右键菜单", ex);
        }
    }

    // ------------------------------------------------------------------ 排序命令

    /// <summary>点击列头：同列则切换升/降序，不同列则切换排序列。</summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column) || !Enum.TryParse<FileSortColumn>(column, ignoreCase: true, out var target))
        {
            return;
        }

        if (SortColumn == target)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = target;
            SortAscending = true;
        }

        ResortItems();
    }

    // ------------------------------------------------------------------ 选中

    /// <summary>由视图层的 SelectionChanged 事件回填选中项。</summary>
    public void SetSelection(IEnumerable<FileItemViewModel> items)
    {
        Selection = items as IReadOnlyList<FileItemViewModel> ?? items.ToList();
    }

    // ------------------------------------------------------------------ 设置联动

    /// <summary>“显示隐藏文件”或“显示扩展名”变化后刷新当前目录。</summary>
    public async Task ApplyViewSettingsAsync()
    {
        _showExtensions = _settings.Current.ShowExtensions;
        ApplyAnimationSettings();
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// “文件列表过渡动画”开关变化：只更新状态，不重新枚举目录
    /// （动画是纯视图行为，刷新一次目录只会白白闪一下）。
    /// </summary>
    public void ApplyAnimationSettings() => EnableListAnimations = _settings.Current.EnableListAnimations;

    /// <summary>“标签页直角”开关变化：只更新圆角，不动目录与选择。</summary>
    public void ApplyTabCornerSettings() => TabCornerRadius = CornerRadiusFor(_settings.Current.SquareTabCorners);

    /// <summary>
    /// 两种标签圆角。圆角值抄的是 WinUI 标签的默认值：<c>OverlayCornerRadius</c>(8) 经
    /// <c>TopCornerRadiusFilterConverter</c> 只保留上面两个角，下面两个角始终是直角
    /// （标签下面就是窗格内容，圆角会把背景露出来）。
    /// </summary>
    private static CornerRadius CornerRadiusFor(bool squareTabs)
        => squareTabs ? new CornerRadius(0) : new CornerRadius(8, 8, 0, 0);

    // ------------------------------------------------------------------ 树形展开

    /// <summary>展开/折叠一行（点击行首箭头时调用）。</summary>
    public async Task ToggleExpandAsync(FileItemViewModel node)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        if (node.IsExpanded)
        {
            Collapse(node);
            return;
        }

        if (!await EnsureChildrenAsync(node).ConfigureAwait(true))
        {
            return;
        }

        node.IsExpanded = true;
        _expandedPaths.Add(node.FullPath);
        InsertChildRows(node);
    }

    /// <summary>折叠一行（左方向键）。</summary>
    public void Collapse(FileItemViewModel node)
    {
        if (!node.IsExpanded)
        {
            return;
        }

        // 先按“仍展开”的口径数出要移除的行数，再改状态
        var start = Items.IndexOf(node);
        var count = CountVisibleDescendants(node);

        node.IsExpanded = false;
        _expandedPaths.Remove(node.FullPath);

        if (start < 0)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            Items.RemoveAt(start + 1);
        }
    }

    /// <summary>
    /// 增量插入子行：整体替换 <see cref="Items"/> 会把滚动位置与选中项一起清掉，
    /// 展开/折叠只影响一行下面的内容，所以这里逐行插入/删除。
    /// </summary>
    private void InsertChildRows(FileItemViewModel node)
    {
        var start = Items.IndexOf(node);
        if (start < 0)
        {
            return;
        }

        var rows = Flatten(node.Children).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            Items.Insert(start + 1 + i, rows[i]);
        }
    }

    private static int CountVisibleDescendants(FileItemViewModel node)
    {
        if (!node.IsExpanded)
        {
            return 0;
        }

        var count = 0;
        foreach (var child in node.Children)
        {
            count += 1 + CountVisibleDescendants(child);
        }

        return count;
    }

    /// <summary>按需加载某个目录行的直接子项；返回是否加载成功（已加载也算成功）。</summary>
    private async Task<bool> EnsureChildrenAsync(FileItemViewModel node)
    {
        if (node.ChildrenLoaded)
        {
            return true;
        }

        var cts = _loadCts;
        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = await _fileSystem.EnumerateDirectoryAsync(
                node.FullPath,
                _settings.Current.ShowHiddenFiles,
                cts?.Token ?? default).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            // 读不到就保持“未加载”，箭头留着让用户重试
            return false;
        }

        if (cts is { IsCancellationRequested: true })
        {
            return false;
        }

        var children = entries
            .Select(entry => new FileItemViewModel(entry, _showExtensions, Columns, node.Depth + 1))
            .ToList();

        children.Sort(CompareNodes);
        node.SetChildren(children);
        return true;
    }

    /// <summary>刷新/重进目录后恢复之前的展开状态（父级路径一定比子级短，按长度升序即可）。</summary>
    private async Task RestoreExpansionAsync()
    {
        if (_expandedPaths.Count == 0)
        {
            return;
        }

        var cts = _loadCts;
        var stale = new List<string>();

        foreach (var path in _expandedPaths.OrderBy(static p => p.Length).ToList())
        {
            var node = FindNode(_rootNodes, path);
            if (node is null || !node.IsDirectory)
            {
                stale.Add(path);
                continue;
            }

            if (!await EnsureChildrenAsync(node).ConfigureAwait(true))
            {
                return;
            }

            if (cts is { IsCancellationRequested: true })
            {
                return;
            }

            node.IsExpanded = true;
        }

        foreach (var path in stale)
        {
            _expandedPaths.Remove(path);
        }

        RebuildFlatList();
    }

    private static FileItemViewModel? FindNode(IEnumerable<FileItemViewModel> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory)
            {
                continue;
            }

            if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.ChildrenLoaded)
            {
                var found = FindNode(node.Children, path);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ 面包屑

    /// <summary>把路径切成面包屑分段；最后一段即当前目录。</summary>
    private static IReadOnlyList<PathSegmentViewModel> BuildSegments(string path)
    {
        var raw = SplitPath(path);
        var segments = new List<PathSegmentViewModel>(raw.Count);

        for (var i = 0; i < raw.Count; i++)
        {
            segments.Add(new PathSegmentViewModel(
                raw[i].Display,
                raw[i].FullPath,
                isFirst: i == 0,
                isCurrent: i == raw.Count - 1));
        }

        return segments;
    }

    private static List<(string Display, string FullPath)> SplitPath(string path)
    {
        var raw = new List<(string Display, string FullPath)>();

        if (string.IsNullOrEmpty(path))
        {
            // “此电脑”没有对应路径，点它只能进编辑态
            raw.Add(("此电脑", string.Empty));
            return raw;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC：\\server\share 当根，再往下的每一级各自成段
            var parts = path[2..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return raw;
            }

            var root = parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : $@"\\{parts[0]}";
            raw.Add((root, root));

            for (var i = 2; i < parts.Length; i++)
            {
                root = root + "\\" + parts[i];
                raw.Add((parts[i], root));
            }

            return raw;
        }

        var volume = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(volume))
        {
            return raw;
        }

        // "C:\" 去掉分隔符剩 "C:"，不能再往下剥（"C:" 会被当成“C 盘的当前目录”，见 AGENTS 坑 14）
        var rootDisplay = volume.TrimEnd(Separators);
        raw.Add((rootDisplay.Length == 0 ? volume : rootDisplay, volume));

        var current = volume.TrimEnd(Separators);
        foreach (var name in path[volume.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = current + Path.DirectorySeparatorChar + name;
            raw.Add((name, current));
        }

        return raw;
    }

    // ------------------------------------------------------------------ 内部

    private void BuildRootNodes()
    {
        _rootNodes.Clear();

        foreach (var entry in _entries)
        {
            _rootNodes.Add(new FileItemViewModel(entry, _showExtensions, Columns));
        }

        _rootNodes.Sort(CompareNodes);
    }

    /// <summary>把整棵树摊平成可见行（整体替换，用于导航/排序/恢复展开）。</summary>
    private void RebuildFlatList()
        => Items = new ObservableCollection<FileItemViewModel>(Flatten(_rootNodes).ToList());

    private static IEnumerable<FileItemViewModel> Flatten(IEnumerable<FileItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            if (node.IsExpanded)
            {
                foreach (var descendant in Flatten(node.Children))
                {
                    yield return descendant;
                }
            }
        }
    }

    /// <summary>排序：树的每一层都按同一列排，整体替换列表（选中项由视图按路径恢复）。</summary>
    private void ResortItems()
    {
        if (_selection.Count > 0)
        {
            _pendingSelection = _selection.Select(i => i.FullPath).ToList();
        }

        _rootNodes.Sort(CompareNodes);
        SortLoadedChildren(_rootNodes);
        RebuildFlatList();
    }

    private void SortLoadedChildren(IEnumerable<FileItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.ChildrenLoaded)
            {
                continue;
            }

            node.SortChildren(CompareNodes);
            SortLoadedChildren(node.Children);
        }
    }

    private int CompareNodes(FileItemViewModel a, FileItemViewModel b)
    {
        if (_foldersFirst && a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        var result = _sortColumn switch
        {
            FileSortColumn.LastWriteTime => a.LastWriteTime.CompareTo(b.LastWriteTime),
            FileSortColumn.Type => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            FileSortColumn.Size => a.Size.CompareTo(b.Size),
            FileSortColumn.SyncState => a.SyncState.CompareTo(b.SyncState),
            _ => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
        };

        if (!_sortAscending)
        {
            result = -result;
        }

        return result != 0
            ? result
            : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>列宽变化后写入设置（不立即落盘，退出时统一保存）。</summary>
    private void OnColumnLayoutChanged(object? sender, EventArgs e)
    {
        _settings.Current.ColumnWidths = Columns.ToArray().ToList();
        _settings.Current.ColumnAutoFillName = Columns.AutoFillName;
        _settings.Current.ColumnAutoFit = Columns.AutoFit;
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void NotifySortGlyphs()
    {
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(DateSortGlyph));
        OnPropertyChanged(nameof(TypeSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(SyncStateSortGlyph));
    }

    private string GlyphFor(FileSortColumn column)
        => SortColumn == column ? (SortAscending ? "\uE70E" : "\uE70D") : string.Empty;
}
