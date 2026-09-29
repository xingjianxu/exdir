using System;
using System.Collections.Generic;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 系统右键菜单（外壳的 <c>IContextMenu</c>）：既能弹出真正的系统菜单（含第三方 shell 扩展），
/// 也能只把菜单项枚举出来供设置页使用。
///
/// 只能在 UI 线程上调用：<c>QueryContextMenu</c> / <c>TrackPopupMenuEx</c> 都要求 STA，
/// 而且需要窗口句柄当菜单宿主。弹菜单是同步阻塞的（走 TrackPopupMenu 自己的模态消息循环）。
/// </summary>
public interface IShellContextMenuService
{
    /// <summary>菜单宿主窗口句柄（<c>WindowNative.GetWindowHandle</c>）；为 0 时弹不出菜单。</summary>
    IntPtr OwnerWindow { get; set; }

    /// <summary>
    /// 设置页要显示的“系统右键菜单项”清单：样本目标（文本文件 / 文件夹 / 目录背景）枚举的结果
    /// 与用户实际右键过的项合并去重，并按“顶级菜单在前、同级按文本”排好序。
    /// 需要窗口句柄；枚举失败（例如外壳拒绝）时返回已经记下来的部分。
    /// </summary>
    IReadOnlyList<ShellMenuItem> GetCatalog();

    /// <summary>某一项是否已被用户在设置里关掉（弹出菜单时会被删掉）。</summary>
    bool IsDisabled(string key);

    /// <summary>
    /// 弹出系统右键菜单，并把用户在设置里关掉的项从菜单里删掉；
    /// 用户选中的项直接交给外壳执行（<c>InvokeCommand</c>），exdir 自己不执行任何命令。
    /// </summary>
    /// <param name="paths">选中项路径；<paramref name="isBackground" /> 为 true 时只取第一个，代表该目录的空白处。</param>
    /// <param name="isBackground">true = 当前目录的背景菜单（查看 / 排序方式 / 新建 / 粘贴……）。</param>
    /// <param name="screenX">弹出位置 X（屏幕物理像素）。</param>
    /// <param name="screenY">弹出位置 Y（屏幕物理像素）。</param>
    void Show(IReadOnlyList<string> paths, bool isBackground, int screenX, int screenY);
}
