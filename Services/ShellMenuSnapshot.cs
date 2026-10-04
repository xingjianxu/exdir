using System;
using System.Collections.Generic;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 一次“读出系统右键菜单项”的结果：可以直接渲染的项树 + 它背后的
/// HMENU / <c>IContextMenu</c> 会话（<see cref="Session" />）。
///
/// 会话必须留着 —— 外壳分配的命令 id（= 我们渲染时用的 <see cref="ShellMenuEntry.Offset" />）
/// 只在**那一张 HMENU** 上有意义，执行子菜单项这类没有规范动词的项只能按偏移交给同一个
/// <c>IContextMenu</c>（见 <c>ShellContextMenuService.InvokeMenuEntry</c>）。
///
/// 生命周期归 <c>ShellContextMenuService</c> 的缓存：它按 LRU 淘汰并调 <see cref="Dispose" />。
/// 视图层只管拿它渲染、点的时候把偏移交回去，**不要**自己 Dispose（会把菜单从缓存里“抽走”）。
/// </summary>
public sealed class ShellMenuSnapshot : IDisposable
{
    public ShellMenuSnapshot(string signature, string scope, IReadOnlyList<ShellMenuEntry> items)
    {
        Signature = signature;
        Scope = scope;
        Items = items;
    }

    /// <summary>缓存键：作用域 + 选中项签名（见 <c>ShellContextMenuService.BuildSignature</c>）。</summary>
    public string Signature { get; }

    /// <summary>这次会话是哪个上下文（文件 / 文件夹 / 背景，见 <see cref="ShellMenuItem" /> 里的常量）。</summary>
    public string Scope { get; }

    /// <summary>顶级项（已去掉分隔符首尾与重复、已滤掉用户关掉的项）。</summary>
    public IReadOnlyList<ShellMenuEntry> Items { get; }

    /// <summary>最近一次被取用的时间（LRU 淘汰用）。</summary>
    internal long LastUsedTicks { get; set; }

    /// <summary>背后那次 <c>Open()</c> 的产物（HMENU + IContextMenu + pidl）；偏移执行必需。</summary>
    internal IDisposable? Session { get; set; }

    public void Dispose()
    {
        Session?.Dispose();
        Session = null;
    }
}
