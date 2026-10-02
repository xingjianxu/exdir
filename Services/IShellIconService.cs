using System.Threading.Tasks;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 提供“系统外壳图标”——也就是资源管理器里显示的那一套图标
/// （.exe 显示程序自带图标、.lnk 显示目标图标 + 小箭头、文件夹/文件类型按系统关联）。
/// </summary>
public interface IShellIconService
{
    /// <summary>
    /// 取某个条目的图标。同一个图标键（扩展名，或图标写在文件自身里的类型用完整路径）
    /// 只会真正提取一次，之后的调用直接命中缓存；提取在后台线程进行。
    /// 拿不到图标（没有图标、路径已消失、权限不足）时返回 <c>null</c>，调用方退回字形占位。
    /// </summary>
    /// <param name="path">条目完整路径。</param>
    /// <param name="isDirectory">是否目录。</param>
    /// <param name="isVirtualDirectory">
    /// 目录是不是压缩包内的目录（路径在磁盘上并不存在）。为 true 时只按“目录属性”取通用文件夹图标，
    /// 不去问外壳要那个路径（问了也拿不到，白花一次系统调用）。
    /// </param>
    Task<IconBitmap?> GetIconAsync(string path, bool isDirectory, bool isVirtualDirectory = false);
}
