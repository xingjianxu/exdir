using System.Collections.Generic;

namespace Exdir.Services;

/// <summary>一个“位置”条目（用户主目录下的常用目录 / 云存储根目录 / 磁盘）。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Path">完整路径。</param>
/// <param name="Glyph">Segoe Fluent Icons 字形。</param>
/// <param name="Kind">来源分类，供 UI 分组使用。</param>
/// <param name="Key">
/// 标准文件夹的稳定标识（桌面 / 文档 / 下载……）。
/// 显示名会被系统语言改写、路径也可能被重定向，只有这个 Key 是稳定的，
/// 所以「侧边栏主目录里显示哪些文件夹」的设置项按它来匹配（见 <see cref="UserFolderKey" />）。
/// 非标准文件夹（例如云存储根目录）用默认的 <see cref="UserFolderKey.None" />。
/// </param>
public sealed record SpecialFolderModel(
    string Name,
    string Path,
    string Glyph,
    SpecialFolderKind Kind,
    UserFolderKey Key = UserFolderKey.None);

/// <summary>位置的来源分类。</summary>
public enum SpecialFolderKind
{
    /// <summary>用户主目录下的标准文件夹。</summary>
    UserFolder = 0,

    /// <summary>云存储（OneDrive 等同步根目录）。</summary>
    CloudStorage = 1,
}

/// <summary>
/// 用户主目录下标准文件夹的稳定标识。
/// 不用显示名（“桌面”这类文字会随系统语言变）也不用路径（可能被重定向）来标识设置项。
/// </summary>
public enum UserFolderKey
{
    /// <summary>不是标准文件夹（云存储根目录等）。</summary>
    None = 0,

    /// <summary>桌面。</summary>
    Desktop = 1,

    /// <summary>文档。</summary>
    Documents = 2,

    /// <summary>下载。</summary>
    Downloads = 3,

    /// <summary>图片。</summary>
    Pictures = 4,

    /// <summary>音乐。</summary>
    Music = 5,

    /// <summary>视频。</summary>
    Videos = 6,
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
