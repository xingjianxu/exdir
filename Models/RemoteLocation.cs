using System;
using System.Text.Json.Serialization;

namespace Exdir.Models;

/// <summary>
/// 远程位置用的协议。
///
/// ⚠ 这个枚举会被写进 <c>config.json</c>（存的是数字），**只能往后加，不能改顺序**。
/// </summary>
public enum RemoteProtocol
{
    /// <summary>SSH 文件传输（默认端口 22）。</summary>
    Sftp = 0,

    /// <summary>明文 FTP（默认端口 21）。</summary>
    Ftp = 1,

    /// <summary>显式 TLS 的 FTP（FTPS，<c>AUTH TLS</c>，默认端口 21）。</summary>
    Ftps = 2,
}

/// <summary>
/// 登录方式。同样会写进 <c>config.json</c>，只能往后加。
/// </summary>
public enum RemoteAuthMethod
{
    /// <summary>用户名 + 密码（FTP / FTPS / SFTP 都支持）。</summary>
    Password = 0,

    /// <summary>用户名 + 私钥文件（可带口令），只对 SFTP 有意义。</summary>
    PrivateKey = 1,

    /// <summary>匿名登录（只有 FTP 有意义；用户名固定用 <c>anonymous</c>）。</summary>
    Anonymous = 2,
}

/// <summary>
/// 一个用户配置好的远程位置（SFTP / FTP / FTPS）。
///
/// <para>
/// 密码与私钥口令**不存明文**：写入时经 DPAPI（当前用户范围）加密成 Base64 存进
/// <see cref="ProtectedPassword" /> / <see cref="ProtectedPassphrase" />，
/// 见 <see cref="Exdir.Helpers.SecretProtector" />。换 Windows 用户或换机器后解不开，
/// 此时按“没填密码”处理并提示重新填写。
/// </para>
/// </summary>
public sealed class RemoteLocation
{
    /// <summary>稳定标识：设置窗口里的行、路径与配置的对应关系都靠它（改名不换 Id）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>侧边栏 / 设置里显示的名字。</summary>
    public string Name { get; set; } = string.Empty;

    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sftp;

    public string Host { get; set; } = string.Empty;

    /// <summary>端口；0 表示用协议默认端口（SFTP 22 / FTP 21）。</summary>
    public int Port { get; set; }

    public string UserName { get; set; } = string.Empty;

    public RemoteAuthMethod Auth { get; set; } = RemoteAuthMethod.Password;

    /// <summary>DPAPI 加密后的密码（Base64）。空 = 没有保存密码。</summary>
    public string ProtectedPassword { get; set; } = string.Empty;

    /// <summary>SFTP 私钥文件路径（<see cref="RemoteAuthMethod.PrivateKey" /> 时使用）。</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的私钥口令（Base64，可空）。</summary>
    public string ProtectedPassphrase { get; set; } = string.Empty;

    /// <summary>打开这个位置时先进入哪个目录；默认根目录。</summary>
    public string StartPath { get; set; } = "/";

    /// <summary>FTP 用被动模式（默认开；主动模式在 NAT 后面基本不可用）。</summary>
    public bool UsePassive { get; set; } = true;

    /// <summary>
    /// FTPS：允许服务器的 TLS 证书无效（自签名 / 主机名不匹配）。默认**关**（校验证书）；
    /// 内网自建 FTP 服务器几乎都用自签名证书，所以给用户一个显式开关。
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>实际要连的端口（把 0 换算成协议默认端口）。</summary>
    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : RemotePath.DefaultPort(Protocol);

    /// <summary>真正发起连接时用的用户名（匿名 FTP 固定 <c>anonymous</c>）。</summary>
    [JsonIgnore]
    public string EffectiveUserName => Auth == RemoteAuthMethod.Anonymous
        ? "anonymous"
        : UserName;

    /// <summary>
    /// 连接身份（协议 + 用户名 + 主机 + 端口）。
    /// 远程路径里只带这些信息，靠它把路径反查回这份配置（含凭据）。
    /// </summary>
    [JsonIgnore]
    public string ConnectionKey
        => RemotePath.ConnectionKeyOf(Protocol, EffectiveUserName, Host, EffectivePort);

    /// <summary>这个位置在字符串形式下的根路径（<c>sftp://user@host:22/</c>）。</summary>
    [JsonIgnore]
    public string RootPath
        => RemotePath.Build(Protocol, EffectiveUserName, Host, EffectivePort, "/");

    /// <summary>打开时进入的目录（<see cref="StartPath" /> 为空或非法时退回根目录）。</summary>
    [JsonIgnore]
    public string EntryPath
    {
        get
        {
            var normalized = RemotePath.NormalizePath(StartPath);
            return RootPath.TrimEnd('/') + normalized;
        }
    }

    /// <summary>侧边栏 / 设置里那行显示的副标题（<c>sftp://user@host:22</c>）。</summary>
    [JsonIgnore]
    public string DisplayTarget
    {
        get
        {
            var authority = RemotePath.AuthorityOf(Protocol, EffectiveUserName, Host, EffectivePort);
            return $"{RemotePath.SchemeOf(Protocol)}://{authority}";
        }
    }

    /// <summary>名字空着时用连接身份当显示名（用户可能懒得填）。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DisplayTarget : Name;

    /// <summary>
    /// 复制一份（设置窗口编辑的是自己的副本，只有「应用设置」那一步才写回 config.json；
    /// 直接用同一批对象会让“改完了又取消”变成“已经改了”）。
    /// </summary>
    public RemoteLocation Clone() => (RemoteLocation)MemberwiseClone();
}
