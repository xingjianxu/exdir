using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Models;
using Exdir.Services.Native;

namespace Exdir.Services;

/// <summary>
/// 系统外壳图标服务：<see cref="ShellIconExtractor"/> 提取 + 进程内两级缓存。
///
/// <list type="number">
/// <item>“图标键 → Lazy&lt;Task&gt;”：同一个键的并发请求只提取一次，后来的直接等同一个任务；</item>
/// <item>“内容哈希 → 像素”：内容相同的图标只留一份数组（列表里成百上千个普通文件夹共用一张图）。</item>
/// </list>
///
/// 键取“扩展名”而不是“完整路径”，是因为绝大多数文件的图标只由扩展名决定：
/// 一个目录里可能有几千个 .txt，为每个都去问一次外壳毫无意义，也白白多几千次文件访问。
/// 只有图标来自文件自身（或它指向的目标）的类型才按完整路径缓存，见 <see cref="PerFileExtensions"/>。
/// </summary>
public sealed class ShellIconService : IShellIconService
{
    /// <summary>图标写在文件自己（或快捷方式指向的目标）里的扩展名：这些必须按路径取。</summary>
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".ico", ".msi", ".scr", ".cpl", ".com", ".pif",
    };

    /// <summary>带“快捷方式小箭头”的扩展名（与资源管理器一致）。</summary>
    private static readonly HashSet<string> OverlayExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url",
    };

    /// <summary>键缓存上限：超过就整体丢掉（图标本身很小，重点是把无上限增长的路径键挡住）。</summary>
    private const int MaxKeys = 4096;

    /// <summary>像素缓存上限（按内容哈希去重后，正常用不到这么多）。</summary>
    private const int MaxBitmaps = 1024;

    /// <summary>日志预算：只有“第一次见到某个图标”才记一行，超过这个条数就不再记。</summary>
    private const int LogBudget = 500;

    /// <summary>失败日志预算：失败是按“图标键”记的，正常情况下根本碰不到这么多。</summary>
    private const int FailureLogBudget = 50;

    private readonly ConcurrentDictionary<string, Lazy<Task<IconBitmap?>>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, byte[]> _pixelsByHash = new();

    private int _logged;
    private int _failureLogged;

    public async Task<IconBitmap?> GetIconAsync(string path, bool isDirectory)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var key = BuildKey(path, isDirectory);

        // Lazy + ExecutionAndPublication：并发命中同一个新键时也只有一个真正去提取
        var lazy = _byKey.GetOrAdd(
            key,
            _ => new Lazy<Task<IconBitmap?>>(
                () => Task.Run(() => Extract(path, isDirectory)),
                LazyThreadSafetyMode.ExecutionAndPublication));

        // 键里可能有一整条路径（.exe/.lnk 与每个目录一个），浏览很多目录后会攒下来；
        // 图标数组本身没丢，丢的只是“键 → 图标”的映射，最多多提取几次
        if (_byKey.Count > MaxKeys)
        {
            _byKey.Clear();
        }

        try
        {
            return await lazy.Value.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"提取图标 @ {path}", ex);
            return null;
        }
    }

    /// <summary>图标键：图标随文件走的类型按路径，其余按扩展名（扩展名为空时是一类“无扩展名文件”）。</summary>
    private static string BuildKey(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            // 目录按路径：文件夹可以有自己的图标（desktop.ini 的 IconResource、云盘品牌的同步根）
            return "dir:" + path;
        }

        var extension = Path.GetExtension(path);
        return PerFileExtensions.Contains(extension)
            ? "file:" + path
            : "ext:" + extension.ToLowerInvariant();
    }

    private IconBitmap? Extract(string path, bool isDirectory)
    {
        var extension = isDirectory ? string.Empty : Path.GetExtension(path);
        var overlay = OverlayExtensions.Contains(extension);

        // 只有“按扩展名取图标”的那一类才用 SHGFI_USEFILEATTRIBUTES：
        // 它不看文件本身，所以更快，而且列表里刚被删掉/还没创建的文件也照样有图标
        var byAttributes = !isDirectory && !PerFileExtensions.Contains(extension);

        var result = ShellIconExtractor.Extract(path, byAttributes, overlay, out var failure);
        if (result is null)
        {
            if (Interlocked.Increment(ref _failureLogged) <= FailureLogBudget)
            {
                Log.Write($"外壳图标：{Path.GetFileName(path)} 提取失败（{failure}，界面退回字形）");
            }

            return null;
        }

        var value = result.Value;
        if (_pixelsByHash.Count >= MaxBitmaps)
        {
            _pixelsByHash.Clear();
        }

        var isNew = _pixelsByHash.TryAdd(value.ContentHash, value.Pixels);
        var pixels = isNew ? value.Pixels : _pixelsByHash[value.ContentHash];

        // 只在“第一次见到这个图标”时记一行：日志量因此只跟“不同图标的个数”成正比，
        // 而不是跟“看过多少个文件”成正比；tools/test-shell-icons.ps1 靠它断言两个 exe 的图标确实不同
        if (isNew && Interlocked.Increment(ref _logged) <= LogBudget)
        {
            Log.Write($"外壳图标：{Path.GetFileName(path)} {value.Width}×{value.Height} #{value.ContentHash:X8}");
        }

        return new IconBitmap(value.Width, value.Height, pixels, value.ContentHash);
    }
}
