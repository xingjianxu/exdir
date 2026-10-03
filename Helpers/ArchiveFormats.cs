using System;
using System.Collections.Generic;
using System.IO;
using Exdir.Services.Native;

namespace Exdir.Helpers;

/// <summary>
/// 「双击压缩包 = 进入压缩包」认哪些扩展名，以及压缩包名字里那点约定。
///
/// 扩展名清单是**故意收窄**的：只把这些当压缩包，
/// 其余（<c>.docx</c> / <c>.jar</c> / <c>.cab</c> …）仍旧交给默认程序。
/// 想加一种就在这里加一行 —— 判断入口只有 <see cref="IsCoreExtension" /> 一处。
/// </summary>
internal static class ArchiveFormats
{
    /// <summary>核心压缩格式（不带点、小写）。</summary>
    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows 常见
        "zip", "zipx", "7z", "rar",
        // Linux 常见（tar + 各种压缩）
        "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz", "zst", "tzst", "lzma", "tlz", "lz",
        // 光盘映像（只读浏览：7z.dll 的 Iso / Udf 两个处理器；Joliet/UDF 的包给出中文名也一样列）
        "iso",
        // 其它常见
        "cpio", "ar", "deb",
    };

    /// <summary>
    /// 别名 / 复合后缀 → 7z.dll 认识的扩展名。
    /// 7z.dll 的格式表里只有 <c>gz</c> / <c>bz2</c> / <c>xz</c> / <c>zst</c> 这些“压缩器本身”的扩展名，
    /// 而 <c>.tgz</c> / <c>.tbz2</c> 这类只是同一个格式的别名。
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tgz"] = "gz",
        ["tbz"] = "bz2",
        ["tbz2"] = "bz2",
        ["txz"] = "xz",
        ["tzst"] = "zst",
        ["tlz"] = "lzma",
        ["zipx"] = "zip",
        ["r00"] = "rar",
    };

    /// <summary>单文件压缩器（里面只有一个文件，通常是一个 .tar）。</summary>
    private static readonly HashSet<string> SingleFileCompressors = new(StringComparer.OrdinalIgnoreCase)
    {
        "gz", "bz2", "xz", "zst", "lzma", "lz", "z", "ppmd",
    };

    /// <summary>是不是“核心压缩格式”的扩展名（参数可以带点，也可以不带）。</summary>
    public static bool IsCoreExtension(string? extension)
        => !string.IsNullOrEmpty(extension) && Core.Contains(extension.TrimStart('.'));

    /// <summary>把别名换成 7z.dll 认识的扩展名。</summary>
    public static string ToCanonicalExtension(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return Aliases.TryGetValue(ext, out var canonical) ? canonical : ext;
    }

    /// <summary>这个扩展名对应的格式是不是单文件压缩器。</summary>
    public static bool IsSingleFileCompressor(string extension)
        => SingleFileCompressors.Contains(ToCanonicalExtension(extension));

    /// <summary>
    /// 单文件压缩器里那唯一一个条目的名字（7z.dll 对 gzip 这类格式给的是空路径）。
    /// 与 7-Zip 自己的做法一致：<c>foo.tar.gz</c> → <c>foo.tar</c>；
    /// <c>foo.tgz</c> / <c>foo.tbz</c> 这类别名 → <c>foo.tar</c>；<c>log.gz</c> → <c>log</c>。
    /// </summary>
    public static string DeriveSingleEntryName(string archiveFileName)
    {
        var name = Path.GetFileName(archiveFileName);
        if (name.Length == 0)
        {
            return "data";
        }

        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || name.Length <= ext.Length + 1)
        {
            return name;
        }

        var withoutExt = name[..^(ext.Length + 1)];

        // foo.tar.gz → foo.tar（去掉最后一个后缀就够了）；
        // foo.tgz / foo.tbz2 … → foo.tar（别名后缀换成 .tar）
        return IsTarAliasExtension(ext) ? withoutExt + ".tar" : withoutExt;
    }

    /// <summary>这个扩展名是不是“tar 别名”（<c>.tgz</c> / <c>.tbz2</c> …）。</summary>
    private static bool IsTarAliasExtension(string extension)
        => extension.ToLowerInvariant() is "tgz" or "tbz" or "tbz2" or "txz" or "tzst" or "taz";

    /// <summary>
    /// 这个压缩包里那唯一一个条目看起来是不是一个 tar（决定要不要把 <c>.tar.gz</c> 透明解开）。
    /// 只看名字：<c>foo.tar.gz</c> → <c>foo.tar</c> 是；<c>log.gz</c> → <c>log</c> 不是。
    /// </summary>
    public static bool SingleEntryLooksLikeTar(string archiveFileName, string extension)
    {
        if (!IsSingleFileCompressor(extension))
        {
            return false;
        }

        var derived = DeriveSingleEntryName(archiveFileName);
        return derived.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>找 7z.dll 里名字叫 <paramref name="formatName" /> 的处理器（例如 tar）。</summary>
    public static SevenZipHandler? FindByName(IReadOnlyList<SevenZipHandler> handlers, string formatName)
    {
        foreach (var handler in handlers)
        {
            if (string.Equals(handler.Name, formatName, StringComparison.OrdinalIgnoreCase))
            {
                return handler;
            }
        }

        return null;
    }
}
