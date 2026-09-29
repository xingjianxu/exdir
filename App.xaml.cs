using System;
using Exdir.Diagnostics;
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

            MainWindow = Services.GetRequiredService<MainWindow>();

            // 系统右键菜单需要一个宿主窗口句柄（TrackPopupMenu / GetUIObjectOf 都要用）
            Services.GetRequiredService<IShellContextMenuService>().OwnerWindow =
                WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);

            MainWindow.Activate();

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
        services.AddSingleton<ICloudSyncService, CloudSyncService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IDriveService, DriveService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IShellIconService, ShellIconService>();
        services.AddSingleton<IShellContextMenuService, ShellContextMenuService>();

        // ViewModel
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SidebarViewModel>();

        // 视图
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
