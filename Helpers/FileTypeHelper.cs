using System;
using System.Collections.Generic;
using System.IO;

namespace Exdir.Helpers;

/// <summary>
/// 由扩展名推断“类型”列文本与图标字形。
/// 说明：当前使用 Segoe Fluent Icons 字形而非真实 shell 图标；
/// 后续可替换为 SHGetFileInfo 提取的系统图标（见 plan.md）。
/// </summary>
public static class FileTypeHelper
{
    public const string FolderGlyph = "\uE8B7";       // Folder
    public const string FileGlyph = "\uE7C3";         // Page
    public const string DriveGlyph = "\uEDA2";        // HardDrive
    public const string CloudGlyph = "\uE753";        // Cloud
    public const string NetworkGlyph = "\uE8CE";      // NetworkTethering / Wifi
    public const string UsbGlyph = "\uE88E";          // USB
    public const string HomeGlyph = "\uE80F";         // Home
    public const string FavoriteGlyph = "\uE734";     // FavoriteStar（侧边栏「收藏夹」用）
    public const string DesktopGlyph = "\uE8FC";      // TVMonitor
    public const string PictureGlyph = "\uEB9F";      // Photo
    public const string MusicGlyph = "\uE8D6";        // MusicInfo
    public const string VideoGlyph = "\uE714";        // Video
    public const string DownloadGlyph = "\uE896";     // Download
    public const string DocumentGlyph = "\uE8A5";     // Document
    public const string ZipGlyph = "\uF012";          // ZipFolder
    public const string ExeGlyph = "\uE7FC";          // ? fallback handled below

    private sealed record TypeInfo(string Description, string Glyph);

    private static readonly Dictionary<string, TypeInfo> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文本 / 文档
        [".txt"] = new("文本文档", "\uE8A5"),
        [".log"] = new("日志文件", "\uE8A5"),
        [".md"] = new("Markdown 文档", "\uE8A5"),
        [".rtf"] = new("RTF 文档", "\uE8A5"),
        [".pdf"] = new("PDF 文档", "\uEA90"),
        [".doc"] = new("Word 文档", "\uE8A5"),
        [".docx"] = new("Word 文档", "\uE8A5"),
        [".xls"] = new("Excel 工作表", "\uE9F9"),
        [".xlsx"] = new("Excel 工作表", "\uE9F9"),
        [".csv"] = new("CSV 文件", "\uE9F9"),
        [".ppt"] = new("PowerPoint 演示文稿", "\uE8A5"),
        [".pptx"] = new("PowerPoint 演示文稿", "\uE8A5"),

        // 代码
        [".cs"] = new("C# 源文件", "\uE943"),
        [".xaml"] = new("XAML 文件", "\uE943"),
        [".c"] = new("C 源文件", "\uE943"),
        [".h"] = new("C 头文件", "\uE943"),
        [".cpp"] = new("C++ 源文件", "\uE943"),
        [".hpp"] = new("C++ 头文件", "\uE943"),
        [".js"] = new("JavaScript 文件", "\uE943"),
        [".ts"] = new("TypeScript 文件", "\uE943"),
        [".py"] = new("Python 源文件", "\uE943"),
        [".rs"] = new("Rust 源文件", "\uE943"),
        [".go"] = new("Go 源文件", "\uE943"),
        [".java"] = new("Java 源文件", "\uE943"),
        [".json"] = new("JSON 文件", "\uE943"),
        [".xml"] = new("XML 文档", "\uE943"),
        [".yml"] = new("YAML 文件", "\uE943"),
        [".yaml"] = new("YAML 文件", "\uE943"),
        [".toml"] = new("TOML 文件", "\uE943"),
        [".ini"] = new("配置设置", "\uE713"),
        [".cfg"] = new("配置设置", "\uE713"),
        [".ps1"] = new("PowerShell 脚本", "\uE756"),
        [".cmd"] = new("Windows 命令脚本", "\uE756"),
        [".bat"] = new("Windows 批处理文件", "\uE756"),
        [".sh"] = new("Shell 脚本", "\uE756"),

        // 图片
        [".png"] = new("PNG 图片", "\uEB9F"),
        [".jpg"] = new("JPEG 图片", "\uEB9F"),
        [".jpeg"] = new("JPEG 图片", "\uEB9F"),
        [".gif"] = new("GIF 图片", "\uEB9F"),
        [".bmp"] = new("BMP 图片", "\uEB9F"),
        [".webp"] = new("WebP 图片", "\uEB9F"),
        [".svg"] = new("SVG 图片", "\uEB9F"),
        [".ico"] = new("图标", "\uEB9F"),
        [".tif"] = new("TIFF 图片", "\uEB9F"),
        [".tiff"] = new("TIFF 图片", "\uEB9F"),
        [".heic"] = new("HEIC 图片", "\uEB9F"),

        // 音视频
        [".mp3"] = new("MP3 音频", "\uE8D6"),
        [".wav"] = new("WAV 音频", "\uE8D6"),
        [".flac"] = new("FLAC 音频", "\uE8D6"),
        [".m4a"] = new("M4A 音频", "\uE8D6"),
        [".ogg"] = new("OGG 音频", "\uE8D6"),
        [".mp4"] = new("MP4 视频", "\uE714"),
        [".mkv"] = new("MKV 视频", "\uE714"),
        [".avi"] = new("AVI 视频", "\uE714"),
        [".mov"] = new("QuickTime 视频", "\uE714"),
        [".wmv"] = new("WMV 视频", "\uE714"),
        [".webm"] = new("WebM 视频", "\uE714"),

        // 压缩
        [".zip"] = new("压缩文件夹", "\uF012"),
        [".7z"] = new("7Z 压缩文件", "\uF012"),
        [".rar"] = new("RAR 压缩文件", "\uF012"),
        [".tar"] = new("TAR 存档", "\uF012"),
        [".gz"] = new("GZIP 压缩文件", "\uF012"),
        [".xz"] = new("XZ 压缩文件", "\uF012"),

        // 程序与系统
        [".exe"] = new("应用程序", "\uE7FC"),
        [".msi"] = new("Windows 安装程序", "\uE7FC"),
        [".dll"] = new("应用程序扩展", "\uE7FC"),
        [".sys"] = new("系统文件", "\uE770"),
        [".lnk"] = new("快捷方式", "\uE71B"),
        [".url"] = new("Internet 快捷方式", "\uE71B"),
        [".iso"] = new("光盘映像文件", "\uE958"),
        [".vhd"] = new("虚拟硬盘", "\uEDA2"),
        [".vhdx"] = new("虚拟硬盘", "\uEDA2"),
        [".ttf"] = new("字体文件", "\uE8D2"),
        [".otf"] = new("字体文件", "\uE8D2"),
        [".db"] = new("数据库文件", "\uE8F1"),
        [".sqlite"] = new("SQLite 数据库", "\uE8F1"),
    };

    /// <summary>返回“类型”列文本。</summary>
    public static string GetTypeName(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "文件夹";
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return "文件";
        }

        return Map.TryGetValue(ext, out var info)
            ? info.Description
            : $"{ext.TrimStart('.').ToUpperInvariant()} 文件";
    }

    /// <summary>返回列表用的图标字形。</summary>
    public static string GetGlyph(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return FolderGlyph;
        }

        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && Map.TryGetValue(ext, out var info)
            ? info.Glyph
            : FileGlyph;
    }
}
