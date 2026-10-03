using System;

namespace Exdir.Services.Remote;

/// <summary>
/// 远程位置相关的失败。消息是**给用户看的中文文案**（标签页直接把它显示在 InfoBar 上），
/// 原始异常放在 <see cref="Exception.InnerException" /> 里。
/// </summary>
public sealed class RemoteAccessException : Exception
{
    public RemoteAccessException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 远程目录里的一项。三种协议（SFTP / FTP / FTPS）的列目录结果都统一成这一种形状，
/// 上层（<c>RemoteFileService</c>）再翻译成 <c>FileSystemEntry</c>。
/// </summary>
public readonly record struct RemoteEntry(
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastWriteTime);

/// <summary>远程路径的存在性。</summary>
public enum RemoteEntryKind
{
    /// <summary>不存在（或者不可见）。</summary>
    None,

    /// <summary>文件。</summary>
    File,

    /// <summary>目录。</summary>
    Directory,
}
