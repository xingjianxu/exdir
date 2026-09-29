using System.Collections.Generic;

namespace Exdir.Models;

/// <summary>
/// 系统右键菜单里的一项（设置页「右键菜单」清单的一行）。
///
/// 系统菜单项是随右键目标变化的（例如 .zip 才会有“解压”、图片才有“设为桌面背景”），
/// 所以 exdir 两头收集：打开设置页时用样本目标（文本文件 / 文件夹 / 目录背景）主动枚举一次，
/// 平时每次真的弹出菜单时也把见到的项记下来，两份合并去重后就是清单。
/// 用户关掉的项只记 <see cref="Key" />，弹出菜单前从 HMENU 里删掉。
/// </summary>
public sealed class ShellMenuItem
{
    /// <summary>文件上下文（右键某个文件）。</summary>
    public const string FileScope = "文件";

    /// <summary>文件夹上下文（右键某个文件夹）。</summary>
    public const string FolderScope = "文件夹";

    /// <summary>目录背景（在空白处右键）。</summary>
    public const string BackgroundScope = "背景";

    /// <summary>
    /// 稳定标识：优先用外壳给的规范动词（<c>IContextMenu::GetCommandString(GCS_VERBW)</c>，
    /// 例如 <c>open</c>、<c>7-Zip.Compress</c>），拿不到动词的项退回“上级菜单文本 + 菜单文本”。
    /// 用动词而不是菜单文本，是为了让“打开”这类项在文件 / 文件夹 / 背景三个上下文里共用同一个开关。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>菜单文本（已去掉 <c>&amp;</c> 加速键与结尾省略号）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>所在子菜单的文本路径（顶级菜单为空串，子菜单项形如 <c>7-Zip › 添加到压缩文件</c> 的上级部分）。</summary>
    public string MenuPath { get; set; } = string.Empty;

    /// <summary>见过它的上下文（<see cref="FileScope" /> / <see cref="FolderScope" /> / <see cref="BackgroundScope" />）。</summary>
    public List<string> Scopes { get; set; } = new();
}
