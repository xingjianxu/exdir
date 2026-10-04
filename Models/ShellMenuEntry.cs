using System.Collections.Generic;

namespace Exdir.Models;

/// <summary>
/// 一份可以直接渲染出来的系统右键菜单项（**运行时对象，不落盘**，所以不需要进
/// <c>SettingsJsonContext</c>）。
///
/// 它是 <c>IContextMenu.QueryContextMenu</c> 建出来的那张 HMENU 的“只读投影”：
/// 文本、子菜单、勾选 / 置灰状态都从 MENUITEMINFO 里读出来，<see cref="Offset" />
/// 是命令 id 减去 <c>idCmdFirst</c> 之后的偏移 —— 执行时用它交给
/// <c>IContextMenu::InvokeCommand</c>（见 <see cref="Services.ShellMenuSnapshot" />）。
///
/// 与 <see cref="ShellMenuItem" />（落盘在 config.json 里的“清单”，只记 Key / 文本 /
/// 作用域，供设置页的逐项开关用）是两回事：这里多了状态与偏移，是给渲染用的。
/// </summary>
public sealed class ShellMenuEntry
{
    /// <summary>菜单项的稳定标识（与 <see cref="ShellMenuItem.Key" /> 同一套：优先 <c>verb:xxx</c>）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>给人看的文本（已去掉 <c>&amp;</c> 加速键与结尾省略号）。分隔符为空。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>外壳给的规范动词（<c>open</c> / <c>7-Zip.Compress</c>…）；拿不到时为 null。</summary>
    public string? Verb { get; set; }

    /// <summary>所在子菜单的路径（顶级为空；<c>打开方式 › 打开方式</c> 这种用来记清单）。</summary>
    public string MenuPath { get; set; } = string.Empty;

    /// <summary>命令 id 偏移（id − idCmdFirst）。分隔符与子菜单项为 0。</summary>
    public uint Offset { get; set; }

    public bool IsSeparator { get; set; }

    /// <summary>外壳把这一项标成置灰（MFS_DISABLED / MFS_GRAYED）时为 false。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>MFS_CHECKED（“查看 → 大图标”这类单选勾）。</summary>
    public bool IsChecked { get; set; }

    /// <summary>MFS_DEFAULT：这一项是双击 / 回车的默认动词，渲染时加粗。</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// owner-draw 项（MFT_OWNERDRAW）：外壳自己画图标与文字，我们只能拿到文本（拿不到就跳过）。
    /// 记下来是为了写日志时能说清“少的那一项是什么货色”。
    /// </summary>
    public bool IsOwnerDraw { get; set; }

    /// <summary>子菜单项（<c>发送到</c> / <c>7-Zip</c> / <c>新建</c>…）里的项；空表示叶子项。</summary>
    public List<ShellMenuEntry> Children { get; } = new();
}
