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
    /// 云同步状态。只有位于云同步根之下的目录才会去读（见 <c>CloudSyncService</c>），
    /// 其它目录恒为 <see cref="CloudSyncState.None"/>，列表里不会显示状态图标。
    /// </summary>
    public CloudSyncState SyncState { get; init; }
}
