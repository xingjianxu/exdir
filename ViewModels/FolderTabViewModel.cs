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
    private readonly IClipboardService _clipboard;
    private readonly IFileOperationService _fileOperations;
    private readonly IArchiveService _archive;
    private readonly IArchiveClipboardService _archiveClipboard;
    private readonly ICompressionService _compression;
    private readonly IRemoteFileService _remote;
    private readonly IDialogService _dialogs;
    private readonly IKnownFolderService _knownFolders;
    private readonly IEverythingSearchService _search;
    private readonly IRecentItemsService _recents;

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

    /// <summary>
    /// 「最新访问」视图的默认顺序就是**访问时间倒序**（服务给什么顺序就是什么顺序，不排），
    /// 为 true 时 <see cref="BuildRootNodes" /> 不排序。用户点过列头（或拨了“文件夹排在文件前面”
    /// 这类排序设置）之后它就变成 false，从此按选中的列排 —— 与普通目录一致。
    /// </summary>
    private bool _sortByAccessOrder;
    private bool _foldersFirst;
    private bool _showExtensions = true;
    private bool _enableListAnimations = true;
    private CornerRadius _tabCornerRadius;
    private string? _busyMessage;
    private string? _statusMessage;
    private string? _statusTitle;
    private string? _statusTargetPath;
    private CancellationTokenSource? _extractCts;
    private CancellationTokenSource? _compressCts;

    // ---- 「基于 Everything 的快速搜索」（见 AGENTS.md 第 4 节）

    /// <summary>搜索框里的文本（每个标签页各自一份，跟着标签页走）。</summary>
    private string _searchQuery = string.Empty;

    /// <summary>列表里现在装的是搜索结果（而不是当前目录的枚举结果）。</summary>
    private bool _isSearchMode;

    /// <summary>搜索范围是不是“整机”（false = 当前目录及其子目录，默认）。</summary>
    private bool _searchAllDrives;

    /// <summary>搜索框旁边的计数文本（“12 项” / “前 5000 项 / 共 81234 项”）。</summary>
    private string? _searchStatusText;

    /// <summary>搜索的去抖 / 取消令牌：每敲一个字就换一个新的，旧的那次结果直接丢弃。</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>
    /// 当前目录**自己枚举出来的**条目数（不含搜索结果、不含就地展开的行）。
    /// 一次搜索一个都没命中时用它区分两种情况：目录本来就是空的（正常）
    /// 与目录里有东西却没搜到（多半是 Everything 的索引没覆盖这个目录）。每次导航 / 刷新都重算。
    /// </summary>
    private int _directoryEntryCount;

    public FolderTabViewModel(
        IFileSystemService fileSystem,
        IShellService shell,
        ISettingsService settings,
        IShellIconService icons,
        IShellContextMenuService contextMenu,
        IClipboardService clipboard,
        IFileOperationService fileOperations,
        IArchiveService archive,
        IArchiveClipboardService archiveClipboard,
        ICompressionService compression,
        IRemoteFileService remote,
        IKnownFolderService knownFolders,
        IDialogService dialogs,
        IEverythingSearchService search,
        IRecentItemsService recents)
    {
        _fileSystem = fileSystem;
        _shell = shell;
        _settings = settings;
        _icons = icons;
        _contextMenu = contextMenu;
        _clipboard = clipboard;
        _fileOperations = fileOperations;
        _archive = archive;
        _archiveClipboard = archiveClipboard;
        _compression = compression;
        _remote = remote;
        _knownFolders = knownFolders;
        _dialogs = dialogs;
        _search = search;
        _recents = recents;

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
                OnPropertyChanged(nameof(IsInsideArchive));
                OnPropertyChanged(nameof(ArchiveFile));
                OnPropertyChanged(nameof(IsRemote));
                OnPropertyChanged(nameof(IsRecentView));
                OnPropertyChanged(nameof(CanSearch));
                OnPropertyChanged(nameof(SearchHint));
            }
        }
    }

    /// <summary>
    /// 当前目录是不是压缩包（压缩包根或包内目录）：是的话文件列表进入**只读**模式 ——
    /// 粘贴 / 删除 / 新建 / 剪切 / 拖放 / 终端 / 属性 / “在资源管理器中显示”全部禁用，
    /// 包内文件双击改成“解到临时目录再用默认程序打开”（见 AGENTS.md 第 4 节）。
    /// 唯一的例外是「复制」：包内条目可以复制到真实目录里粘贴（见 <see cref="CopyArchiveSelection" />）。
    /// 注意：在真实目录里**就地展开**出来的那几行（压缩包文件行展开后）不算这个标记，
    /// 它们由 <see cref="FileItemViewModel.IsInArchive" /> 单独标记，守卫见 <see cref="RefuseSelectionInArchive" />。
    /// </summary>
    public bool IsInsideArchive => _fileSystem.IsInsideArchive(_currentPath);

    /// <summary>当前所在压缩包的文件路径（不在压缩包里时为 null）。</summary>
    public string? ArchiveFile
        => _fileSystem.TryParseArchivePath(_currentPath, out var location) ? location.ArchiveFile : null;

    /// <summary>
    /// 当前目录在远程位置（SFTP / FTP）上：整个标签页只读 —— 粘贴 / 删除 / 剪切 / 新建文件夹 /
    /// 拖入 / 终端 / 属性 / 在资源管理器中显示全部拒绝，双击文件改成“先下到本地再用默认程序打开”。
    /// 例外是「复制」与“拖出去”：它们把远程条目下到本地中转目录再交给系统（见 AGENTS.md“远程位置”）。
    /// </summary>
    public bool IsRemote => _fileSystem.IsRemotePath(_currentPath);

    /// <summary>
    /// 当前标签页是「最新访问」虚拟视图（<c>exdir://recent</c>）：列表里是最近访问过的目录与文件，
    /// 按访问时间倒序，双击进入 / 打开它们。它没有上一级、不能搜索，也不能往里写东西
    /// （当前“目录”不是真目录）。
    /// </summary>
    public bool IsRecentView => _fileSystem.IsRecentViewPath(_currentPath);

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

            // 远程路径不能交给 DirectoryInfo 解：里面的 ':' 会被当成非法字符
            //（根目录用登录身份当名字：sftp://user@host/ → user@host）
            if (RemotePath.TryParse(_currentPath, out var remote))
            {
                return remote.Path == "/" ? RemotePath.RootDisplayOf(remote) : RemotePath.NameOf(remote);
            }

            if (IsRecentView)
            {
                return RecentView.DisplayName;
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

    public string TooltipText => IsRecentView ? RecentView.DisplayName : _currentPath;

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
                CopySelectionCommand.NotifyCanExecuteChanged();
                CutSelectionCommand.NotifyCanExecuteChanged();
                DeleteSelectionCommand.NotifyCanExecuteChanged();
                DeleteSelectionPermanentlyCommand.NotifyCanExecuteChanged();
                OpenWithSevenZipCommand.NotifyCanExecuteChanged();
                ExtractToDownloadsCommand.NotifyCanExecuteChanged();
                CompressSelectionCommand.NotifyCanExecuteChanged();
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
                OnPropertyChanged(nameof(ErrorTitle));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <summary>
    /// 用户关掉错误 / 提示条时清掉文案：<c>InfoBar.IsOpen</c> 是 OneWay 绑到 <see cref="HasError" /> 的，
    /// 控件自己关掉只会改本地值，不清 VM 里这份的话下次换了内容也弹不出来（见 AGENTS.md 第 90 条）。
    /// </summary>
    public void ClearError() => ErrorMessage = null;

    /// <summary>
    /// 一次操作成功后的提示（目前只有「解压到下载文件夹」）：文件列表顶部的绿色 InfoBar 显示它，
    /// 旁边带一个「打开目录」按钮（<see cref="OpenStatusTarget" />）。导航会把它清掉。
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusMessage);

    /// <summary>
    /// 一件“看得见会慢一点”的事正在做时的提示（目前只有“拖拽之前先把包内条目解到临时目录”，
    /// 见 <see cref="BuildDragPayloadAsync" />）：文件列表顶部一条不可关闭、没有按钮的 InfoBar。
    /// </summary>
    public string? BusyMessage
    {
        get => _busyMessage;
        private set
        {
            if (SetProperty(ref _busyMessage, value))
            {
                OnPropertyChanged(nameof(HasBusy));
            }
        }
    }

    public bool HasBusy => !string.IsNullOrEmpty(_busyMessage);

    /// <summary>
    /// 提示条的标题（「解压完成」/「压缩完成」）：由 <see cref="SetStatus" /> 按具体操作设置。
    /// 标题是视图里 <c>InfoBar.Title</c> 的绑定源，所以放 VM 上而不是在 XAML 里写死。
    /// </summary>
    public string? StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    /// <summary>InfoBar 上「打开目录」要打开的目录（这次解压到的地方 / 压缩包所在目录）。</summary>
    public void OpenStatusTarget()
    {
        if (!string.IsNullOrEmpty(_statusTargetPath))
        {
            _shell.RevealInFileExplorer(_statusTargetPath);
        }
    }

    /// <summary>用户关掉提示条 / 导航到别处时清掉它（下一次 SetStatus 才能重新弹出来）。</summary>
    public void ClearStatus()
    {
        StatusMessage = null;
        StatusTitle = null;
        _statusTargetPath = null;
    }

    private void SetStatus(string title, string message, string targetPath)
    {
        ErrorMessage = null;
        _statusTargetPath = targetPath;
        StatusTitle = title;
        StatusMessage = message;
    }

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

    // ------------------------------------------------------------------ Everything 快速搜索

    /// <summary>
    /// 搜索去的去抖时长（毫秒）。Everything 本身几毫秒就答，等太久反而拖手感；
    /// 一点去抖只是为了不在快速输入时白发一堆查询。
    /// </summary>
    private const int SearchDebounceMs = 180;

    /// <summary>
    /// 当前标签页能不能搜索：得站在一个**真实目录**里。
    /// 压缩包内 / 远程位置的东西不在 Everything 的索引里，搜了只会得到空结果；
    /// “此电脑”这种没有路径的标签页也没有可限定的范围。
    /// </summary>
    public bool CanSearch => !IsInsideArchive && !IsRemote && !IsRecentView && !string.IsNullOrEmpty(_currentPath);

    /// <summary>搜索框的悬停提示（不可用时写明原因，不至于让人以为功能没做）。</summary>
    public string SearchHint
    {
        get
        {
            if (IsInsideArchive)
            {
                return "压缩包内不支持搜索";
            }

            if (IsRemote)
            {
                return "远程位置不支持搜索";
            }

            if (IsRecentView)
            {
                return "「最新访问」列表不支持搜索";
            }

            if (string.IsNullOrEmpty(_currentPath))
            {
                return "先打开一个目录再搜索";
            }

            return _search.IsAvailable ? "搜索（Everything）" : "未检测到 Everything";
        }
    }

    /// <summary>搜索框里的文本：输入去抖后自动搜，回车立即搜，清空就退出搜索模式。</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(ErrorTitle));
                RestartSearchDebounce();
            }
        }
    }

    /// <summary>列表里现在装的是搜索结果（而不是当前目录的枚举结果）。</summary>
    public bool IsSearchMode
    {
        get => _isSearchMode;
        private set
        {
            if (SetProperty(ref _isSearchMode, value))
            {
                OnPropertyChanged(nameof(EmptyHint));
                OnPropertyChanged(nameof(ErrorTitle));
            }
        }
    }

    /// <summary>搜索范围：false = 当前目录（含子目录，默认），true = 整机（整个 Everything 索引）。</summary>
    public bool SearchAllDrives
    {
        get => _searchAllDrives;
        set
        {
            if (SetProperty(ref _searchAllDrives, value) && _isSearchMode)
            {
                SearchNow();
            }
        }
    }

    /// <summary>搜索框旁边的计数文本；空串 = 不显示。</summary>
    public string? SearchStatusText
    {
        get => _searchStatusText;
        private set
        {
            if (SetProperty(ref _searchStatusText, value))
            {
                OnPropertyChanged(nameof(HasSearchStatus));
            }
        }
    }

    public bool HasSearchStatus => !string.IsNullOrEmpty(_searchStatusText);

    /// <summary>
    /// 列表为空时显示的那句话：搜索模式下是“没有匹配项”——
    /// 不然说“此文件夹为空”会让人以为目录真的是空的。
    /// </summary>
    public string EmptyHint => _isSearchMode
        ? "没有匹配项"
        : IsRecentView
            ? "还没有最近访问的记录"
            : "此文件夹为空";

    /// <summary>顶部提示条的标题：搜索框里有字时说“搜索”，导航失败才是“无法打开”。</summary>
    public string ErrorTitle => _searchQuery.Length > 0 ? "搜索" : "无法打开";

    /// <summary>搜索框请求聚焦（Ctrl+F）：视图订阅它去 Focus 那个输入框。</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>Ctrl+F：把焦点交给本标签页的搜索框。</summary>
    public void RequestSearchFocus()
    {
        if (CanSearch)
        {
            SearchFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>回车 / 切换搜索范围：不等去抖，立刻搜一次。</summary>
    [RelayCommand]
    public void SearchNow()
    {
        if (!CanSearch || string.IsNullOrWhiteSpace(_searchQuery))
        {
            return;
        }

        _ = RunSearchAsync(ReplaceSearchCts());
    }

    /// <summary>清空搜索框并回到原目录（Esc / 搜索框上那个 ×）。</summary>
    [RelayCommand]
    public Task ClearSearchAsync() => ExitSearchAsync();

    /// <summary>退出搜索模式：清掉搜索状态 + 重新枚举当前目录（保留就地展开状态）。</summary>
    public async Task ExitSearchAsync()
    {
        var wasSearching = _isSearchMode;
        ResetSearchState();

        if (wasSearching)
        {
            await NavigateAsync(_currentPath, pushHistory: false).ConfigureAwait(true);
        }
    }

    /// <summary>输入变化后的去抖；输入框被清空 = 直接退出搜索模式。</summary>
    private void RestartSearchDebounce()
    {
        var cts = ReplaceSearchCts();

        if (!CanSearch)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            if (_isSearchMode)
            {
                // 这里不能同步 await（在属性 setter 里），丢到后台跑；
                // ExitSearchAsync → ResetSearchState 会再把 Cts 取消掉
                _ = ExitSearchAsync();
            }

            return;
        }

        _ = DebouncedSearchAsync(cts);
    }

    private async Task DebouncedSearchAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;

        try
        {
            await Task.Delay(SearchDebounceMs, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            await RunSearchAsync(cts).ConfigureAwait(true);
        }
    }

    private void CancelSearch()
    {
        var previous = _searchCts;
        _searchCts = null;

        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
            previous.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 上一轮已经收尾过了
        }
    }

    private CancellationTokenSource ReplaceSearchCts()
    {
        CancelSearch();
        return _searchCts = new CancellationTokenSource();
    }

    private async Task RunSearchAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;

        if (!CanSearch || string.IsNullOrWhiteSpace(_searchQuery))
        {
            return;
        }

        // 范围：当前目录（含子目录）或整机；限定词由 EverythingQuery 拼
        var directory = _searchAllDrives ? null : _currentPath;

        IsLoading = true;
        try
        {
            var result = await _search
                .SearchAsync(
                    _searchQuery.Trim(),
                    directory,
                    _settings.Current.ShowHiddenFiles,
                    EverythingQuery.MaxResults,
                    token)
                .ConfigureAwait(true);

            if (!token.IsCancellationRequested)
            {
                ApplySearchResult(result, directory);

                // 一个都没搜到、而目录里其实有东西：多半是 Everything 的索引没覆盖这个目录
                // （没装/没启用 Everything 服务时，它只索引手动加进去的那几个文件夹）。
                // 那时“没有匹配项”会让人以为搜索坏了，所以把原因与怎么办说清楚。
                if (result.Status == EverythingSearchStatus.NoResults && directory is not null)
                {
                    await WarnIfDirectoryNotIndexedAsync(directory, token).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户又敲了一个字：这一轮算了
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                IsSearchMode = true;
                ErrorMessage = ex.Message;
                Log.Exception($"Everything 搜索（{_searchQuery}）", ex);
            }
        }
        finally
        {
            // 只有“最新那一轮”才能收掉转圈：旧的一轮收尾时新的可能正在跑
            if (ReferenceEquals(_searchCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    private void ApplySearchResult(EverythingSearchResult result, string? directory)
    {
        if (result.Status is EverythingSearchStatus.NotAvailable
            or EverythingSearchStatus.NotRunning
            or EverythingSearchStatus.Failed)
        {
            // 查不了就**不动列表**：把当前目录（或上一次的结果）留在眼前，只把原因提示出来。
            // 清空列表 + 弹错误会让人以为“这个目录空了”。
            ErrorMessage = result.Status switch
            {
                EverythingSearchStatus.NotAvailable =>
                    "未检测到 Everything：装一份 Everything 并把 Everything64.dll 放到 exdir 旁边即可（见 native/README.md）。",
                EverythingSearchStatus.NotRunning =>
                    result.Message ?? "Everything 没有在运行：先启动 Everything，再回来搜索。",
                _ => result.Message ?? "Everything 查询失败。",
            };

            return;
        }

        IsSearchMode = true;
        ErrorMessage = null;
        ClearStatus();

        _entries = result.Entries;
        _expandedPaths.Clear();
        _rootNodes.Clear();

        foreach (var entry in _entries)
        {
            _rootNodes.Add(new FileItemViewModel(
                entry,
                _showExtensions,
                Columns,
                depth: 0,
                searchPath: SearchPathFor(entry.FullPath, directory)));
        }

        _rootNodes.Sort(CompareNodes);

        // 搜索结果不是云同步目录：别留着上一个目录的状态列
        Columns.ShowSyncColumn = false;

        _pendingSelection = Array.Empty<string>();
        Selection = Array.Empty<FileItemViewModel>();
        RebuildFlatList();

        SearchStatusText = result.IsTruncated
            ? $"前 {_entries.Count} 项 / 共 {result.TotalCount} 项"
            : $"{_entries.Count} 项";

        NotifyNavigationState();
    }

    /// <summary>
    /// 结果行右侧那条目录：当前目录范围给“相对搜索根的目录”，整机范围给完整目录，
    /// 直接位于搜索根里的项返回 null（不显示）。
    /// </summary>
    private static string? SearchPathFor(string fullPath, string? root)
    {
        string? parent;
        try
        {
            parent = Path.GetDirectoryName(fullPath);
        }
        catch (Exception)
        {
            return null;
        }

        if (string.IsNullOrEmpty(parent))
        {
            return null;
        }

        // 整机范围没有共同根：直接显示完整目录
        if (string.IsNullOrEmpty(root))
        {
            return parent;
        }

        if (parent.Length <= root.Length || !parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = parent[root.Length..].TrimStart('\\', '/');
        return relative.Length == 0 ? null : relative;
    }

    /// <summary>
    /// Everything 的索引没覆盖当前目录时给出可操作的原因。
    ///
    /// 三个条件同时成立才提示：一个都没搜到、这个目录在磁盘上确实有东西、Everything 里这个目录下一条都没有。
    /// 目录本来就是空的、或者只是关键字没匹配上，都不该弹这句话。
    /// </summary>
    private async Task WarnIfDirectoryNotIndexedAsync(string directory, CancellationToken token)
    {
        if (_directoryEntryCount <= 0)
        {
            return;
        }

        if (await _search.IsDirectoryIndexedAsync(directory, token).ConfigureAwait(true))
        {
            return;
        }

        if (token.IsCancellationRequested || !IsSearchMode)
        {
            return;
        }

        ErrorMessage = $"Everything 的索引里没有「{directory}」，所以这里搜不到文件（「整机」范围也只能搜到索引里的内容）。"
            + "→ 让 Everything 索引整块磁盘：以管理员身份运行一次 Everything.exe -install-service"
            + "（或在 Everything 的「工具 → 选项 → 常规」里安装服务），"
            + "也可以把这个目录加进「工具 → 选项 → 索引 → 文件夹」。";
        Log.Write($"Everything 的索引没有覆盖当前目录：{directory}（当前目录 {_directoryEntryCount} 项）");
    }

    /// <summary>清掉搜索相关的一切状态（导航到别处 / 退出搜索模式时调）。</summary>
    private void ResetSearchState()
    {
        CancelSearch();
        SearchStatusText = null;

        if (_isSearchMode)
        {
            _isSearchMode = false;
            OnPropertyChanged(nameof(IsSearchMode));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(ErrorTitle));
        }

        // 有意不走 setter：setter 会触发去抖，反过来又调 ExitSearchAsync（回环）
        if (_searchQuery.Length > 0)
        {
            _searchQuery = string.Empty;
            OnPropertyChanged(nameof(SearchQuery));
        }
    }

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
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        // 换目录 = 退出搜索模式（搜索框、计数、结果行右侧的相对目录都跟着清）
        ResetSearchState();

        IsLoading = true;
        ErrorMessage = null;
        ClearStatus();

        string? normalized = null;
        IReadOnlyList<FileSystemEntry> entries = Array.Empty<FileSystemEntry>();

        try
        {
            // 密码是“边解析边可能才发现要”：包内目录的存在性判定本身就要打开压缩包，
            // 所以解析与枚举包在同一个重试循环里。
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    normalized = await _fileSystem.ResolveDirectoryAsync(path, cts.Token).ConfigureAwait(true);
                    if (normalized is null)
                    {
                        ErrorMessage = $"无法打开：{path}";
                        return;
                    }

                    // 路径合法才退出地址栏编辑态：输错了要留在框里让用户改
                    IsPathEditing = false;

                    entries = await _fileSystem.EnumerateDirectoryAsync(
                        normalized,
                        _settings.Current.ShowHiddenFiles,
                        cts.Token).ConfigureAwait(true);

                    break;
                }
                catch (ArchivePasswordRequiredException ex)
                {
                    if (!await TryAskPasswordAsync(ex, attempt).ConfigureAwait(true))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // 压缩包打不开（格式不支持 / 文件损坏）之类：留在当前目录，把错误显示出来
            ErrorMessage = ex.Message;
            return;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }

        if (normalized is null || cts.IsCancellationRequested)
        {
            return;
        }

        var previous = _currentPath;

        if (pushHistory && !string.IsNullOrEmpty(previous) &&
            !string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _backStack.Add(previous);
            _forwardStack.Clear();
        }

        _entries = entries;
        _directoryEntryCount = entries.Count;
        CurrentPath = normalized;
        PathInput = normalized;

        // 只有云同步目录才有状态可显示（非云目录里整列隐藏）；离开云目录后“按状态排序”也就没意义了
        Columns.ShowSyncColumn = entries.Any(static entry => entry.SyncState != CloudSyncState.None);
        if (!Columns.ShowSyncColumn && SortColumn == FileSortColumn.SyncState)
        {
            SortColumn = FileSortColumn.Name;
            SortAscending = true;
        }

        // 换了目录：上一个目录的展开状态没有意义；「最新访问」视图同时恢复“按访问时间倒序”的默认顺序
        if (!string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _expandedPaths.Clear();
            _sortByAccessOrder = _fileSystem.IsRecentViewPath(normalized);
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

        // 搜索结果里的 F5 = 拿当前关键字重搜一次（重新枚举原目录没有意义）
        if (_isSearchMode)
        {
            SearchNow();
            return;
        }

        // 压缩包：先丢掉索引缓存 —— F5 的语义就是“外部改动也刷新到”
        if (ArchiveFile is { } archiveFile)
        {
            _archive.Invalidate(archiveFile);
        }

        // 在当前目录里就地展开的压缩包同样要丢缓存（那种情况下 ArchiveFile 为 null）
        foreach (var path in _expandedPaths.ToList())
        {
            if (_archive.IsArchiveFile(path))
            {
                _archive.Invalidate(path);
            }
        }

        await NavigateAsync(_currentPath, pushHistory: false, preserveSelection: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 「最新访问」列表内容变了（又访问了一条 / 被清空）之后重读一遍。
    /// 只换行集合：不动后退历史、不动当前路径、也不记这一次变化（那是 <c>NavigateAsync</c> 的事），
    /// 所以不会自激。非「最新访问」的标签页直接返回。
    /// </summary>
    public async Task ReloadRecentViewAsync()
    {
        if (!IsRecentView)
        {
            return;
        }

        try
        {
            _entries = await _fileSystem
                .EnumerateDirectoryAsync(_currentPath, _settings.Current.ShowHiddenFiles)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception("刷新「最新访问」列表", ex);
            return;
        }

        _directoryEntryCount = _entries.Count;
        BuildRootNodes();
        RebuildFlatList();
    }

    // ------------------------------------------------------------------ 压缩包（只读）

    /// <summary>最多让用户输几次压缩包密码（输完还是不对就显示「需要密码」）。</summary>
    private const int MaxPasswordAttempts = 3;

    /// <summary>压缩包里只读：写操作的统一拒绝文案。</summary>
    public const string ArchiveReadOnlyMessage = "压缩包内不支持该操作（只读浏览）";

    /// <summary>远程位置（SFTP / FTP）只读：写操作的统一拒绝文案。</summary>
    public const string RemoteReadOnlyMessage = "远程位置不支持该操作（只能浏览与下载）";

    /// <summary>「最新访问」列表（虚拟视图）里不能往“当前目录”写东西时的统一拒绝文案。</summary>
    public const string RecentViewReadOnlyMessage = "「最新访问」列表不支持该操作（它不是一个真实目录）";

    /// <summary>
    /// 当前标签页是「最新访问」虚拟视图：是的话显示提示并返回 true。
    /// 只管**针对当前目录**的写操作（粘贴 / 新建文件夹 / 终端 / 拖入）—— 那些会把
    /// <c>exdir://recent</c> 当成目标目录去规整，变成“目标目录不存在”这种看不懂的错。
    /// 作用于**选中项**的操作不受影响：列表里的每一行都是真实文件，可以打开 / 复制 / 删除。
    /// </summary>
    private bool RefuseInRecentView()
    {
        if (!IsRecentView)
        {
            return false;
        }

        Log.Write("「最新访问」列表：当前不是真实目录，拒绝以它为目标的写操作");
        ErrorMessage = RecentViewReadOnlyMessage;
        return true;
    }

    /// <summary>当前目录是远程位置（SFTP / FTP）；是的话显示只读提示并返回 true。</summary>
    private bool RefuseInRemote()
    {
        if (!IsRemote)
        {
            return false;
        }

        Log.Write("远程位置只读：当前目录在远程位置上，拒绝写操作");
        ErrorMessage = RemoteReadOnlyMessage;
        return true;
    }

    /// <summary>
    /// 弹密码框并记下来；返回 false 表示不该重试（已经给出错误文案）。
    /// </summary>
    private async Task<bool> TryAskPasswordAsync(ArchivePasswordRequiredException ex, int attempt)
    {
        var name = Path.GetFileName(ex.ArchiveFile);

        if (attempt >= MaxPasswordAttempts)
        {
            ErrorMessage = $"需要密码：{name}";
            return false;
        }

        // 之前存的密码不对（或被用户在对话框里取消了）就先清掉，免得一直拿着错的
        _archive.SetPassword(ex.ArchiveFile, null);

        var password = await _dialogs.RequestPasswordAsync(name).ConfigureAwait(true);
        if (string.IsNullOrEmpty(password))
        {
            ErrorMessage = $"需要密码：{name}";
            return false;
        }

        _archive.SetPassword(ex.ArchiveFile, password);
        return true;
    }

    /// <summary>当前目录是不是在压缩包里（压缩包根或包内目录）；是的话显示只读提示并返回 true。</summary>
    private bool RefuseInArchive()
    {
        if (!IsInsideArchive)
        {
            return false;
        }

        Log.Write("压缩包只读：当前目录在压缩包内，拒绝写操作");
        ErrorMessage = ArchiveReadOnlyMessage;
        return true;
    }

    /// <summary>
    /// 选中项里有没有压缩包内部的条目（虚拟路径）。
    /// 在真实目录里就地展开压缩包时，标签页本身不在包内（<see cref="IsInsideArchive" /> 为 false），
    /// 但展开出来的那几行是虚拟的 —— 写操作不能只看当前目录。
    /// </summary>
    private bool SelectionContainsArchiveEntries()
        => _selection.Any(static item => item.IsInArchive);

    /// <summary>作用于“选中项”的写操作守卫（剪切 / 删除 / 属性 / 在资源管理器中显示）。</summary>
    private bool RefuseSelectionInArchive()
    {
        if (!SelectionContainsArchiveEntries())
        {
            return false;
        }

        Log.Write("压缩包只读：选中项里有包内条目，拒绝写操作");
        ErrorMessage = ArchiveReadOnlyMessage;
        return true;
    }

    // ------------------------------------------------------------------ 压缩包右键：用 7-Zip 打开 / 解压到「下载」

    /// <summary>
    /// 选中的是不是**真实**压缩包文件（包内条目、目录都不算）：只有它们才能交给外部 7-Zip，也只有它们能解压。
    /// 内置右键菜单据此决定要不要加这两个入口。
    /// </summary>
    public bool CanUseArchiveCommands
        => _selection.Count > 0 && _selection.All(static item => item.IsArchive);

    /// <summary>系统上有没有 7-Zip 的界面程序（没装就让菜单项置灰，见 <see cref="SevenZipLocator" />）。</summary>
    public bool HasSevenZip => SevenZipLocator.IsAvailable;

    /// <summary>
    /// 「使用 7-Zip 打开」可不可点：选中的都是真实压缩包，且本机真的装了 7-Zip。
    /// 写成 <c>CanExecute</c> 而不是只靠菜单项的 <c>IsEnabled</c>：命令自己的状态与置灰结果必须一致。
    /// </summary>
    private bool CanOpenWithSevenZip => CanUseArchiveCommands && HasSevenZip;

    /// <summary>「使用 7-Zip 打开」：把选中的压缩包交给系统的 7zFM.exe（exdir 自己只带 7z.dll，没有界面）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenWithSevenZip))]
    private void OpenWithSevenZip()
    {
        var archives = ArchiveSelectionPaths();
        if (archives.Count == 0)
        {
            return;
        }

        if (SevenZipLocator.LauncherPath is not { } sevenZip)
        {
            // 菜单项本来就是置灰的，这里是防守：真正没装 7-Zip 时不静默失败
            ErrorMessage = "没找到 7-Zip（7zFM.exe），无法用它打开压缩包";
            return;
        }

        if (_shell.OpenWithProgram(sevenZip, archives))
        {
            Log.Write($"使用 7-Zip 打开：{string.Join(" / ", archives)} → {sevenZip}");
        }
        else
        {
            ErrorMessage = "无法启动 7-Zip（7zFM.exe）";
        }
    }

    /// <summary>
    /// 「解压到下载文件夹」：每个选中的压缩包解到 <c>Downloads\&lt;包名&gt;\</c>
    /// （同名目录已存在时自动加 <c>(2)(3)…</c>，不往已有的目录里混）。
    /// 解压在后台线程做，期间显示转圈；成功后用绿色 InfoBar 报一声（带「打开目录」）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseArchiveCommands))]
    private async Task ExtractToDownloadsAsync()
    {
        var archives = ArchiveSelectionPaths();
        if (archives.Count == 0)
        {
            return;
        }

        // 同一时刻只跑一次解压：再点一次会把上一次取消掉（半成品目录会被清掉）
        _extractCts?.Cancel();
        _extractCts?.Dispose();
        var cts = new CancellationTokenSource();
        _extractCts = cts;

        IsLoading = true;
        ErrorMessage = null;

        var downloads = string.Empty;
        var targets = new List<string>();
        var files = 0;

        try
        {
            downloads = ResolveDownloadsDirectory();

            foreach (var archive in archives)
            {
                var target = UniqueDirectory(Path.Combine(downloads, Path.GetFileNameWithoutExtension(archive)));
                Directory.CreateDirectory(target);

                var (ok, count) = await ExtractArchiveToAsync(archive, target, cts.Token).ConfigureAwait(true);

                if (!ok)
                {
                    // 失败 / 取消：这个目录是我们刚建的，里面只有半成品，直接删掉（不然下载目录里会多一堆垃圾）
                    TryDeleteDirectory(target);
                    return;
                }

                targets.Add(target);
                files += count;
            }
        }
        catch (Exception ex)
        {
            // 找「下载」目录 / 建子目录这些同步步骤也会失败（磁盘满、没有权限…）
            ErrorMessage = $"解压失败：{ex.Message}";
            Log.Exception("解压到下载文件夹", ex);
            return;
        }
        finally
        {
            IsLoading = false;

            if (ReferenceEquals(_extractCts, cts))
            {
                _extractCts = null;
            }

            cts.Dispose();
        }

        if (targets.Count == 1)
        {
            SetStatus("解压完成", $"已解压 {files} 个文件到 {targets[0]}", targets[0]);
        }
        else
        {
            SetStatus("解压完成", $"已解压 {archives.Count} 个压缩包（共 {files} 个文件）到下载文件夹", downloads);
        }
    }

    /// <summary>选中的真实压缩包文件路径（包内条目、目录一律跳掉）。</summary>
    private List<string> ArchiveSelectionPaths()
        => _selection.Where(static item => item.IsArchive).Select(static item => item.FullPath).ToList();

    /// <summary>解一个包，加密包会问密码（最多几次，见 <see cref="TryAskPasswordAsync" />）；失败时已经写好提示。</summary>
    private async Task<(bool Ok, int Files)> ExtractArchiveToAsync(
        string archiveFile,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var count = await _archive.ExtractAllAsync(archiveFile, targetDirectory, cancellationToken).ConfigureAwait(true);
                return (true, count);
            }
            catch (OperationCanceledException)
            {
                Log.Write($"解压已取消：{archiveFile}");
                return (false, 0);
            }
            catch (ArchivePasswordRequiredException ex)
            {
                if (!await TryAskPasswordAsync(ex, attempt).ConfigureAwait(true))
                {
                    return (false, 0);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"解压失败：{ex.Message}";
                Log.Exception($"解压（{archiveFile}）", ex);
                return (false, 0);
            }
        }
    }

    /// <summary>
    /// 目标目录 = 「下载」文件夹，每个包在里面再建一个与包同名的子目录。
    /// 路径取自 <see cref="IKnownFolderService" />（「下载」可能被用户重定向过，只有那一处算这个路径）。
    /// </summary>
    private string ResolveDownloadsDirectory()
    {
        var downloads = _knownFolders.GetUserFolders()
            .FirstOrDefault(static folder => folder.Key == UserFolderKey.Downloads)?.Path;

        // GetUserFolders 把“目录不存在”的项滤掉了（「下载」被删掉时就会这样）→ 退回 %USERPROFILE%\Downloads 并建出来
        if (string.IsNullOrWhiteSpace(downloads))
        {
            downloads = Path.Combine(_knownFolders.UserProfile, "Downloads");
        }

        Directory.CreateDirectory(downloads);
        return downloads;
    }

    /// <summary>同名目录 / 文件已经存在时依次加 <c>(2)(3)…</c>（与「新建文件夹」同一套做法）。</summary>
    private static string UniqueDirectory(string candidate)
    {
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 2; ; index++)
        {
            var next = $"{candidate} ({index})";

            if (!Directory.Exists(next) && !File.Exists(next))
            {
                return next;
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 半成品目录删不掉不影响功能（用户自己删掉就是）
        }
    }

    // ------------------------------------------------------------------ 右键「压缩」：把选中项打成一个 zip

    /// <summary>
    /// 选中的是不是“真实”条目（压缩包内部展开出来的虚拟行不行：它们没有真实路径）。
    /// 内置右键菜单里的「压缩」据此决定可不可点（命令的 CanExecute 同源）。
    /// 目录与文件都可以压缩。
    /// </summary>
    public bool CanCompressSelection => _selection.Count > 0
                                        && !IsRemote
                                        && _selection.All(static item => !item.IsInArchive);

    /// <summary>
    /// 内置右键菜单「压缩」：把选中的文件 / 目录（目录含整棵子树）打成一个 zip，
    /// 写到设置里的「压缩输出目录」（留空 = 「下载」文件夹），
    /// 然后把生成的 zip **复制到剪贴板**，并弹一条带「打开目录」的绿色提示条。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCompressSelection))]
    private async Task CompressSelectionAsync()
    {
        // 防守：命令的 CanExecute 已经挡过，但右键菜单与键盘入口不保证同时只走一条
        if (RefuseInArchive() || RefuseSelectionInArchive())
        {
            return;
        }

        var sources = _selection
            .Where(static item => !item.IsInArchive)
            .Select(static item => item.FullPath)
            .ToList();

        if (sources.Count == 0)
        {
            return;
        }

        // 同一时刻只跑一次：再点一次会把上一次取消掉（写了一半的 zip 由服务自己删掉）
        _compressCts?.Cancel();
        _compressCts?.Dispose();
        var cts = new CancellationTokenSource();
        _compressCts = cts;

        IsLoading = true;
        ErrorMessage = null;

        string zipPath;
        string outputDirectory;

        try
        {
            outputDirectory = ResolveCompressionDirectory();
            zipPath = await _compression
                .CompressAsync(sources, outputDirectory, CompressTargets.BaseName(sources, _currentPath), cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log.Write("压缩已取消");
            return;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"压缩失败：{ex.Message}";
            Log.Exception("压缩", ex);
            return;
        }
        finally
        {
            IsLoading = false;

            if (ReferenceEquals(_compressCts, cts))
            {
                _compressCts = null;
            }

            cts.Dispose();
        }

        // 剪贴板只能有一份内容：把刚生成的 zip 放进去，顺手清掉内存里的包内条目
        //（粘贴时先看系统剪贴板，所以这一步之后粘出来的一定是这个 zip）
        _archiveClipboard.Clear();

        var copiedToClipboard = _clipboard.SetFiles(new[] { zipPath }, move: false);

        if (copiedToClipboard)
        {
            Log.Write($"压缩产物已复制到剪贴板：{zipPath}");
        }
        else
        {
            // 剪贴板被别的程序占着时不值得让整次压缩报错：包已经好了，但提示里要说清楚没复制上
            Log.Write($"压缩产物复制到剪贴板失败：{zipPath}");
        }

        SetStatus(
            "压缩完成",
            copiedToClipboard
                ? $"已压缩 {sources.Count} 项到 {Path.GetFileName(zipPath)}（已复制到剪贴板）"
                : $"已压缩 {sources.Count} 项到 {Path.GetFileName(zipPath)}（复制到剪贴板失败，可以直接拖这个包）",
            Path.GetDirectoryName(zipPath) ?? string.Empty);
    }

    /// <summary>
    /// 压缩包放到哪里：优先用设置里的「压缩输出目录」，留空则用「下载」文件夹
    ///（规则在 <see cref="CompressTargets.ResolveOutputDirectory" />，好在那一条不依赖界面就能测）。
    /// 目录不存在会自动建出来；配置的路径不可用时给一句明确的提示，不静默落到别的目录。
    /// </summary>
    private string ResolveCompressionDirectory()
    {
        var directory = CompressTargets.ResolveOutputDirectory(
            _settings.Current.CompressionOutputDirectory,
            ResolveDownloadsDirectory());

        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex)
        {
            Log.Exception($"压缩输出目录（{directory}）", ex);
            throw new IOException($"压缩输出目录不可用：{directory}", ex);
        }
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
                RecordRecentFile(text);
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

    /// <summary>「打开所在文件夹」：导航到选中项的目录并把它选中（搜索结果的右键菜单用）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenContainingFolder()
    {
        if (_selection.FirstOrDefault() is { } item)
        {
            OpenSearchResult(item);
        }
    }

    /// <summary>「打开」：用默认程序打开选中的文件（右键菜单用；双击在搜索模式里是“打开所在文件夹”）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenWithDefaultApp() => OpenSelectionWithDefaultApp();

    /// <summary>搜索结果行的默认动作：文件 → 去它所在的目录并选中；目录 → 直接进去。</summary>
    private void OpenSearchResult(FileItemViewModel item)
    {
        if (item.IsDirectory)
        {
            _ = NavigateAsync(item.FullPath);
            return;
        }

        var parent = Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrEmpty(parent))
        {
            _shell.OpenWithDefaultApp(item.FullPath);
            return;
        }

        _ = NavigateAsync(parent, pushHistory: true, selectPath: item.FullPath);
    }

    /// <summary>
    /// 远程文件：先下到 <c>remote-cache\open</c>（同名且大小一致就复用上次的副本），
    /// 再交给默认程序打开。下载期间显示一条忙提示。
    /// </summary>
    private async Task OpenRemoteFileAsync(FileItemViewModel item)
    {
        var target = RemoteCache.OpenPathFor(item.FullPath, item.Name, item.Size);

        SetBusy($"正在下载 {item.DisplayName}…");

        try
        {
            await _remote.DownloadFileToAsync(item.FullPath, target, item.Size).ConfigureAwait(true);
            _shell.OpenWithDefaultApp(target);
            Log.Write($"打开远程文件：{item.FullPath} → {target}");
        }
        catch (OperationCanceledException)
        {
            // 用户取消：什么都不做
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Log.Exception($"下载远程文件（{item.FullPath}）", ex);
        }
        finally
        {
            BusyMessage = null;
        }
    }

    /// <summary>
    /// 「下载到…」：把选中项（目录含整棵子树）下到用户挑的本地目录。
    /// 只对远程位置上的选中项有意义（本地条目用普通复制）。
    /// </summary>
    public bool CanDownloadSelection => _selection.Count > 0 && IsRemote;

    [RelayCommand(CanExecute = nameof(CanDownloadSelection))]
    private async Task DownloadSelectionAsync()
    {
        var paths = _selection.Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        string? destination;
        try
        {
            destination = await _dialogs.PickFolderAsync(ResolveDownloadsDirectory()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception("选择远程下载目录", ex);
            return;
        }

        if (string.IsNullOrEmpty(destination))
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var written = await _remote.DownloadAsync(paths, destination).ConfigureAwait(true);

            // 绿色提示条（带「打开目录」）——与「解压到下载文件夹」同一套
            SetStatus("下载完成", $"已下载 {written.Count} 项到 {destination}", destination);
            Log.Write($"远程下载完成：{written.Count} 项 → {destination}");
        }
        catch (OperationCanceledException)
        {
            // 用户取消
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Log.Exception($"下载远程条目（{paths.Count} 项）", ex);
        }
        finally
        {
            IsLoading = false;
        }
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

        // 远程条目没有本地路径，问外壳要图标没意义（可能拿到一个不相干的通用图标，还白花时间）：
        // 直接用按扩展名推断的字形（行模板里字形就是没图标时的占位）
        if (IsRemote || _fileSystem.IsRemotePath(item.FullPath))
        {
            return;
        }

        try
        {
            var bitmap = await _icons
                .GetIconAsync(item.FullPath, item.IsDirectory, isVirtualDirectory: IsInsideArchive || item.IsInArchive)
                .ConfigureAwait(true);
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

    /// <summary>
    /// 预热：一次性把前面 <paramref name="count" /> 行的图标取好，返回取到的个数。
    /// 只给待机预热用（见 <see cref="MainViewModel.PreloadIconsAsync" />）：没有可见窗口时行容器不会创建，
    /// 图标就不会被按需取到，首屏差的就是那几百毫秒。失败与单行一样只记日志。
    /// </summary>
    public async Task<int> PreloadIconsAsync(int count)
    {
        var loaded = 0;

        foreach (var item in Items.Take(Math.Max(0, count)).ToList())
        {
            await EnsureIconAsync(item).ConfigureAwait(true);

            if (item.HasIcon)
            {
                loaded++;
            }
        }

        return loaded;
    }

    /// <summary>双击 / 回车打开某一项。目录进入，文件交给默认程序。</summary>
    public void OpenItem(FileItemViewModel item)
    {
        // 搜索结果：双击 = 打开所在文件夹并选中它（目录结果直接进去）
        // —— 搜完了通常是要到那个位置去做事，而不是把文件丢给默认程序
        if (_isSearchMode)
        {
            OpenSearchResult(item);
            return;
        }

        if (item.IsDirectory)
        {
            _ = NavigateAsync(item.FullPath);
            return;
        }

        // 远程位置上的文件：没有本地路径，先下到中转目录再用默认程序打开
        //（与压缩包里的文件同一种做法，见 AGENTS.md“远程位置”）
        if (IsRemote || _fileSystem.IsRemotePath(item.FullPath))
        {
            _ = OpenRemoteFileAsync(item);
            return;
        }

        // 压缩包：双击就**进去**（以目录形式浏览），不再交给外部程序
        if (item.IsArchive || _archive.IsArchiveFile(item.FullPath))
        {
            _ = NavigateAsync(item.FullPath);
            return;
        }

        // 包内的文件（含在真实目录里就地展开出来的那几行）：先解到临时目录，再用默认程序打开
        // （资源管理器的做法）
        if (item.IsInArchive || IsInsideArchive)
        {
            _ = OpenArchiveEntryAsync(item);
            return;
        }

        _shell.OpenWithDefaultApp(item.FullPath);
        RecordRecentFile(item.FullPath);
    }

    /// <summary>
    /// 记一次“用默认程序打开了这个文件”进「最新访问」（与目录导航用同一个服务）。
    /// 只记真实存在的本地文件：包内条目 / 远程文件打开的是中转目录里的临时副本，记下来没有意义。
    /// </summary>
    private void RecordRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || _fileSystem.IsRemotePath(path)
            || _fileSystem.IsInsideArchive(path)
            || !_fileSystem.FileExists(path))
        {
            return;
        }

        _recents.Add(path, isDirectory: false);
    }

    /// <summary>包内文件：解到临时目录再交给默认程序打开（加密包会先问密码）。</summary>
    private async Task OpenArchiveEntryAsync(FileItemViewModel item)
    {
        if (!_fileSystem.TryParseArchivePath(item.FullPath, out var location))
        {
            ErrorMessage = ArchiveReadOnlyMessage;
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var temp = await _archive.ExtractToTempAsync(location).ConfigureAwait(true);
                _shell.OpenWithDefaultApp(temp);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ArchivePasswordRequiredException ex)
            {
                if (!await TryAskPasswordAsync(ex, attempt).ConfigureAwait(true))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Log.Exception($"打开压缩包内文件（{item.FullPath}）", ex);
                return;
            }
        }
    }

    public void OpenSelectionWithDefaultApp()
    {
        // 远程位置：没有本地路径，先下到中转目录再分别交给默认程序
        if (IsRemote)
        {
            foreach (var item in _selection.Where(static i => !i.IsDirectory).ToList())
            {
                _ = OpenRemoteFileAsync(item);
            }

            return;
        }

        // 包内条目没有真实路径，交给外壳也打不开
        if (RefuseSelectionInArchive())
        {
            return;
        }

        foreach (var item in _selection)
        {
            _shell.OpenWithDefaultApp(item.FullPath);
            RecordRecentFile(item.FullPath);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopySelectionPath()
    {
        _shell.CopyTextToClipboard(string.Join(Environment.NewLine, _selection.Select(i => i.FullPath)));
    }

    [RelayCommand]
    private void CopyCurrentPath() => _shell.CopyTextToClipboard(_currentPath);

    // ------------------------------------------------------------------ 复制 / 剪切 / 粘贴

    /// <summary>剪贴板上现在有没有文件（内置菜单据此决定「粘贴」能不能点）。
    /// 包内条目记在内存里（<see cref="IArchiveClipboardService" />），也算“有文件”。</summary>
    public bool HasFileClipboard => _clipboard.HasFiles() || _archiveClipboard.HasEntries;

    /// <summary>
    /// Ctrl+C / 内置菜单「复制」。
    /// 真实目录里的条目走系统剪贴板（与资源管理器互通）；**压缩包里的条目没有真实路径**，
    /// 只能把“压缩包 + 包内路径”记在内存里（<see cref="IArchiveClipboardService" />），
    /// 到真实目录里粘贴时再解出来（见 <see cref="PasteAsync" />）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopySelection()
    {
        // 远程位置的条目：先下到中转目录，再把**真实文件**放进系统剪贴板
        //（粘到本地目录 / 粘到资源管理器都能用；中转副本在复制完成后回收，见 RemoteCache）
        if (IsRemote)
        {
            _ = CopyRemoteSelectionAsync();
            return;
        }

        // 压缩包内部的条目（含在真实目录里就地展开出来的那几行）没有真实路径：
        // 只能把“压缩包 + 包内路径”记进内存，到真实目录粘贴时再解出来
        if (SelectionContainsArchiveEntries())
        {
            // 真实文件与包内条目混选没法用一份剪贴板表达（系统剪贴板只认真实路径）
            if (_selection.Count != _selection.Count(static item => item.IsInArchive))
            {
                ErrorMessage = "不能同时复制压缩包内外的条目";
                return;
            }

            CopyArchiveSelection();
            return;
        }

        var paths = _selection.Select(i => i.FullPath).ToList();

        // 剪贴板只能有一份内容：复制真实文件就把内存里的包内条目清掉
        _archiveClipboard.Clear();

        if (_clipboard.SetFiles(paths, move: false))
        {
            Log.Write($"复制到剪贴板：{paths.Count} 项");
        }
    }

    /// <summary>
    /// 远程「复制」：把选中项（目录含整棵子树）下到 <c>remote-cache\copy\&lt;guid&gt;</c>，
    /// 再把解出来的真实文件写进系统剪贴板（<c>CF_HDROP</c> + 复制意图）。
    ///
    /// <para>
    /// 为什么不只记在内存里：剪贴板必须能跨进程用（粘到资源管理器、粘贴时交给 <c>SHFileOperation</c>），
    /// 而 <c>CF_HDROP</c> 只认真实文件路径。代价是“按 Ctrl+C”那一刻就开始下载了。
    /// </para>
    /// </summary>
    private async Task CopyRemoteSelectionAsync()
    {
        var remotePaths = _selection.Select(i => i.FullPath).ToList();
        if (remotePaths.Count == 0)
        {
            return;
        }

        var staging = RemoteCache.NewStaging(RemoteCache.CopyCategory);
        SetBusy($"正在下载 {remotePaths.Count} 项到剪贴板…");

        try
        {
            var written = await _remote.DownloadAsync(remotePaths, staging).ConfigureAwait(true);

            // 剪贴板只能有一份内容：复制真实文件就把内存里的包内条目清掉
            _archiveClipboard.Clear();

            if (_clipboard.SetFiles(written, move: false))
            {
                Log.Write($"远程复制到剪贴板：{written.Count} 项（中转目录 {staging}）");
            }
            else
            {
                _remote.ReleaseStaging(staging);
                ErrorMessage = "无法写入剪贴板";
            }
        }
        catch (OperationCanceledException)
        {
            _remote.ReleaseStaging(staging);
        }
        catch (Exception ex)
        {
            _remote.ReleaseStaging(staging);
            ErrorMessage = ex.Message;
            Log.Exception($"下载远程条目到剪贴板（{remotePaths.Count} 项）", ex);
        }
        finally
        {
            BusyMessage = null;
        }
    }

    /// <summary>
    /// 把选中的包内条目记进内存剪贴板（当成“复制”，粘贴时才解出来）。
    /// 压缩包从选中项自己解析出来（不再看当前目录）：在真实目录里就地展开压缩包时，
    /// 当前目录并不是那个包。
    /// </summary>
    private void CopyArchiveSelection()
    {
        string? archiveFile = null;
        var inner = new List<string>();

        foreach (var item in _selection)
        {
            if (!_fileSystem.TryParseArchivePath(item.FullPath, out var location)
                || location.InnerPath.Length == 0)
            {
                continue;
            }

            if (archiveFile is null)
            {
                archiveFile = location.ArchiveFile;
            }
            else if (!string.Equals(archiveFile, location.ArchiveFile, StringComparison.OrdinalIgnoreCase))
            {
                // 内存剪贴板一次只记得住一个压缩包（就地展开了好几个时只能分开复制）
                ErrorMessage = "一次只能复制同一个压缩包里的条目";
                return;
            }

            if (!inner.Contains(location.InnerPath, StringComparer.OrdinalIgnoreCase))
            {
                inner.Add(location.InnerPath);
            }
        }

        if (archiveFile is null || inner.Count == 0)
        {
            return;
        }

        // 系统剪贴板上的内容与这份互斥：留着的话粘贴会拿到两份（而且旧的那份更早）
        _clipboard.Clear();
        _archiveClipboard.Set(archiveFile, inner);

        Log.Write($"复制压缩包内条目：{inner.Count} 项（{Path.GetFileName(archiveFile)}）");
    }

    /// <summary>Ctrl+X / 内置菜单「剪切」：把选中项放进剪贴板，粘贴时是移动。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CutSelection()
    {
        // 远程位置只读：不能从远程剪走东西（“复制到本地”请用「下载到…」或「复制」）
        if (RefuseInRemote() || RefuseSelectionInArchive())
        {
            return;
        }

        var paths = _selection.Select(i => i.FullPath).ToList();

        // 同 CopySelection：剪贴板上只能留一份内容
        _archiveClipboard.Clear();

        if (_clipboard.SetFiles(paths, move: true))
        {
            Log.Write($"剪切到剪贴板：{paths.Count} 项");
        }
    }

    /// <summary>
    /// Ctrl+V / 内置菜单「粘贴」：把剪贴板上的东西弄进当前目录。
    /// 优先看系统剪贴板 —— 包内复制会清空它，所以它非空就一定比内存里的包内条目更新；
    /// 系统剪贴板空着而内存里有包内条目时，就解包再复制进来。
    /// </summary>
    [RelayCommand]
    private async Task PasteAsync()
    {
        // 包内 / 远程目录都不能粘（写的是虚拟路径 / 只读位置），「最新访问」不是真实目录
        if (RefuseInArchive() || RefuseInRemote() || RefuseInRecentView())
        {
            return;
        }

        if (string.IsNullOrEmpty(_currentPath))
        {
            ErrorMessage = "当前窗口没有目录，无法粘贴";
            return;
        }

        var snapshot = _clipboard.GetFiles();

        if (snapshot is not null && snapshot.Paths.Count > 0)
        {
            // 复制到同一个目录时交给外壳处理（它会问是否覆盖 / 生成“(2)”副本），
            // 只有拖放才需要把“已经在目标目录里”的项跳过（拖过去本来就没变化）
            var isMove = snapshot.IsMove;
            var ok = await TransferAsync(snapshot.Paths, _currentPath, isMove, skipItemsAlreadyInTarget: false)
                .ConfigureAwait(true);

            // 剪切只生效一次：成功后清掉剪贴板（与资源管理器一致）
            if (ok && isMove)
            {
                _clipboard.Clear();
            }

            return;
        }

        if (_archiveClipboard.Get() is { } archive)
        {
            await PasteArchiveEntriesAsync(archive).ConfigureAwait(true);
            return;
        }

        ErrorMessage = "剪贴板上没有文件";
    }

    /// <summary>
    /// 包内条目粘到真实目录：先把选中条目（含目录的整棵子树）解到临时目录，
    /// 再交给外壳的 <c>SHFileOperation</c> 复制进来 —— 进度对话框、同名冲突询问与普通复制完全一致。
    /// </summary>
    private async Task PasteArchiveEntriesAsync(ArchiveClipboardContent content)
    {
        var target = _fileSystem.NormalizeDirectoryPath(_currentPath);
        if (target is null)
        {
            ErrorMessage = $"目标目录不存在：{_currentPath}";
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            ArchiveExtraction extraction;

            try
            {
                extraction = await _archive
                    .ExtractForCopyAsync(content.ArchiveFile, content.InnerPaths)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ArchivePasswordRequiredException ex)
            {
                if (!await TryAskPasswordAsync(ex, attempt).ConfigureAwait(true))
                {
                    return;
                }

                continue;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Log.Exception($"解出压缩包内条目（{content.ArchiveFile}）", ex);
                return;
            }

            Log.Write($"粘贴压缩包内条目：{extraction.Paths.Count} 项 → {target}");

            try
            {
                // 复制（不是移动）：内存里的包内条目留着，可以反复粘（与“复制”的语义一致）
                var result = await _fileOperations.CopyAsync(extraction.Paths, target).ConfigureAwait(true);

                if (!result.Canceled && result.ErrorMessage is { } message)
                {
                    ErrorMessage = message;
                }
            }
            finally
            {
                // 中转副本没用了（复制成功 / 取消 / 失败都一样），不要占着磁盘
                _archive.ReleaseStaging(extraction.StagingDirectory);
            }

            return;
        }
    }

    /// <summary>
    /// 拖拽开始前把选中项变成“交给外壳的真实路径”。
    ///
    /// <para>
    /// 真实条目原样用；**压缩包里的条目没有真实路径**，先解到临时目录
    /// （<see cref="IArchiveService.ExtractForDragAsync" />）—— 否则数据包里就没有
    /// <c>CF_HDROP</c>，拖到资源管理器 / 桌面会被直接拒掉（WinUI 3 的“延迟提供 StorageItems”
    /// 有已知 bug，不能让壳自己去问，见 AGENTS.md 第 6 节第 95 条）。
    /// </para>
    /// <para>
    /// 返回 null 表示这次拖拽不该开始（原因已经写进 <see cref="ErrorMessage" />）。
    /// 解包期间显示一条忙提示（几百 MB 的大条目要等一会儿）；加密包照旧问密码。
    /// </para>
    /// </summary>
    public async Task<DragPayload?> BuildDragPayloadAsync(IReadOnlyList<FileItemViewModel> items)
    {
        var paths = new List<string>();
        var staging = new List<string>();

        // 包内行 -> “压缩包 + 包内路径”；解析不出来的按陈旧选中项跳过
        var archiveEntries = new List<(string ArchiveFile, string InnerPath)>();

        // 远程行：没有本地路径，要先把它们下到中转目录（交不出 CF_HDROP 就拖不出去）
        var remoteEntries = new List<string>();

        foreach (var item in items)
        {
            if (_fileSystem.IsRemotePath(item.FullPath))
            {
                remoteEntries.Add(item.FullPath);
                continue;
            }

            if (item.IsInArchive)
            {
                if (_fileSystem.TryParseArchivePath(item.FullPath, out var location) && location.InnerPath.Length > 0)
                {
                    archiveEntries.Add((location.ArchiveFile, location.InnerPath));
                }

                continue;
            }

            paths.Add(item.FullPath);
        }

        if (archiveEntries.Count == 0 && remoteEntries.Count == 0)
        {
            return paths.Count > 0 ? new DragPayload(paths, staging) : null;
        }

        var succeeded = true;

        // 先把远程条目下下来（可能很慢，所以显示忙提示）
        if (remoteEntries.Count > 0)
        {
            var directory = RemoteCache.NewStaging(RemoteCache.DragCategory);
            SetBusy($"正在从远程位置下载 {remoteEntries.Count} 项…");

            try
            {
                var written = await _remote.DownloadAsync(remoteEntries, directory).ConfigureAwait(true);
                paths.AddRange(written);
                staging.Add(directory);
            }
            catch (OperationCanceledException)
            {
                _remote.ReleaseStaging(directory);
                succeeded = false;
            }
            catch (Exception ex)
            {
                _remote.ReleaseStaging(directory);
                ErrorMessage = $"无法从远程位置下载：{ex.Message}";
                Log.Exception($"拖拽前下载远程条目（{remoteEntries.Count} 项）", ex);
                succeeded = false;
            }
            finally
            {
                BusyMessage = null;
            }
        }

        // 再解压缩包内的条目（一个包只解一次）
        if (succeeded && archiveEntries.Count > 0)
        {
            SetBusy($"正在解出压缩包内条目（{archiveEntries.Count} 项）…");

            try
            {
                succeeded = await ExtractForDragAsync(archiveEntries, paths, staging).ConfigureAwait(true);
            }
            finally
            {
                BusyMessage = null;
            }
        }

        if (!succeeded)
        {
            // 中途失败（或用户取消）：前面已经解出来 / 下下来的临时副本没用了，别占着磁盘
            foreach (var directory in staging)
            {
                _archive.ReleaseStaging(directory);
                _remote.ReleaseStaging(directory);
            }

            return null;
        }

        Log.Write(
            $"拖拽准备：{paths.Count} 项（其中 {staging.Count} 处临时副本：远程 {remoteEntries.Count} 项、"
            + $"包内 {archiveEntries.Count} 条）");

        return new DragPayload(paths, staging);
    }

    /// <summary>
    /// 按压缩包分组解出包内条目：一个包只解一次（固实包逐个条目解会把同一块数据重复解多遍）。
    /// 失败 / 取消时返回 false（提示已经写好）。
    /// </summary>
    private async Task<bool> ExtractForDragAsync(
        IReadOnlyList<(string ArchiveFile, string InnerPath)> entries,
        List<string> paths,
        List<string> staging)
    {
        foreach (var group in entries.GroupBy(static entry => entry.ArchiveFile, StringComparer.OrdinalIgnoreCase))
        {
            var inner = group.Select(static entry => entry.InnerPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var extraction = await _archive.ExtractForDragAsync(group.Key, inner).ConfigureAwait(true);
                    paths.AddRange(extraction.Paths);
                    staging.Add(extraction.StagingDirectory);
                    break;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (ArchivePasswordRequiredException ex)
                {
                    if (!await TryAskPasswordAsync(ex, attempt).ConfigureAwait(true))
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"无法解出压缩包内条目：{ex.Message}";
                    Log.Exception($"解出压缩包内条目（{group.Key}）", ex);
                    return false;
                }
            }
        }

        return true;
    }

    private void SetBusy(string message)
    {
        ErrorMessage = null;
        BusyMessage = message;
    }

    /// <summary>
    /// 拖放落下：把一批文件 / 目录移动（按住 Ctrl 时是复制）到目标目录。
    /// 目标可能是当前目录（拖到列表空白处），也可能是列表里的某个目录行，甚至另一个窗格。
    /// </summary>
    public Task DropFilesAsync(IReadOnlyList<string> paths, string targetDirectory, bool move)
        => TransferAsync(paths, targetDirectory, move, skipItemsAlreadyInTarget: true);

    // ------------------------------------------------------------------ 删除

    /// <summary>
    /// Delete 键 / 内置菜单「删除」：把选中项丢进回收站。
    /// 确认框（“确实要将其移至回收站吗？”）由外壳弹，用户点“否”时什么都不发生。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteSelectionAsync() => RunDeleteAsync(permanent: false);

    /// <summary>Shift+Delete：不经过回收站，直接永久删除（外壳会就此单独警告一次）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteSelectionPermanentlyAsync() => RunDeleteAsync(permanent: true);

    private async Task RunDeleteAsync(bool permanent)
    {
        if (RefuseSelectionInArchive() || RefuseInRemote())
        {
            return;
        }

        // 选中项可能是别处已经删掉的陈旧行（刷新前），过滤一遍免得外壳报“找不到文件”
        var paths = _selection
            .Select(i => i.FullPath)
            .Where(p => _fileSystem.DirectoryExists(p) || _fileSystem.FileExists(p))
            .ToList();

        if (paths.Count == 0)
        {
            return;
        }

        Log.Write($"删除：{paths.Count} 项 → {(permanent ? "永久删除" : "回收站")}");

        var result = await _fileOperations.DeleteAsync(paths, permanent).ConfigureAwait(true);

        // 用户在外壳的确认框里点“否”不是错误，不要把“操作已取消”当成失败报出来
        if (!result.Canceled && result.ErrorMessage is { } message)
        {
            ErrorMessage = message;
        }

        // 成功后由 FileOperationService.Completed 事件让受影响的标签页重新枚举（包括本页），
        // 所以这里不自己刷新：别处的同名目录、另一个窗格也要跟着变
    }

    /// <summary>真正执行复制 / 移动；返回是否全部成功。</summary>
    private async Task<bool> TransferAsync(
        IReadOnlyList<string> paths,
        string targetDirectory,
        bool move,
        bool skipItemsAlreadyInTarget)
    {
        // 包内 / 远程目录不能粘 / 拖入（写的是虚拟路径 / 只读位置），「最新访问」不是真实目录；
        // 包内条目**拖出去**走的是解出来的临时副本（真实路径），不在这里拒绝
        if (RefuseInArchive() || RefuseInRemote() || RefuseInRecentView())
        {
            return false;
        }

        var target = _fileSystem.NormalizeDirectoryPath(targetDirectory);
        if (target is null)
        {
            ErrorMessage = $"目标目录不存在：{targetDirectory}";
            return false;
        }

        var sources = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_fileSystem.DirectoryExists(path) && !_fileSystem.FileExists(path))
            {
                continue;
            }

            if (SameDirectory(path, target))
            {
                continue;
            }

            // 目录不能搬进它自己（或它的子目录）里
            if (_fileSystem.DirectoryExists(path) && IsUnder(target, path))
            {
                continue;
            }

            // 拖进它本来就在的目录：什么都不用做
            if (skipItemsAlreadyInTarget && SameDirectory(_fileSystem.GetParentDirectory(path) ?? string.Empty, target))
            {
                continue;
            }

            sources.Add(path);
        }

        if (sources.Count == 0)
        {
            Log.Write($"文件操作：没有需要处理的项（目标 {target}）");
            return false;
        }

        var result = move
            ? await _fileOperations.MoveAsync(sources, target).ConfigureAwait(true)
            : await _fileOperations.CopyAsync(sources, target).ConfigureAwait(true);

        // 用户在外壳的进度/冲突对话框里点取消不是错误
        if (!result.Canceled && result.ErrorMessage is { } message)
        {
            ErrorMessage = message;
        }

        return result.Success;
    }

    /// <summary>两个路径是不是同一个目录（忽略末尾分隔符与大小写；`C:\` 与 `C:\` 这种根路径也要相等）。</summary>
    private static bool SameDirectory(string a, string b)
        => string.Equals(a.TrimEnd(Separators), b.TrimEnd(Separators), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="candidate" /> 是不是在 <paramref name="ancestor" /> 目录里（含任意深度）。</summary>
    private static bool IsUnder(string candidate, string ancestor)
    {
        var root = ancestor.TrimEnd(Separators);
        return candidate.Length > root.Length
               && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && (candidate[root.Length] == '\\' || candidate[root.Length] == '/');
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RevealInExplorer()
    {
        if (RefuseSelectionInArchive() || RefuseInRemote())
        {
            return;
        }

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
        if (RefuseSelectionInArchive() || RefuseInRemote())
        {
            return;
        }

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
        if (RefuseInArchive() || RefuseInRemote() || RefuseInRecentView())
        {
            return;
        }

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
    private void OpenTerminal()
    {
        if (RefuseInArchive() || RefuseInRemote() || RefuseInRecentView())
        {
            return;
        }

        _shell.OpenTerminal(_currentPath);
    }

    [RelayCommand]
    private void OpenTerminalAsAdmin()
    {
        if (RefuseInArchive() || RefuseInRemote() || RefuseInRecentView())
        {
            return;
        }

        _shell.OpenTerminal(_currentPath, asAdministrator: true);
    }

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

    /// <summary>展开/折叠一行（点击行首箭头时调用）。目录与压缩包文件都能展开。</summary>
    public async Task ToggleExpandAsync(FileItemViewModel node)
    {
        if (!node.IsExpandable)
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

    /// <summary>
    /// 按需加载某个可展开行的直接子项；返回是否加载成功（已加载也算成功）。
    /// 目录走真实文件系统，压缩包行走 <see cref="IArchiveService" />（加密包会先问密码）。
    /// </summary>
    private async Task<bool> EnsureChildrenAsync(FileItemViewModel node)
    {
        if (node.ChildrenLoaded)
        {
            return true;
        }

        var cts = _loadCts;
        IReadOnlyList<FileSystemEntry> entries;

        // 压缩包的密码是“边解析边可能才发现要”的，所以枚举包在重试循环里
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                entries = await _fileSystem.EnumerateDirectoryAsync(
                    node.FullPath,
                    _settings.Current.ShowHiddenFiles,
                    cts?.Token ?? default).ConfigureAwait(true);

                break;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ArchivePasswordRequiredException ex)
            {
                if (!await TryAskPasswordAsync(ex, attempt).ConfigureAwait(true))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                // 真实目录不会走到这里（枚举内部吞掉异常返回部分结果），所以这是压缩包打不开：
                // 把原因显示出来，并保持“未加载”状态，箭头留着让用户重试
                ErrorMessage = ex.Message;
                Log.Exception($"展开 {node.FullPath}", ex);
                return false;
            }
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
            if (node is null || !node.IsExpandable)
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
            if (!node.IsExpandable)
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
        // 远程路径（sftp://…）不能走 SplitPath：它靠 Path.GetPathRoot 拆盘符，对协议头一无所知
        var raw = RemotePath.TryParse(path, out var remote)
            ? RemotePath.Segments(remote)
            : SplitPath(path);

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

        if (RecentView.IsRecentViewPath(path))
        {
            // 「最新访问」是顶层虚拟视图：面包屑只有它自己那一段
            raw.Add((RecentView.DisplayName, path));
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

        // 「最新访问」默认保持服务给的顺序（访问时间倒序），用户点过列头之后才按列排
        if (!_sortByAccessOrder)
        {
            _rootNodes.Sort(CompareNodes);
        }
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
        // 任何一个“请排序”的动作（点列头 / 拨“文件夹排在文件前面”）都会取消「最新访问」
        // 视图的“按访问时间”默认顺序 —— 用户明确要求排序，就听他的
        _sortByAccessOrder = false;

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
