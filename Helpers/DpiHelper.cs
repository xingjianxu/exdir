using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Exdir.Helpers;

/// <summary>
/// 窗口 DPI 相关工具。
/// 不能依赖 <c>XamlRoot.RasterizationScale</c>：窗口刚激活时 XamlRoot 可能还是 null，
/// 会导致窗口尺寸按 100% 缩放计算，在高 DPI 屏幕上窗口过小。
/// </summary>
public static class DpiHelper
{
    private const uint DefaultDpi = 96;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>返回窗口当前的缩放因子（96 DPI = 1.0）。无法获取时回退到 1.0。</summary>
    public static double GetScale(Window window)
    {
        // XamlRoot 在首次布局后才可用
        try
        {
            if (window.Content is FrameworkElement { XamlRoot: { } xamlRoot } && xamlRoot.RasterizationScale > 0)
            {
                return xamlRoot.RasterizationScale;
            }
        }
        catch (Exception)
        {
            // 忽略，继续用 Win32 查询
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero)
            {
                return 1.0;
            }

            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / (double)DefaultDpi;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    /// <summary>DIP → 物理像素。</summary>
    public static int ToPhysical(double dips, double scale) => (int)Math.Round(dips * scale);

    /// <summary>物理像素 → DIP。</summary>
    public static double ToDips(int physical, double scale) => scale <= 0 ? physical : physical / scale;
}
