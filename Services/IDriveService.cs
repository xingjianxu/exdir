using System.Collections.Generic;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>驱动器（含网络盘、可移动盘、光驱）枚举。</summary>
public interface IDriveService
{
    IReadOnlyList<DriveModel> GetDrives();

    /// <summary>
    /// 取某个路径所在卷的当前信息（容量 / 可用空间）——状态栏要的是“最新的”值，
    /// 不能复用启动时枚举出来的快照。路径所在卷无法确定时返回 null（UNC 路径就是这样）。
    /// 读盘可能很慢（断开的网络盘），调用方不要放在 UI 线程上。
    /// </summary>
    DriveModel? GetDriveForPath(string? path);
}
