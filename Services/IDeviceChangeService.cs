using System;

namespace Exdir.Services;

/// <summary>
/// 驱动器 / U 盘等“卷”的插入与移除通知。
///
/// 侧边栏「此电脑」分组与工具条左侧的磁盘区靠它在插入 U 盘后**实时**出现新盘、
/// 拔出后立刻消失（见 <c>MainWindow</c> 里的延迟刷新：消息会连发，盘符可用又有延迟）。
/// </summary>
public interface IDeviceChangeService
{
    /// <summary>
    /// 有卷插入 / 移除（或设备树变动）。在 UI 线程上触发（它就是窗口消息线程），
    /// 所以处理器里可以直接改 <c>ObservableCollection</c>。
    /// </summary>
    event EventHandler? VolumesChanged;

    /// <summary>挂到主窗口句柄上开始监听（窗口必须已经创建）。重复调用无效。</summary>
    void Attach(IntPtr windowHandle);

    /// <summary>摘掉监听（真退出时调用；只是隐藏到托盘的话要留着，隐藏的窗口照样收得到广播）。</summary>
    void Detach();
}
