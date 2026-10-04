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

    /// <summary>
    /// 只把这一批目标的菜单项**读出来**（不弹菜单），供 exdir 自己的 <c>MenuFlyout</c> 重画。
    ///
    /// 结果是进程内缓存的一份：键 = 作用域 + 选中项签名（所在目录 + 每项是不是目录 / 什么扩展名），
    /// 因为外壳给的内容本来就跟着**这一次的选中项**走（<c>.zip</c> 才有「解压到」、仓库里才有 Git 那几项、
    /// 目录背景与文件行不一样），一个“启动时定死的清单”拿去渲染别的选中项只会张冠李戴。
    /// 同一目录里反复右键同一类东西不重复读；<see cref="Preheat" /> 会把常见上下文先读一遍。
    ///
    /// 用户关掉的项（<see cref="IsDisabled" />）已经滤掉，分隔符也修整过。
    /// 拿不到窗口句柄 / 外壳拒绝时返回 null（调用方照旧只显示内置项）。
    /// </summary>
    ShellMenuSnapshot? GetMenuItems(IReadOnlyList<string> paths, bool isBackground);

    /// <summary>
    /// 按 <see cref="ShellMenuEntry.Offset" /> 执行某一项 —— 仍然交给外壳的 <c>InvokeCommand</c>，
    /// exdir 自己不解命令。
    /// </summary>
    /// <param name="snapshot">渲染时用的那一份（会话/ HMENU 必须还活着，所以缓存不会在菜单开着时淘汰它）。</param>
    bool InvokeMenuEntry(ShellMenuSnapshot snapshot, ShellMenuEntry entry, int screenX, int screenY);

    /// <summary>
    /// 预热：把常见上下文（%TEMP% 里的样本文件、配置目录本身 / 它的背景）先读一遍并缓存。
    /// 单纯一次 <c>QueryContextMenu</c> 就要把第三方 shell 扩展 Load 进本进程（几十~几百毫秒），
    /// 所以只应该在**预热启动**（<c>--preload</c>）这种不在用户等待路径上的时候调。
    /// </summary>
    void Preheat();
}
