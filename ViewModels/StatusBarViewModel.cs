using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>
/// 文件列表区底部状态栏的 ViewModel。
///
/// 状态栏全窗口只有一条（跨 1~2 个窗格，见 MainWindow），所以这里自己盯着
/// <see cref="MainViewModel.ActivePane"/> → <see cref="PanelViewModel.ActiveTab"/> 这条链，
/// 换窗格 / 换标签页 / 换目录 / 改选中都会跟着刷新。
/// </summary>
public sealed class StatusBarViewModel : ObservableObject
{
    /// <summary>同一个卷的容量信息在这个时间窗内复用：连点几个目录不必每次都去问一次磁盘。</summary>
    private static readonly TimeSpan DriveCacheLifetime = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _main;
    private readonly IDriveService _driveService;

    private PanelViewModel? _pane;
    private FolderTabViewModel? _tab;

    /// <summary>磁盘信息是异步取的，用递增序号丢弃“已经导航走之后才回来”的过期结果。</summary>
    private int _driveQueryId;

    private string _cachedDriveRoot = string.Empty;
    private DriveModel? _cachedDrive;
    private DateTimeOffset _cachedDriveAt = DateTimeOffset.MinValue;

    private string _itemCountText = string.Empty;
    private string _selectionText = string.Empty;
    private string? _selectionTooltip;
    private string _driveText = string.Empty;

    public StatusBarViewModel(MainViewModel main, IDriveService driveService)
    {
        _main = main;
        _driveService = driveService;

        _main.PropertyChanged += OnMainPropertyChanged;
        AttachPane(_main.ActivePane);
    }

    /// <summary>当前目录的条目数，例如“12 项”。</summary>
    public string ItemCountText
    {
        get => _itemCountText;
        private set => SetProperty(ref _itemCountText, value);
    }

    /// <summary>选中摘要，例如“选中 2 项（合计 1.2 MB）”；没有选中时为空串。</summary>
    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    /// <summary>选中摘要的悬停提示（解释“合计”不含文件夹）；为空时不弹提示框。</summary>
    public string? SelectionTooltip
    {
        get => _selectionTooltip;
        private set => SetProperty(ref _selectionTooltip, value);
    }

    /// <summary>当前目录所在卷的容量，例如“D: 可用 120 GB / 共 512 GB”；取不到时为空串。</summary>
    public string DriveText
    {
        get => _driveText;
        private set => SetProperty(ref _driveText, value);
    }

    // ------------------------------------------------------------------ 数据源切换

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActivePane))
        {
            AttachPane(_main.ActivePane);
        }
    }

    private void AttachPane(PanelViewModel? pane)
    {
        if (!ReferenceEquals(_pane, pane))
        {
            if (_pane is not null)
            {
                _pane.PropertyChanged -= OnPanePropertyChanged;
            }

            _pane = pane;

            if (_pane is not null)
            {
                _pane.PropertyChanged += OnPanePropertyChanged;
            }
        }

        AttachTab(pane?.ActiveTab);
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.ActiveTab))
        {
            AttachTab(_pane?.ActiveTab);
        }
    }

    private void AttachTab(FolderTabViewModel? tab)
    {
        if (!ReferenceEquals(_tab, tab))
        {
            if (_tab is not null)
            {
                _tab.PropertyChanged -= OnTabPropertyChanged;
            }

            _tab = tab;

            if (_tab is not null)
            {
                _tab.PropertyChanged += OnTabPropertyChanged;
            }
        }

        RefreshAll();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FolderTabViewModel.ItemCount):
                UpdateItemCount();
                break;

            case nameof(FolderTabViewModel.Selection):
                UpdateSelection();
                break;

            case nameof(FolderTabViewModel.CurrentPath):
                UpdateDriveText();
                break;
        }
    }

    private void RefreshAll()
    {
        UpdateItemCount();
        UpdateSelection();
        UpdateDriveText();
    }

    // ------------------------------------------------------------------ 各段文本

    private void UpdateItemCount()
        => ItemCountText = _tab is null ? string.Empty : $"{_tab.ItemCount} 项";

    /// <summary>
    /// 选中摘要 + 合计大小。目录的大小要递归枚举才知道，这里不去算（选中一大堆目录时
    /// 会把磁盘拖住），所以只累加文件；全是目录时干脆不显示“合计”，并用悬停提示说明原因。
    /// </summary>
    private void UpdateSelection()
    {
        var selection = _tab?.Selection;
        if (selection is null || selection.Count == 0)
        {
            SelectionText = string.Empty;
            SelectionTooltip = null;
            return;
        }

        var folders = 0;
        long total = 0;
        foreach (var item in selection)
        {
            if (item.IsDirectory)
            {
                folders++;
            }
            else
            {
                total += item.Size;
            }
        }

        var text = new StringBuilder();
        text.Append($"选中 {selection.Count} 项");

        if (selection.Count == 1)
        {
            text.Append(folders == 0 ? $"（{SizeFormatter.Format(total)}）" : "（文件夹）");
        }
        else if (folders < selection.Count)
        {
            text.Append($"（合计 {SizeFormatter.Format(total)}）");
        }
        else
        {
            text.Append("（均为文件夹）");
        }

        SelectionText = text.ToString();
        SelectionTooltip = folders > 0
            ? "合计大小只统计选中的文件，文件夹不递归统计"
            : null;
    }

    private void UpdateDriveText()
    {
        var path = _tab?.CurrentPath;
        if (string.IsNullOrEmpty(path))
        {
            // “此电脑”这种没有路径的状态：清空并让在途查询失效
            _driveQueryId++;
            DriveText = string.Empty;
            return;
        }

        _ = UpdateDriveTextAsync(path);
    }

    private async Task UpdateDriveTextAsync(string path)
    {
        var id = ++_driveQueryId;

        var root = TryGetRoot(path);
        if (root is null)
        {
            DriveText = string.Empty;
            return;
        }

        if (string.Equals(root, _cachedDriveRoot, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.Now - _cachedDriveAt < DriveCacheLifetime)
        {
            DriveText = FormatDrive(_cachedDrive);
            return;
        }

        DriveModel? drive;
        try
        {
            // 读盘放到后台：断开的网络盘能让 GetDriveForPath 卡住好几秒
            drive = await Task.Run(() => _driveService.GetDriveForPath(path)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception("状态栏读取磁盘信息", ex);
            drive = null;
        }

        if (id != _driveQueryId)
        {
            return;
        }

        _cachedDriveRoot = root;
        _cachedDrive = drive;
        _cachedDriveAt = DateTimeOffset.Now;

        DriveText = FormatDrive(drive);
    }

    private static string? TryGetRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatDrive(DriveModel? drive)
        => drive is null || !drive.IsReady
            ? string.Empty
            : $"{drive.DisplayName} 可用 {SizeFormatter.Format(drive.FreeSpace)} / 共 {SizeFormatter.Format(drive.TotalSize)}";
}
