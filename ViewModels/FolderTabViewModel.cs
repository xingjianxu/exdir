using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exdir.Models;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>
/// 一个文件标签页：维护当前目录、条目集合、选中项、前进/后退历史与排序状态。
/// </summary>
public sealed partial class FolderTabViewModel : ObservableObject
{
    private readonly IFileSystemService _fileSystem;
    private readonly IShellService _shell;
    private readonly ISettingsService _settings;

    private readonly List<string> _backStack = new();
    private readonly List<string> _forwardStack = new();
    private CancellationTokenSource? _loadCts;

    private IReadOnlyList<FileSystemEntry> _entries = Array.Empty<FileSystemEntry>();

    private string _currentPath = string.Empty;
    private string _currentDirectoryName = string.Empty;
    private string _pathInput = string.Empty;
    private bool _isLoading;
    private string? _errorMessage;
    private ObservableCollection<FileItemViewModel> _items = new();
    private IReadOnlyList<FileItemViewModel> _selection = Array.Empty<FileItemViewModel>();
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortAscending = true;
    private bool _foldersFirst;
    private bool _showExtensions = true;

    public FolderTabViewModel(IFileSystemService fileSystem, IShellService shell, ISettingsService settings)
    {
        _fileSystem = fileSystem;
        _shell = shell;
        _settings = settings;

        _foldersFirst = settings.Current.FoldersFirst;
        _showExtensions = settings.Current.ShowExtensions;
    }

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
                OnPropertyChanged(nameof(CurrentDirectoryName));
                OnPropertyChanged(nameof(TabHeader));
                OnPropertyChanged(nameof(TooltipText));
            }
        }
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

    public ObservableCollection<FileItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public IReadOnlyList<FileItemViewModel> Selection
    {
        get => _selection;
        private set
        {
            if (SetProperty(ref _selection, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionSummary));
                OpenSelectionCommand.NotifyCanExecuteChanged();
                CopySelectionPathCommand.NotifyCanExecuteChanged();
                RevealInExplorerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => _selection.Count > 0;

    public string SelectionSummary => _selection.Count switch
    {
        0 => string.Empty,
        1 => _selection[0].DisplayName,
        var n => $"已选择 {n} 项",
    };

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

    // ------------------------------------------------------------------ 导航

    /// <summary>加载指定目录。</summary>
    /// <param name="path">目标路径。</param>
    /// <param name="pushHistory">是否写入后退历史。</param>
    /// <param name="selectPath">导航完成后要选中的条目路径（用于返回上一级后定位）。</param>
    public async Task NavigateAsync(string path, bool pushHistory = true, string? selectPath = null)
    {
        var normalized = _fileSystem.NormalizeDirectoryPath(path);
        if (normalized is null)
        {
            ErrorMessage = $"无法打开：{path}";
            return;
        }

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

        Selection = Array.Empty<FileItemViewModel>();
        RebuildItems(selectPath);

        NotifyNavigationState();
        Navigated?.Invoke(this, normalized);
    }

    /// <summary>重新枚举当前目录。</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_currentPath))
        {
            return;
        }

        var keepSelection = _selection.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await NavigateAsync(_currentPath, pushHistory: false).ConfigureAwait(true);

        if (keepSelection.Count > 0)
        {
            var restored = Items.Where(i => keepSelection.Contains(i.FullPath)).ToList();
            if (restored.Count > 0)
            {
                Selection = restored;
            }
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
            PathInput = _currentPath;
            return;
        }

        var normalized = _fileSystem.NormalizeDirectoryPath(text);
        if (normalized is null)
        {
            // 可能是文件路径：直接交给 shell 打开
            if (_fileSystem.FileExists(text))
            {
                _shell.OpenWithDefaultApp(text);
                PathInput = _currentPath;
                return;
            }

            ErrorMessage = $"路径不存在：{text}";
            PathInput = _currentPath;
            return;
        }

        await NavigateAsync(normalized).ConfigureAwait(true);
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

    [RelayCommand]
    private void OpenTerminal() => _shell.OpenTerminal(_currentPath);

    [RelayCommand]
    private void OpenTerminalAsAdmin() => _shell.OpenTerminal(_currentPath, asAdministrator: true);

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
        await RefreshAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ 内部

    private void RebuildItems(string? selectPath)
    {
        var items = _entries
            .Select(entry => new FileItemViewModel(entry, _showExtensions))
            .ToList();

        Sort(items);

        Items = new ObservableCollection<FileItemViewModel>(items);

        if (!string.IsNullOrEmpty(selectPath))
        {
            var match = items.FirstOrDefault(i => string.Equals(i.FullPath, selectPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                Selection = new[] { match };
            }
        }
    }

    private void ResortItems()
    {
        var items = Items.ToList();
        Sort(items);
        Items = new ObservableCollection<FileItemViewModel>(items);
    }

    private void Sort(List<FileItemViewModel> items)
    {
        Comparison<FileItemViewModel> byColumn = _sortColumn switch
        {
            FileSortColumn.LastWriteTime => static (a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime),
            FileSortColumn.Type => static (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            FileSortColumn.Size => static (a, b) => a.Size.CompareTo(b.Size),
            _ => static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
        };

        items.Sort((a, b) =>
        {
            if (_foldersFirst && a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            var result = byColumn(a, b);
            if (!_sortAscending)
            {
                result = -result;
            }

            return result != 0
                ? result
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });
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
    }

    private string GlyphFor(FileSortColumn column)
        => SortColumn == column ? (SortAscending ? "\uE70E" : "\uE70D") : string.Empty;
}
