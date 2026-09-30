using System;
using System.Threading;
using Exdir.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Exdir;

/// <summary>
/// 自定义入口点（见 exdir.csproj 里的 DISABLE_XAML_GENERATED_MAIN）。
///
/// 关掉 XAML 自动生成的 Main 只为一件事：把单实例闸门放在 WinUI 初始化**之前**。
/// exdir 关窗口后一直驻留在托盘里，用户第二次双击 exe 时应该只是把已有窗口叫出来——
/// 这一步只置位一个内核事件就结束，不必再初始化一遍 WinUI。
///
/// 下面 Application.Start 里的写法与 XAML 生成的那份（obj\...\App.g.i.cs 的 Program.Main）逐行一致，
/// 升级 Windows App SDK 后如果启动方式变了，要同步这里。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!SingleInstance.TryClaim())
        {
            // 已有实例（已在帮我们显示主窗口），本进程直接退出
            return;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            // 信号可能比主窗口先到（用户在启动过程中又点了一次 exe），
            // 所以先把 UI 线程登记进闸门，回调等 App 起来后再注册（见 App.OnLaunched）
            SingleInstance.BindDispatcher(DispatcherQueue.GetForCurrentThread());

            new App();
        });
    }
}
