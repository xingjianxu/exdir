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

    public FileSystemService(ICloudSyncService cloudSync) => _cloudSync = cloudSync;

    public Task<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryAsync(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<FileSystemEntry>>(() => Enumerate(path, includeHidden, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<FileSystemEntry>> EnumerateSubDirectoriesAsync(
        string path,
        int maxCount,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<FileSystemEntry>>(
            () => Enumerate(path, includeHidden: false, cancellationToken, directoriesOnly: true, maxCount: maxCount),
            cancellationToken);

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

    private static FileSystemEntry CreateEntry(EnumerationItem item, CloudSyncState syncState)
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
        };
    }

    /// <summary>枚举出来的原始条目：目录项 + 已经读过的属性（属性要留给云状态兜底用）。</summary>
    private readonly record struct EnumerationItem(FileSystemInfo Info, FileAttributes Attributes, bool IsDirectory);
}
