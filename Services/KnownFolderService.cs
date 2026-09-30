using System;
using System.Collections.Generic;
using System.IO;
using Exdir.Helpers;
using Microsoft.Win32;

namespace Exdir.Services;

/// <inheritdoc cref="IKnownFolderService" />
/// <remarks>
/// 非打包（unpackaged）应用无法直接使用 <c>Windows.Storage.KnownFolders</c>，
/// 因此这里改用 Win32/注册表等价实现：
///   1) <see cref="Environment.GetFolderPath(Environment.SpecialFolder)" /> 获取标准目录；
///   2) OneDrive 通过环境变量 + <c>HKCU\Software\Microsoft\OneDrive\Accounts</c> 探测；
///   3) 其它云盘通过 <c>HKLM\...\Explorer\SyncRootManager</c> 的 <c>UserSyncRoots</c> 探测。
/// </remarks>
public sealed class KnownFolderService : IKnownFolderService
{
    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public IReadOnlyList<SpecialFolderModel> GetUserFolders()
    {
        var result = new List<SpecialFolderModel>();

        Add(result, "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FileTypeHelper.DesktopGlyph, UserFolderKey.Desktop);
        Add(result, "文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FileTypeHelper.DocumentGlyph, UserFolderKey.Documents);
        Add(result, "下载", Path.Combine(UserProfile, "Downloads"), FileTypeHelper.DownloadGlyph, UserFolderKey.Downloads);
        Add(result, "图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), FileTypeHelper.PictureGlyph, UserFolderKey.Pictures);
        Add(result, "音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), FileTypeHelper.MusicGlyph, UserFolderKey.Music);
        Add(result, "视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), FileTypeHelper.VideoGlyph, UserFolderKey.Videos);

        return result;
    }

    public IReadOnlyList<SpecialFolderModel> GetCloudFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SpecialFolderModel>();

        // --- OneDrive：环境变量 ---
        foreach (var variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                AddCloud(result, seen, "OneDrive", value);
            }
        }

        // --- OneDrive：账号注册表 ---
        TryReadOneDriveAccounts(result, seen);

        // --- 其它同步提供程序（Google Drive / Dropbox / iCloud / 第三方同步客户端）---
        TryReadSyncRootManager(result, seen);

        return result;
    }

    public IReadOnlyList<string> GetDefaultPinnedFolders()
    {
        var candidates = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(UserProfile, "Downloads"),
        };

        foreach (var cloud in GetCloudFolders())
        {
            candidates.Add(cloud.Path);
            break;
        }

        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static void Add(List<SpecialFolderModel> target, string name, string path, string glyph, UserFolderKey key)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            target.Add(new SpecialFolderModel(name, path, glyph, SpecialFolderKind.UserFolder, key));
        }
    }

    private static void AddCloud(List<SpecialFolderModel> target, HashSet<string> seen, string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            full = new DirectoryInfo(path).FullName;
        }
        catch (Exception)
        {
            return;
        }

        if (!seen.Add(full))
        {
            return;
        }

        target.Add(new SpecialFolderModel(name, full, FileTypeHelper.CloudGlyph, SpecialFolderKind.CloudStorage));
    }

    private static void TryReadOneDriveAccounts(List<SpecialFolderModel> target, HashSet<string> seen)
    {
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts is null)
            {
                return;
            }

            foreach (var subName in accounts.GetSubKeyNames())
            {
                using var account = accounts.OpenSubKey(subName);
                if (account?.GetValue("UserFolder") is string folder)
                {
                    AddCloud(target, seen, "OneDrive", folder);
                }
            }
        }
        catch (Exception)
        {
            // 注册表不可用时忽略
        }
    }

    private static void TryReadSyncRootManager(List<SpecialFolderModel> target, HashSet<string> seen)
    {
        try
        {
            using var roots = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (roots is null)
            {
                return;
            }

            foreach (var providerName in roots.GetSubKeyNames())
            {
                using var provider = roots.OpenSubKey(providerName);
                if (provider is null)
                {
                    continue;
                }

                using var userRoots = provider.OpenSubKey("UserSyncRoots");
                if (userRoots is null)
                {
                    continue;
                }

                var displayName = ResolveDisplayName(provider)
                    ?? (providerName.IndexOf('!') is var bang && bang > 0 ? providerName[..bang] : providerName);

                foreach (var valueName in userRoots.GetValueNames())
                {
                    if (userRoots.GetValue(valueName) is string path)
                    {
                        // 同步根的注册表项名往往只是一个内部标识（如 sync!S-1-5-...!Personal），
                        // 因此优先使用文件夹自身的名字，用户一眼就能认出来。
                        var leaf = GetLeafName(path);
                        AddCloud(target, seen, string.IsNullOrEmpty(leaf) ? displayName : leaf, path);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 忽略：没有同步根目录或权限不足
        }
    }

    /// <summary>取路径的最后一段作为显示名；失败返回空字符串。</summary>
    private static string GetLeafName(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>尝试解析同步提供程序的友好名称，解析不到就回退到注册表项名。</summary>
    private static string? ResolveDisplayName(RegistryKey provider)
    {
        try
        {
            if (provider.GetValue("DisplayNameResource") is string resource && !string.IsNullOrWhiteSpace(resource))
            {
                // 形如 "@%SystemRoot%\system32\shell32.dll,-34582"：取分号前的标识，保留可读部分
                var hash = resource.IndexOf('#');
                var value = hash >= 0 ? resource[(hash + 1)..] : resource;
                var comma = value.LastIndexOf(',');
                if (comma > 0)
                {
                    value = value[..comma];
                }

                value = value.TrimStart('@');
                var slash = value.LastIndexOf('\\');
                if (slash >= 0 && slash < value.Length - 1)
                {
                    value = value[(slash + 1)..];
                }

                value = Path.GetFileNameWithoutExtension(value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (provider.GetValue("DisplayName") is string display && !string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        return null;
    }
}
