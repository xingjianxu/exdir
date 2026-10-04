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

    /// <summary>返回“向上”目录，已经在根目录时返回 null。压缩包路径也适用（包内目录 → 包根）。</summary>
    string? GetParentDirectory(string path);

    /// <summary>把任意输入规整为**真实目录**路径；失败返回 null（压缩包路径不算）。</summary>
    string? NormalizeDirectoryPath(string input);

    /// <summary>路径是不是指向某个压缩包（压缩包根或包内目录 / 文件）。</summary>
    bool IsInsideArchive(string path);

    /// <summary>
    /// 路径是不是远程位置（<c>sftp://</c> / <c>ftp://</c> / <c>ftps://</c>）。
    /// 只看协议头，不联网；见 <c>IRemoteFileService</c>。
    /// </summary>
    bool IsRemotePath(string path);

    /// <summary>把路径解释成压缩包位置；不是压缩包路径时返回 false。</summary>
    bool TryParseArchivePath(string path, out ArchivePath location);

    /// <summary>
    /// 路径是不是「最新访问」虚拟视图（<c>exdir://recent</c>）：它不是真实目录，
    /// 枚举出来的是最近访问过的目录与文件（按访问时间倒序），见 <c>Helpers/RecentView</c>。
    /// </summary>
    bool IsRecentViewPath(string path);

    /// <summary>
    /// 把任意输入规整成可导航的目录路径：
    /// 真实目录 → 全路径；压缩包根 / 包内存在的目录 → 虚拟路径（<c>D:\a\b.zip\sub</c>）；
    /// 「最新访问」虚拟视图 → 它的哨兵路径；
    /// 真实文件、不存在的路径、非法路径 → null。
    /// 包内目录要真的存在，所以这一条可能要去读压缩包，因此是异步的。
    /// </summary>
    Task<string?> ResolveDirectoryAsync(string input, CancellationToken cancellationToken = default);
}
