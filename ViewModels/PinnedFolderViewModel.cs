using System;
using System.IO;

namespace Exdir.ViewModels;

/// <summary>title 栏上固定显示的常用目录。</summary>
public sealed class PinnedFolderViewModel
{
    public PinnedFolderViewModel(string path)
    {
        Path = path;
        Name = ResolveName(path);
    }

    public string Path { get; }

    /// <summary>按钮上显示的名称（路径最后一段）。</summary>
    public string Name { get; }

    public string Tooltip => Path;

    private static string ResolveName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
        catch (Exception)
        {
            return path;
        }
    }
}
