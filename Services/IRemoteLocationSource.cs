using System.Collections.Generic;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 已配置的远程位置清单（设置窗口里增删改的那些）。
///
/// <para>
/// 单独抽一个接口而不是直接依赖 <see cref="ISettingsService" />：远程服务只关心“现在有哪些位置”，
/// 这样服务级冒烟测试（<c>tools\remote-smoke</c>）能塞一份固定清单进来，不用把整个设置体系拉起来。
/// </para>
/// </summary>
public interface IRemoteLocationSource
{
    /// <summary>
    /// 当前配置的远程位置。每次读都要取**最新**的一份（设置里改完立即生效，不需要重启）。
    /// </summary>
    IReadOnlyList<RemoteLocation> Locations { get; }

    /// <summary>按连接身份（见 <see cref="RemoteLocation.ConnectionKey" />）找位置；没有返回 null。</summary>
    RemoteLocation? Find(string connectionKey);
}
