namespace Exdir.Helpers;

/// <summary>
/// 内置右键菜单里 <b>exdir 自己那些项</b>用的 Segoe Fluent Icons 字形（由
/// <c>Views/DetailsView.AddContextMenuItem</c> 变成 <c>FontIcon</c>，大小 14 DIP —— 与文件列表的
/// 字形占位一致）。
///
/// 为什么用字形而不是外壳图标：剪切 / 复制 / 粘贴 / 删除 / 属性这些标准动词，系统菜单本来就不给图标
/// （<c>MENUITEMINFO.hbmpItem</c> 是空的，只有“打开”与第三方扩展的项才有），拿不到现成的；
/// 而单色字形正好与 Windows 11 自带的菜单风格一致。
/// **系统菜单项那一份图标走另一条路**（<c>ShellMenuEntry.Icon</c>，从 HMENU 的 hbmpItem 抄下来的真图标，
/// 见 AGENTS.md 第 4 节“内置菜单里合并系统菜单项”）。
/// </summary>
public static class MenuGlyphs
{
    public const string Open = "\uE8E5";                 // OpenFile：带箭头的文件
    public const string OpenContainingFolder = "\uE838"; // FolderOpen：打开的文件夹
    public const string RevealInExplorer = "\uE8DE";     // 带箭头徽标的文件夹（“去别处打开”）
    public const string OpenWith = "\uE8A7";             // 方框 + 斜向箭头：交给别的程序打开
    public const string Download = "\uE896";             // Download：下到本地 / 解压到「下载」
    public const string Compress = "\uF012";             // ZipFolder
    public const string Cut = "\uE8C6";                  // Cut：剪刀
    public const string Copy = "\uE8C8";                 // Copy：两页纸
    public const string Paste = "\uE77F";                // Paste：剪贴板
    public const string Delete = "\uE74D";               // Delete：垃圾桶
    public const string CopyPath = "\uE71B";             // Link：地址 / 路径
    public const string Properties = "\uE946";           // Info：属性对话框
    public const string NewFolder = "\uE8F4";            // 文件夹 + 加号
    public const string Refresh = "\uE72C";              // Refresh
    public const string SelectAll = "\uE8B3";            // 四个选择框：全选
    public const string Terminal = "\uE756";             // CommandPrompt：窗口 + 提示符
}
