using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;

namespace Exdir.Services;

/// <inheritdoc cref="ICompressionService" />
public sealed class CompressionService : ICompressionService
{
    /// <summary>拷贝文件时的缓冲区大小（与 FileStream 自己用的默认值一致）。</summary>
    private const int CopyBufferSize = 81920;

    /// <summary>zip 里条目时间戳的下限（zip 格式从 1980 年开始，更早的值会被 .NET 拒绝）。</summary>
    private static readonly DateTime ZipEpoch = new(1980, 1, 1);

    public event EventHandler<string>? ArchiveCreated;

    public async Task<string> CompressAsync(
        IReadOnlyList<string> sourcePaths,
        string outputDirectory,
        string baseName,
        CancellationToken cancellationToken = default)
    {
        var sources = NormalizeSources(sourcePaths);
        if (sources.Count == 0)
        {
            throw new IOException("没有可压缩的项目");
        }

        Directory.CreateDirectory(outputDirectory);
        var zipPath = UniqueFile(Path.Combine(outputDirectory, Sanitize(baseName) + ".zip"));

        var files = 0;

        try
        {
            // 打包是磁盘 + CPU 的活儿，放到后台线程上做（主窗口的消息循环不能被卡住）；
            // 索引 / 递归用的都是同步 API，就没必要再写一套异步版本
            await Task.Run(
                () =>
                {
                    using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                    foreach (var source in sources)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        files += AddSource(archive, source, cancellationToken);
                    }
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            // 失败 / 取消：把写了一半的包删掉，别在输出目录里留个打不开的 zip
            TryDelete(zipPath);
            throw;
        }

        Log.Write($"压缩：{sources.Count} 项 / {files} 个文件 → {zipPath}");
        ArchiveCreated?.Invoke(this, zipPath);
        return zipPath;
    }

    // ------------------------------------------------------------------ 收集要打包的条目

    /// <summary>
    /// 规整调用方给的路径：去掉末尾分隔符、去重，并且**丢掉被另一个选中目录涵盖的条目**
    ///（在列表里同时选了目录和它下面的行时，否则 zip 里会出现两份）。
    /// </summary>
    private static List<string> NormalizeSources(IReadOnlyList<string> sourcePaths)
    {
        var cleaned = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(CompressTargets.TrimTrailingSeparators)
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path.Length)
            .ToList();

        var roots = new List<string>();
        foreach (var path in cleaned)
        {
            var covered = roots.Any(root =>
                path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

            if (!covered)
            {
                roots.Add(path);
            }
        }

        return roots;
    }

    /// <summary>把一个顶层选中项（文件或目录）加进 zip，返回写进去的文件数。</summary>
    private static int AddSource(ZipArchive archive, string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            AddFile(archive, path, Path.GetFileName(path), cancellationToken);
            return 1;
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException($"找不到要压缩的项目：{path}", path);
        }

        var root = Path.GetFileName(path);
        if (string.IsNullOrEmpty(root))
        {
            // 整个盘根（D:\）没有名字，给一个占位名
            root = "root";
        }

        // 目录自己也要写一条（空目录才能被还原出来）
        AddDirectoryEntry(archive, root);
        return AddDirectoryContents(archive, path, root, cancellationToken);
    }

    /// <summary>递归把一个目录的内容写进 zip；<paramref name="prefix" /> 是它在包内的路径。</summary>
    private static int AddDirectoryContents(
        ZipArchive archive,
        string directory,
        string prefix,
        CancellationToken cancellationToken)
    {
        var count = 0;

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            var entryName = prefix + "/" + name;
            var attributes = File.GetAttributes(entry);

            if ((attributes & FileAttributes.Directory) != 0)
            {
                // 符号链接 / 目录联接点不跟进去：可能绕圈，也可能指到选中目录之外
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                AddDirectoryEntry(archive, entryName);
                count += AddDirectoryContents(archive, entry, entryName, cancellationToken);
            }
            else
            {
                AddFile(archive, entry, entryName, cancellationToken);
                count++;
            }
        }

        return count;
    }

    /// <summary>目录条目：名字以 <c>/</c> 结尾（各家解压工具都靠这个认目录），并带上 DOS 目录属性位。</summary>
    private static void AddDirectoryEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.CreateEntry(entryName + "/", CompressionLevel.NoCompression);
        entry.ExternalAttributes = (int)FileAttributes.Directory;
    }

    private static void AddFile(ZipArchive archive, string path, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

        try
        {
            var written = File.GetLastWriteTime(path);
            entry.LastWriteTime = written < ZipEpoch ? ZipEpoch : written;
        }
        catch (Exception)
        {
            // 时间戳写不进去不影响内容（zip 会退回 1980），不值得让整次压缩失败
        }

        // FileShare 放开：正被别的程序打开的文件也能读（打不开就抛异常，由调用方报给用户）
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = entry.Open();

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ------------------------------------------------------------------ 输出路径

    /// <summary>同名文件 / 目录已存在时依次加 <c>(2)(3)…</c>（与「新建文件夹」「解压到下载文件夹」同一套做法）。</summary>
    private static string UniqueFile(string candidate)
    {
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var directory = Path.GetDirectoryName(candidate) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);

        for (var index = 2; ; index++)
        {
            var next = Path.Combine(directory, $"{name} ({index}){extension}");

            if (!File.Exists(next) && !Directory.Exists(next))
            {
                return next;
            }
        }
    }

    /// <summary>把压缩包名里不能做文件名的字符换掉（用户选中的条目名可能带 <c>:</c> 这类字符）。</summary>
    private static string Sanitize(string? baseName)
    {
        var value = string.IsNullOrWhiteSpace(baseName) ? "压缩包" : baseName.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        value = value.TrimEnd('.', ' ');
        return value.Length == 0 ? "压缩包" : value;
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
            // 删不掉不影响：用户自己删掉就是（半成品包不会被覆盖，见 UniqueFile）
        }
    }
}
