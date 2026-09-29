using Exdir.Models;

namespace Exdir.Helpers;

/// <summary>
/// 云同步状态 → 列表里显示的字形 / 文案。
/// 字形与资源管理器一致：绿勾 = 已同步，云朵 = 仅在云端，图钉 = 始终保留，同步箭头 = 正在同步。
/// </summary>
public static class CloudSyncStateHelper
{
    /// <summary>状态列里显示的字形（Segoe Fluent Icons）；无状态时为空串。</summary>
    public static string GetGlyph(CloudSyncState state) => state switch
    {
        CloudSyncState.Synced => "\uE930",    // Completed（圆圈对勾）
        CloudSyncState.CloudOnly => "\uE753", // Cloud
        CloudSyncState.Pinned => "\uE718",    // Pin
        CloudSyncState.Syncing => "\uE895",   // Sync
        CloudSyncState.Error => "\uEA39",     // ErrorBadge
        CloudSyncState.Excluded => "\uE711",  // Cancel
        _ => string.Empty,
    };

    /// <summary>状态的短文案（无障碍名称 / 提示）。</summary>
    public static string GetText(CloudSyncState state) => state switch
    {
        CloudSyncState.Synced => "已同步",
        CloudSyncState.CloudOnly => "仅在云端",
        CloudSyncState.Pinned => "已固定",
        CloudSyncState.Syncing => "正在同步",
        CloudSyncState.Error => "同步错误",
        CloudSyncState.Excluded => "未同步",
        _ => string.Empty,
    };

    /// <summary>悬停提示：说明这个状态对用户意味着什么。</summary>
    public static string GetTooltip(CloudSyncState state) => state switch
    {
        CloudSyncState.Synced => "已同步（内容在本机）",
        CloudSyncState.CloudOnly => "仅在云端（内容未下载到本机，打开时按需下载）",
        CloudSyncState.Pinned => "始终保留在此设备上",
        CloudSyncState.Syncing => "正在同步…",
        CloudSyncState.Error => "同步出错，需要处理",
        CloudSyncState.Excluded => "未同步（已排除在此设备之外）",
        _ => string.Empty,
    };
}
