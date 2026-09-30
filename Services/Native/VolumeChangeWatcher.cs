using System;
using System.Runtime.InteropServices;

namespace Exdir.Services.Native;

/// <summary>
/// 卷（驱动器 / U 盘 / 光驱 / 网络盘）挂载与卸载的监听器：把宿主窗口子类化，
/// 接 <c>WM_DEVICECHANGE</c>。
///
/// 卷事件（<c>DBT_DEVTYP_VOLUME</c>）和“设备接口事件”不一样：系统把它**广播给所有顶层窗口**，
/// 不需要 <c>RegisterDeviceNotification</c>（那个只用于按 GUID 订阅设备接口）。
/// 所以这里只做窗口子类化；窗口隐藏到托盘时它仍是顶层窗口，照样收得到消息。
/// </summary>
internal sealed class VolumeChangeWatcher : IDisposable
{
    private const uint WmDeviceChange = 0x0219;

    /// <summary>有设备 / 介质到达（插入）。</summary>
    private const int DbtDeviceArrival = 0x8000;

    /// <summary>设备 / 介质已移除（拔出）。</summary>
    private const int DbtDeviceRemoveComplete = 0x8004;

    /// <summary>设备树变了（任何设备增删都会广播一次，没有 <c>DEV_BROADCAST_HDR</c>）。</summary>
    private const int DbtDevNodesChanged = 0x0007;

    /// <summary><c>DEV_BROADCAST_HDR.dbch_devicetype</c>：卷（盘符 / 装入点）。</summary>
    private const int DbtDevTypVolume = 0x0002;

    /// <summary>子类化 Id（同一窗口上每个监听器一个，这里是 "vn"）。</summary>
    private static readonly IntPtr SubclassId = new(0x766E);

    private readonly SubclassProc _proc;

    private IntPtr _hwnd;
    private bool _installed;

    public VolumeChangeWatcher() => _proc = OnMessage;

    /// <summary>有卷插入 / 移除。窗口消息线程（UI 线程）上触发。</summary>
    public event EventHandler? VolumesChanged;

    /// <summary>挂到宿主窗口句柄上开始监听。重复调用无效。</summary>
    public void Attach(IntPtr hwnd)
    {
        if (_installed || hwnd == IntPtr.Zero)
        {
            return;
        }

        _hwnd = hwnd;

        try
        {
            _installed = SetWindowSubclass(hwnd, _proc, SubclassId, IntPtr.Zero);
        }
        catch (Exception)
        {
            _installed = false;
        }
    }

    public void Dispose()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        }
        catch (Exception)
        {
            // 通常是在进程退出的路上，摘不掉也无所谓
        }

        _installed = false;
        _hwnd = IntPtr.Zero;
    }

    private IntPtr OnMessage(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr uIdSubclass,
        IntPtr dwRefData)
    {
        try
        {
            if (uMsg == WmDeviceChange && IsRelevant(wParam, lParam))
            {
                VolumesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception)
        {
            // 回调里出问题不能往外抛（异常跨 native 边界会直接崩进程），吞掉继续转发
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static bool IsRelevant(IntPtr wParam, IntPtr lParam)
    {
        var evt = wParam.ToInt64();

        // 设备树变化（U 盘插拔一定会带上它）也认：它没有任何 lParam，跨进程发消息也不会踩内存，
        // 而“盘符可用”有时比单独的卷事件晚一点，多刷一次正好兜住（刷新本身是幂等的）
        if (evt == DbtDevNodesChanged)
        {
            return true;
        }

        if (evt != DbtDeviceArrival && evt != DbtDeviceRemoveComplete)
        {
            return false;
        }

        // 到达 / 移除也用于别的设备类型，lParam 指向 DEV_BROADCAST_HDR；
        // 不是卷（例如 USB 设备接口）就忽略
        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Marshal.ReadInt32(lParam) == DbtDevTypVolume;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        IntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
