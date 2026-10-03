using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Exdir.Services.Remote;

/// <summary>
/// SFTP 连接（SSH.NET）。
///
/// <para>
/// 认证方式：密码，或私钥文件（可带口令）。口令 / 私钥文件读不出来时给出明确文案 ——
/// 这两种失败最容易和“密码错”混为一谈。
/// </para>
/// </summary>
internal sealed partial class SftpSession : RemoteSession
{
    private readonly string _password;
    private readonly string _passphrase;

    private SftpClient? _client;

    public SftpSession(RemoteLocation location, string password, string passphrase)
        : base(location)
    {
        _password = password;
        _passphrase = passphrase;
    }

    public override string Kind => "SFTP";

    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return;
        }

        Reset();

        var client = new SftpClient(BuildConnectionInfo())
        {
            // 网络慢 / 服务器卡住时不要无限等：列目录 30 秒、传文件时靠取消令牌兜底
            OperationTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            BufferSize = 64 * 1024,
        };

        // 主机密钥：exdir 没有 known_hosts 管理界面（也不做交互式确认），所以默认接受，
        // 但把指纹写进日志 —— 用户核对过一次之后就能发现“中间人”换过密钥。
        client.HostKeyReceived += (_, e) =>
            Log.Write($"SFTP 主机密钥：{Location.Host} {e.HostKeyName} {e.KeyLength} 位 SHA256={e.FingerPrintSHA256}");

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw TranslateConnect(ex);
        }

        _client = client;
        Log.Write($"远程已连接：SFTP {Location.EffectiveUserName}@{Location.Host}:{Location.EffectivePort}（{Location.DisplayName}）");
    }

    public override async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var client = Client();
        var result = new List<RemoteEntry>();

        try
        {
            await foreach (var file in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(true))
            {
                // "." 与 ".." 是 SFTP 协议里约定的伪条目，不是真文件
                if (file.Name is "." or "..")
                {
                    continue;
                }

                var isDirectory = file.IsDirectory;

                // 符号链接：readdir 给的是 link 自身的属性（目录链接会显示成文件），
                // 交给服务器跟着链接看一次才知道目标是什么类型
                if (!isDirectory && file.IsSymbolicLink)
                {
                    isDirectory = await IsDirectoryAsync(client, file.FullName, cancellationToken).ConfigureAwait(true);
                }

                result.Add(new RemoteEntry(
                    file.Name,
                    isDirectory,
                    isDirectory ? 0 : Math.Max(0, file.Length),
                    new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc))));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SftpPermissionDeniedException ex)
        {
            throw new RemoteAccessException($"没有权限读取 {path}", ex);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new RemoteAccessException($"目录不存在：{path}", ex);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        return result;
    }

    public override async Task<RemoteEntryKind> StatAsync(string path, CancellationToken cancellationToken)
    {
        var client = Client();

        try
        {
            var attributes = await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(true);
            return attributes.IsDirectory ? RemoteEntryKind.Directory : RemoteEntryKind.File;
        }
        catch (SftpPathNotFoundException)
        {
            return RemoteEntryKind.None;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken)
    {
        var client = Client();

        try
        {
            await using var stream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await client.DownloadFileAsync(remotePath, stream, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(localPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(localPath);
            throw Translate(ex);
        }
    }

    public override void Reset()
    {
        var client = _client;
        _client = null;

        try
        {
            if (client is { IsConnected: true })
            {
                client.Disconnect();
            }
        }
        catch (Exception)
        {
            // 断开失败无所谓：反正这个客户端已经不用了
        }

        client?.Dispose();
    }

    private SftpClient Client()
        => _client is { IsConnected: true } client
            ? client
            : throw new RemoteAccessException($"SFTP 未连接（{Location.Host}）");

    private ConnectionInfo BuildConnectionInfo()
    {
        var host = Location.Host;
        var port = Location.EffectivePort;
        var user = Location.EffectiveUserName;

        if (Location.Auth != RemoteAuthMethod.PrivateKey)
        {
            return new PasswordConnectionInfo(host, port, user, _password);
        }

        if (string.IsNullOrWhiteSpace(Location.PrivateKeyPath))
        {
            throw new RemoteAccessException("这个 SFTP 位置用的是私钥登录，但没设置私钥文件");
        }

        PrivateKeyFile key;
        try
        {
            key = string.IsNullOrEmpty(_passphrase)
                ? new PrivateKeyFile(Location.PrivateKeyPath)
                : new PrivateKeyFile(Location.PrivateKeyPath, _passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new RemoteAccessException("私钥需要口令（passphrase），请在连接设置里填写", ex);
        }
        catch (Exception ex)
        {
            throw new RemoteAccessException($"读不了私钥文件：{Location.PrivateKeyPath}（{ex.Message}）", ex);
        }

        return new PrivateKeyConnectionInfo(host, port, user, key);
    }

    private RemoteAccessException TranslateConnect(Exception ex) => ex switch
    {
        SshAuthenticationException => new RemoteAccessException(
            $"登录失败：用户名或密码 / 私钥不正确（{Location.EffectiveUserName}@{Location.Host}）", ex),
        SshPassPhraseNullOrEmptyException => new RemoteAccessException(
            "私钥需要口令（passphrase），请在连接设置里填写", ex),
        SshOperationTimeoutException => new RemoteAccessException(
            $"连接超时：{Location.Host}:{Location.EffectivePort}", ex),
        SshConnectionException => new RemoteAccessException(
            $"无法连接 {Location.Host}:{Location.EffectivePort}：{ex.Message}", ex),
        _ => Translate(ex),
    };

    private static async Task<bool> IsDirectoryAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        try
        {
            var attributes = await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(true);
            return attributes.IsDirectory;
        }
        catch (Exception)
        {
            // 链接指到不存在的目标：按文件处理，双击时报错就行
            return false;
        }
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
            // 半截文件留着也不影响（下次会换个名字），清不掉就算了
        }
    }
}
