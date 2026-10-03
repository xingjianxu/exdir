using System;
using System.Collections.Generic;
using System.Globalization;

namespace Exdir.Models;

/// <summary>
/// 解析出来的一个远程位置（协议 + 登录身份 + 绝对路径）。
/// 字符串形式是 <c>sftp://user@host:22/home/me/docs</c> —— 直接用普通路径字符串当“虚拟路径”，
/// 于是面包屑、前进后退、会话落盘、命令行调用这些都不用为远程单独写一套
/// （与压缩包的 <see cref="ArchivePath" /> 同一种思路）。
/// </summary>
/// <param name="Protocol">协议。</param>
/// <param name="UserName">登录用户名（匿名 FTP 是 <c>anonymous</c>）。</param>
/// <param name="Host">主机名或 IP（IPv6 在字符串形式里带方括号）。</param>
/// <param name="Port">端口（总是已经换算成实际端口）。</param>
/// <param name="Path">服务器上的绝对目录路径，总是以 <c>/</c> 开头、无反斜杠、无 <c>.</c>/<c>..</c> 段。</param>
public sealed record RemotePathInfo(
    RemoteProtocol Protocol,
    string UserName,
    string Host,
    int Port,
    string Path)
{
    /// <summary>协议名（<c>sftp</c> / <c>ftp</c> / <c>ftps</c>）。</summary>
    public string Scheme => RemotePath.SchemeOf(Protocol);

    /// <summary><c>user@host:port</c> 形式的登录身份。</summary>
    public string Authority => RemotePath.AuthorityOf(Protocol, UserName, Host, Port);

    /// <summary>连接身份：同一个配置（含凭据）只认这一串。</summary>
    public string ConnectionKey => RemotePath.ConnectionKeyOf(Protocol, UserName, Host, Port);

    /// <summary>这个远程位置的根路径（<c>sftp://user@host:22/</c>）。</summary>
    public string Root => RemotePath.Build(Protocol, UserName, Host, Port, "/");

    /// <summary>换一个目录、保留登录身份。</summary>
    public RemotePathInfo WithPath(string path) => this with { Path = RemotePath.NormalizePath(path) };

    /// <summary>完整路径字符串。</summary>
    public override string ToString() => RemotePath.Build(Protocol, UserName, Host, Port, Path);
}

/// <summary>
/// 远程路径的解析 / 拼接 / 规整。
///
/// <para>
/// 只认三种协议头（<c>sftp://</c> / <c>ftp://</c> / <c>ftps://</c>），拿不到协议头就当不是远程路径 ——
/// 这样它天然不会和 Windows 路径（含 <c>C:\</c> 与 <c>\\server\share</c>）、压缩包虚拟路径打架。
/// </para>
/// </summary>
public static class RemotePath
{
    public const string SftpScheme = "sftp";
    public const string FtpScheme = "ftp";
    public const string FtpsScheme = "ftps";

    /// <summary>协议默认端口。</summary>
    public static int DefaultPort(RemoteProtocol protocol)
        => protocol == RemoteProtocol.Sftp ? 22 : 21;

    /// <summary>协议名。</summary>
    public static string SchemeOf(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => SftpScheme,
        RemoteProtocol.Ftps => FtpsScheme,
        _ => FtpScheme,
    };

    /// <summary>协议名 → 枚举；不认识时返回 false。</summary>
    public static bool TryParseProtocol(string? scheme, out RemoteProtocol protocol)
    {
        switch (scheme?.ToLowerInvariant())
        {
            case SftpScheme:
                protocol = RemoteProtocol.Sftp;
                return true;
            case FtpScheme:
                protocol = RemoteProtocol.Ftp;
                return true;
            case FtpsScheme:
                protocol = RemoteProtocol.Ftps;
                return true;
            default:
                protocol = RemoteProtocol.Sftp;
                return false;
        }
    }

    /// <summary>路径是不是远程路径（只看协议头，不校验内容也不联网）。</summary>
    public static bool LooksRemote(string? path)
        => path is not null && TryFindScheme(path, out _, out _);

    /// <summary>解析远程路径；不是远程路径 / 写坏了 / 端口非法时返回 false。</summary>
    public static bool TryParse(string? path, out RemotePathInfo info)
    {
        info = null!;

        if (!TryFindScheme(path, out var protocol, out var rest))
        {
            return false;
        }

        // rest 形如 "user@host:22/home/me"（主机与路径之间的第一个 '/' 是分界）
        var authority = rest;
        var rawPath = "/";

        var slash = rest.IndexOf('/');
        if (slash >= 0)
        {
            authority = rest[..slash];
            rawPath = rest[slash..];
        }

        if (!TryParseAuthority(authority, protocol, out var user, out var host, out var port))
        {
            return false;
        }

        info = new RemotePathInfo(protocol, user, host, port, NormalizePath(rawPath));
        return true;
    }

    /// <summary>
    /// 把绝对路径规整成“服务器上的路径”：反斜杠统一成 <c>/</c>、去掉空段与 <c>.</c>、
    /// 就地消掉 <c>..</c>（不会退到根之上），最后总以 <c>/</c> 开头且不以 <c>/</c> 结尾。
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var segments = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.None))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(part);
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>拼出完整路径字符串。</summary>
    public static string Build(RemoteProtocol protocol, string userName, string host, int port, string absolutePath)
        => $"{SchemeOf(protocol)}://{AuthorityOf(protocol, userName, host, port)}{NormalizePath(absolutePath)}";

    /// <summary>
    /// <c>user@host:port</c>。主机是 IPv6（含冒号）时加方括号；
    /// 用户名为空时不写 <c>@</c>；端口就是该协议的默认值时省略（地址栏里更干净）。
    /// </summary>
    public static string AuthorityOf(RemoteProtocol protocol, string userName, string host, int port)
    {
        var literal = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var user = string.IsNullOrEmpty(userName) ? string.Empty : userName + "@";

        return user + literal + PortSuffix(protocol, port);
    }

    /// <summary>连在主机后面的端口后缀；就是协议默认端口时为空串。</summary>
    public static string PortSuffix(RemoteProtocol protocol, int port)
        => port == DefaultPort(protocol) ? string.Empty : ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>连接身份：协议 + 用户名 + 主机 + 端口（用户名 / 主机大小写不敏感）。</summary>
    public static string ConnectionKeyOf(RemoteProtocol protocol, string userName, string host, int port)
        => string.Join(
            '|',
            SchemeOf(protocol),
            userName?.ToLowerInvariant() ?? string.Empty,
            host?.ToLowerInvariant() ?? string.Empty,
            port.ToString(CultureInfo.InvariantCulture));

    /// <summary>上一层目录；已经在根目录时返回 null。</summary>
    public static string? GetParent(string absolutePath)
    {
        var normalized = NormalizePath(absolutePath);
        if (normalized == "/")
        {
            return null;
        }

        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    /// <summary>
    /// 面包屑分段：第 0 段是“登录身份”那个根（<c>sftp://user@host</c>），后面每级目录一段。
    /// <see cref="string.Empty" /> 路径（“此电脑”）不会走到这里。
    /// </summary>
    public static IReadOnlyList<(string Display, string FullPath)> Segments(RemotePathInfo info)
    {
        var result = new List<(string Display, string FullPath)>
        {
            // 根那一段显示成 user@host（sftp）或 host（ftp），端口非默认时才带上
            (RootDisplayOf(info), info.Root),
        };

        var root = info.Root.TrimEnd('/');
        var current = root;

        foreach (var name in info.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current + "/" + name;
            result.Add((name, current));
        }

        return result;
    }

    /// <summary>面包屑第一段 / 侧边栏里显示的身份文本。</summary>
    public static string RootDisplayOf(RemotePathInfo info)
    {
        var user = string.IsNullOrEmpty(info.UserName) ? string.Empty : info.UserName + "@";
        var host = info.Host.Contains(':', StringComparison.Ordinal) ? $"[{info.Host}]" : info.Host;

        return user + host + PortSuffix(info.Protocol, info.Port);
    }

    /// <summary>目录名（路径最后一段）；根目录返回 <c>/</c>。</summary>
    public static string NameOf(RemotePathInfo info)
    {
        var normalized = NormalizePath(info.Path);
        if (normalized == "/")
        {
            return "/";
        }

        var slash = normalized.LastIndexOf('/');
        return normalized[(slash + 1)..];
    }

    /// <summary>
    /// 把协议头从路径里切出来。成功时 <paramref name="rest" /> 是 <c>://</c> 之后的部分。
    /// 用“冒号在 <c>://</c> 之前且协议名只含字母数字”来判断，避免把 <c>C:\</c> 之类当远程路径。
    /// </summary>
    private static bool TryFindScheme(string? path, out RemoteProtocol protocol, out string rest)
    {
        protocol = RemoteProtocol.Sftp;
        rest = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var mark = path.IndexOf("://", StringComparison.Ordinal);
        if (mark <= 0)
        {
            return false;
        }

        var scheme = path[..mark];
        for (var i = 0; i < scheme.Length; i++)
        {
            if (!char.IsLetterOrDigit(scheme[i]))
            {
                return false;
            }
        }

        if (!TryParseProtocol(scheme, out protocol))
        {
            return false;
        }

        rest = path[(mark + 3)..];
        return true;
    }

    /// <summary>解析 <c>user@host:port</c>（IPv6 用方括号）；端口缺省时用协议默认端口。</summary>
    private static bool TryParseAuthority(
        string authority,
        RemoteProtocol protocol,
        out string userName,
        out string host,
        out int port)
    {
        userName = string.Empty;
        host = string.Empty;
        port = DefaultPort(protocol);

        if (string.IsNullOrEmpty(authority))
        {
            return false;
        }

        var hostPort = authority;

        // 用户名里允许出现 '\'（域账号），但 '@' 只能有一个分隔作用，所以从最后一个 '@' 切
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            userName = authority[..at];
            hostPort = authority[(at + 1)..];
        }

        if (hostPort.Length == 0)
        {
            return false;
        }

        if (hostPort[0] == '[')
        {
            var close = hostPort.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = hostPort[1..close];
            var tail = hostPort[(close + 1)..];

            if (tail.Length > 0)
            {
                if (tail[0] != ':' || !TryParsePort(tail[1..], out port))
                {
                    return false;
                }
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon >= 0)
            {
                host = hostPort[..colon];
                if (!TryParsePort(hostPort[(colon + 1)..], out port))
                {
                    return false;
                }
            }
            else
            {
                host = hostPort;
            }
        }

        return host.Length > 0;
    }

    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
           && port > 0
           && port <= 65535;
}
