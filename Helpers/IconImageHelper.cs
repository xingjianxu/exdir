using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Exdir.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Exdir.Helpers;

/// <summary>
/// 把 <see cref="IconBitmap"/> 转成可绑定的 XAML 图像源。
///
/// <para>
/// <b>必须在 UI 线程调用</b>：<see cref="WriteableBitmap"/> 是 XAML 对象，有线程亲和性。
/// </para>
/// 内容相同的图标（同一个 <see cref="IconBitmap.ContentHash"/>）共用同一个图像源：
/// 列表里几千个普通文件夹因此只占一张位图的内存，也省掉几千次缓冲区拷贝。
/// </summary>
public static class IconImageHelper
{
    /// <summary>图像源缓存上限；正常用不到（不同图标的个数通常只有几十），超过就整体丢掉重建。</summary>
    private const int MaxCached = 512;

    private static readonly Dictionary<int, ImageSource> Cache = new();

    public static ImageSource ToImageSource(IconBitmap bitmap)
    {
        if (Cache.TryGetValue(bitmap.ContentHash, out var cached))
        {
            return cached;
        }

        var writeable = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using (var stream = writeable.PixelBuffer.AsStream())
        {
            stream.Write(bitmap.Pixels, 0, bitmap.Pixels.Length);
        }

        writeable.Invalidate();

        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        Cache[bitmap.ContentHash] = writeable;
        return writeable;
    }
}
