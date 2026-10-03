using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services.Remote;

namespace Exdir.Services;

/// <inheritdoc cref="IRemoteFileService" />
public sealed class RemoteFileService : IRemoteFileService
{
    /// <summary>递归下载时最多往下钻几层（符号链接成环 / 服务器报告怪路径时的保险）。</summary>
    private const int MaxDownloadDepth = 32;

    private readonly IRemoteLocationSource _locations;
    private readonly RemoteSessionPool _pool = new();

    public RemoteFileService(IRemoteLocationSource locations)
    {
        _locations = locations;
    }

    /// <inheritdoc />
    public event EventHandler<string>? Downloaded;

    public bool IsRemotePath(string? path) => RemotePath.LooksRemote(path);

    public bool TryParse(string? path, out RemotePathInfo info) => RemotePath.TryParse(path, out info);

    public string? GetParent(string path)
    {
        if (!RemotePath.TryParse(path, out var info) || RemotePath.GetParent(info.Path) is not { } parent)
        {
            return null;
        }

        return RemotePath.Build(info.Protocol, info.UserName, info.Host, info.Port, parent);
    }

    public async Task<string?> ResolveDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!RemotePath.TryParse(path, out var info))
        {
            return null;
        }

        // 根目录不用问服务器（有些 FTP 服务器对 "/" 的 STAT 支持得很差）
        if (info.Path == "/")
        {
            return info.ToString();
        }

        var location = Require(info);
        var kind = await _pool.UseAsync(
            location,
            (session, ct) => session.StatAsync(info.Path, ct),
            cancellationToken).ConfigureAwait(true);

        switch (kind)
        {
            case RemoteEntryKind.Directory:
                return info.ToString();

            case RemoteEntryKind.File:
                // 与真实路径一致：给一个文件路径就打开它所在的目录
                return RemotePath.GetParent(info.Path) is { } parent
                    ? RemotePath.Build(info.Protocol, info.UserName, info.Host, info.Port, parent)
                    : info.ToString();

            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<FileSystemEntry>> ListAsync(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken = default)
    {
        if (!RemotePath.TryParse(path, out var info))
        {
            throw new RemoteAccessException($"不是有效的远程路径：{path}");
        }

        var location = Require(info);
        var entries = await _pool.UseAsync(
            location,
            (session, ct) => session.ListAsync(info.Path, ct),
            cancellationToken).ConfigureAwait(true);

        var result = new List<FileSystemEntry>(entries.Count);
        var root = info.Root.TrimEnd('/');

        foreach (var entry in entries)
        {
            // 远程目录没法问“隐藏属性”，按 Unix 惯例把点开头的当隐藏项（与 ShowHiddenFiles 一致）
            var isHidden = entry.Name.StartsWith('.');
            if (isHidden && !includeHidden)
            {
                continue;
            }

            var fullPath = info.Path == "/"
                ? $"{root}/{entry.Name}"
                : $"{root}{info.Path}/{entry.Name}";

            result.Add(new FileSystemEntry
            {
                FullPath = fullPath,
                Name = entry.Name,
                IsDirectory = entry.IsDirectory,
                Size = entry.Size,
                LastWriteTime = entry.LastWriteTime,
                CreationTime = entry.LastWriteTime,
                TypeName = FileTypeHelper.GetTypeName(entry.Name, entry.IsDirectory),
                IsHidden = isHidden,

                // 远程的压缩包**不**当可展开的压缩包：7z.dll 只认本地文件，
                // 双击它也不会“进包”，而是下载到本地再由默认程序打开
                IsArchive = false,
                IsInArchive = false,
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<FileSystemEntry>> ListDirectoriesAsync(
        string path,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var entries = await ListAsync(path, includeHidden: false, cancellationToken).ConfigureAwait(true);
        var result = new List<FileSystemEntry>(Math.Min(entries.Count, maxCount));

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            result.Add(entry);
            if (result.Count >= maxCount)
            {
                break;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> DownloadAsync(
        IReadOnlyList<string> remotePaths,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            throw new RemoteAccessException($"目标目录不存在：{destinationDirectory}");
        }

        var written = new List<string>();

        foreach (var remotePath in remotePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!RemotePath.TryParse(remotePath, out var info))
            {
                throw new RemoteAccessException($"不是有效的远程路径：{remotePath}");
            }

            var location = Require(info);
            var name = info.Path == "/" ? RemotePath.RootDisplayOf(info) : RemotePath.NameOf(info);

            var target = UniquePath(destinationDirectory, name);
            await DownloadEntryAsync(location, info.Path, target, 0, cancellationToken).ConfigureAwait(true);
            written.Add(target);
        }

        Log.Write($"远程下载：{written.Count} 项 → {destinationDirectory}");
        Downloaded?.Invoke(this, destinationDirectory);

        return written;
    }

    public async Task DownloadFileToAsync(
        string remotePath,
        string localFilePath,
        long expectedSize,
        CancellationToken cancellationToken = default)
    {
        if (!RemotePath.TryParse(remotePath, out var info))
        {
            throw new RemoteAccessException($"不是有效的远程路径：{remotePath}");
        }

        // 本地已经有一份同样大小的副本（上次打开留下的）就直接用
        try
        {
            if (expectedSize >= 0 && File.Exists(localFilePath) && new FileInfo(localFilePath).Length == expectedSize)
            {
                return;
            }
        }
        catch (Exception)
        {
            // 读不到就当要重新下载
        }

        var location = Require(info);
        var directory = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _pool.UseAsync(
            location,
            (session, ct) =>
            {
                Log.Write($"远程下载：{info.Path} → {localFilePath}");
                return session.DownloadFileAsync(info.Path, localFilePath, ct);
            },
            cancellationToken).ConfigureAwait(true);
    }

    public void ResetConnections() => _pool.ResetAll();

    public void ReleaseStaging(string stagingDirectory) => RemoteCache.Release(stagingDirectory);

    public void ReleaseStagingFor(IReadOnlyList<string> localPaths) => RemoteCache.ReleaseStagingFor(localPaths);

    public void CleanupTemp() => RemoteCache.Cleanup();

    // ------------------------------------------------------------------ 内部

    /// <summary>
    /// 把一个条目下载到本地落点：文件直接下，目录就建目录 + 逐个下子项（递归）。
    /// 胜负之分取决于 <c>StatAsync</c>，不能靠“先 List 一下”猜 —— FTP 对文件调 LIST 会直接报错。
    /// </summary>
    private async Task DownloadEntryAsync(
        RemoteLocation location,
        string remotePath,
        string targetPath,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDownloadDepth)
        {
            throw new RemoteAccessException($"目录层级过深（超过 {MaxDownloadDepth} 层）：{remotePath}");
        }

        var kind = await _pool.UseAsync(
            location,
            (session, ct) => session.StatAsync(remotePath, ct),
            cancellationToken).ConfigureAwait(true);

        switch (kind)
        {
            case RemoteEntryKind.None:
                throw new RemoteAccessException($"远程路径不存在：{remotePath}");

            case RemoteEntryKind.File:
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");
                Log.Write($"远程下载：{remotePath} → {targetPath}");
                await _pool.UseAsync(
                    location,
                    (session, ct) => session.DownloadFileAsync(remotePath, targetPath, ct),
                    cancellationToken).ConfigureAwait(true);
                return;
        }

        var entries = await _pool.UseAsync(
            location,
            (session, ct) => session.ListAsync(remotePath, ct),
            cancellationToken).ConfigureAwait(true);

        Directory.CreateDirectory(targetPath);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var childRemote = remotePath.TrimEnd('/') + "/" + entry.Name;
            var childTarget = Path.Combine(targetPath, Sanitize(entry.Name));

            await DownloadEntryAsync(location, childRemote, childTarget, depth + 1, cancellationToken)
                .ConfigureAwait(true);
        }
    }

    /// <summary>找到这个路径对应的连接配置；没配过就明确报错（不做匿名“猜一个主机连过去”这种事）。</summary>
    private RemoteLocation Require(RemotePathInfo info)
        => _locations.Find(info.ConnectionKey)
           ?? throw new RemoteAccessException(
               $"没有这个远程位置的配置：{info.Scheme}://{info.Authority}（请在「设置 → 远程」里添加）");

    /// <summary>本地重名时依次试 <c>name</c> / <c>name (2)</c> / <c>name (3)</c>…。</summary>
    private static string UniquePath(string directory, string name)
    {
        var safe = Sanitize(name);
        var candidate = Path.Combine(directory, safe);

        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);

        for (var index = 2; index < 10000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new RemoteAccessException($"目标目录里同名文件太多了：{safe}");
    }

    /// <summary>把远程条目名里的非法字符换掉（服务器上合法的名字在 Windows 上不一定合法）。</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        var result = new string(chars).Trim();
        return result.Length == 0 ? "download" : result;
    }
}
