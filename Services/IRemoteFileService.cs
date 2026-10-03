using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 远程位置（SFTP / FTP / FTPS）的只读访问：列目录、判断是否存在、把文件下载到本地。
///
/// <para>
/// **不提供任何写操作**（上传 / 删除 / 重命名 / 新建目录）—— 当前的定位是“浏览 + 复制到本地”，
/// 远程位置在文件列表里与压缩包内部一样是只读的。以后要支持上传时，在这个接口上成对加。
/// </para>
///
/// <para>
/// 路径形式见 <see cref="RemotePath" />：<c>sftp://user@host:22/home/me</c>。
/// 连接与凭据来自设置里的“远程位置”清单（见 <see cref="IRemoteLocationSource" />）。
/// </para>
/// </summary>
public interface IRemoteFileService
{
    /// <summary>
    /// 一批远程条目被下载到某个本地目录之后触发（参数是目标目录）。
    /// 主窗口据此把正开着那个目录（及其父目录）的标签页刷新一遍 —— 与解压 / 压缩的通知同一套处理。
    /// </summary>
    event EventHandler<string>? Downloaded;

    /// <summary>这个路径是不是远程路径（只看协议头，不联网）。</summary>
    bool IsRemotePath(string? path);

    /// <summary>解析远程路径；不是远程路径时返回 false。</summary>
    bool TryParse(string? path, out RemotePathInfo info);

    /// <summary>上一层目录（远程路径的父目录）；已经在根目录时返回 null。</summary>
    string? GetParent(string path);

    /// <summary>
    /// 把输入解释成一个**存在的**远程目录路径：
    /// 目录 → 规整后的路径；文件 → 它所在的目录（与真实路径的行为一致）；不存在 → null。
    /// </summary>
    Task<string?> ResolveDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>列目录。<paramref name="includeHidden" /> 为 false 时跳过以 <c>.</c> 开头的条目。</summary>
    Task<IReadOnlyList<FileSystemEntry>> ListAsync(
        string path,
        bool includeHidden,
        CancellationToken cancellationToken = default);

    /// <summary>只列子目录（侧边栏树懒加载用）。</summary>
    Task<IReadOnlyList<FileSystemEntry>> ListDirectoriesAsync(
        string path,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一批远程条目下到本地目录（目录含整棵子树），返回落盘的本地路径。
    /// 同名时自动加 <c>(2)(3)…</c> 后缀，不覆盖已有文件。
    /// </summary>
    Task<IReadOnlyList<string>> DownloadAsync(
        IReadOnlyList<string> remotePaths,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把单个远程文件下到指定的本地文件路径（双击打开 / 拖拽中转用）。
    /// 调用方给出期望大小（列表里的那个），一致时直接复用已有副本。
    /// </summary>
    Task DownloadFileToAsync(
        string remotePath,
        string localFilePath,
        long expectedSize,
        CancellationToken cancellationToken = default);

    /// <summary>设置里改过远程位置之后调用：已有连接全部作废，下次重连。</summary>
    void ResetConnections();

    /// <summary>删掉一个中转目录（见 <see cref="Exdir.Helpers.RemoteCache" />）。</summary>
    void ReleaseStaging(string stagingDirectory);

    /// <summary>按“交出去的本地路径”回收拖拽 / 复制中转目录。</summary>
    void ReleaseStagingFor(IReadOnlyList<string> localPaths);

    /// <summary>清理中转目录（启动 / 退出各一次）。</summary>
    void CleanupTemp();
}
