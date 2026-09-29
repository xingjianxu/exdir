using System;
using System.Collections.Generic;
using System.IO;
using Exdir.Models;
using Exdir.Services.Native;

namespace Exdir.Services;

/// <summary>云同步根下文件的同步状态查询（列表里的“状态”列）。</summary>
public interface ICloudSyncService
{
    /// <summary>路径是否位于某个云同步根（OneDrive / WPS 云盘 / 其它 CFAPI 同步目录）之下。</summary>
    bool IsCloudPath(string path);

    /// <summary>
    /// 读取一个条目的同步状态。<paramref name="attributes"/> 由目录枚举顺带得到，用于属性存储读不到时兜底。
    /// 读不到任何信息时返回 <see cref="CloudSyncState.None"/>（列表里不显示图标）。
    /// </summary>
    CloudSyncState GetState(string path, FileAttributes attributes);
}

/// <inheritdoc cref="ICloudSyncService" />
/// <remarks>
/// 状态来源按可靠性排序：
/// <list type="number">
/// <item><c>System.StorageProviderState</c>：资源管理器“状态”列同源，能区分全部状态；</item>
/// <item><c>System.FilePlaceholderStatus</c> 占位符状态位：老客户端没写可用性状态时用它判断
///       “已同步 / 仅在云端 / 正在同步”；</item>
/// <item>文件属性位（offline / recall-on-data-access / pinned）：最后的兜底。</item>
/// </list>
/// 读取属性存储比目录枚举贵得多，因此只在云同步根之下才做（普通目录一次字符串比较就排除了）。
/// </remarks>
public sealed class CloudSyncService : ICloudSyncService
{
    /// <summary>PS_CLOUDFILE_PLACEHOLDER：这个条目是云占位符。</summary>
    private const uint PlaceholderCloudFile = 0x8;

    /// <summary>PS_FULL_PRIMARY_STREAM_AVAILABLE：内容已经完整地在本机。</summary>
    private const uint PlaceholderFullStreamAvailable = 0x2;

    /// <summary>PS_MARKED_FOR_OFFLINE_AVAILABILITY：已标记为要在本机保留。</summary>
    private const uint PlaceholderMarkedForOffline = 0x1;

    // 占位符相关属性位；.NET 的 FileAttributes 不保证为这些高位位提供枚举名，所以用原始值
    private const int AttributeRecallOnOpen = 0x0004_0000;
    private const int AttributePinned = 0x0008_0000;
    private const int AttributeRecallOnDataAccess = 0x0040_0000;

    private readonly Lazy<string[]> _rootPrefixes;

    public CloudSyncService(IKnownFolderService knownFolders)
        => _rootPrefixes = new Lazy<string[]>(() => BuildRootPrefixes(knownFolders));

    public bool IsCloudPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var prefixes = _rootPrefixes.Value;
        if (prefixes.Length == 0)
        {
            return false;
        }

        // 统一补尾分隔符，避免 "C:\sync" 误判成 "C:\sync2" 的子目录
        var candidate = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var prefix in prefixes)
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public CloudSyncState GetState(string path, FileAttributes attributes)
    {
        if (!IsCloudPath(path))
        {
            return CloudSyncState.None;
        }

        if (!ShellPropertyStore.TryGetCloudStates(path, out var placeholder, out var provider))
        {
            // 属性存储都建不起来：只能看文件属性
            return FromAttributes(attributes);
        }

        var state = MapProviderState(provider);
        if (state != CloudSyncState.None)
        {
            return state;
        }

        // 提供程序没写可用性状态（老客户端）：用占位符状态位兜底
        if ((placeholder & PlaceholderMarkedForOffline) != 0)
        {
            // 标记了要在本机保留、但还没给可用性状态：按“同步挂起”处理（与资源管理器一致）
            return CloudSyncState.Syncing;
        }

        if ((placeholder & PlaceholderCloudFile) != 0)
        {
            return (placeholder & PlaceholderFullStreamAvailable) != 0
                ? FromAttributes(attributes)
                : CloudSyncState.CloudOnly;
        }

        return FromAttributes(attributes);
    }

    /// <summary>把 System.StorageProviderState 映射成界面状态；未知/未提供返回 None。</summary>
    private static CloudSyncState MapProviderState(uint state) => state switch
    {
        1 => CloudSyncState.CloudOnly,                 // 仅在联机时可用（占位符，内容不在本机）
        2 => CloudSyncState.Synced,                    // 在本机可用
        3 => CloudSyncState.Pinned,                    // 始终保留在此设备上
        4 or 5 or 6 or 10 => CloudSyncState.Syncing,   // 上传中 / 下载中 / 传输中 / 排队等待
        7 or 8 => CloudSyncState.Error,                // 同步出错 / 需要用户处理
        9 => CloudSyncState.Excluded,                  // 已排除（不参与同步）
        _ => CloudSyncState.None,
    };

    /// <summary>属性位兜底：内容不在本机 → 仅在云端；标记了固定 → 始终保留；否则视为已同步。</summary>
    private static CloudSyncState FromAttributes(FileAttributes attributes)
    {
        var raw = (int)attributes;
        if (raw == 0)
        {
            // 拿不到属性也拿不到状态位：宁可不显示，也不要瞎猜
            return CloudSyncState.None;
        }

        if ((raw & (AttributeRecallOnDataAccess | AttributeRecallOnOpen | (int)FileAttributes.Offline)) != 0)
        {
            return CloudSyncState.CloudOnly;
        }

        return (raw & AttributePinned) != 0 ? CloudSyncState.Pinned : CloudSyncState.Synced;
    }

    /// <summary>云同步根 → 带尾分隔符的前缀，用于快速判断“这个目录要不要读同步状态”。</summary>
    private static string[] BuildRootPrefixes(IKnownFolderService knownFolders)
    {
        var result = new List<string>();

        try
        {
            foreach (var folder in knownFolders.GetCloudFolders())
            {
                if (string.IsNullOrWhiteSpace(folder.Path))
                {
                    continue;
                }

                result.Add(folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar);
            }
        }
        catch (Exception)
        {
            // 注册表/环境变量异常时当作没有云目录
        }

        return result.ToArray();
    }
}
