using System;
using System.Collections.Generic;
using System.IO;

namespace Exdir.Helpers;

/// <summary>
/// 右键「压缩」的两条“纯规则”：压缩包放到哪个目录、叫什么名字。
///
/// <para>
/// 单独拎出来的原因：这两条是用户直接看得到的约定（也是 <c>tools\archive-smoke</c> 唯一能
/// 不依赖界面就验证的部分），写在 <c>FolderTabViewModel</c> 里就只剩人工点验了。
/// </para>
/// </summary>
public static class CompressTargets
{
    /// <summary>
    /// 压缩包放哪：设置里配了「压缩输出目录」就用它（去掉首尾空白），留空用「下载」文件夹。
    /// 目录存不存在、建不建得出来由调用方负责（这里只做选择）。
    /// </summary>
    public static string ResolveOutputDirectory(string? configured, string downloadsDirectory)
        => string.IsNullOrWhiteSpace(configured) ? downloadsDirectory : configured.Trim();

    /// <summary>
    /// 压缩包名（不含 <c>.zip</c> 后缀）：
    /// <list type="bullet">
    /// <item>只选了一个条目 → 用条目自己的名字：文件去掉扩展名（<c>a.txt</c> → <c>a.zip</c>），
    ///       目录保留全名（<c>my.folder</c> → <c>my.folder.zip</c>，目录名里的点不是扩展名）；</item>
    /// <item>选了多个 → 用当前目录名（与 7-Zip 的默认做法一致）；</item>
    /// <item>当前目录是盘根（<c>D:\</c>）这种取不到名字的情况 → <c>压缩包</c>。</item>
    /// </list>
    /// </summary>
    public static string BaseName(IReadOnlyList<string> sourcePaths, string currentDirectory)
    {
        if (sourcePaths.Count == 1)
        {
            var single = sourcePaths[0];
            var name = Directory.Exists(single)
                ? Path.GetFileName(TrimTrailingSeparators(single))
                : Path.GetFileNameWithoutExtension(single);

            return string.IsNullOrEmpty(name) ? "压缩包" : name;
        }

        var currentName = Path.GetFileName(TrimTrailingSeparators(currentDirectory));
        return string.IsNullOrEmpty(currentName) ? "压缩包" : currentName;
    }

    /// <summary>
    /// 去掉末尾的 <c>\</c> / <c>/</c>，但保留 <c>D:\</c> 这种“盘根”形式
    ///（去掉就成了 <c>D:</c>，那是“D 盘的当前目录”，含义完全不同，见 AGENTS.md 第 6 节第 14 条）。
    /// </summary>
    public static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }
}
