using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Helpers;
using Exdir.Models;

namespace Exdir.Services;

/// <inheritdoc cref="IFileSystemService" />
public sealed class FileSystemService : IFileSystemService
{
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

            // 已经是 "C:" 或 "\\server\share" 这类根路径
            if (trimmed.Length == 0)
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

    private static IReadOnlyList<FileSystemEntry> Enumerate(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken,
        bool directoriesOnly = false,
        int maxCount = int.MaxValue)
    {
        var result = new List<FileSystemEntry>();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return result;
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

                var isDirectory = (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                if (directoriesOnly && !isDirectory)
                {
                    continue;
                }

                if (result.Count >= maxCount)
                {
                    break;
                }

                result.Add(CreateEntry(info, isDirectory));
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

        return result;
    }

    private static FileSystemEntry CreateEntry(FileSystemInfo info, bool isDirectory)
    {
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

        var hidden = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

        return new FileSystemEntry
        {
            FullPath = info.FullName,
            Name = info.Name,
            IsDirectory = isDirectory,
            Size = size,
            LastWriteTime = lastWrite,
            CreationTime = created,
            TypeName = FileTypeHelper.GetTypeName(info.FullName, isDirectory),
            IsHidden = hidden,
        };
    }
}
