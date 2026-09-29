using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exdir.Models;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>
/// 外壳主 ViewModel：磁盘条、固定目录、快捷菜单、侧边栏、窗格集合与全局命令。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IDriveService _driveService;
    private readonly IKnownFolderService _knownFolders;
    private readonly IFileSystemService _fileSystem;
    private readonly IShellService _shell;

    private PanelViewModel _activePane = null!;
    private bool _isDualPane;
    private bool _isToolbarVisible = true;
    private bool _isSidebarVisible = true;

    public MainViewModel(
        ISettingsService settings,
        IDriveService driveService,
        IKnownFolderService knownFolders,
        IFileSystemService fileSystem,
        IShellService shell)
    {
        _settings = settings;
        _driveService = driveService;
        _knownFolders = knownFolders;
        _fileSystem = fileSystem;
        _shell = shell;

        Sidebar = new SidebarViewModel(fileSystem, knownFolders, driveService);
        Sidebar.NavigateRequested += OnSidebarNavigateRequested;

        PrimaryPane = new PanelViewModel("primary", fileSystem, shell, settings);
        SecondaryPane = new PanelViewModel("secondary", fileSystem, shell, settings);

        PrimaryPane.Navigated += OnPaneNavigated;
        SecondaryPane.Navigated += OnPaneNavigated;

        PrimaryPane.ActivateRequested += OnPaneActivateRequested;
        SecondaryPane.ActivateRequested += OnPaneActivateRequested;

        ActivePane = PrimaryPane;

        ReloadDrives();
        LoadPinnedFolders();
        LoadQuickCommands();
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
        var path = ActivePane.ActiveTab?.CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (PinnedFolders.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        PinnedFolders.Add(new PinnedFolderViewModel(path));
        PersistPinnedFolders();
    }

    [RelayCommand]
    private void UnpinFolder(PinnedFolderViewModel? folder)
    {
        if (folder is null)
        {
            return;
        }

        PinnedFolders.Remove(folder);
        PersistPinnedFolders();
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

    /// <summary>磁盘热插拔后刷新磁盘条与侧边栏。</summary>
    [RelayCommand]
    public void RefreshDrives()
    {
        ReloadDrives();
        Sidebar.BuildTree();
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
    }

    private void ReloadDrives()
    {
        Drives.Clear();
        foreach (var drive in _driveService.GetDrives())
        {
            Drives.Add(drive);
        }
    }

    private void LoadPinnedFolders()
    {
        PinnedFolders.Clear();

        var configured = _settings.Current.PinnedFolders;
        if (configured.Count == 0)
        {
            configured = _knownFolders.GetDefaultPinnedFolders().ToList();
        }

        foreach (var path in configured)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                PinnedFolders.Add(new PinnedFolderViewModel(path));
            }
        }

        PersistPinnedFolders();
    }

    private void PersistPinnedFolders()
        => _settings.Current.PinnedFolders = PinnedFolders.Select(p => p.Path).ToList();

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
