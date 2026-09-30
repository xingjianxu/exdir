using System.Collections.Generic;
using Exdir.Helpers;

namespace Exdir.Models;

/// <summary>持久化到 <c>%LOCALAPPDATA%\exdir\settings.json</c> 的应用设置。</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // ---------- 窗口 ----------
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    // ---------- 布局 ----------
    public bool IsDualPane { get; set; }
    public double SidebarWidth { get; set; } = 232;
    public bool IsSidebarVisible { get; set; } = true;
    public double? PrimaryPaneWidth { get; set; }

    // ---------- 视图 ----------
    public bool ShowHiddenFiles { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public bool FoldersFirst { get; set; } = true;
    public bool ShowToolbar { get; set; } = true;

    /// <summary>文件列表是否播放过渡动画（换目录的入场、插行/排序时的重排动画）。</summary>
    public bool EnableListAnimations { get; set; } = true;

    /// <summary>
    /// 详细信息列表的列宽（状态/名称/修改日期/类型/大小），单位 DIP；空表示用默认值。
    /// 结构版本 1 时只有后 4 列，读取时会自动在最前面补上状态列的宽度（见 SettingsService.Migrate）。
    /// </summary>
    public List<double> ColumnWidths { get; set; } = new();

    /// <summary>名称列是否自动填满窗格剩余宽度（用户手动拖过名称列后为 false）。</summary>
    public bool ColumnAutoFillName { get; set; } = true;

    /// <summary>
    /// 文件列表每一行的行高（DIP），可在设置窗口「文件列表 → 行高」里调。
    /// 图标与名称在行内仍然垂直居中，行高只改变上下留白；
    /// 取值范围与夹取逻辑见 <see cref="ColumnLayout.NormalizeRowHeight" />。
    /// </summary>
    public double RowHeight { get; set; } = ColumnLayout.DefaultRowHeight;

    /// <summary>列宽是否整体自适应窗格宽度（用户拖动过列宽后为 false）。</summary>
    public bool ColumnAutoFit { get; set; } = true;

    // ---------- 会话 ----------
    /// <summary>左（主）窗格打开的标签页路径。</summary>
    public List<string> PrimaryTabs { get; set; } = new();

    /// <summary>左（主）窗格的活动标签索引。</summary>
    public int PrimaryActiveTab { get; set; }

    public List<string> SecondaryTabs { get; set; } = new();

    public int SecondaryActiveTab { get; set; }

    // ---------- 收藏与快捷菜单 ----------
    /// <summary>title 栏上固定显示的常用目录。</summary>
    public List<string> PinnedFolders { get; set; } = new();

    /// <summary>
    /// 用户是否已经拥有自己的固定目录列表。
    /// 为 false（从未配置过）时首次启动会填入默认值；用户把所有固定目录都删掉后
    /// 这个标志仍为 true，下次启动就不会把默认值又塞回来。
    /// </summary>
    public bool PinnedFoldersInitialized { get; set; }

    /// <summary>快捷菜单中的自定义命令。</summary>
    public List<QuickCommand> QuickCommands { get; set; } = new();

    // ---------- 右键菜单 ----------

    /// <summary>
    /// 文件列表的右键菜单用哪一种：
    /// true（默认）= exdir 自己用 WinUI <c>MenuFlyout</c> 现搭一份轻量菜单，弹出几乎瞬时，
    ///               但内容只含 exdir 自己实现的那几个命令；
    /// false = 现场向系统外壳要 <c>IContextMenu</c>（内容完整，含 7-Zip / Git 这类第三方扩展，
    ///         但要建 COM 对象、枚举菜单，弹出明显慢）。
    /// 详见 AGENTS.md 第 4 节“右键菜单”。
    /// </summary>
    public bool UseBuiltInContextMenu { get; set; } = true;

    /// <summary>
    /// 已经在设置里见过、可以逐项开关的系统右键菜单项。
    /// 用户在设置页里看到的就是这份清单（再并上打开设置页时用样本目标现枚举出来的那些）。
    /// </summary>
    public List<ShellMenuItem> ShellMenuKnownItems { get; set; } = new();

    /// <summary>
    /// 被关掉的系统右键菜单项（存的是 <see cref="ShellMenuItem.Key" />）：
    /// 弹出系统菜单前会把这些项从 HMENU 里删掉。空列表 = 全部开启（默认）。
    /// </summary>
    public List<string> ShellMenuDisabledItems { get; set; } = new();
}
