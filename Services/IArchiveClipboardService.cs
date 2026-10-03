using System.Collections.Generic;

namespace Exdir.Services;

/// <summary>
/// 内存里记着的“压缩包内被复制的一批条目”。
/// 包内条目没有真实路径，写不进系统剪贴板的 <c>CF_HDROP</c>，所以只能记在这里，
/// 等用户在真实目录里按 Ctrl+V 时再解出来（见 <see cref="IArchiveService.ExtractForCopyAsync" />）。
/// </summary>
/// <param name="ArchiveFile">压缩包文件的完整路径。</param>
/// <param name="InnerPaths">包内相对路径（<c>\</c> 分隔；目录表示整棵子树）。</param>
public sealed record ArchiveClipboardContent(string ArchiveFile, IReadOnlyList<string> InnerPaths);

/// <summary>
/// 压缩包剪贴板（内存）：在包内 Ctrl+C / 右键「复制」时记下这批包内路径，
/// 在真实目录 Ctrl+V 时解出来复制过去。
///
/// <para>
/// 它与系统剪贴板是**互斥**的两份内容，否则粘贴会拿到两份：
/// 复制包内条目时清空系统剪贴板；复制 / 剪切真实文件时清空这里
/// （见 <c>FolderTabViewModel.CopySelection</c> / <c>CutSelection</c>）。
/// 粘贴时优先看系统剪贴板 —— 包内复制会清空它，所以它非空就一定比这里更新
/// （例如：包内复制 → 在资源管理器里复制 → 回到 exdir 粘贴，应该粘资源管理器那批）。
/// </para>
/// </summary>
public interface IArchiveClipboardService
{
    /// <summary>现在有没有包内条目（内置菜单据此决定「粘贴」能不能点）。</summary>
    bool HasEntries { get; }

    /// <summary>读出当前内容；没有时返回 null。</summary>
    ArchiveClipboardContent? Get();

    /// <summary>记下一批包内条目（覆盖上一次的内容）。</summary>
    void Set(string archiveFile, IReadOnlyList<string> innerPaths);

    /// <summary>清掉（复制真实文件时调；包内复制也会先清系统剪贴板）。</summary>
    void Clear();
}
