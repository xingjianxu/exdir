using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Models;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Exdir.Services.Remote;

/// <summary>
/// FTP / FTPS 连接（FluentFTP）。
///
/// <para>
/// 默认走被动模式（<c>PASV</c>）—— 主动模式在客户端有防火墙 / 在 NAT 后面时基本连不上；
/// FTPS 用显式 TLS（<c>AUTH TLS</c>，端口仍是 21），这也是绝大多数服务器支持的那种。
/// </para>
/// </summary>
internal sealed partial class FtpSession : RemoteSession
{
    private readonly string _password;

    private AsyncFtpClient? _client;

    public FtpSession(RemoteLocation location, string password)
        : base(location)
    {
        _password = password;
    }

    public override string Kind => Location.Protocol == RemoteProtocol.Ftps ? "FTPS" : "FTP";

    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return;
        }

        Reset();

        var config = new FtpConfig
        {
            ConnectTimeout = 20000,
            ReadTimeout = 30000,
            DataConnectionConnectTimeout = 20000,
            DataConnectionReadTimeout = 30000,
            DataConnectionType = Location.UsePassive
                ? FtpDataConnectionType.AutoPassive
                : FtpDataConnectionType.AutoActive,
            SocketKeepAlive = true,
            LogToConsole = false,

            // 自签名证书的服务器很常见（尤其是内网），所以给一个显式的“允许无效证书”开关，
            // 默认仍然是校验证书（见 RemoteLocation.AllowInvalidCertificate）
            ValidateAnyCertificate = Location.AllowInvalidCertificate,
            ValidateCertificateRevocation = false,
        };

        if (Location.Protocol == RemoteProtocol.Ftps)
        {
            config.EncryptionMode = FtpEncryptionMode.Explicit;
        }

        var client = new AsyncFtpClient(
            Location.Host,
            Location.EffectiveUserName,
            _password,
            Location.EffectivePort,
            config,
            null);

        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SafeDispose(client);
            throw TranslateConnect(ex);
        }

        _client = client;
        Log.Write($"远程已连接：{Kind} {Location.EffectiveUserName}@{Location.Host}:{Location.EffectivePort}（{Location.DisplayName}）");
    }

    public override async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var client = Client();
        var result = new List<RemoteEntry>();

        try
        {
            var items = await client.GetListing(path, cancellationToken).ConfigureAwait(true);

            foreach (var item in items)
            {
                // FTP 的 LIST 结果里 "." / ".." 通常不出现（MLSD 更不会），但个别服务器会给
                // （FluentFTP 把它们标成 SelfDirectory / ParentDirectory）
                if (item.Name is "." or ".."
                    || item.SubType is FtpObjectSubType.SelfDirectory or FtpObjectSubType.ParentDirectory)
                {
                    continue;
                }

                var isDirectory = item.Type == FtpObjectType.Directory
                    || (item.Type == FtpObjectType.Link && item.SubType == FtpObjectSubType.SubDirectory);

                result.Add(new RemoteEntry(
                    item.Name,
                    isDirectory,
                    isDirectory ? 0 : Math.Max(0, item.Size),
                    ToDateTimeOffset(item.Modified)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FtpCommandException ex) when (ex.CompletionCode == "550")
        {
            throw new RemoteAccessException($"目录不存在或没有权限：{path}", ex);
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
            if (await client.DirectoryExists(path, cancellationToken).ConfigureAwait(true))
            {
                return RemoteEntryKind.Directory;
            }

            return await client.FileExists(path, cancellationToken).ConfigureAwait(true)
                ? RemoteEntryKind.File
                : RemoteEntryKind.None;
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
            var status = await client
                .DownloadFile(localPath, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, null, cancellationToken)
                .ConfigureAwait(true);

            if (status != FtpStatus.Success)
            {
                throw new RemoteAccessException($"下载失败：{remotePath}（服务器返回 {status}）");
            }
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

        if (client is not null)
        {
            SafeDispose(client);
        }
    }

    private AsyncFtpClient Client()
        => _client is { IsConnected: true } client
            ? client
            : throw new RemoteAccessException($"{Kind} 未连接（{Location.Host}）");

    private RemoteAccessException TranslateConnect(Exception ex) => ex switch
    {
        FtpAuthenticationException => new RemoteAccessException(
            $"登录失败：用户名或密码不正确（{Location.EffectiveUserName}@{Location.Host}）", ex),
        FtpSecurityNotAvailableException => new RemoteAccessException(
            $"服务器不支持 FTPS（AUTH TLS），{Location.Host} 需要改用普通 FTP", ex),
        FtpInvalidCertificateException => new RemoteAccessException(
            $"服务器的 TLS 证书无效（{Location.Host}）；确认可信后可在连接设置里勾选“允许无效证书”", ex),
        TimeoutException => new RemoteAccessException(
            $"连接超时：{Location.Host}:{Location.EffectivePort}", ex),
        _ => Translate(ex),
    };

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value),
            // FluentFTP 的 Modified 按 UTC 解释（服务器时间，尽量换算）
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        };

    private static void SafeDispose(AsyncFtpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception)
        {
            // 断开失败无所谓：这个客户端已经不用了
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
            // 半截文件留着也不影响（下次会换个名字）
        }
    }
}
