using System;
using System.Threading;
using Exdir.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Exdir;

/// <summary>
/// 自定义入口点（见 exdir.csproj 里的 DISABLE_XAML_GENERATED_MAIN）。
///
/// 关掉 XAML 自动生成的 Main 为了两件事：
///   1. 把单实例闸门放在 WinUI 初始化**之前** —— exdir 关窗口后一直驻留在托盘里，
///      用户第二次双击 exe 时只是把已有窗口叫出来（顺带把命令行里的路径交给它）,
///      这一步不必再初始化一遍 WinUI；
///   2. 命令行（<c>exdir [path]</c>）要在最早的时刻解析：不带路径参数时打开的就是
///      **本进程**的工作目录（见 Helpers/CommandLine）。
///
/// 下面 Application.Start 里的写法与 XAML 生成的那份（obj\...\App.g.i.cs 的 Program.Main）逐行一致，
/// 升级 Windows App SDK 后如果启动方式变了，要同步这里。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 先解析命令行：相对路径要按本进程的工作目录解析，无参数时用的也是它
        CommandLine.Parse(args);

        if (!SingleInstance.TryClaim(CommandLine.Request))
        {
            // 已有实例（请求已转交给它），本进程直接退出
            return;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            // 请求可能比主窗口先到（用户在启动过程中又敲了一次 exdir），
            // 所以先把 UI 线程登记进闸门，回调等 App 起来后再注册（见 App.OnLaunched）
            SingleInstance.BindDispatcher(DispatcherQueue.GetForCurrentThread());

            new App();
        });
    }
}
