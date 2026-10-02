using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Exdir.Models;

/// <summary>
/// 压缩包里的一个位置：一个**真实的压缩包文件** + 包内相对路径（<see cref="InnerPath" />）。
///
/// <para>
/// 它在界面上就是一条普通目录路径 —— <c>D:\dl\foo.tar.gz</c> 是压缩包根，
/// <c>D:\dl\foo.tar.gz\sub\a.txt</c> 是包内的文件（即 <see cref="FullPath" /> 就是
/// “压缩包全路径 + '\' + 包内路径”）。这样 <c>DirectoryInfo.Parent</c>、面包屑切分、
/// 会话落盘这些现成逻辑不用改就能用（见 AGENTS.md 第 4 节“压缩包只读浏览”）。
/// </para>
/// <para>
/// 包内路径一律用 <c>\</c> 分隔、不带首尾分隔符；<c>.</c> / <c>..</c> / 空段 / 冒号与控制字符
/// 会被拒绝（<see cref="NormalizeInner" /> 返回 null），免得压缩包里塞一条能跑到包外的路径。
/// </para>
/// </summary>
public sealed class ArchivePath
{
    private ArchivePath(string archiveFile, string innerPath, IReadOnlyList<string> segments)
    {
        ArchiveFile = archiveFile;
        InnerPath = innerPath;
        Segments = segments;
        FullPath = innerPath.Length == 0 ? archiveFile : archiveFile + Path.DirectorySeparatorChar + innerPath;
    }

    /// <summary>压缩包文件的完整路径（不含包内部分）。</summary>
    public string ArchiveFile { get; }

    /// <summary>包内相对路径（<c>\</c> 分隔、无首尾分隔符）；空串 = 压缩包根。</summary>
    public string InnerPath { get; }

    /// <summary>包内路径的分段（根为空集合）。</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>完整路径：压缩包全路径 + 包内路径，可以直接当目录路径用。</summary>
    public string FullPath { get; }

    /// <summary>是不是压缩包根。</summary>
    public bool IsRoot => InnerPath.Length == 0;

    /// <summary>叶子显示名（根 = 压缩包文件名）。</summary>
    public string Name => Segments.Count > 0 ? Segments[^1] : Path.GetFileName(ArchiveFile);

    public ArchivePath Child(string name) => Create(ArchiveFile, JoinInner(InnerPath, name));

    /// <summary>包内路径 + 一段（两段都必须是已经规范化过的）。</summary>
    public static string JoinInner(string innerPath, string segment)
        => string.IsNullOrEmpty(innerPath) ? segment : innerPath + Path.DirectorySeparatorChar + segment;

    /// <summary>建一个位置；<paramref name="innerPath" /> 非法时抛 <see cref="ArgumentException" />。</summary>
    public static ArchivePath Create(string archiveFile, string innerPath)
    {
        var normalized = NormalizeInner(innerPath)
            ?? throw new ArgumentException($"非法的包内路径：{innerPath}", nameof(innerPath));

        var segments = normalized.Length == 0
            ? Array.Empty<string>()
            : normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        return new ArchivePath(archiveFile, normalized, segments);
    }

    /// <summary>
    /// 规范化包内路径：<c>/</c> 统一成 <c>\</c>、去掉首尾与重复分隔符。
    /// 含 <c>.</c> / <c>..</c> / 空段之外的非法段（冒号、控制字符）时返回 null。
    /// 空串 / 只有分隔符时返回空串（= 压缩包根）。
    /// </summary>
    public static string? NormalizeInner(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var text = raw.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var parts = text.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(text.Length);

        foreach (var part in parts)
        {
            // tar 里常见的 "./" 前缀（`tar -C dir .` 造的包都是这样）：跳过，不当成非法段
            if (part == ".")
            {
                continue;
            }

            if (!IsValidSegment(part))
            {
                return null;
            }

            if (buffer.Length > 0)
            {
                buffer.Append(Path.DirectorySeparatorChar);
            }

            buffer.Append(part);
        }

        return buffer.ToString();
    }

    /// <summary>单段是否安全：不能是 <c>..</c>，不能带盘符冒号或控制字符（<c>.</c> 在调用处已经被跳过）。</summary>
    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || segment == "..")
        {
            return false;
        }

        foreach (var ch in segment)
        {
            if (ch == ':' || char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 把包内路径变成可以真正写到磁盘上的相对路径（解压到临时目录时用）：
    /// 非法文件名字符换成 <c>_</c>，末尾的空白与点去掉。
    /// </summary>
    public static string ToFileSystemPath(string innerPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = innerPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var buffer = new StringBuilder(innerPath.Length);

        foreach (var part in parts)
        {
            var clean = new StringBuilder(part.Length);
            foreach (var ch in part)
            {
                clean.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            var name = clean.ToString().TrimEnd(' ', '.');
            if (name.Length == 0)
            {
                name = "_";
            }

            if (buffer.Length > 0)
            {
                buffer.Append(Path.DirectorySeparatorChar);
            }

            buffer.Append(name);
        }

        return buffer.ToString();
    }

    public override string ToString() => FullPath;
}
