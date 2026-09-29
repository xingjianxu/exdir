using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Models;
using Exdir.Services;

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
    private readonly IShellContextMenuService _contextMenu;

    /// <summary>构建快照时被关掉的那些系统菜单项（用来给出初始开关状态）。</summary>
    private readonly HashSet<string> _shellMenuDisabled;

    private SettingsCategoryViewModel _selectedCategory;
    private bool _showHiddenFiles;
    private bool _showExtensions;
    private bool _foldersFirst;
    private bool _enableListAnimations;
    private bool _columnAutoFit;
    private bool _showToolbar;
    private bool _showSidebar;
    private bool _dualPane;

    /// <summary>按当前设置生成一份快照。</summary>
    public SettingsViewModel(AppSettings settings, IShellContextMenuService contextMenu)
    {
        _contextMenu = contextMenu;
        // 顺序即左侧导航的显示顺序
        Categories = new List<SettingsCategoryViewModel>
        {
            new(SettingsCategory.FileList, "文件列表"),
            new(SettingsCategory.Appearance, "外观"),
            new(SettingsCategory.Layout, "布局"),
            new(SettingsCategory.ShellMenu, "右键菜单"),
        };

        // 默认停在第一个分类，保证右侧永远有一页是可见的
        _selectedCategory = Categories[0];

        _shellMenuDisabled = new HashSet<string>(settings.ShellMenuDisabledItems, StringComparer.Ordinal);

        // 先用已经记下来的清单把页面填上（不碰 COM），再在对话框 Loaded 时用样本目标枚举一次补全
        RebuildShellMenuItems(settings.ShellMenuKnownItems);

        _showHiddenFiles = settings.ShowHiddenFiles;
        _showExtensions = settings.ShowExtensions;
        _foldersFirst = settings.FoldersFirst;
        _enableListAnimations = settings.EnableListAnimations;
        _columnAutoFit = settings.ColumnAutoFit;
        _showToolbar = settings.ShowToolbar;
        _showSidebar = settings.IsSidebarVisible;
        _dualPane = settings.IsDualPane;
    }

    // ------------------------------------------------------------------ 分类导航

    /// <summary>左侧导航的配置大类（顺序即显示顺序）。</summary>
    public IReadOnlyList<SettingsCategoryViewModel> Categories { get; }

    /// <summary>
    /// 当前选中的配置大类，右侧只显示它对应的那一页。
    /// 绑到左侧 <c>ListView.SelectedItem</c>（TwoWay）；ListView 在重建选择时可能推 null 过来，
    /// 这里直接忽略，保证永远有一项是选中的。
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

            // 三个页面各自绑一个 bool（比让 XAML 去比枚举省事，也不用给每个页面写转换器参数）
            OnPropertyChanged(nameof(IsFileListPageVisible));
            OnPropertyChanged(nameof(IsAppearancePageVisible));
            OnPropertyChanged(nameof(IsLayoutPageVisible));
            OnPropertyChanged(nameof(IsShellMenuPageVisible));
        }
    }

    /// <summary>右侧是否显示「文件列表」页。</summary>
    public bool IsFileListPageVisible => _selectedCategory.Key == SettingsCategory.FileList;

    /// <summary>右侧是否显示「外观」页。</summary>
    public bool IsAppearancePageVisible => _selectedCategory.Key == SettingsCategory.Appearance;

    /// <summary>右侧是否显示「布局」页。</summary>
    public bool IsLayoutPageVisible => _selectedCategory.Key == SettingsCategory.Layout;

    /// <summary>右侧是否显示「右键菜单」页。</summary>
    public bool IsShellMenuPageVisible => _selectedCategory.Key == SettingsCategory.ShellMenu;

    // ------------------------------------------------------------------ 右键菜单

    /// <summary>
    /// 系统右键菜单项清单（含第三方扩展）。顺序：顶级菜单在前，然后按“子菜单路径 + 文本”排。
    /// 每项的开关就是“是否保留在右键菜单里”，默认全开。
    /// </summary>
    public ObservableCollection<ShellMenuItemViewModel> ShellMenuItems { get; } = new();

    /// <summary>清单是不是空的（第一次运行时枚举还没回来），用来显示“正在读取…”。</summary>
    public bool HasShellMenuItems => ShellMenuItems.Count > 0;

    /// <summary>
    /// 用样本目标（文本文件 / 文件夹 / 目录背景）现枚举一次系统菜单，并把结果并进页面。
    /// 由对话框在 Loaded 之后调用：枚举要建 COM 对象、耗时几十到几百毫秒，不能在构造里做。
    /// </summary>
    public void RefreshShellMenuItems()
    {
        try
        {
            RebuildShellMenuItems(_contextMenu.GetCatalog());
        }
        catch (Exception)
        {
            // 枚举失败就保留构造时用已记下来的那份清单，不影响对话框保存
        }
    }

    /// <summary>
    /// 重建清单：已有的项保留用户刚拨过的开关（Refresh 时不会把用户的改动冲掉），
    /// 没见过的项按“设置里被关掉的集合”给默认值。
    /// </summary>
    private void RebuildShellMenuItems(IEnumerable<ShellMenuItem> items)
    {
        // 用索引器而不是 ToDictionary：万一 settings.json 被手改出重复 key，也不能把对话框开崩
        var previous = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var existing in ShellMenuItems)
        {
            previous[existing.Item.Key] = existing.IsEnabled;
        }

        var usedTitles = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        ShellMenuItems.Clear();

        foreach (var item in items)
        {
            var enabled = previous.TryGetValue(item.Key, out var current)
                ? current
                : !_shellMenuDisabled.Contains(item.Key);

            ShellMenuItems.Add(new ShellMenuItemViewModel(item, MakeUniqueTitle(item, usedTitles), enabled));
        }

        OnPropertyChanged(nameof(HasShellMenuItems));
    }

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

    // ------------------------------------------------------------------ 外观

    /// <summary>列表过渡动画（换目录入场 / 插行重排）。</summary>
    public bool EnableListAnimations
    {
        get => _enableListAnimations;
        set => SetProperty(ref _enableListAnimations, value);
    }

    // ------------------------------------------------------------------ 布局

    /// <summary>列宽自动适应窗格宽度（关掉 = 固定列宽 + 横向滚动）。</summary>
    public bool ColumnAutoFit
    {
        get => _columnAutoFit;
        set => SetProperty(ref _columnAutoFit, value);
    }

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
