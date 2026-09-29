using System.Collections.Generic;

namespace Exdir.Models;

/// <summary>持久化到 <c>%LOCALAPPDATA%\exdir\settings.json</c> 的应用设置。</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

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

    /// <summary>详细信息列表的列宽（名称/修改日期/类型/大小），单位 DIP；空表示用默认值。</summary>
    public List<double> ColumnWidths { get; set; } = new();

    /// <summary>名称列是否自动填满窗格剩余宽度（用户手动拖过名称列后为 false）。</summary>
    public bool ColumnAutoFillName { get; set; } = true;

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

    /// <summary>快捷菜单中的自定义命令。</summary>
    public List<QuickCommand> QuickCommands { get; set; } = new();
}
