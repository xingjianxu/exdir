using Microsoft.UI.Xaml;

namespace Exdir.Helpers;

/// <summary>
/// 详细信息视图的列宽定义。
/// 说明：WinUI 的 XAML 不会为资源值做类型转换，
/// <c>&lt;x:Double&gt;</c> 资源无法赋给 <see cref="GridLength"/> 类型的
/// <c>ColumnDefinition.Width</c>，因此这里用强类型静态属性 + <c>x:Bind</c> 共享列宽，
/// 保证列头与数据行永远一致。
/// </summary>
public static class ColumnLayout
{
    /// <summary>名称列：占满剩余宽度。</summary>
    public static GridLength NameWidth { get; } = new(1, GridUnitType.Star);

    /// <summary>名称列最小宽度（像素）。
    /// 故意取得较小：窗格很窄时让名称列先压缩，避免固定列被挤出可视区。
    /// 后续可改为“宽度不足时自动隐藏低优先级列”（见 plan.md）。</summary>
    public static double NameMinWidth => 80;

    /// <summary>修改日期列宽度（像素）。</summary>
    public static GridLength DateWidth { get; } = new(136, GridUnitType.Pixel);

    /// <summary>类型列宽度（像素）。</summary>
    public static GridLength TypeWidth { get; } = new(104, GridUnitType.Pixel);

    /// <summary>大小列宽度（像素）。</summary>
    public static GridLength SizeWidth { get; } = new(86, GridUnitType.Pixel);
}
