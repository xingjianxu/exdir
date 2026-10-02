using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 压缩包只读浏览（见 AGENTS.md 第 4 节“压缩包只读浏览”）。
///
/// <list type="bullet">
/// <item>把「压缩包文件 + 包内路径」当作一条普通目录路径来解释（<see cref="ArchivePath" />），
///       所以导航 / 面包屑 / 行内展开 / 排序 / 会话恢复都不用为压缩包写第二套；</item>
/// <item>枚举走 <see cref="SevenZipInterop" />（随程序分发的原生 7z.dll），
///       压缩包索引按「路径 + 大小 + 修改时间」缓存，F5 可以 <see cref="Invalidate" />；</item>
/// <item>只读：不提供向压缩包里写的能力；包里双击文件时用 <see cref="ExtractToTempAsync" />
///       把单个条目解到临时目录再用默认程序打开。</item>
/// </list>
/// </summary>
public interface IArchiveService
{
    /// <summary>7z.dll 是不是可用（缺失 / 非 x64 时为 false，压缩包浏览整体关闭）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用的原因（只用于日志）。</summary>
    string AvailabilityFailure { get; }

    /// <summary>这个路径是不是一个“可进入的压缩包文件”（核心扩展名 + 7z.dll 认识 + 文件存在）。</summary>
    bool IsArchiveFile(string? path);

    /// <summary>把路径解释成压缩包位置（压缩包根或包内的某个目录 / 文件）。</summary>
    bool TryParse(string? path, out ArchivePath location);

    /// <summary>路径是不是指向某个压缩包（根或包内）。</summary>
    bool IsInsideArchive(string? path);

    /// <summary>列出压缩包根 / 包内某个目录的直接子项。</summary>
    Task<IReadOnlyList<FileSystemEntry>> ListAsync(ArchivePath location, CancellationToken cancellationToken = default);

    /// <summary>包内这个路径是不是一个存在的目录（根恒为 true）。</summary>
    Task<bool> DirectoryExistsAsync(ArchivePath location, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把包内一个文件解到临时目录并返回真实文件路径（同一个条目重复调用会命中已解出来的那份）。
    /// 只用于“包内文件双击 → 用默认程序打开”。
    /// </summary>
    Task<string> ExtractToTempAsync(ArchivePath file, CancellationToken cancellationToken = default);

    /// <summary>丢掉某个压缩包的索引缓存（外部改过压缩包时用；F5 会调）。</summary>
    void Invalidate(string? archiveFile);

    /// <summary>记住（或清除）某个压缩包的密码，只保存在本次运行的内存里。</summary>
    void SetPassword(string? archiveFile, string? password);

    /// <summary>清掉临时目录（启动时清一次、退出时再清一次）。</summary>
    void CleanupTemp();
}

/// <summary>压缩包打不开（格式不支持 / 文件损坏 / 密码错误之外的失败）。</summary>
public sealed class ArchiveOpenException : Exception
{
    public ArchiveOpenException(string message)
        : base(message)
    {
    }
}

/// <summary>压缩包需要密码（或之前给的密码不对），调用方应当去问用户。</summary>
public sealed class ArchivePasswordRequiredException : Exception
{
    public ArchivePasswordRequiredException(string archiveFile)
        : base($"需要密码：{archiveFile}")
    {
        ArchiveFile = archiveFile;
    }

    public string ArchiveFile { get; }
}
