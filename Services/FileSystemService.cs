using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;

namespace Exdir.Services;

/// <inheritdoc cref="IFileSystemService" />
public sealed class FileSystemService : IFileSystemService
{
    private readonly ICloudSyncService _cloudSync;
    private readonly IArchiveService _archive;
    private readonly IRemoteFileService _remote;
    private readonly IRecentItemsService _recents;

    public FileSystemService(
        ICloudSyncService cloudSync,
        IArchiveService archive,
        IRemoteFileService remote,
        IRecentItemsService recents)
    {
        _cloudSync = cloudSync;
        _archive = archive;
        _remote = remote;
        _recents = recents;
    }

    public Task<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryAsync(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken = default)
    {
        // 远程位置（SFTP / FTP）：条目来自网络，走的完全是另一套（见 IRemoteFileService）
        if (_remote.IsRemotePath(path))
        {
            return _remote.ListAsync(path, includeHidden, cancellationToken);
        }

        // 「最新访问」虚拟视图：条目来自最近访问记录（目录 + 文件，按访问时间倒序），不碰真实文件系统
        if (RecentView.IsRecentViewPath(path))
        {
            return EnumerateRecentAsync(cancellationToken);
        }

        // 压缩包里的目录：条目来自 7z.dll，不碰真实文件系统
        if (_archive.TryParse(path, out var location))
        {
            return _archive.ListAsync(location, cancellationToken);
        }

        return Task.Run<IReadOnlyList<FileSystemEntry>>(() => Enumerate(path, includeHidden, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<FileSystemEntry>> EnumerateSubDirectoriesAsync(
        string path,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        // 侧边栏懒加载：远程目录也只取子目录（同一个连接，少传一点数据）
        if (_remote.IsRemotePath(path))
        {
            return _remote.ListDirectoriesAsync(path, maxCount, cancellationToken);
        }

        return Task.Run<IReadOnlyList<FileSystemEntry>>(
            () => Enumerate(path, includeHidden: false, cancellationToken, directoriesOnly: true, maxCount: maxCount),
            cancellationToken);
    }

    // 注意：DirectoryExists / FileExists 故意**不**处理远程路径与「最新访问」虚拟视图（一律返回 false）。
    // 它们被写操作守卫（粘贴 / 删除 / 新建文件夹 / 拖放落点 / 固定目录）当成“磁盘上真有这个路径”，
    // 远程路径与 exdir://recent 在那里必须是不存在的；这两类位置的存在性判断走 ResolveDirectoryAsync。

    public bool DirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool FileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string? GetParentDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // 「最新访问」是顶层虚拟视图，没有上一层（“向上一级”按钮因此是灰的）
        if (RecentView.IsRecentViewPath(path))
        {
            return null;
        }

        // 远程路径有自己的“上一层”（协议与登录身份那一段是根）
        if (_remote.IsRemotePath(path))
        {
            return _remote.GetParent(path);
        }

        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 已经是 "C:\" 或 "\\server\share" 这类根路径。
            // 注意不能只看 Length==0："D:\" 去掉反斜杠后是 "D:"，
            // 而 new DirectoryInfo("D:") 会被当成“D 盘的当前目录”解析到进程工作目录去。
            if (trimmed.Length == 0 || trimmed[^1] == ':')
            {
                return null;
            }

            var info = new DirectoryInfo(trimmed);
            return info.Parent?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? NormalizeDirectoryPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        // 远程路径不是“真实目录”：写操作（粘贴 / 新建文件夹 / 拖放落点）拿到 null 自然就被拒了
        if (_remote.IsRemotePath(input))
        {
            return null;
        }

        var candidate = input.Trim().Trim('"');

        try
        {
            candidate = Environment.ExpandEnvironmentVariables(candidate);

            if (Directory.Exists(candidate))
            {
                return new DirectoryInfo(candidate).FullName;
            }

            // 允许用户输入文件路径，返回其所在目录
            if (File.Exists(candidate))
            {
                return new FileInfo(candidate).DirectoryName;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    public bool IsInsideArchive(string path) => _archive.IsInsideArchive(path);

    public bool IsRemotePath(string path) => _remote.IsRemotePath(path);

    public bool IsRecentViewPath(string path) => RecentView.IsRecentViewPath(path);

    public bool TryParseArchivePath(string path, out ArchivePath location) => _archive.TryParse(path, out location);

    public async Task<string?> ResolveDirectoryAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var candidate = input.Trim().Trim('"');

        // 「最新访问」虚拟视图：不用问文件系统，字符串对上就是它（会话恢复也靠这一条）
        if (RecentView.IsRecentViewPath(candidate))
        {
            return RecentView.Path;
        }

        try
        {
            candidate = Environment.ExpandEnvironmentVariables(candidate);

            // 远程位置：根目录不用连服务器（有些 FTP 对 "/" 的 STAT 支持很差），
            // 其它目录要真连上去看一眼（不然“无法打开”就白报了）
            if (_remote.IsRemotePath(candidate))
            {
                return await _remote.ResolveDirectoryAsync(candidate, cancellationToken).ConfigureAwait(true);
            }

            if (Directory.Exists(candidate))
            {
                return new DirectoryInfo(candidate).FullName;
            }

            // 压缩包：根直接进去（打不开时由枚举去报错），包内目录要先确认它真的存在
            if (_archive.TryParse(candidate, out var location))
            {
                if (location.IsRoot)
                {
                    return location.FullPath;
                }

                return await _archive.DirectoryExistsAsync(location, cancellationToken).ConfigureAwait(true)
                    ? location.FullPath
                    : null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// 「最新访问」视图的枚举：把最近访问过的目录与文件变成列表里的行，**顺序就是访问时间倒序**
    /// （所以这个视图默认不参与列头排序，见 <c>FolderTabViewModel</c>）。
    /// </summary>
    private Task<IReadOnlyList<FileSystemEntry>> EnumerateRecentAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<FileSystemEntry>>(
            () =>
            {
                var list = new List<FileSystemEntry>();

                foreach (var entry in _recents.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (TryCreateRecentEntry(entry, out var item))
                    {
                        list.Add(item);
                    }
                }

                return list;
            },
            cancellationToken);

    /// <summary>
    /// 把一条「最近访问」记录变成列表里的一行。本地路径已经不在了（被删 / 被移走）就跳过这一行；
    /// 远程位置不联网去看一眼（只看记录时记下的类型），免得为了画一份列表把服务器全连一遍。
    /// 注意这个视图**不看**“显示隐藏文件”开关：它是使用痕迹，用户自己进过的隐藏目录也该回得去。
    /// </summary>
    private bool TryCreateRecentEntry(RecentEntry entry, out FileSystemEntry result)
    {
        result = null!;

        var path = entry.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (_remote.IsRemotePath(path))
        {
            var name = RemotePath.TryParse(path, out var remote)
                ? (remote.Path == "/" ? RemotePath.RootDisplayOf(remote) : RemotePath.NameOf(remote))
                : path;

            result = new FileSystemEntry
            {
                FullPath = path,
                Name = name,
                IsDirectory = entry.IsDirectory,
                TypeName = entry.IsDirectory ? "远程文件夹" : "远程文件",
            };

            return true;
        }

        var isDirectory = entry.IsDirectory;
        if (isDirectory ? !Directory.Exists(path) : !File.Exists(path))
        {
            return false;
        }

        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);

            result = new FileSystemEntry
            {
                FullPath = info.FullName,
                Name = info.Name,
                IsDirectory = isDirectory,
                Size = info is FileInfo file ? file.Length : 0,
                LastWriteTime = info.LastWriteTimeUtc,
                CreationTime = info.CreationTimeUtc,
                TypeName = FileTypeHelper.GetTypeName(info.FullName, isDirectory),
                IsHidden = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0,
                IsArchive = !isDirectory && _archive.IsArchiveFile(info.FullName),
            };

            return true;
        }
        catch (Exception)
        {
            // 属性读不出来（权限之类）就不显示这一行，不要因为一条记录让整个列表打不开
            return false;
        }
    }

    /// <summary>
    /// 真实目录的枚举（压缩包不在这里）。
    /// </summary>
    private IReadOnlyList<FileSystemEntry> Enumerate(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken,
        bool directoriesOnly = false,
        int maxCount = int.MaxValue)
    {
        // 侧边栏只按目录层级建树（enumerateSubDirectories）不显示状态图标，不必付这份开销
        var includeSyncState = !directoriesOnly;

        var items = new List<EnumerationItem>();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return Array.Empty<FileSystemEntry>();
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = includeHidden
                ? FileAttributes.None
                : FileAttributes.Hidden | FileAttributes.System,
        };

        var directory = new DirectoryInfo(path);

        try
        {
            foreach (var info in directory.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = info.Attributes;
                }
                catch (Exception)
                {
                    // 单个条目读不到属性就跳过，不影响同目录其它条目
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;
                if (directoriesOnly && !isDirectory)
                {
                    continue;
                }

                if (items.Count >= maxCount)
                {
                    break;
                }

                items.Add(new EnumerationItem(info, attributes, isDirectory));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 目录在枚举过程中被删除 / 权限不足：返回已获得的部分
        }

        // 云同步目录：条目状态要去属性系统里逐个读，因此单独放到第二遍（并行）里填
        var syncStates = includeSyncState ? ReadCloudSyncStates(path, items, cancellationToken) : null;
        var result = new List<FileSystemEntry>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            result.Add(CreateEntry(
                items[i],
                syncStates is null ? CloudSyncState.None : syncStates[i]));
        }

        return result;
    }

    /// <summary>
    /// 读云同步状态：每个条目要建一次属性存储（比读目录项贵得多，实测约 2~3 ms/项），
    /// 所以按核数并行，并且加一个时间预算——碰到上万项的云目录时宁可少几行的状态图标，
    /// 也不要让目录转圈转很久（超预算的部分保持 None，不显示图标）。
    /// 非云目录返回 null，一个额外的系统调用都不做。
    /// </summary>
    private CloudSyncState[]? ReadCloudSyncStates(
        string path,
        List<EnumerationItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0 || !_cloudSync.IsCloudPath(path))
        {
            return null;
        }

        var states = new CloudSyncState[items.Count];
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
        };

        var stopwatch = Stopwatch.StartNew();
        var abandoned = 0;

        try
        {
            Parallel.For(0, items.Count, options, i =>
            {
                if (Volatile.Read(ref abandoned) != 0)
                {
                    return;
                }

                if (stopwatch.ElapsedMilliseconds > CloudSyncStateBudgetMs)
                {
                    Interlocked.Exchange(ref abandoned, 1);
                    return;
                }

                states[i] = _cloudSync.GetState(items[i].Info.FullName, items[i].Attributes);
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AggregateException)
        {
            // 个别条目读失败不该让整个目录打不开：已读到的状态保留
        }

        // 留一条日志，便于以后回头看这套读取到底多贵（见 AGENTS.md 的性能备注）
        Log.Write(abandoned == 0
            ? $"云同步状态：{path} 共 {items.Count} 项，耗时 {stopwatch.ElapsedMilliseconds} ms"
            : $"云同步状态：{path} 共 {items.Count} 项，超出 {CloudSyncStateBudgetMs} ms 预算（耗时 {stopwatch.ElapsedMilliseconds} ms），部分条目不显示状态");

        return states;
    }

    /// <summary>读同步状态的时间预算（毫秒），超出后剩余条目不再读。</summary>
    private const int CloudSyncStateBudgetMs = 1500;

    private FileSystemEntry CreateEntry(EnumerationItem item, CloudSyncState syncState)
    {
        var info = item.Info;
        var isDirectory = item.IsDirectory;
        long size = 0;
        var lastWrite = default(DateTimeOffset);
        var created = default(DateTimeOffset);

        try
        {
            lastWrite = info.LastWriteTimeUtc;
            created = info.CreationTimeUtc;

            if (!isDirectory && info is FileInfo file)
            {
                size = file.Length;
            }
        }
        catch (Exception)
        {
            // 属性读取失败时保留默认值
        }

        return new FileSystemEntry
        {
            FullPath = info.FullName,
            Name = info.Name,
            IsDirectory = isDirectory,
            Size = size,
            LastWriteTime = lastWrite,
            CreationTime = created,
            TypeName = FileTypeHelper.GetTypeName(info.FullName, isDirectory),
            IsHidden = (item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0,
            SyncState = syncState,

            // 压缩包文件在列表里也像目录一样可以就地展开；IsArchiveFile 先看扩展名，
            // 非压缩扩展名在这里只是一次字典查询，不会多出文件系统访问
            IsArchive = !isDirectory && _archive.IsArchiveFile(info.FullName),
        };
    }

    /// <summary>枚举出来的原始条目：目录项 + 已经读过的属性（属性要留给云状态兜底用）。</summary>
    private readonly record struct EnumerationItem(FileSystemInfo Info, FileAttributes Attributes, bool IsDirectory);
}
