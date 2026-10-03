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
/// <item>只读（不向压缩包里写）：包里双击文件用 <see cref="ExtractToTempAsync" /> 解出来再用默认程序打开；
///       包内「复制 → 在真实目录粘贴」用 <see cref="ExtractForCopyAsync" /> 解出真实文件再交给外壳复制。</item>
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

    /// <summary>
    /// 把包内若干条目（文件，或目录——目录含整棵子树）解到临时目录，供“在真实目录粘贴”使用。
    /// 包内条目没有真实路径、写不进系统剪贴板，所以“包内复制”只记下包内路径，
    /// 粘贴时才走这里解出真实文件再交给外壳复制。
    /// 用完必须调 <see cref="ReleaseStaging" /> 把这批临时副本删掉。
    /// </summary>
    Task<ArchiveExtraction> ExtractForCopyAsync(
        string archiveFile,
        IReadOnlyList<string> innerPaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把包内若干条目（文件，或目录——目录含整棵子树）解到临时目录，供“把包内条目拖到别处”使用。
    /// 与 <see cref="ExtractForCopyAsync" /> 的差别只在临时目录的分类（<c>drag</c>）与清理时机：
    /// 拖到资源管理器时副本是**别的进程**在拷，交出去之后我们不知道什么时候拷完，
    /// 所以只能留在原地等 <see cref="CleanupTemp" /> 按时间扫（拖进 exdir 自己的窗格时
    /// 复制一完成就会 <see cref="ReleaseStagingFor" /> 掉）。
    /// </summary>
    Task<ArchiveExtraction> ExtractForDragAsync(
        string archiveFile,
        IReadOnlyList<string> innerPaths,
        CancellationToken cancellationToken = default);

    /// <summary>删掉 <see cref="ExtractForCopyAsync" /> 用过的临时目录（只认它自己造的那一类目录）。</summary>
    void ReleaseStaging(string stagingDirectory);

    /// <summary>
    /// 按“解出来的路径”反查并删掉 <see cref="ExtractForDragAsync" /> 创建的临时目录。
    /// 一次文件复制 / 移动完成（<see cref="IFileOperationService.Completed" />）后调用：
    /// 源路径里凡是落在 <c>archive-cache\drag\&lt;guid&gt;</c> 下的，就是这次拖拽解出来的临时副本，
    /// 复制完了就没用了。真实路径与 <c>copy</c> 分类（粘贴的中转副本）一律不动。
    /// </summary>
    void ReleaseStagingFor(IReadOnlyList<string> paths);

    /// <summary>丢掉某个压缩包的索引缓存（外部改过压缩包时用；F5 会调）。</summary>
    void Invalidate(string? archiveFile);

    /// <summary>记住（或清除）某个压缩包的密码，只保存在本次运行的内存里。</summary>
    void SetPassword(string? archiveFile, string? password);

    /// <summary>清掉临时目录（启动时清一次、退出时再清一次）。</summary>
    void CleanupTemp();

    /// <summary>
    /// 把**整包**解到真实目录（内置右键菜单「解压到下载文件夹」用），返回真正写出的文件数。
    /// <paramref name="destinationDirectory" /> 由调用方决定并在解压时自动创建；
    /// 包内的目录结构（含空目录）照原样重建，同名文件覆盖。
    /// </summary>
    Task<int> ExtractAllAsync(string archiveFile, string destinationDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// 一次“解压到目录”真的写出了文件之后触发，参数是解压到的目录。
    /// 宿主据此刷新正开在那个目录（以及它的父目录）里的标签页 —— 与复制 / 移动完成后的处理一致。
    /// </summary>
    event EventHandler<string>? Extracted;
}

/// <summary>
/// “把包内条目解到临时目录”的结果（见 <see cref="IArchiveService.ExtractForCopyAsync" />）。
/// </summary>
/// <param name="Paths">解出来的**顶层**真实路径（文件 / 目录名与包内一致，可直接交给外壳复制）。</param>
/// <param name="StagingDirectory">这批临时副本的根目录（用完交给 <see cref="IArchiveService.ReleaseStaging" />）。</param>
public sealed record ArchiveExtraction(IReadOnlyList<string> Paths, string StagingDirectory);

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
