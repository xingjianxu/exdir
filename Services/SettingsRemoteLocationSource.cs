using System.Collections.Generic;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 从应用设置里取远程位置清单（生产环境用的那一份实现）。
///
/// <para>
/// 与接口分开两个文件，是为了让服务级冒烟测试（<c>tools\remote-smoke</c>）只编接口那一个文件 ——
/// 不然会把整个设置体系（含 CommunityToolkit 的 <c>ColumnLayout</c>）也拖进去。
/// </para>
/// </summary>
public sealed class SettingsRemoteLocationSource : IRemoteLocationSource
{
    private readonly ISettingsService _settings;

    public SettingsRemoteLocationSource(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>每次读都取最新的一份（设置里改完立即生效，见 <see cref="IRemoteLocationSource" />）。</summary>
    public IReadOnlyList<RemoteLocation> Locations => _settings.Current.RemoteLocations;

    public RemoteLocation? Find(string connectionKey)
    {
        foreach (var location in _settings.Current.RemoteLocations)
        {
            if (string.Equals(location.ConnectionKey, connectionKey, System.StringComparison.Ordinal))
            {
                return location;
            }
        }

        return null;
    }
}
