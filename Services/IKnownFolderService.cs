using System.Collections.Generic;

namespace Exdir.Services;

/// <summary>一个“位置”条目（用户主目录下的常用目录 / 云存储根目录 / 磁盘）。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Path">完整路径。</param>
/// <param name="Glyph">Segoe Fluent Icons 字形。</param>
/// <param name="Kind">来源分类，供 UI 分组使用。</param>
public sealed record SpecialFolderModel(string Name, string Path, string Glyph, SpecialFolderKind Kind);

/// <summary>位置的来源分类。</summary>
public enum SpecialFolderKind
{
    /// <summary>用户主目录下的标准文件夹。</summary>
    UserFolder = 0,

    /// <summary>云存储（OneDrive 等同步根目录）。</summary>
    CloudStorage = 1,
}

/// <summary>用户目录与云存储位置枚举。</summary>
public interface IKnownFolderService
{
    /// <summary>用户主目录（<c>%USERPROFILE%</c>）。</summary>
    string UserProfile { get; }

    /// <summary>用户主目录下的常用目录（桌面、文档、下载……）。</summary>
    IReadOnlyList<SpecialFolderModel> GetUserFolders();

    /// <summary>云存储同步根目录（OneDrive 等）。</summary>
    IReadOnlyList<SpecialFolderModel> GetCloudFolders();

    /// <summary>用于首次启动时固定到 title 栏的默认目录。</summary>
    IReadOnlyList<string> GetDefaultPinnedFolders();
}
