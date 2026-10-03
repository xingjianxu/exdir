using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Models;

namespace Exdir.Services.Remote;

/// <summary>
/// 一条到远程位置的连接（SFTP / FTP / FTPS 各一个实现）。
///
/// <para>
/// 连接**不是线程安全**的（SSH.NET 与 FluentFTP 都要求同一时刻只有一个操作在跑），
/// 所以由 <see cref="RemoteSessionPool" /> 用一个信号量把每条连接的调用串起来，
/// 同一个位置不会并发跑两个请求。
/// </para>
/// </summary>
internal abstract class RemoteSession : IDisposable
{
    protected RemoteSession(RemoteLocation location)
    {
        Location = location;
    }

    /// <summary>这条连接对应的配置（含主机 / 端口 / 用户名）。</summary>
    protected RemoteLocation Location { get; }

    /// <summary>日志里用的协议名。</summary>
    public abstract string Kind { get; }

    /// <summary>连上（已经连上时直接返回）。失败要抛 <see cref="RemoteAccessException" />。</summary>
    public abstract Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>列目录；路径是服务器上的绝对路径。</summary>
    public abstract Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>看某个路径是文件、目录还是不存在。</summary>
    public abstract Task<RemoteEntryKind> StatAsync(string path, CancellationToken cancellationToken);

    /// <summary>把一个文件下载到本地路径（调用方保证目标文件不存在）。</summary>
    public abstract Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken);

    /// <summary>丢掉当前连接（下一次 <see cref="ConnectAsync" /> 会重连）。</summary>
    public abstract void Reset();

    public void Dispose() => Reset();

    /// <summary>把协议异常翻译成给用户看的中文文案。</summary>
    protected RemoteAccessException Translate(Exception ex)
    {
        if (ex is RemoteAccessException remote)
        {
            return remote;
        }

        var target = $"{Location.Host}:{Location.EffectivePort}";
        return ex switch
        {
            System.Net.Sockets.SocketException => new RemoteAccessException($"无法连接 {target}：{ex.Message}", ex),
            UnauthorizedAccessException => new RemoteAccessException($"无法读取本地文件：{ex.Message}", ex),
            ArgumentException => new RemoteAccessException($"连接设置不完整：{ex.Message}", ex),
            _ => new RemoteAccessException($"{Kind} 出错（{target}）：{ex.Message}", ex),
        };
    }
}
