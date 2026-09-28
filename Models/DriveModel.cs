using Exdir.Helpers;

namespace Exdir.Models;

/// <summary>磁盘类型。</summary>
public enum DriveKind
{
    Unknown = 0,
    Fixed,
    Removable,
    Network,
    Optical,
    Ram,
}

/// <summary>描述一个可枚举的驱动器 / 挂载点。</summary>
public sealed class DriveModel
{
    /// <summary>根路径，例如 <c>C:\</c>。</summary>
    public required string RootPath { get; init; }

    /// <summary>用于显示的短名称，例如 <c>C:</c>。</summary>
    public required string DisplayName { get; init; }

    /// <summary>卷标，可能为空。</summary>
    public string VolumeLabel { get; init; } = string.Empty;

    /// <summary>UNC 路径（网络驱动器），否则为空。</summary>
    public string UncPath { get; init; } = string.Empty;

    public DriveKind Kind { get; init; } = DriveKind.Unknown;

    /// <summary>是否已就绪（未插入介质的光驱 / 断开的网络盘为 false）。</summary>
    public bool IsReady { get; init; }

    public long TotalSize { get; init; }

    public long FreeSpace { get; init; }

    /// <summary>工具栏按钮上显示的文字。</summary>
    public string ToolbarText => string.IsNullOrEmpty(VolumeLabel)
        ? DisplayName
        : $"{DisplayName} {VolumeLabel}";

    /// <summary>工具栏按钮上的图标字形。</summary>
    public string Glyph => Kind switch
    {
        DriveKind.Network => FileTypeHelper.NetworkGlyph,
        DriveKind.Removable => FileTypeHelper.UsbGlyph,
        DriveKind.Optical => "\uE958",
        DriveKind.Ram => "\uE964",
        _ => FileTypeHelper.DriveGlyph,
    };

    /// <summary>悬停提示。</summary>
    public string Tooltip
    {
        get
        {
            if (!IsReady)
            {
                return $"{DisplayName}（未就绪）";
            }

            var kind = Kind switch
            {
                DriveKind.Fixed => "本地磁盘",
                DriveKind.Removable => "可移动磁盘",
                DriveKind.Network => "网络驱动器",
                DriveKind.Optical => "光盘",
                DriveKind.Ram => "内存盘",
                _ => "驱动器",
            };

            return $"{DisplayName}  {kind}\n可用 {SizeFormatter.Format(FreeSpace)} / 共 {SizeFormatter.Format(TotalSize)}";
        }
    }
}
