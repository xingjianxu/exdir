using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Exdir.Helpers;

/// <summary>
/// 内部拖放共用的数据交换格式（文件列表 → 工具条“固定目录”/ 另一目录行 / 另一个窗格，
/// 侧边栏树 → 工具条“固定目录” / 侧边栏「收藏夹」）。
/// 自定义格式只放一串路径、用 <c>'\n'</c> 分隔：Windows 文件名不允许出现控制字符，
/// 所以这个分隔符不会和路径本身冲突（不用 JSON 是为了让拖放数据同时也是一段可读纯文本）。
/// </summary>
public static class DragDropHelper
{
    /// <summary>本应用自己的拖放格式标识（值是一个多行字符串，每行一个路径）。</summary>
    public const string PathsFormat = "exdir/paths";

    /// <summary>
    /// “拖动工具条上已有的固定目录按钮”专用的格式标识（值是那一个目录的路径）。
    /// 单独用一个格式是为了让落点能区分“调整顺序”和“新增固定”：只带
    /// <see cref="PathsFormat" /> 的拖拽一律是新增，带这个格式的一律是排序。
    /// </summary>
    public const string PinnedReorderFormat = "exdir/pinned-reorder";

    /// <summary>
    /// 数据包属性：这一拖是不是“只有目录”（工具条固定目录区据此决定要不要显示“固定到工具条”）。
    /// 拖放数据本身只能同步判断格式，读不了内容，所以由拖拽源在开始拖的时候把这个结论一起写上。
    /// </summary>
    public const string FoldersOnlyProperty = "exdir/folders-only";

    /// <summary>
    /// “从压缩包里 / 从远程位置拖出来的条目”专用的标记格式（值是临时副本的根目录，一行一个）。
    /// <para>
    /// 这两类条目都没有本地真实路径（包内是虚拟路径，远程条目在服务器上），所以拖拽源先把它们
    /// 解出来 / 下下来到 <c>archive-cache\drag\&lt;guid&gt;</c> 或 <c>remote-cache\drag\&lt;guid&gt;</c>，
    /// 再把得到的**真实文件**放进 <see cref="StandardDataFormats.StorageItems" />
    /// （这样资源管理器 / 桌面也能收）。
    /// 这个标记用来让内部落点区分两类拖拽：工具条固定目录区与侧边栏收藏夹据此拒绝
    /// （不能把缓存里的临时目录固定 / 收藏起来），文件列表据此一律按“复制”处理
    /// （源是只读的，交出去的也只是临时副本）。
    /// </para>
    /// </summary>
    public const string ArchiveDragFormat = "exdir/archive-drag";

    /// <summary>把一批路径写进拖放数据包。</summary>
    /// <param name="data">拖放数据包。</param>
    /// <param name="paths">要拖的路径。</param>
    /// <param name="operations">允许的拖放效果（默认只允许复制 / 链接，避免拖到资源管理器时把源删掉）。</param>
    /// <param name="foldersOnly">这批路径是不是全是目录（用于“固定到工具条”的提示）。</param>
    public static void SetPaths(
        DataPackage data,
        IEnumerable<string> paths,
        DataPackageOperation operations = DataPackageOperation.Copy | DataPackageOperation.Link,
        bool foldersOnly = true)
    {
        var payload = string.Join('\n', paths.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (payload.Length == 0)
        {
            return;
        }

        data.SetData(PathsFormat, payload);
        data.SetText(payload);

        data.Properties[FoldersOnlyProperty] = foldersOnly;

        // 目标是“固定一个链接”，所以要允许 Link；带上 Copy 是为了让接受方（例如资源管理器）
        // 在不支持 Link 时仍有可选项，否则拖拽会直接被系统判为“不可放置”。
        data.RequestedOperation = operations;
    }

    /// <summary>
    /// 给“从压缩包里 / 从远程位置拖出来的条目”写数据包：只有
    /// <see cref="StandardDataFormats.StorageItems" />（解出来 / 下下来的真实文件 / 目录）+
    /// <see cref="ArchiveDragFormat" /> 标记。
    /// 刻意不写 <see cref="PathsFormat" />：那是“内部拖拽（默认移动）”的判据，
    /// 而这里交给壳的是临时副本，语义上永远是“复制出来”（见 <see cref="ArchiveDragFormat" />）。
    /// </summary>
    public static void SetArchiveDrag(DataPackage data, IEnumerable<IStorageItem> items, IEnumerable<string> stagingRoots)
    {
        data.SetStorageItems(items);
        data.SetData(ArchiveDragFormat, string.Join('\n', stagingRoots));
        data.Properties[FoldersOnlyProperty] = false;
        data.RequestedOperation = DataPackageOperation.Copy;
    }

    /// <summary>
    /// 这个数据包“可能”含文件夹吗？用于 <c>DragOver</c>：那里只能同步判断，不能真去读数据。
    /// 外部来源（资源管理器等）只有 <see cref="StandardDataFormats.StorageItems"/>，读不到内容也先接受，
    /// 真正是不是目录留到 <see cref="GetPathsAsync"/> 之后再筛。
    /// </summary>
    public static bool MayContainFolder(DataPackageView view)
    {
        // 从压缩包里 / 从远程位置拖出来的临时副本不是用户的目录：固定到工具条 / 收藏到侧边栏只会在缓存目录里
        // 留一个迟早会被清掉的路径（真正固定得住的是压缩包本身 / 远程位置，而不是里面的条目）
        if (view.Contains(ArchiveDragFormat))
        {
            return false;
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            return true;
        }

        if (!view.Contains(PathsFormat))
        {
            return false;
        }

        // 同进程内的拖拽带着拖拽源写下的结论（见 FoldersOnlyProperty）；
        // 别的程序恰好也用了这个格式名时读不到属性，按“可能有目录”处理
        try
        {
            if (view.Properties.TryGetValue(FoldersOnlyProperty, out var value) && value is bool foldersOnly)
            {
                return foldersOnly;
            }
        }
        catch (Exception)
        {
            // 属性读不到就当没有
        }

        return true;
    }

    /// <summary>读出数据包里的路径（本应用的格式 + 来自资源管理器的存储项），已按大小写去重。</summary>
    public static async Task<IReadOnlyList<string>> GetPathsAsync(DataPackageView view)
    {
        var paths = new List<string>();

        if (view.Contains(PathsFormat))
        {
            try
            {
                if (await view.GetDataAsync(PathsFormat) is string payload)
                {
                    paths.AddRange(payload.Split(
                        '\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
            catch (Exception)
            {
                // 格式对但内容取不到：当作没有拖放内容，不打断交互
            }
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            try
            {
                foreach (var item in await view.GetStorageItemsAsync())
                {
                    if (!string.IsNullOrEmpty(item.Path))
                    {
                        paths.Add(item.Path);
                    }
                }
            }
            catch (Exception)
            {
                // 同上
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
