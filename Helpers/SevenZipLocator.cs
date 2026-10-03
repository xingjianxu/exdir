using System;
using System.Collections.Generic;
using System.IO;
using Exdir.Diagnostics;
using Microsoft.Win32;

namespace Exdir.Helpers;

/// <summary>
/// 找系统上装的 7-Zip 图形界面程序（<c>7zFM.exe</c>）——“使用 7-Zip 打开”那个菜单项用。
///
/// exdir 自己只带 <c>7z.dll</c>（只读浏览引擎，见 <c>native/README.md</c>），**不带** 7-Zip 的界面程序，
/// 所以这一项依赖用户自己装的 7-Zip：找不到就让菜单项置灰（标题里写明原因），不静默失败。
///
/// 查找顺序：安装程序写的注册表 <c>Path</c> → 几个常见安装目录 → <c>PATH</c>
/// （scoop / winget / 手工加进 PATH 的安装目录都在这里被找到）。
/// 结果缓存一次：装了 7-Zip 之后要重启 exdir 才能用上（够用，省掉每次右键都扫一遍 PATH）。
/// </summary>
internal static class SevenZipLocator
{
    private const string ExeName = "7zFM.exe";

    private static readonly Lazy<string?> Located = new(Locate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary><c>7zFM.exe</c> 的完整路径；没装 7-Zip 时是 null。</summary>
    public static string? LauncherPath => Located.Value;

    /// <summary>有没有可用的 7-Zip 界面程序（菜单项据此决定置不置灰）。</summary>
    public static bool IsAvailable => Located.Value is not null;

    private static string? Locate()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    Log.Write($"7-Zip：找到 {candidate}");
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 路径里带非法字符之类，换下一个候选
            }
        }

        Log.Write($"7-Zip：没找到 {ExeName}（注册表 Path / 常见安装目录 / PATH 都试过了），「使用 7-Zip 打开」置灰");
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // 7-Zip 安装程序会把安装目录（带结尾反斜杠）写进这里；32 位版装在 64 位系统上是 WOW6432Node
        foreach (var key in new[]
                 {
                     @"HKEY_CURRENT_USER\SOFTWARE\7-Zip",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\7-Zip",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\7-Zip",
                 })
        {
            if (ReadRegistryPath(key) is { Length: > 0 } directory)
            {
                yield return Path.Combine(directory, ExeName);
            }
        }

        var directories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var directory in directories)
        {
            if (!string.IsNullOrEmpty(directory))
            {
                yield return Path.Combine(directory, "7-Zip", ExeName);
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate;

            try
            {
                candidate = Path.Combine(trimmed, ExeName);
            }
            catch (Exception)
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static string? ReadRegistryPath(string keyName)
    {
        try
        {
            // 值不存在时 GetValue 返回 null，注册表不可用时抛异常 —— 都只是“这一条候选没有”
            return Registry.GetValue(keyName, "Path", null) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
