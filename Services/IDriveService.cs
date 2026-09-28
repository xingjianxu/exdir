using System.Collections.Generic;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>驱动器（含网络盘、可移动盘、光驱）枚举。</summary>
public interface IDriveService
{
    IReadOnlyList<DriveModel> GetDrives();
}
