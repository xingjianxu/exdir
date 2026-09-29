namespace Exdir.Models;

/// <summary>
/// 云同步目录里一个条目的同步状态（即资源管理器“状态 / 可用性”列的含义）。
/// 只对云同步根（OneDrive、WPS 云盘、坚果云等 CFAPI 同步目录）下的条目有意义。
/// </summary>
public enum CloudSyncState
{
    /// <summary>非云目录，或云盘客户端没有提供状态：列表里不显示任何图标。</summary>
    None = 0,

    /// <summary>已同步：内容在本机，且与云端一致。</summary>
    Synced,

    /// <summary>仅在云端：是个占位符，内容还没下载到本机（打开时才按需下载）。</summary>
    CloudOnly,

    /// <summary>已同步且被标记为“始终保留在此设备上”。</summary>
    Pinned,

    /// <summary>正在同步：上传 / 下载 / 传输中，或已排队等待。</summary>
    Syncing,

    /// <summary>同步失败或需要用户处理（冲突、配额不足等）。</summary>
    Error,

    /// <summary>未同步：已被排除在此设备之外（不参与同步）。</summary>
    Excluded,
}
