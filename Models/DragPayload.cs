using System.Collections.Generic;

namespace Exdir.Models;

/// <summary>
/// 一次拖拽要交给系统的内容（由 <c>FolderTabViewModel.BuildDragPayloadAsync</c> 算出来）。
///
/// <para>
/// 正常情况就是选中项的**真实路径**；选中项里有压缩包内部条目 / 远程位置上的条目时，
/// 那些条目会先被解到 / 下到 <c>archive-cache\drag</c> 或 <c>remote-cache\drag</c> 下的临时目录，
/// 这里给的是拿到的**真实文件路径** —— 虚拟路径 / 远程路径既写不进 <c>CF_HDROP</c>、
/// 也没法交给别的程序（所以是“拖拽开始时先解出来 / 下下来”，见 AGENTS.md 第 6 节第 95 条）。
/// </para>
/// </summary>
/// <param name="Paths">交给外壳的真实路径（含从压缩包 / 远程位置取到的临时副本）。</param>
/// <param name="StagingRoots">临时副本的根目录（<c>archive-cache\drag\&lt;guid&gt;</c> /
/// <c>remote-cache\drag\&lt;guid&gt;</c>）；没有这两类条目时为空。</param>
public sealed record DragPayload(IReadOnlyList<string> Paths, IReadOnlyList<string> StagingRoots)
{
    /// <summary>这次拖拽里有没有“解出来 / 下下来的临时副本”。</summary>
    public bool FromArchive => StagingRoots.Count > 0;
}
