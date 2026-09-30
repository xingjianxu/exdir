using System;
using System.Collections.Generic;
using System.IO;
using Exdir.Helpers;
using Exdir.Services.Native;

namespace Exdir.Services;

/// <inheritdoc cref="INetworkLocationService" />
/// <remarks>
/// 「网络位置」在磁盘上的形态是 <c>%APPDATA%\Microsoft\Windows\Network Shortcuts</c> 下的一个子目录，
/// 里面放一个隐藏的 <c>target.lnk</c>（目标为 <c>\\server\share</c>）与一个 <c>desktop.ini</c>。
/// 目录名就是资源管理器里显示的名字。
/// 非打包应用同样不能用 <c>Windows.Storage</c> 那套 shell 命名空间枚举，所以这里直接读目录 + 解快捷方式。
/// </remarks>
public sealed class NetworkLocationService : INetworkLocationService
{
    private static readonly string ShortcutsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Network Shortcuts");

    public IReadOnlyList<NetworkLocationModel> GetNetworkLocations()
    {
        var result = new List<NetworkLocationModel>();

        try
        {
            if (!Directory.Exists(ShortcutsDirectory))
            {
                return result;
            }

            // 侧边栏节点的身份是目标路径，两个不同名字指向同一个共享时只留一个
            // （否则 RefreshDrives 的差量更新对同一路径会拿到同一个节点，行为看着像“少了一个”）。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in Directory.EnumerateDirectories(ShortcutsDirectory))
            {
                var name = Path.GetFileName(
                    directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var target = ShellLinkInterop.ResolveTarget(Path.Combine(directory, "target.lnk"));
                if (string.IsNullOrWhiteSpace(target) || !seen.Add(target))
                {
                    continue;
                }

                result.Add(new NetworkLocationModel(name, target, FileTypeHelper.NetworkGlyph));
            }
        }
        catch (Exception)
        {
            // 枚举失败（权限 / 目录刚被删）时返回已经收集到的部分
        }

        result.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }
}
