using System;

namespace Exdir.Models;

/// <summary>
/// 一次「在线更新检查」的结果：远端最新的那个 Release（且它比当前版本新）。
/// 由 <see cref="Exdir.Services.IUpdateService.CheckAsync" /> 组装，界面（提示条 / 更新窗口）只读它。
/// </summary>
public sealed class UpdateInfo
{
    /// <summary>GitHub Release 的 tag，形如 <c>v0.0.20261002</c>（界面原样显示）。</summary>
    public required string Tag { get; init; }

    /// <summary>规整后的版本串，形如 <c>0.0.20261002</c>（比较与显示都用它）。</summary>
    public required string Version { get; init; }

    /// <summary>要下载的资产文件名，形如 <c>exdir-v0.0.20261002-win-x64.zip</c>。</summary>
    public string AssetName { get; init; } = string.Empty;

    /// <summary>资产下载地址；为 null 表示这个 Release 没有可下载的 zip（只能打开发布页）。</summary>
    public string? AssetUrl { get; init; }

    /// <summary>GitHub 给的 SHA256（<c>sha256:...</c> 里的那一段）；拿不到时为 null（跳过校验）。</summary>
    public string? Sha256 { get; init; }

    /// <summary>资产大小（字节），用来算下载进度。</summary>
    public long Size { get; init; }

    /// <summary>Release 说明（markdown，界面原样显示）。</summary>
    public string ReleaseNotes { get; init; } = string.Empty;

    /// <summary>Release 的网页地址（「打开发布页」/ 手动下载）。</summary>
    public string ReleasePageUrl { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>能不能由 exdir 自己替换安装目录（发布版且安装目录可写）。</summary>
    public bool CanSelfUpdate { get; init; }

    /// <summary>不能自动替换的原因（<see cref="CanSelfUpdate" /> 为 false 时非空），界面直接显示。</summary>
    public string? SelfUpdateBlockedReason { get; init; }

    /// <summary>有没有可下载的 zip。</summary>
    public bool CanDownload => !string.IsNullOrEmpty(AssetUrl);
}
