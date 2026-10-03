using System;
using System.Collections.Generic;
using System.IO;

namespace Exdir.Helpers;

/// <summary>
/// 远程位置用的本地中转目录（<c>%LOCALAPPDATA%\exdir\remote-cache</c>），
/// 与压缩包的 <c>archive-cache</c> 同一种思路、同一套清理规则：
///
/// <list type="bullet">
/// <item><c>open</c> —— 双击远程文件时下载的副本，交给默认程序打开。用户可能正开着它，
///       所以只清**一天前**的（见 <see cref="Cleanup" />）；</item>
/// <item><c>drag</c> —— 把远程条目拖出去时下载的副本。别的程序可能还在拷，
///       同样只清一天前的；拖进 exdir 自己的窗格时由
///       <c>MainViewModel.OnFileOperationCompleted</c> → <see cref="ReleaseStagingFor" /> 立即回收；</item>
/// <item><c>copy</c> —— 「复制到剪贴板 / 粘贴到本地目录」的中转副本，复制一结束就删，
///       启动时的清理可以无脑全删。</item>
/// </list>
/// </summary>
public static class RemoteCache
{
    public const string OpenCategory = "open";
    public const string CopyCategory = "copy";
    public const string DragCategory = "drag";

    private static string? _root;

    /// <summary>中转目录根（第一次用时才建）。</summary>
    public static string Root
    {
        get
        {
            if (_root is not null)
            {
                return _root;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _root = Path.Combine(local, "exdir", "remote-cache");
            return _root;
        }
    }

    /// <summary>
    /// 双击打开远程文件时的本地副本路径：<c>open\&lt;路径哈希&gt;\&lt;文件名&gt;</c>。
    /// 同一个远程文件重复打开会落到同一个路径（<paramref name="expectedSize" /> 与已有文件一致时直接复用）。
    /// </summary>
    public static string OpenPathFor(string remotePath, string fileName, long expectedSize)
    {
        var directory = Path.Combine(Root, OpenCategory, HashOf(remotePath));
        var target = Path.Combine(directory, SanitizeFileName(fileName));

        try
        {
            // 同名且大小一致就复用（上次打开留下的副本；资源管理器也是这么做的）
            if (expectedSize >= 0 && File.Exists(target) && new FileInfo(target).Length == expectedSize)
            {
                return target;
            }
        }
        catch (Exception)
        {
            // 读不到就当要重新下载
        }

        Directory.CreateDirectory(directory);
        return target;
    }

    /// <summary>新建一个中转目录（<c>copy\&lt;guid&gt;</c> 或 <c>drag\&lt;guid&gt;</c>）。</summary>
    public static string NewStaging(string category)
    {
        var directory = Path.Combine(Root, category, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>删掉一个中转目录（只认自家根下的路径）。</summary>
    public static void Release(string stagingDirectory)
    {
        if (string.IsNullOrEmpty(stagingDirectory) || !IsUnder(Root, stagingDirectory))
        {
            return;
        }

        TryDeleteDirectory(stagingDirectory);
    }

    /// <summary>
    /// 复制 / 移动结束后按“交出去的本地路径”回收拖拽中转目录
    /// （与压缩包的 <c>ReleaseStagingFor</c> 一样：只认 <c>drag</c> 分类，<c>copy</c> 分类由调用方自己删）。
    /// </summary>
    public static void ReleaseStagingFor(IReadOnlyList<string> localPaths)
    {
        if (localPaths.Count == 0)
        {
            return;
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // copy 与 drag 两类都收：交出去的都是“复制完就没用的临时副本”，
        // 而调用方（文件操作完成事件）拿到的只是源路径，分不出当时是拖拽还是走的剪贴板
        foreach (var category in new[] { CopyCategory, DragCategory })
        {
            var categoryRoot = Path.Combine(Root, category) + Path.DirectorySeparatorChar;

            foreach (var path in localPaths)
            {
                if (StagingRootOf(path, categoryRoot) is { } staging)
                {
                    roots.Add(staging);
                }
            }
        }

        foreach (var staging in roots)
        {
            Release(staging);
        }
    }

    /// <summary>
    /// 启动与退出各调一次：<c>copy</c> 直接清空（它只有“用完就删”这一种命运），
    /// <c>open</c> / <c>drag</c> 只删一天前的。
    /// </summary>
    public static void Cleanup()
    {
        TryDeleteDirectory(Path.Combine(Root, CopyCategory));
        SweepOldEntries(Path.Combine(Root, OpenCategory));
        SweepOldEntries(Path.Combine(Root, DragCategory));
    }

    /// <summary>本地路径属于哪个中转根（不在 <c>drag</c> / <c>copy</c> 下时返回 null）。</summary>
    private static string? StagingRootOf(string path, string categoryRoot)
    {
        if (!path.StartsWith(categoryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = path[categoryRoot.Length..];
        var separator = rest.IndexOf(Path.DirectorySeparatorChar);
        var name = separator < 0 ? rest : rest[..separator];

        return name.Length == 0 ? null : Path.Combine(categoryRoot.TrimEnd(Path.DirectorySeparatorChar), name);
    }

    private static void SweepOldEntries(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);

            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception)
                {
                    // 被别的程序占着就下次再说
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响使用
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 被占用就下次再说
        }
    }

    /// <summary>路径的稳定短哈希（FNV-1a 64 位，不能用 <c>string.GetHashCode</c> —— 每次进程都不一样）。</summary>
    private static string HashOf(string path)
    {
        var hash = unchecked((ulong)14695981039346656037);

        foreach (var ch in path)
        {
            hash = (hash ^ ch) * 1099511628211;
        }

        return hash.ToString("X16");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        var result = new string(chars).Trim();
        return result.Length == 0 ? "download" : result;
    }

    private static bool IsUnder(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
