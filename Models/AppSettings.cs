using System.Collections.Generic;
using Exdir.Helpers;

namespace Exdir.Models;

/// <summary>持久化到 <c>%USERPROFILE%\.config\exdir\config.json</c> 的应用设置。</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 11;

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

    // ---------- 侧边栏 ----------
    /// <summary>侧边栏「主目录」分组（主目录入口 + 桌面 / 文档 / 下载等标准文件夹）是否显示。</summary>
    public bool SidebarShowHome { get; set; } = true;

    /// <summary>侧边栏「收藏夹」分组（工具条固定目录的镜像，也是拖拽收藏的落点）是否显示。</summary>
    public bool SidebarShowFavorites { get; set; } = true;

    /// <summary>侧边栏「云存储」分组（OneDrive / WPS 等同步根）是否显示。</summary>
    public bool SidebarShowCloud { get; set; } = true;

    /// <summary>侧边栏「此电脑」分组（各磁盘）是否显示。</summary>
    public bool SidebarShowComputer { get; set; } = true;

    /// <summary>侧边栏「远程」分组（SFTP / FTP 位置）是否显示。</summary>
    public bool SidebarShowRemote { get; set; } = true;

    /// <summary>
    /// 侧边栏「最新访问」分组（最近导航过的目录，排在最上面）是否显示。
    /// 列表本身落盘在 <c>%USERPROFILE%\.local\share\exdir\recents.json</c>，不在 config.json 里。
    /// </summary>
    public bool SidebarShowRecent { get; set; } = true;

    // ---------- 侧边栏：主目录里显示哪些标准文件夹 ----------
    // 侧边栏「主目录」分组本身（SidebarShowHome）控制的是这个分组在不在；
    // 下面六个开关控制分组里有哪些子项。默认只开「桌面」与「下载」（其余默认关）。
    // 不用显示名匹配（系统语言会变），实际筛选按 UserFolderKey 做，见 SidebarViewModel.ApplyHomeFolders。

    /// <summary>「主目录」分组里是否显示“桌面”。</summary>
    public bool SidebarHomeDesktop { get; set; } = true;

    /// <summary>「主目录」分组里是否显示“文档”。</summary>
    public bool SidebarHomeDocuments { get; set; }

    /// <summary>「主目录」分组里是否显示“下载”。</summary>
    public bool SidebarHomeDownloads { get; set; } = true;

    /// <summary>「主目录」分组里是否显示“图片”。</summary>
    public bool SidebarHomePictures { get; set; }

    /// <summary>「主目录」分组里是否显示“音乐”。</summary>
    public bool SidebarHomeMusic { get; set; }

    /// <summary>「主目录」分组里是否显示“视频”。</summary>
    public bool SidebarHomeVideos { get; set; }

    // ---------- 启动 ----------

    /// <summary>
    /// 登录时自动启动 exdir（默认关）。
    /// 打开后会在 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 里登记一个
    /// <c>"&lt;exe&gt;" --preload</c> 的自启项：登录时启动的那一份**不显示主窗口**，
    /// 只把进程、上次的目录会话与首屏图标先备好，之后用户双击 exe 几乎是瞬时的。
    /// 见 <see cref="Exdir.Helpers.AutoStart" /> 与 AGENTS.md 第 4 节“开机自启 / 预热启动”。
    /// </summary>
    public bool StartWithWindows { get; set; }

    // ---------- 外观 ----------

    /// <summary>
    /// 应用主题：跟随系统（默认）/ 浅色 / 深色。
    /// 两个入口共用这一个值：标题栏左侧的太阳 / 月亮图标按钮（只在浅 / 深之间切）与
    /// 设置窗口「外观 → 主题」下拉框（三态）。视图侧的实际应用见 <c>MainWindow.ApplyTheme</c>。
    /// </summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    // ---------- 视图 ----------
    public bool ShowHiddenFiles { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public bool FoldersFirst { get; set; } = true;
    public bool ShowToolbar { get; set; } = true;

    /// <summary>文件列表是否播放过渡动画（换目录的入场、插行/排序时的重排动画）。</summary>
    public bool EnableListAnimations { get; set; } = true;

    /// <summary>
    /// 标签页（<c>TabView</c> 的标签头）是否用直角，默认 true。
    /// 关掉则回到 WinUI 默认的圆角（<c>OverlayCornerRadius</c> 只保留上面两个角）。
    /// 值由 <c>FolderTabViewModel.TabCornerRadius</c> 推给每个标签页，见 AGENTS.md 第 4 节“标签条”。
    /// </summary>
    public bool SquareTabCorners { get; set; } = true;

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

    // ---------- 压缩 ----------

    /// <summary>
    /// 右键「压缩」生成的 zip 保存到哪个目录（设置窗口「文件列表 → 压缩输出目录」）。
    /// 空（默认）= 用户的「下载」文件夹（按 <see cref="Exdir.Services.IKnownFolderService" /> 解析，用户重定向过也一样）；
    /// 非空时该目录不存在会自动建出来。
    /// </summary>
    public string CompressionOutputDirectory { get; set; } = string.Empty;

    // ---------- 远程位置（SFTP / FTP） ----------

    /// <summary>
    /// 用户配置的远程位置（SFTP / FTP / FTPS），显示在侧边栏「远程」分组里。
    /// 密码与私钥口令是 DPAPI 加密后的 Base64（见 <see cref="Exdir.Helpers.SecretProtector" />），
    /// config.json 里看不到明文。
    /// </summary>
    public List<RemoteLocation> RemoteLocations { get; set; } = new();

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
    /// 把系统右键菜单里的项（含 7-Zip / Git 这类第三方扩展）**平铺合并进内置菜单**：
    /// 内置菜单的末尾接一个分隔符，后面是这一批选中项在外壳里的菜单项（按规范动词与内置项去重，
    /// 子菜单（发送到 / 打开方式 / 新建……）原样保留下来）。
    ///
    /// 默认关（false）：读一遍系统菜单要把第三方 shell 扩展 Load 进本进程，弹出会明显变慢
    /// （与直接弹系统菜单同一个量级）。开启后按“作用域 + 选中项签名”缓存，同一目录反复右键不再重读，
    /// 详见 AGENTS.md 第 4 节“内置菜单里合并系统菜单项”。
    /// </summary>
    public bool BuiltInMenuIncludeShellItems { get; set; }

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
