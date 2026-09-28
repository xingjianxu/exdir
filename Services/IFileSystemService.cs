using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>文件系统读取抽象。所有耗时 I/O 都通过这里进入，便于替换/测试。</summary>
public interface IFileSystemService
{
    /// <summary>异步枚举目录内容。无法访问的子项会被静默跳过。</summary>
    Task<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryAsync(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken = default);

    /// <summary>异步枚举子目录（仅目录），用于侧边栏树。</summary>
    Task<IReadOnlyList<FileSystemEntry>> EnumerateSubDirectoriesAsync(
        string path,
        int maxCount,
        CancellationToken cancellationToken = default);

    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>返回“向上”目录，已经在根目录时返回 null。</summary>
    string? GetParentDirectory(string path);

    /// <summary>把任意输入规整为合法目录路径；失败返回 null。</summary>
    string? NormalizeDirectoryPath(string input);
}
