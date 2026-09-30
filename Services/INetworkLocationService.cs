using System.Collections.Generic;

namespace Exdir.Services;

/// <summary>一个「网络位置」：用资源管理器的「添加一个网络位置」创建、指向 UNC 共享的快捷方式。</summary>
/// <param name="Name">显示名称（快捷方式容器目录名，和资源管理器里看到的一致）。</param>
/// <param name="Path">目标路径，通常是 <c>\\server\share</c>（<c>target.lnk</c> 的目标）。</param>
/// <param name="Glyph">Segoe Fluent Icons 字形。</param>
public sealed record NetworkLocationModel(string Name, string Path, string Glyph);

/// <summary>Windows「网络位置」枚举。</summary>
public interface INetworkLocationService
{
    /// <summary>
    /// 当前用户的网络位置列表（<c>%APPDATA%\Microsoft\Windows\Network Shortcuts</c>）。
    /// 每一项是一个快捷方式目录，<see cref="NetworkLocationModel.Path" /> 是它 <c>target.lnk</c> 的目标。
    /// 目标解析不出来（快捷方式损坏 / 指向 shell 虚拟项）的项直接跳过。
    /// <para>
    /// 这里不判断目标是否在线：离线的网络位置在资源管理器里也照样列出来，打开时才报错。
    /// </para>
    /// </summary>
    IReadOnlyList<NetworkLocationModel> GetNetworkLocations();
}
