using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>
/// 一个文件管理窗格。每个窗格拥有独立的标签页集合；界面支持 1 个或 2 个窗格。
/// </summary>
public sealed partial class PanelViewModel : ObservableObject
{
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
    private readonly IKnownFolderService _knownFolders;
    private readonly IDialogService _dialogs;
    private readonly IEverythingSearchService _everythingSearch;
    private readonly IRecentItemsService _recents;

    private FolderTabViewModel? _activeTab;
    private bool _isActive;

    public PanelViewModel(
        string id,
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
        IEverythingSearchService everythingSearch,
        IRecentItemsService recents)
    {
        Id = id;
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
        _everythingSearch = everythingSearch;
        _recents = recents;
    }

    /// <summary>窗格标识：<c>primary</c> 或 <c>secondary</c>。</summary>
    public string Id { get; }

    public ObservableCollection<FolderTabViewModel> Tabs { get; } = new();

    /// <summary>导航事件（当前标签页发生导航时触发）。</summary>
    public event EventHandler<string>? Navigated;

    /// <summary>窗格被点击/获得焦点时请求成为活动窗格。</summary>
    public event EventHandler? ActivateRequested;

    /// <summary>由视图层调用：请求把本窗格设为活动窗格。</summary>
    public void RequestActivate() => ActivateRequested?.Invoke(this, EventArgs.Empty);

    public FolderTabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            var previous = _activeTab;
            if (!SetProperty(ref _activeTab, value))
            {
                return;
            }

            if (previous is not null)
            {
                previous.Navigated -= OnTabNavigated;
            }

            if (value is not null)
            {
                value.Navigated += OnTabNavigated;
            }

            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(PaneTitle));
            OnPropertyChanged(nameof(CanCloseTab));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string CurrentPath => _activeTab?.CurrentPath ?? string.Empty;

    /// <summary>窗格标题（标签栏为空时显示）。</summary>
    public string PaneTitle => _activeTab?.CurrentDirectoryName ?? "此电脑";

    public bool CanCloseTab => Tabs.Count > 1;

    /// <summary>创建一个标签页并激活它。返回新建的标签页。</summary>
    public FolderTabViewModel CreateTab()
    {
        var tab = new FolderTabViewModel(
            _fileSystem, _shell, _settings, _icons, _contextMenu, _clipboard, _fileOperations, _archive, _archiveClipboard, _compression, _remote, _knownFolders, _dialogs, _everythingSearch, _recents);
        tab.PropertyChanged += OnTabPropertyChanged;
        Tabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(CanCloseTab));
        CloseTabCommand.NotifyCanExecuteChanged();
        return tab;
    }

    /// <summary>关闭一个标签页；若关闭的是活动标签则切换到相邻标签。</summary>
    public void RemoveTab(FolderTabViewModel tab)
    {
        if (Tabs.Count <= 1)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(ActiveTab, tab);
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.Navigated -= OnTabNavigated;
        Tabs.RemoveAt(index);

        if (wasActive)
        {
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        OnPropertyChanged(nameof(CanCloseTab));
        CloseTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public async Task NewTabAsync()
    {
        // 必须先取路径：CreateTab 会把活动标签切到新标签，之后 CurrentPath 就是空的了
        var path = CurrentPath;
        var tab = CreateTab();

        if (!string.IsNullOrEmpty(path))
        {
            await tab.NavigateAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 在指定路径新建标签页（用于“在新标签页中打开”）。
    /// <paramref name="selectPath" /> 非空时导航完成后选中它（命令行 "exdir <文件路径>" 用）。
    /// </summary>
    public async Task<FolderTabViewModel> OpenInNewTabAsync(string path, string? selectPath = null)
    {
        var tab = CreateTab();
        await tab.NavigateAsync(path, selectPath: selectPath).ConfigureAwait(true);
        return tab;
    }

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private void CloseTab(FolderTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is not null)
        {
            RemoveTab(tab);
        }
    }

    /// <summary>第一个标签页就绪后调用，避免初始状态是空的。</summary>
    public async Task InitializeAsync(string path)
    {
        if (Tabs.Count == 0)
        {
            CreateTab();
        }

        if (ActiveTab is not null)
        {
            await ActiveTab.NavigateAsync(path).ConfigureAwait(true);
        }
    }

    private void OnTabNavigated(object? sender, string path) => Navigated?.Invoke(this, path);

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab) &&
            e.PropertyName is nameof(FolderTabViewModel.CurrentPath) or nameof(FolderTabViewModel.TabHeader))
        {
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(PaneTitle));
        }
    }
}
