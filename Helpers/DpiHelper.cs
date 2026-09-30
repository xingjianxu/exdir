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

    /// <summary>通知区域图标边长（物理像素），随主显示器 DPI 变化。</summary>
    private const int SmCxSmIcon = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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

    /// <summary>
    /// 通知区域（托盘）图标在当前 DPI 下的物理像素边长。
    /// <c>SM_CXSMICON</c> 正是这个值（100% → 16、150% → 24、200% → 32），
    /// 与 <c>ShellIconExtractor</c> 取列表小图标用的是同一个口径；
    /// 按它选 .ico 里的那一张，托盘图标在任何缩放下都不会被系统重采样得发虚。
    /// </summary>
    public static int GetSmallIconSize()
    {
        try
        {
            var size = GetSystemMetrics(SmCxSmIcon);
            return size > 0 ? size : 16;
        }
        catch (Exception)
        {
            return 16;
        }
    }

    /// <summary>物理像素 → DIP。</summary>
    public static double ToDips(int physical, double scale) => scale <= 0 ? physical : physical / scale;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>
    /// 把“元素内的 DIP 坐标”换算成屏幕物理像素，用来给原生菜单定位。
    ///
    /// 不能直接用窗口位置 + 缩放：WinUI 窗口外面还套着一圈系统边框，客户区原点并不等于窗口左上角；
    /// 所以先 <c>TransformToVisual(null)</c> 拿到客户区坐标，再用 <c>ClientToScreen</c> 交给系统换算。
    /// 拿不到窗口句柄（理论上不该发生）时返回未换算的值，位置可能偏一点，但不会崩。
    /// </summary>
    public static Windows.Foundation.Point ToScreenPoint(FrameworkElement element, IntPtr hwnd, Windows.Foundation.Point dipPoint)
    {
        try
        {
            var inClient = element.TransformToVisual(null).TransformPoint(dipPoint);
            var scale = element.XamlRoot?.RasterizationScale ?? 1.0;

            var point = new POINT
            {
                X = (int)Math.Round(inClient.X * scale),
                Y = (int)Math.Round(inClient.Y * scale),
            };

            if (hwnd != IntPtr.Zero)
            {
                ClientToScreen(hwnd, ref point);
            }

            return new Windows.Foundation.Point(point.X, point.Y);
        }
        catch (Exception)
        {
            return dipPoint;
        }
    }
}
