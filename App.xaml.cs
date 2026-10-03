using System;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Services;
using Exdir.Services.Native;
using Exdir.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Exdir;

/// <summary>
/// 应用入口。负责搭建依赖注入容器并创建主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>全局服务定位器。仅在无法使用构造函数注入的场合使用（例如 XAML 实例化的用户控件、XamlRoot 获取）。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>当前主窗口。用于弹窗时获取 XamlRoot。</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) => Log.Exception("Application.UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Exception("AppDomain.UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler_UnobservedTaskException();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = ConfigureServices();

            // 先读取设置：MainViewModel 构造时会依赖其中若干项
            Services.GetRequiredService<ISettingsService>().Load();

            // 让本进程看得到云占位符的真实属性（reparse/sparse/offline 位）：
            // 否则“同步状态”列可能只能拿到固定的“同步挂起”，放在枚举任何目录之前调用
            ShellPropertyStore.EnsurePlaceholdersExposed();

            // 压缩包只读浏览：启动时清一次临时文件（链式解开的中间 tar / 双击包内文件解出来的副本）
            Services.GetRequiredService<IArchiveService>().CleanupTemp();

            // 远程位置：同样在启动时清一次本地中转目录（见 Helpers/RemoteCache）
            Services.GetRequiredService<IRemoteFileService>().CleanupTemp();

            var window = Services.GetRequiredService<MainWindow>();
            MainWindow = window;

            // 系统右键菜单需要一个宿主窗口句柄（TrackPopupMenu / GetUIObjectOf 都要用）
            Services.GetRequiredService<IShellContextMenuService>().OwnerWindow =
                WinRT.Interop.WindowNative.GetWindowHandle(window);

            if (CommandLine.IsPreload)
            {
                // 开机自启（--preload）：这一份**不显示主窗口**，只把会话与首屏图标先备好。
                // 之后用户双击 exe / 点托盘图标时，单实例闸门会把请求转给这份进程，
                // ShowWindow 一下就能出现 —— 自启时弹出一个主窗口正是要避开的。
                window.StartPreload();
            }
            else
            {
                window.Activate();
            }

            // 托盘驻留 + 命令行：第二个实例（双击 exe 或 exdir <path>）的请求都从这里进来——
            // 把已在运行的这只窗口叫出来，并打开它带来的目录
            // （单实例闸门在 Program.Main 里就已经就位，这里只是把回调补上）
            SingleInstance.Listen(window.HandleActivation);

            Log.Write("应用已启动");
        }
        catch (Exception ex)
        {
            Log.Exception("App.OnLaunched", ex);
            throw;
        }
    }

    private static void TaskScheduler_UnobservedTaskException()
        => System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Exception("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 基础设施
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IKnownFolderService, KnownFolderService>();
        services.AddSingleton<INetworkLocationService, NetworkLocationService>();
        services.AddSingleton<ICloudSyncService, CloudSyncService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IDriveService, DriveService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IShellIconService, ShellIconService>();
        services.AddSingleton<IShellContextMenuService, ShellContextMenuService>();
        services.AddSingleton<IDeviceChangeService, DeviceChangeService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IFileOperationService, FileOperationService>();

        // 压缩包只读浏览（原生 7z.dll，见 Services/Native/SevenZipInterop.cs）
        services.AddSingleton<IArchiveService, ArchiveService>();

        // 远程位置（SFTP / FTP）：只读浏览 + 下载到本地，见 Services/RemoteFileService.cs
        services.AddSingleton<IRemoteLocationSource, SettingsRemoteLocationSource>();
        services.AddSingleton<IRemoteFileService, RemoteFileService>();

        // 打包成 zip（右键「压缩」；走 BCL 的 System.IO.Compression，与上面那条只读的路互不干扰）
        services.AddSingleton<ICompressionService, CompressionService>();

        // 包内条目没有真实路径，写不进系统剪贴板，所以“包内复制”另记在内存里（粘贴时才解出来）
        services.AddSingleton<IArchiveClipboardService, ArchiveClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();

        // ViewModel
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SidebarViewModel>();

        // 视图
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
