namespace Exdir.Models;

/// <summary>
/// 从系统外壳取到的一张图标位图。
/// 只承载数据、不含任何 XAML 类型：XAML 图像源由 <c>Helpers/IconImageHelper</c> 在 UI 线程上转换，
/// 这样服务层照旧只在后台线程干活（见 AGENTS.md 的分层约定）。
/// </summary>
public sealed class IconBitmap
{
    public IconBitmap(int width, int height, byte[] pixels, int contentHash)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        ContentHash = contentHash;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>预乘 alpha 的 BGRA 像素，自上而下、逐行紧密排列（<c>Width * Height * 4</c> 字节）。</summary>
    public byte[] Pixels { get; }

    /// <summary>像素内容哈希：内容相同的图标共用同一份数组、同一个图像源。</summary>
    public int ContentHash { get; }
}
