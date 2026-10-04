using System.Collections.Generic;

namespace Exdir.Models;

/// <summary>「最新访问」里的一条记录：一个最近访问过的目录或文件。</summary>
public sealed class RecentEntry
{
    /// <summary>绝对路径（本地目录 / 文件，或远程位置；压缩包内的虚拟路径不记）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 记录时它是目录还是文件。
    /// 记下来而不是每次去问文件系统：远程位置的条目要联网才知道类型，
    /// 而且列表里“目录双击进去、文件双击打开”得当场决定。
    /// </summary>
    public bool IsDirectory { get; set; }
}

/// <summary>
/// 「最新访问」列表的落盘结构（<c>%USERPROFILE%\.local\share\exdir\recents.json</c>，见
/// <see cref="Exdir.Services.IRecentItemsService" />）。
///
/// 单独一个文件、不塞进 <c>config.json</c>：它是纯粹的使用痕迹（导航 / 开文件一次就变一次），
/// 和用户显式配置分开，方便单独删除、也不会让设置文件被频繁改写。
/// </summary>
public sealed class RecentItems
{
    /// <summary>最近访问过的目录与文件，**最前面的是最新的一次**（列表顺序就是访问时间倒序）。</summary>
    public List<RecentEntry> Entries { get; set; } = new();

    /// <summary>
    /// 旧格式（只有目录路径，没有类型）。读进来后由 <c>RecentItemsService</c> 迁移进
    /// <see cref="Entries" />，写回时这个字段是 null、不会再出现在文件里。
    /// </summary>
    public List<string>? Folders { get; set; }
}
