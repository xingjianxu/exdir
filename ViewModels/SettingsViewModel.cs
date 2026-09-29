using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Models;

namespace Exdir.ViewModels;

/// <summary>
/// 设置对话框的编辑副本（当前所有用户可配项的快照）。
///
/// 为什么是副本而不是直接绑 <see cref="AppSettings" />：对话框底部有“取消”。
/// 直接改 <see cref="AppSettings" /> 的话，取消就得逐项回滚，还要记住“哪些项被动过”；
/// 而这里只在点“保存”时由 <see cref="MainViewModel.ApplySettings" /> 一次性写回，
/// 点“取消”自然就是“什么都不做”（连落盘都不会发生）。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private bool _showHiddenFiles;
    private bool _showExtensions;
    private bool _foldersFirst;
    private bool _enableListAnimations;
    private bool _columnAutoFit;
    private bool _showToolbar;
    private bool _showSidebar;
    private bool _dualPane;

    /// <summary>按当前设置生成一份快照。</summary>
    public SettingsViewModel(AppSettings settings)
    {
        _showHiddenFiles = settings.ShowHiddenFiles;
        _showExtensions = settings.ShowExtensions;
        _foldersFirst = settings.FoldersFirst;
        _enableListAnimations = settings.EnableListAnimations;
        _columnAutoFit = settings.ColumnAutoFit;
        _showToolbar = settings.ShowToolbar;
        _showSidebar = settings.IsSidebarVisible;
        _dualPane = settings.IsDualPane;
    }

    // ------------------------------------------------------------------ 文件列表

    /// <summary>显示隐藏文件与系统文件。</summary>
    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        set => SetProperty(ref _showHiddenFiles, value);
    }

    /// <summary>显示文件扩展名。</summary>
    public bool ShowExtensions
    {
        get => _showExtensions;
        set => SetProperty(ref _showExtensions, value);
    }

    /// <summary>文件夹排在文件前面。</summary>
    public bool FoldersFirst
    {
        get => _foldersFirst;
        set => SetProperty(ref _foldersFirst, value);
    }

    /// <summary>列表过渡动画（换目录入场 / 插行重排）。</summary>
    public bool EnableListAnimations
    {
        get => _enableListAnimations;
        set => SetProperty(ref _enableListAnimations, value);
    }

    /// <summary>列宽自动适应窗格宽度（关掉 = 固定列宽 + 横向滚动）。</summary>
    public bool ColumnAutoFit
    {
        get => _columnAutoFit;
        set => SetProperty(ref _columnAutoFit, value);
    }

    // ------------------------------------------------------------------ 界面

    /// <summary>显示工具条。</summary>
    public bool ShowToolbar
    {
        get => _showToolbar;
        set => SetProperty(ref _showToolbar, value);
    }

    /// <summary>显示侧边栏文件夹树。</summary>
    public bool ShowSidebar
    {
        get => _showSidebar;
        set => SetProperty(ref _showSidebar, value);
    }

    /// <summary>双窗格模式。</summary>
    public bool DualPane
    {
        get => _dualPane;
        set => SetProperty(ref _dualPane, value);
    }
}
