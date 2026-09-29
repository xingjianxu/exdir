using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Exdir.Helpers;

/// <summary>
/// 内部拖放（文件列表 / 侧边栏树 → 工具条“固定目录”）共用的数据交换格式。
/// 自定义格式只放一串目录路径、用 <c>'\n'</c> 分隔：Windows 文件名不允许出现控制字符，
/// 所以这个分隔符不会和路径本身冲突（不用 JSON 是为了让拖放数据同时也是一段可读纯文本）。
/// </summary>
public static class DragDropHelper
{
    /// <summary>本应用自己的拖放格式标识（值是一个多行字符串，每行一个路径）。</summary>
    public const string PathsFormat = "exdir/paths";

    /// <summary>把一批路径写进拖放数据包。</summary>
    public static void SetPaths(DataPackage data, IEnumerable<string> paths)
    {
        var payload = string.Join('\n', paths.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (payload.Length == 0)
        {
            return;
        }

        data.SetData(PathsFormat, payload);
        data.SetText(payload);

        // 目标是“固定一个链接”，所以要允许 Link；带上 Copy 是为了让接受方（例如资源管理器）
        // 在不支持 Link 时仍有可选项，否则拖拽会直接被系统判为“不可放置”。
        data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Link;
    }

    /// <summary>
    /// 这个数据包“可能”含文件夹吗？用于 <c>DragOver</c>：那里只能同步判断，不能真去读数据。
    /// 外部来源（资源管理器等）只有 <see cref="StandardDataFormats.StorageItems"/>，读不到内容也先接受，
    /// 真正是不是目录留到 <see cref="GetPathsAsync"/> 之后再筛。
    /// </summary>
    public static bool MayContainFolder(DataPackageView view)
        => view.Contains(PathsFormat) || view.Contains(StandardDataFormats.StorageItems);

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
