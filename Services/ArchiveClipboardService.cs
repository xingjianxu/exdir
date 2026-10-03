using System.Collections.Generic;

namespace Exdir.Services;

/// <inheritdoc cref="IArchiveClipboardService" />
/// <remarks>
/// 状态只在 UI 线程上读写（命令、右键菜单、粘贴都在 UI 线程），所以不做加锁。
/// </remarks>
public sealed class ArchiveClipboardService : IArchiveClipboardService
{
    private ArchiveClipboardContent? _content;

    public bool HasEntries => _content is { InnerPaths.Count: > 0 };

    public ArchiveClipboardContent? Get() => HasEntries ? _content : null;

    public void Set(string archiveFile, IReadOnlyList<string> innerPaths)
    {
        if (string.IsNullOrWhiteSpace(archiveFile) || innerPaths is null || innerPaths.Count == 0)
        {
            Clear();
            return;
        }

        _content = new ArchiveClipboardContent(archiveFile, innerPaths);
    }

    public void Clear() => _content = null;
}
