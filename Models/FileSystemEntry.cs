namespace Exdir.Models;

/// <summary>把一个文件系统条目（文件或目录）的元数据从 I/O 层传递到 UI 层。</summary>
public sealed class FileSystemEntry
{
    public required string FullPath { get; init; }

    public required string Name { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>文件字节数；目录为 0。</summary>
    public long Size { get; init; }

    public DateTimeOffset LastWriteTime { get; init; }

    public DateTimeOffset CreationTime { get; init; }

    /// <summary>人类可读的类型名，例如“文件夹”“文本文档”。</summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>是否隐藏或系统项。</summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// 这个条目是一个“可浏览的压缩包文件”（核心扩展名 + 7z.dll 认识 + 文件真的存在）：
    /// 双击进包，也能像目录一样在文件列表里就地展开（见 AGENTS.md 第 4 节）。
    /// </summary>
    public bool IsArchive { get; init; }

    /// <summary>
    /// 这个条目位于压缩包内部（虚拟路径）：磁盘上没有这个文件，所有写操作都要拒绝。
    /// 与 <see cref="IsArchive" /> 的区别：包内的嵌套压缩包也带这个标记，但不是独立的压缩包文件
    /// （<c>ArchivePath.TryParse</c> 只认最外层那个包）。
    /// </summary>
    public bool IsInArchive { get; init; }

    /// <summary>
    /// 云同步状态。只有位于云同步根之下的目录才会去读（见 <c>CloudSyncService</c>），
    /// 其它目录恒为 <see cref="CloudSyncState.None"/>，列表里不会显示状态图标。
    /// </summary>
    public CloudSyncState SyncState { get; init; }
}
