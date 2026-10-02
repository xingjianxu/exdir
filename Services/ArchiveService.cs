using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services.Native;

namespace Exdir.Services;

/// <inheritdoc cref="IArchiveService" />
public sealed class ArchiveService : IArchiveService
{
    /// <summary>索引缓存条数上限（每个压缩包一份条目清单，几十兆的 tar 也不小）。</summary>
    private const int MaxCachedIndexes = 32;

    /// <summary>
    /// 链式解开的中间 tar 超过这个大小就不落地了（改成显示 <c>xxx.tar</c> 一行）。
    /// 中间 tar 是**解压后**的大小，一个 1 GB 的 tar.gz 会占 1 GB 临时空间。
    /// </summary>
    private const long MaxChainedTarBytes = 2L << 30;

    private readonly ConcurrentDictionary<string, string?> _passwords = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, ArchiveIndex> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Dictionary<string, List<SevenZipHandler>> _handlersByExtension = new(StringComparer.OrdinalIgnoreCase);

    private string? _tempRoot;

    public ArchiveService()
    {
        foreach (var handler in SevenZipInterop.Handlers)
        {
            foreach (var extension in handler.Extensions)
            {
                if (!_handlersByExtension.TryGetValue(extension, out var list))
                {
                    _handlersByExtension[extension] = list = new List<SevenZipHandler>();
                }

                if (!list.Contains(handler))
                {
                    list.Add(handler);
                }
            }
        }

        if (SevenZipInterop.IsAvailable)
        {
            Log.Write($"压缩包浏览：7z.dll 可用，共 {SevenZipInterop.Handlers.Count} 种格式");
        }
        else
        {
            Log.Write($"压缩包浏览：不可用（{SevenZipInterop.FailureReason}），双击压缩包仍交给默认程序");
        }
    }

    public bool IsAvailable => SevenZipInterop.IsAvailable;

    public string AvailabilityFailure => SevenZipInterop.FailureReason;

    // ------------------------------------------------------------------ 路径

    public bool IsArchiveFile(string? path)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = GetExtensionOf(path);
        if (!ArchiveFormats.IsCoreExtension(extension) || ResolveHandlers(extension).Count == 0)
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool IsInsideArchive(string? path) => TryParse(path, out _);

    public bool TryParse(string? path, out ArchivePath location)
    {
        location = null!;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        var trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar);

        // "D:\" 这种根没有可解释的部分
        if (trimmed.Length == 0)
        {
            return false;
        }

        // 路径本身就是压缩包文件
        if (IsArchiveFile(trimmed))
        {
            location = ArchivePath.Create(trimmed, string.Empty);
            return true;
        }

        // 从左往右找第一个“既存在又是压缩包”的前缀 —— 它就是这个位置所属的压缩包。
        // 取最外层那个：D:\a.zip\b.zip\c 里的 b.zip 是包内条目，不是独立的压缩包文件。
        var start = 0;

        while (true)
        {
            var separator = trimmed.IndexOf(Path.DirectorySeparatorChar, start);
            if (separator < 0)
            {
                return false;
            }

            var prefix = trimmed[..separator];

            // "D:" 这种前缀不可能是一个文件（File.Exists 也返回 false），顺手挡掉
            if (prefix.Length > 1 && IsArchiveFile(prefix))
            {
                var inner = ArchivePath.NormalizeInner(trimmed[(separator + 1)..]);
                if (inner is null)
                {
                    return false;
                }

                location = ArchivePath.Create(prefix, inner);
                return true;
            }

            start = separator + 1;
            if (start >= trimmed.Length)
            {
                return false;
            }
        }
    }

    /// <summary>取扩展名（带点，小写）；<c>foo.tar.gz</c> 只取最后一个。</summary>
    private static string GetExtensionOf(string path)
    {
        try
        {
            return Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>某个扩展名对应的 7z.dll 处理器（可能有多个，例如 <c>.rar</c> 同时挂在 Rar 与 Rar5 上）。</summary>
    private List<SevenZipHandler> ResolveHandlers(string extension)
    {
        var canonical = ArchiveFormats.ToCanonicalExtension(extension);

        if (_handlersByExtension.TryGetValue(canonical, out var list))
        {
            return list;
        }

        return _handlersByExtension.TryGetValue(extension, out var direct) ? direct : new List<SevenZipHandler>();
    }

    // ------------------------------------------------------------------ 枚举

    public async Task<IReadOnlyList<FileSystemEntry>> ListAsync(
        ArchivePath location,
        CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(location.ArchiveFile, cancellationToken).ConfigureAwait(true);

        if (!index.Children.TryGetValue(location.InnerPath, out var children) || children.Count == 0)
        {
            return Array.Empty<FileSystemEntry>();
        }

        var result = new List<FileSystemEntry>(children.Count);
        foreach (var node in children)
        {
            result.Add(CreateEntry(location.ArchiveFile, node));
        }

        return result;
    }

    public async Task<bool> DirectoryExistsAsync(
        ArchivePath location,
        CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(location.ArchiveFile, cancellationToken).ConfigureAwait(true);

        if (location.IsRoot)
        {
            return true;
        }

        return index.ByPath.TryGetValue(location.InnerPath, out var node) && node.IsDirectory;
    }

    private static FileSystemEntry CreateEntry(string archiveFile, ArchiveNode node) => new()
    {
        FullPath = node.InnerPath.Length == 0
            ? archiveFile
            : archiveFile + Path.DirectorySeparatorChar + node.InnerPath,
        Name = node.Name,
        IsDirectory = node.IsDirectory,
        Size = node.Size,
        LastWriteTime = node.LastWrite,
        CreationTime = node.LastWrite,
        TypeName = FileTypeHelper.GetTypeName(node.Name, node.IsDirectory),

        // 压缩包条目没有 Windows 的隐藏属性，也不参与云同步状态
        IsHidden = false,
        SyncState = CloudSyncState.None,
    };

    // ------------------------------------------------------------------ 解出单个文件（双击打开用）

    public async Task<string> ExtractToTempAsync(
        ArchivePath file,
        CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(file.ArchiveFile, cancellationToken).ConfigureAwait(true);

        if (!index.ByPath.TryGetValue(file.InnerPath, out var node)
            || node.IsDirectory
            || node.ArchiveIndex is not uint archiveIndex)
        {
            throw new ArchiveOpenException($"压缩包里没有这个文件：{file.InnerPath}");
        }

        var target = Path.Combine(
            TempRoot,
            "open",
            HashOf(file.ArchiveFile) + "-" + SanitizeName(Path.GetFileName(file.ArchiveFile)),
            ArchivePath.ToFileSystemPath(file.InnerPath));

        // 同一个条目重复打开就直接用已经解出来的那份（大小对得上就算同一份）
        if (File.Exists(target) && new FileInfo(target).Length == node.Size)
        {
            return target;
        }

        var password = GetPassword(file.ArchiveFile);

        var opened = await Task.Run(() =>
        {
            var archive = SevenZipInterop.TryOpen(index.SourceFile, index.SourceClassId, password, cancellationToken, out var openFailure);
            return (Archive: archive, Failure: openFailure);
        }, cancellationToken).ConfigureAwait(true);

        if (opened.Archive is null)
        {
            ThrowOpenFailure(file.ArchiveFile, opened.Failure);
        }

        using (opened.Archive!)
        {
            var extractFailure = string.Empty;
            var ok = await Task.Run(
                () => opened.Archive!.ExtractToFile(archiveIndex, target, password, cancellationToken, out extractFailure),
                cancellationToken).ConfigureAwait(true);

            if (!ok)
            {
                ThrowOpenFailure(file.ArchiveFile, extractFailure);
            }
        }

        Log.Write($"打开压缩包内文件：{file.ArchiveFile} :: {file.InnerPath} → {target}");
        return target;
    }

    private static void ThrowOpenFailure(string archiveFile, string? failure)
    {
        if (!string.IsNullOrEmpty(failure) && failure.Contains("密码", StringComparison.Ordinal))
        {
            throw new ArchivePasswordRequiredException(archiveFile);
        }

        throw new ArchiveOpenException(string.IsNullOrEmpty(failure)
            ? $"无法读取压缩包：{archiveFile}"
            : failure);
    }

    // ------------------------------------------------------------------ 索引缓存

    private sealed record ArchiveNode(
        string InnerPath,
        string Name,
        bool IsDirectory,
        long Size,
        DateTimeOffset LastWrite,
        uint? ArchiveIndex);

    private sealed class ArchiveIndex
    {
        public required string ArchiveFile { get; init; }

        public required long Length { get; init; }

        public required DateTime LastWriteUtc { get; init; }

        /// <summary>真正被打开的那个文件（链式解开 <c>.tar.gz</c> 时是落地的临时 tar）。</summary>
        public required string SourceFile { get; init; }

        public required Guid SourceClassId { get; init; }

        public required Dictionary<string, ArchiveNode> ByPath { get; init; }

        public required Dictionary<string, List<ArchiveNode>> Children { get; init; }

        /// <summary>链式解开时落地的临时 tar（清理时删）。</summary>
        public string? MaterializedTar { get; init; }
    }

    private async Task<ArchiveIndex> GetIndexAsync(string archiveFile, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(archiveFile, out var cached) && IsUpToDate(cached))
            {
                return cached;
            }
        }

        var gate = GetBuildLock(archiveFile);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(archiveFile, out var cached) && IsUpToDate(cached))
                {
                    return cached;
                }
            }

            var built = await Task.Run(() => BuildIndex(archiveFile, cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(archiveFile, out var old))
                {
                    DeleteMaterialized(old);
                }

                _cache[archiveFile] = built;
                _cacheOrder.Enqueue(archiveFile);

                while (_cacheOrder.Count > MaxCachedIndexes)
                {
                    var victim = _cacheOrder.Dequeue();
                    if (string.Equals(victim, archiveFile, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (_cache.Remove(victim, out var dropped))
                    {
                        DeleteMaterialized(dropped);
                    }
                }
            }

            return built;
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetBuildLock(string archiveFile)
    {
        lock (_cacheLock)
        {
            if (!_buildLocks.TryGetValue(archiveFile, out var gate))
            {
                _buildLocks[archiveFile] = gate = new SemaphoreSlim(1, 1);
            }

            return gate;
        }
    }

    private static bool IsUpToDate(ArchiveIndex index)
    {
        try
        {
            var info = new FileInfo(index.ArchiveFile);
            return info.Exists
                   && info.Length == index.Length
                   && info.LastWriteTimeUtc == index.LastWriteUtc;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Invalidate(string? archiveFile)
    {
        if (string.IsNullOrWhiteSpace(archiveFile))
        {
            return;
        }

        lock (_cacheLock)
        {
            if (_cache.Remove(archiveFile, out var dropped))
            {
                DeleteMaterialized(dropped);
            }
        }
    }

    private void DeleteMaterialized(ArchiveIndex index)
    {
        if (index.MaterializedTar is { } tar)
        {
            TryDelete(tar);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响功能（下次启动会整目录清掉）
        }
    }

    // ------------------------------------------------------------------ 建索引

    private ArchiveIndex BuildIndex(string archiveFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new FileInfo(archiveFile);
        if (!info.Exists)
        {
            throw new ArchiveOpenException($"压缩包不存在：{archiveFile}");
        }

        var length = info.Length;
        var lastWriteUtc = info.LastWriteTimeUtc;
        var extension = GetExtensionOf(archiveFile);
        var handlers = ResolveHandlers(extension);

        if (handlers.Count == 0)
        {
            throw new ArchiveOpenException($"不支持的压缩包格式：.{extension}");
        }

        var password = GetPassword(archiveFile);
        string? lastFailure = null;

        // .rar 这类扩展名同时挂在两个处理器上（Rar / Rar5），一个打不开就试下一个
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var openFailure = string.Empty;
            var archive = SevenZipInterop.TryOpen(archiveFile, handler.ClassId, password, cancellationToken, out openFailure);

            if (archive is null)
            {
                if (LooksLikePasswordFailure(openFailure))
                {
                    throw new ArchivePasswordRequiredException(archiveFile);
                }

                lastFailure = openFailure;
                continue;
            }

            using (archive)
            {
                var byPath = ReadNodes(archive, archiveFile, cancellationToken);

                var chained = TryChainTar(archive, archiveFile, extension, byPath, password, length, lastWriteUtc, cancellationToken);
                if (chained is not null)
                {
                    return chained;
                }

                Log.Write($"压缩包：{Path.GetFileName(archiveFile)} 共 {byPath.Count} 项（{handler.Name}）");

                return new ArchiveIndex
                {
                    ArchiveFile = archiveFile,
                    Length = length,
                    LastWriteUtc = lastWriteUtc,
                    SourceFile = archiveFile,
                    SourceClassId = handler.ClassId,
                    ByPath = byPath,
                    Children = BuildChildren(byPath),
                };
            }
        }

        throw new ArchiveOpenException(string.IsNullOrEmpty(lastFailure)
            ? $"无法打开压缩包：{Path.GetFileName(archiveFile)}"
            : lastFailure);
    }

    private static bool LooksLikePasswordFailure(string? failure)
        => !string.IsNullOrEmpty(failure) && failure.Contains("密码", StringComparison.Ordinal);

    /// <summary>把 7z.dll 的扁平条目读成“包内路径 → 节点”的表，并补齐隐式目录。</summary>
    private static Dictionary<string, ArchiveNode> ReadNodes(
        SevenZipArchive archive,
        string archiveFile,
        CancellationToken cancellationToken)
    {
        var byPath = new Dictionary<string, ArchiveNode>(StringComparer.OrdinalIgnoreCase);

        for (uint i = 0; i < archive.ItemCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = archive.GetEntry(i);
            if (entry is null)
            {
                continue;
            }

            // 单文件压缩器（gzip / xz …）不给条目名，要自己按压缩包名派生内层文件名。
            // 只有“整包真的只有一个条目”时才派生，免得包里真有坏条目时凭空多出一行。
            var raw = entry.Value.Path;
            if (string.IsNullOrEmpty(raw))
            {
                if (archive.ItemCount != 1)
                {
                    continue;
                }

                raw = ArchiveFormats.DeriveSingleEntryName(archiveFile);
            }

            var inner = ArchivePath.NormalizeInner(raw);

            // 无路径（包根自身）/ 含 ".." 之类不安全段的条目直接跳过
            if (inner is null || inner.Length == 0)
            {
                continue;
            }

            EnsureParents(byPath, inner, entry.Value.LastWriteTime);

            byPath[inner] = new ArchiveNode(
                inner,
                LeafOf(inner),
                entry.Value.IsDirectory,
                entry.Value.IsDirectory ? 0 : entry.Value.Size,
                entry.Value.LastWriteTime,
                i);
        }

        return byPath;
    }

    /// <summary>补齐 <paramref name="innerPath" /> 的各级父目录（压缩包可能只存文件路径，没有目录条目）。</summary>
    private static void EnsureParents(
        Dictionary<string, ArchiveNode> byPath,
        string innerPath,
        DateTimeOffset lastWrite)
    {
        var separator = innerPath.IndexOf(Path.DirectorySeparatorChar);

        while (separator >= 0)
        {
            var parentPath = innerPath[..separator];

            if (!byPath.ContainsKey(parentPath))
            {
                byPath[parentPath] = new ArchiveNode(parentPath, LeafOf(parentPath), true, 0, lastWrite, null);
            }

            separator = innerPath.IndexOf(Path.DirectorySeparatorChar, separator + 1);
        }
    }

    private static string LeafOf(string innerPath)
    {
        var separator = innerPath.LastIndexOf(Path.DirectorySeparatorChar);
        return separator >= 0 ? innerPath[(separator + 1)..] : innerPath;
    }

    private static Dictionary<string, List<ArchiveNode>> BuildChildren(Dictionary<string, ArchiveNode> byPath)
    {
        var children = new Dictionary<string, List<ArchiveNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in byPath.Values)
        {
            var separator = node.InnerPath.LastIndexOf(Path.DirectorySeparatorChar);
            var parent = separator >= 0 ? node.InnerPath[..separator] : string.Empty;

            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = new List<ArchiveNode>();
            }

            list.Add(node);
        }

        return children;
    }

    /// <summary>
    /// <c>.tar.gz</c> / <c>.tgz</c> 这类“单文件压缩器里只有一个 .tar”的情况：
    /// 把中间那个 tar 解到临时目录再用 tar 处理器打开，于是双击一次就能看到 tar 里的东西
    /// （7-Zip 的图形界面要点两次，这里按 Directory Opus 的习惯合并成一次）。
    /// 打不开 / 太大 / 不是 tar 都返回 null，退回“一个文件”的正常表现。
    /// </summary>
    private ArchiveIndex? TryChainTar(
        SevenZipArchive archive,
        string archiveFile,
        string extension,
        Dictionary<string, ArchiveNode> byPath,
        string? password,
        long length,
        DateTime lastWriteUtc,
        CancellationToken cancellationToken)
    {
        if (!ArchiveFormats.SingleEntryLooksLikeTar(archiveFile, extension))
        {
            return null;
        }

        if (byPath.Count != 1)
        {
            return null;
        }

        var inner = byPath.Values.First();
        if (inner.IsDirectory || inner.ArchiveIndex is not uint itemIndex)
        {
            return null;
        }

        if (inner.Size > MaxChainedTarBytes)
        {
            Log.Write($"压缩包：{Path.GetFileName(archiveFile)} 的内层 tar 有 {inner.Size / (1024 * 1024)} MB，超过上限，按单文件显示");
            return null;
        }

        var tarPath = Path.Combine(TempRoot, "tar", HashOf(archiveFile) + ".tar");

        try
        {
            if (!File.Exists(tarPath) || new FileInfo(tarPath).Length != inner.Size)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);

                var failure = string.Empty;
                if (!archive.ExtractToFile(itemIndex, tarPath, password, cancellationToken, out failure))
                {
                    Log.Write($"压缩包：{Path.GetFileName(archiveFile)} 的内层 tar 解不出来（{failure}），按单文件显示");
                    TryDelete(tarPath);
                    return null;
                }
            }

            foreach (var handler in ResolveHandlers("tar"))
            {
                var openFailure = string.Empty;
                var tarArchive = SevenZipInterop.TryOpen(tarPath, handler.ClassId, password, cancellationToken, out openFailure);
                if (tarArchive is null)
                {
                    continue;
                }

                using (tarArchive)
                {
                    var nodes = ReadNodes(tarArchive, archiveFile, cancellationToken);
                    if (nodes.Count == 0)
                    {
                        continue;
                    }

                    Log.Write($"压缩包：{Path.GetFileName(archiveFile)} 的内层 tar 已解开（{nodes.Count} 项）");

                    return new ArchiveIndex
                    {
                        ArchiveFile = archiveFile,
                        Length = length,
                        LastWriteUtc = lastWriteUtc,
                        SourceFile = tarPath,
                        SourceClassId = handler.ClassId,
                        ByPath = nodes,
                        Children = BuildChildren(nodes),
                        MaterializedTar = tarPath,
                    };
                }
            }

            TryDelete(tarPath);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Exception($"压缩包内层 tar（{archiveFile}）", ex);
            TryDelete(tarPath);
            return null;
        }
    }

    // ------------------------------------------------------------------ 密码 / 临时目录

    private string? GetPassword(string archiveFile)
        => _passwords.TryGetValue(archiveFile, out var password) ? password : null;

    public void SetPassword(string? archiveFile, string? password)
    {
        if (string.IsNullOrWhiteSpace(archiveFile))
        {
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            _passwords.TryRemove(archiveFile, out _);
            return;
        }

        _passwords[archiveFile] = password;
    }

    /// <summary>
    /// 临时目录：<c>%LOCALAPPDATA%\exdir\archive-cache</c>（日志所在目录旁边）。
    /// 里面放两类东西：链式解开的中间 tar、双击打开时解出来的单个文件。
    /// </summary>
    private string TempRoot
    {
        get
        {
            if (_tempRoot is not null)
            {
                return _tempRoot;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                local = Path.GetTempPath();
            }

            _tempRoot = Path.Combine(local, "exdir", "archive-cache");
            return _tempRoot;
        }
    }

    /// <summary>
    /// 清临时目录。策略故意保守：
    /// <list type="bullet">
    /// <item><c>tar</c> 子目录（链式解开 <c>.tar.gz</c> 落地的中间 tar）每次都直接删 —— 那个文件只有 exdir 在用；</item>
    /// <item><c>open</c> 子目录里“给默认程序打开”而解出来的文件只删**一天前**的：
    ///       用户很可能正拿记事本 / 播放器开着它，删早了会让别人的保存失败。</item>
    /// </list>
    /// 启动时清一次、退出时再清一次（见 App.OnLaunched / MainWindow.RequestExit）。
    /// </summary>
    public void CleanupTemp()
    {
        lock (_cacheLock)
        {
            foreach (var index in _cache.Values)
            {
                DeleteMaterialized(index);
            }

            _cache.Clear();
            _cacheOrder.Clear();
        }

        TryDeleteDirectory(Path.Combine(TempRoot, "tar"));
        SweepOldOpenFiles(Path.Combine(TempRoot, "open"));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 被占用就先留着，下次启动再删
        }
    }

    private static void SweepOldOpenFiles(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);
            var removed = 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception)
                {
                    // 单个文件删不掉不影响其它
                }
            }

            // 顺手扫掉空目录（解压出来的目录结构）
            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (Exception)
                {
                    // 忽略
                }
            }

            if (removed > 0)
            {
                Log.Write($"压缩包临时文件：清掉 {removed} 个一天前的副本");
            }
        }
        catch (Exception ex)
        {
            Log.Exception("清理压缩包临时文件", ex);
        }
    }

    /// <summary>路径的稳定短哈希（FNV-1a；不要用 <c>string.GetHashCode</c>，它每个进程都不一样）。</summary>
    private static string HashOf(string path)
    {
        var hash = unchecked((uint)2166136261);

        foreach (var ch in path.ToLowerInvariant())
        {
            hash = (hash ^ ch) * 16777619;
        }

        return hash.ToString("X8");
    }

    /// <summary>文件名里不能出现的字符换成下划线（临时目录名用）。</summary>
    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new System.Text.StringBuilder(name.Length);

        foreach (var ch in name)
        {
            buffer.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        return buffer.Length == 0 ? "archive" : buffer.ToString();
    }
}
