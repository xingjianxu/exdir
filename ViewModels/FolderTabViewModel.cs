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
    private readonly IDialogService _dialogs;
    private readonly IKnownFolderService _knownFolders;

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
    private string? _statusMessage;
    private string? _statusTitle;
    private string? _statusTargetPath;
    private CancellationTokenSource? _extractCts;
    private CancellationTokenSource? _compressCts;

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
        IKnownFolderService knownFolders,
        IDialogService dialogs)
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
        _knownFolders = knownFolders;
        _dialogs = dialogs;

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
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

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

    // ------------------------------------------------------------------ 压缩包（只读）

    /// <summary>最多让用户输几次压缩包密码（输完还是不对就显示「需要密码」）。</summary>
    private const int MaxPasswordAttempts = 3;

    /// <summary>压缩包里只读：写操作的统一拒绝文案。</summary>
    public const string ArchiveReadOnlyMessage = "压缩包内不支持该操作（只读浏览）";

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
    public bool CanCompressSelection => _selection.Count > 0 && _selection.All(static item => !item.IsInArchive);

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

    /// <summary>双击 / 回车打开某一项。</summary>
    public void OpenItem(FileItemViewModel item)
    {
        if (item.IsDirectory)
        {
            _ = NavigateAsync(item.FullPath);
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
        // 包内条目没有真实路径，交给外壳也打不开
        if (RefuseSelectionInArchive())
        {
            return;
        }

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
        if (RefuseSelectionInArchive())
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
        if (RefuseInArchive())
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
        if (RefuseSelectionInArchive())
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
        // 包内不能粘 / 拖入（写入路径）；包内条目也拖不出来（没有真实路径），统一拒绝
        if (RefuseInArchive())
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
        if (RefuseSelectionInArchive())
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
        if (RefuseSelectionInArchive())
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
        if (RefuseInArchive())
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
        if (RefuseInArchive())
        {
            return;
        }

        _shell.OpenTerminal(_currentPath);
    }

    [RelayCommand]
    private void OpenTerminalAsAdmin()
    {
        if (RefuseInArchive())
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
