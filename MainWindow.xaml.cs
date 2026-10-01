using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Services;
using Exdir.ViewModels;
using Exdir.Views;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.UI;

namespace Exdir;

/// <summary>
/// 主窗口外壳：顶部菜单栏（含窗口按钮）、title 栏、侧边栏与 1~2 个文件窗格。
///
/// 窗口是**常驻托盘**的：点关闭按钮（或 Alt+F4）只是把窗口隐藏起来，进程、DI 容器、
/// ViewModel、目录树、图标缓存这些全部原封不动地留着，所以再打开（托盘图标 / 再次双击 exe）
/// 就是一次 ShowWindow，不需要重建任何一个窗格；只有菜单里的「退出」才会真的结束进程。
/// 详见 AGENTS.md 第 4 节“托盘驻留”。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>托盘驻留的单实例唤回、以及「配置 → 设置…」打开的设置窗口（同一时刻只开一个）。</summary>
    private SettingsWindow? _settingsWindow;

    /// <summary>卷（U 盘 / 光驱 / 网络盘）插拔通知。</summary>
    private readonly IDeviceChangeService _deviceChange;

    /// <summary>
    /// 两次延迟刷新：<c>WM_DEVICECHANGE</c> 常常连发好几条（设备节点 + 卷 + 介质），而且盘符
    /// 往往比消息晚几百毫秒才可用。所以“合并成一次稍后再刷”+“再补一次兜底刷新”，
    /// 避免刷得太早、U 盘还没挂上而看不到它。
    /// </summary>
    private readonly DispatcherQueueTimer _driveRefreshSoon;
    private readonly DispatcherQueueTimer _driveRefreshBackstop;

    private double _sidebarWidth = 232;
    private bool _loaded;

    /// <summary>用户是否点了「退出」（而不是关窗口）：决定 AppWindow.Closing 里拦不拦。</summary>
    private bool _exitRequested;

    /// <summary>隐藏到托盘那一刻窗口是不是最大化的（ShowWindow(SW_SHOWNORMAL) 会把最大化还原掉）。</summary>
    private bool _restoreMaximized;

    /// <summary>窗口是不是正藏在托盘里：隐藏时才需要走 ShowWindow，否则它会把最大化窗口还原掉。</summary>
    private bool _hiddenToTray;

    /// <summary>
    /// 会话恢复完成。命令行请求要等它结束再导航，否则新标签页会和恢复出来的标签页抢活动标签。
    /// </summary>
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 待处理命令行请求（启动参数与其它实例转发过来的可能几乎同时到），串行处理。
    /// 空串 = 不导航（只把窗口唤到前台），不入队。
    /// </summary>
    private readonly Queue<string> _pendingActivations = new();
    private bool _activating;

    public MainWindow(MainViewModel viewModel, IDeviceChangeService deviceChange)
    {
        // x:Bind 在 InitializeComponent 期间求值，因此必须先赋值
        ViewModel = viewModel;
        _deviceChange = deviceChange;

        // 托盘菜单是 H.NotifyIcon 转成 Win32 弹出菜单再执行的，只能走 Command（不能挂 Click）
        ShowWindowCommand = new RelayCommand(ShowFromTray);
        HideWindowCommand = new RelayCommand(HideToTray);

        InitializeComponent();

        _driveRefreshSoon = CreateDriveRefreshTimer(600);
        _driveRefreshBackstop = CreateDriveRefreshTimer(2500);

        // 卷插拔是系统广播给所有顶层窗口的 WM_DEVICECHANGE，所以要挂在自己的窗口句柄上。
        // 窗口隐藏到托盘时它仍是顶层窗口，消息照样收得到。
        _deviceChange.VolumesChanged += OnVolumesChanged;
        _deviceChange.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // 「转到 → 所有位置」列的是磁盘清单：插拔后要跟着重列一遍，否则会一直列着已经拔掉的盘
        ViewModel.Drives.CollectionChanged += (_, _) => BuildLocationsMenu();

        Title = "exdir";

        // 托盘图标：用和窗口图标同一个 .ico。这里直接按路径读文件（不走 ms-appx），
        // 非打包部署下少一层 URI 解析，也少一种“图标空白”的失败方式；
        // 尺寸按 SM_CXSMICON 选（.ico 里 16/24/32 都有，不会走到重采样）
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "exdir.ico");
        if (File.Exists(iconPath))
        {
            var iconSize = DpiHelper.GetSmallIconSize();
            TrayIcon.Icon = new System.Drawing.Icon(iconPath, new System.Drawing.Size(iconSize, iconSize));
        }
        else
        {
            // 没有它托盘图标会是一块空白，日志里留一笔
            //（非打包部署下这个文件靠 csproj 里的 CopyToOutputDirectory 带出来）
            Log.Write($"托盘图标：找不到 {iconPath}，托盘图标将没有图案");
        }

        // 隐藏系统标题栏，改用 WinUI TitleBar 控件（保留系统窗口按钮与贴靠布局）
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 主题要在窗口第一次渲染之前就位，否则浅色机器上会先闪一下深色
        //（ActualTheme 要到首次布局之后才是最终值，那时由 OnRootGridLoaded / ActualThemeChanged 再刷一次）
        ApplyTheme();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.ExitRequested += (_, _) => RequestExit();

        // 「跟随系统」时系统主题一变，RootGrid.ActualTheme 会自己跟着变（不需要重设 RequestedTheme），
        // 但图标、悬停提示与系统窗口按钮的颜色得跟着刷新
        RootGrid.ActualThemeChanged += (_, _) => SyncThemeVisuals();

        AppWindow.Closing += OnAppWindowClosing;

        // 窗口位置/尺寸必须等首次布局完成后才能恢复：
        // 在那之前 XamlRoot 和窗口 DPI 都取不到，会把 DIP 当成物理像素。
        RootGrid.Loaded += OnRootGridLoaded;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>托盘左键单击 / 菜单「显示主窗口」。</summary>
    public ICommand ShowWindowCommand { get; }

    /// <summary>菜单「文件 → 隐藏到托盘」。</summary>
    public ICommand HideWindowCommand { get; }

    // ------------------------------------------------------------------ 生命周期

    private async void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;

        if (_loaded)
        {
            return;
        }

        _loaded = true;

        // 首次布局之后 ActualTheme 才是最终值：把图标与系统窗口按钮的颜色再刷一遍
        SyncThemeVisuals();

        RestoreWindowPlacement();
        BuildLocationsMenu();

        await ViewModel.InitializeAsync();

        ApplyLayout();
        SyncSidebarSelection();

        // 会话已恢复：从现在起可以处理命令行请求了（启动时就带了路径的那一次也在队列里等这一步）
        _initialized.TrySetResult();
        QueueActivation(CommandLine.Request);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
        ViewModel.SaveSession();

        if (_exitRequested)
        {
            // 真的要退出：让窗口销毁（进程退出由 RequestExit 保证）
            return;
        }

        // 关窗口 = 隐藏到托盘。这里必须拦下这次关闭：窗口一旦真的销毁，
        // 托盘图标、DI 容器、两个窗格、图标缓存就全部跟着没了，再打开又得从头建。
        args.Cancel = true;
        HideToTray();
    }

    // ------------------------------------------------------------------ 命令行 / 其它实例的请求

    /// <summary>
    /// 处理其它实例转发过来的请求（双击 exe 或 <c>exdir [path]</c>）：
    /// 先把窗口叫到眼前，再按请求打开路径。
    ///
    /// 唯一例外是 <c>--preload</c>（开机自启）：那只代表“系统里已经有一份在预热的 exdir 了”，
    /// 绝不能把窗口弹出来（否则开机时反而会看到一个主窗口）。
    /// </summary>
    public void HandleActivation(string request)
    {
        if (CommandLine.IsPreloadRequest(request))
        {
            Log.Write("收到预热请求：已有进程在跑，不显示主窗口");
            return;
        }

        ShowFromTray();
        QueueActivation(request);
    }

    /// <summary>把请求排进队列并串行处理（空请求 = 只唤回窗口、预热请求 = 什么都不做，都不必排队）。</summary>
    private void QueueActivation(string request)
    {
        if (string.IsNullOrEmpty(request) || CommandLine.IsPreloadRequest(request))
        {
            return;
        }

        _pendingActivations.Enqueue(request);

        if (_activating)
        {
            return;
        }

        _ = DrainActivationsAsync();
    }

    private async Task DrainActivationsAsync()
    {
        _activating = true;

        try
        {
            while (_pendingActivations.Count > 0)
            {
                var request = _pendingActivations.Dequeue();

                // 会话没恢复完就先等着：否则新标签页会和恢复出来的标签页抢活动标签
                await _initialized.Task;
                await ViewModel.HandleActivationAsync(request);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("命令行请求", ex);
        }
        finally
        {
            _activating = false;
        }
    }

    // ------------------------------------------------------------------ 开机自启（预热启动）

    /// <summary>
    /// 开机自启的预热启动：**不显示主窗口**，只把“用户下一次双击 exe 时要等的东西”先备好。
    ///
    /// 预热的内容：进程 / DI 容器（已就绪）、上次打开的目录会话（包括枚举）、侧边栏树、
    /// 以及首屏那几十行的外壳图标。真正关窗口与“只藏到托盘”的区别只是这里从来不 Activate ——
    /// 进程、托盘、两个窗格、标签页全都在，所以之后双击 exe / 点托盘图标时
    /// （第二次进程会把请求转进来，见 <see cref="HandleActivation" />）只要一次 ShowWindow。
    /// </summary>
    internal void StartPreload()
    {
        // 窗口自始至终没 Activate 过，本来就是隐藏的；但“已隐藏到托盘”这个状态必须记上，
        // 否则 ShowFromTray 会走进“只是被最小化”那条分支、什么都不做（窗口永远出不来）。
        _hiddenToTray = true;

        Log.Write("预热启动：不显示主窗口，只恢复会话与首屏图标");
        _ = PreloadAsync();
    }

    private async Task PreloadAsync()
    {
        try
        {
            var started = Environment.TickCount64;

            // 会话恢复（枚举上次打开的目录、建侧边栏树）是弹出窗口前最贵的一步，在这里做掉。
            // 窗口第一次真的显示时 OnRootGridLoaded 还会再调一次，但 InitializeAsync 是幂等的。
            await ViewModel.InitializeAsync();

            var icons = await ViewModel.PreloadIconsAsync();
            var folder = ViewModel.ActivePane.ActiveTab?.CurrentPath ?? "(无)";

            // 命令行请求的等待者不必等窗口显示
            _initialized.TrySetResult();

            Log.Write($"预热完成：用时 {Environment.TickCount64 - started} ms，目录={folder}，图标={icons} 个");
        }
        catch (Exception ex)
        {
            Log.Exception("预热", ex);
        }
    }

    // ------------------------------------------------------------------ 托盘驻留

    /// <summary>把窗口藏进托盘（进程继续跑）。</summary>
    private void HideToTray()
    {
        if (_exitRequested)
        {
            return;
        }

        // 最大化状态要自己记：H.NotifyIcon 的 Show 走的是 ShowWindow(SW_SHOWNORMAL)，
        // 它会把最大化的窗口还原成普通尺寸，唤回时要补回来
        _restoreMaximized = AppWindow.Presenter is OverlappedPresenter
            { State: OverlappedPresenterState.Maximized };

        try
        {
            // 扩展方法：ShowWindow(SW_HIDE) + 打开效率模式（Idle 时把 CPU 让出来，
            // 隐藏起来的文件管理器没必要占着高优先级）
            this.Hide();
            _hiddenToTray = true;
        }
        catch (Exception ex)
        {
            // 隐藏失败（极少见）就保持窗口可见：不能让用户点了关闭之后既看不到窗口、也不在托盘里
            Log.Exception("隐藏到托盘", ex);
            return;
        }

        Log.Write($"窗口已隐藏到托盘（进程继续驻留，托盘图标={TrayIcon.IsCreated}）");
    }

    /// <summary>
    /// 把窗口叫到眼前。第二次双击 exe / 命令行 <c>exdir</c> / 点托盘图标都走这里。
    /// </summary>
    public void ShowFromTray()
    {
        if (_exitRequested)
        {
            return;
        }

        try
        {
            if (_hiddenToTray)
            {
                // 扩展方法：ShowWindow(SW_SHOWNORMAL) + 关掉效率模式
                this.Show();
                _hiddenToTray = false;

                if (_restoreMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                    _restoreMaximized = false;
                }
            }
            else if (AppWindow.Presenter is OverlappedPresenter minimized
                     && minimized.State == OverlappedPresenterState.Minimized)
            {
                // 窗口只是被最小化了（不是藏进托盘）：Activate 不会把它还原，得自己来。
                // 这一支不能走 ShowWindow(SW_SHOWNORMAL) —— 那会顺手取消最大化。
                minimized.Restore();
            }

            // ShowWindow 不抢前台；从托盘唤回本来就是要“跳到眼前”，必须自己激活。
            // 日志固定一句（上面几个分支都算“唤回”），回归脚本据此断言请求被兑现了。
            Activate();
            Log.Write("窗口已从托盘唤回");
        }
        catch (Exception ex)
        {
            Log.Exception("唤回窗口", ex);
        }
    }

    // ------------------------------------------------------------------ 卷插拔刷新

    /// <summary>
    /// 收到卷变化通知（U 盘插入 / 拔出、光盘换盘、网络盘映射变化）：安排两次延迟刷新。
    /// 处理器是在窗口消息线程（UI 线程）上调用的，所以里面可以直接改集合。
    /// </summary>
    private void OnVolumesChanged(object? sender, EventArgs e)
    {
        RestartDriveRefresh(_driveRefreshSoon);
        RestartDriveRefresh(_driveRefreshBackstop);
        Log.Write("检测到卷变化（WM_DEVICECHANGE），安排刷新磁盘");
    }

    private DispatcherQueueTimer CreateDriveRefreshTimer(int delayMs)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        timer.IsRepeating = false;
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            ViewModel.RefreshDrives();
        };

        return timer;
    }

    /// <summary>重新开始计时（连发的事件只留下一轮刷新，不会刷出一串）。</summary>
    private static void RestartDriveRefresh(DispatcherQueueTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    /// <summary>真正退出：摘掉托盘图标、允许窗口销毁，并保证进程结束。</summary>
    private void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;

        try
        {
            SaveWindowPlacement();
            ViewModel.SaveSession();

            // 窗口都要销毁了，卷插拔监听也一并摘掉
            _deviceChange.Detach();

            // 先摘托盘图标：退出过程中窗口还会收到 WM_CLOSE，图标不能等到进程结束才消失
            TrayIcon.Dispose();

            Log.Write("退出：关闭窗口并结束进程");

            Close();

            // 窗口销毁后 WinUI 未必自己结束消息循环（隐藏过的窗口尤其如此），显式收尾一次
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Log.Exception("退出", ex);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSidebarVisible):
            case nameof(MainViewModel.IsDualPane):
            case nameof(MainViewModel.IsToolbarVisible):
                ApplyLayout();
                break;

            case nameof(MainViewModel.ActiveDirectoryName):
            case nameof(MainViewModel.ActiveDirectoryPath):
                SyncSidebarSelection();
                break;

            case nameof(MainViewModel.Theme):
                ApplyTheme();
                break;
        }
    }

    // ------------------------------------------------------------------ 主题

    /// <summary>
    /// 应用主题：只改根元素的 <c>RequestedTheme</c>。
    /// <c>Application.RequestedTheme</c> 启动之后不允许再改，而根元素这一个属性一改，
    /// 菜单栏 / 工具条 / 侧边栏 / 两个窗格 / 状态栏就全部跟着换（弹层同理：MenuFlyout 与
    /// ContentDialog 的 XamlRoot 都是它）。设置窗口是另一个 Window，不继承这份主题，要单独推一次。
    /// </summary>
    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ThemeHelper.ToElementTheme(ViewModel.Theme);

        SyncThemeVisuals();

        _settingsWindow?.ApplyTheme();

        Log.Write($"主题已应用：{ThemeHelper.ToDisplayName(ViewModel.Theme)}（实际 {RootGrid.ActualTheme}）");
    }

    /// <summary>按**实际画出来的**主题刷新与主题相关的视觉：图标 / 悬停提示 + 系统窗口按钮颜色。</summary>
    private void SyncThemeVisuals()
    {
        SyncThemeToggle();
        UpdateCaptionButtons();
    }

    /// <summary>
    /// 标题栏那个太阳 / 月亮按钮：字形与悬停提示都按**实际画出来的**主题来。
    /// 读 ActualTheme 而不是设置值，是为了「跟随系统」时图标与画面一致。
    /// </summary>
    private void SyncThemeToggle()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;

        ThemeToggle.IsChecked = dark;
        ThemeGlyph.Glyph = dark ? "\uE708" : "\uE706";
        ToolTipService.SetToolTip(ThemeToggle, dark ? "切换到浅色模式" : "切换到深色模式");
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        // ToggleButton 已经把自己的 IsChecked 翻过来了，这里只把结果固定成显式的浅 / 深
        // （从「跟随系统」拨动也是一样：拨哪儿算哪儿）
        ViewModel.SetDarkMode(ThemeToggle.IsChecked == true);
    }

    /// <summary>
    /// 系统窗口按钮（最小化 / 最大化 / 关闭）由 AppWindow 原生绘制，不跟着我们的 ElementTheme 走，
    /// 系统主题与 exdir 主题不一致时必须自己给它们上色，否则“深色系统 + 浅色 exdir”下
    /// 按钮会是白的、几乎看不见。给 null 表示回到系统默认。
    /// </summary>
    private void UpdateCaptionButtons()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var dark = RootGrid.ActualTheme == ElementTheme.Dark;

            var foreground = dark ? Colors.White : Colors.Black;
            var hover = dark
                ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
            var pressed = dark
                ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x28, 0x00, 0x00, 0x00);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = dark
                ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x80, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = foreground;
        }
        catch (Exception ex)
        {
            // 颜色设不上不影响用（只是一块观感不好），但要留一笔
            Log.Exception("设置系统窗口按钮颜色", ex);
        }
    }

    // ------------------------------------------------------------------ 布局

    private void ApplyLayout()
    {
        // --- 侧边栏 ---
        var sidebarVisible = ViewModel.IsSidebarVisible;
        Sidebar.Visibility = sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.MinWidth = sidebarVisible ? 140 : 0;
        SidebarColumn.MaxWidth = sidebarVisible ? 560 : 0;
        SidebarColumn.Width = sidebarVisible
            ? new GridLength(Math.Clamp(_sidebarWidth, 140, 560))
            : new GridLength(0);

        // --- 工具条 ---
        DriveBar.Visibility = ViewModel.IsToolbarVisible ? Visibility.Visible : Visibility.Collapsed;

        // --- 双窗格 ---
        var dual = ViewModel.IsDualPane;
        SecondaryPane.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        PanesSplitter.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        PaneSplitterColumn.Width = dual ? new GridLength(6) : new GridLength(0);

        if (dual)
        {
            var saved = ViewModel.PrimaryPaneWidth;
            PrimaryPaneColumn.Width = saved is > 200
                ? new GridLength(saved.Value)
                : new GridLength(1, GridUnitType.Star);
            SecondaryPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            PrimaryPaneColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryPaneColumn.Width = new GridLength(0);
        }
    }

    private void SidebarSplitter_DeltaChanged(object? sender, double delta)
    {
        if (!ViewModel.IsSidebarVisible)
        {
            return;
        }

        var target = Math.Clamp(SidebarColumn.ActualWidth + delta, 140, 560);
        _sidebarWidth = target;
        ViewModel.SidebarWidth = target;

        SidebarColumn.MinWidth = 0;
        SidebarColumn.MaxWidth = double.PositiveInfinity;
        SidebarColumn.Width = new GridLength(target);
    }

    private void PanesSplitter_DeltaChanged(object? sender, double delta)
    {
        if (!ViewModel.IsDualPane)
        {
            return;
        }

        var total = PrimaryPaneColumn.ActualWidth + SecondaryPaneColumn.ActualWidth;
        var min = 200d;
        var target = Math.Clamp(PrimaryPaneColumn.ActualWidth + delta, min, Math.Max(min, total - min));

        PrimaryPaneColumn.Width = new GridLength(target);
        SecondaryPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        ViewModel.PrimaryPaneWidth = target;
    }

    // ------------------------------------------------------------------ 菜单

    /// <summary>“转到 → 所有位置”在窗口激活时按当前用户环境构建。</summary>
    private void BuildLocationsMenu()
    {
        LocationsSubItem.Items.Clear();

        AddLocation("主目录", ViewModel.HomePath);
        foreach (var folder in ViewModel.SpecialFolders)
        {
            AddLocation(folder.Name, folder.Path);
        }

        LocationsSubItem.Items.Add(new MenuFlyoutSeparator());

        foreach (var drive in ViewModel.Drives)
        {
            AddLocation(drive.ToolbarText, drive.RootPath);
        }
    }

    private void AddLocation(string name, string path)
    {
        LocationsSubItem.Items.Add(new MenuFlyoutItem
        {
            Text = name,
            Command = ViewModel.NavigateToCommand,
            CommandParameter = path,
        });
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
        => ViewModel.IsSidebarVisible = !ViewModel.IsSidebarVisible;

    /// <summary>
    /// 「配置 → 设置…」：打开设置窗口（已经开着就唤到前台）。
    /// 窗口里每改一项就即时生效并立即落盘（见 <see cref="Views.SettingsWindow" />），所以这里没有返回值。
    /// </summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        try
        {
            var window = new SettingsWindow(ViewModel);
            window.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow = window;
            window.Activate();
        }
        catch (Exception ex)
        {
            _settingsWindow = null;
            Diagnostics.Log.Exception("设置窗口", ex);
        }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "关于 exdir",
            Content = new TextBlock
            {
                Text = "exdir —— 一个紧凑型双窗格文件管理器\n\n"
                     + "技术栈：WinUI 3 + Windows App SDK（非打包部署）\n"
                     + $"版本：{typeof(App).Assembly.GetName().Version}\n"
                     + $"配置文件：{ViewModel.SettingsFilePath}",
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "确定",
        };

        await dialog.ShowAsync();
    }

    // ------------------------------------------------------------------ 快捷键

    private void Accelerator_GoBack(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.GoBackCommand.Execute(null);
    }

    private void Accelerator_GoForward(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.GoForwardCommand.Execute(null);
    }

    private void Accelerator_GoUp(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.GoUpCommand.Execute(null);
    }

    private void Accelerator_Refresh(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.RefreshActivePaneCommand.Execute(null);
    }

    private void Accelerator_NewTab(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.NewTabCommand.Execute(null);
    }

    private void Accelerator_CloseTab(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.CloseActiveTabCommand.Execute(null);
    }

    private void Accelerator_ToggleHidden(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ToggleHiddenFilesCommand.Execute(null);
    }

    private void Accelerator_ToggleSidebar(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ToggleSidebarCommand.Execute(null);
    }

    private void Accelerator_SwitchPane(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.SwitchActivePaneCommand.Execute(null);
    }

    private void Accelerator_ToggleDualPane(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ToggleDualPaneCommand.Execute(null);
    }

    private void Accelerator_EditPath(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.EditActivePathCommand.Execute(null);
    }

    // ------------------------------------------------------------------ 窗口位置

    private void RestoreWindowPlacement()
    {
        try
        {
            var settings = ViewModel.Settings;
            var scale = DpiHelper.GetScale(this);

            // 下限是 DIP（720×480）：先夹再换算成物理像素。
            // 反过来（先换算再对物理像素取 max）在高分屏上会把窗口压成下限的一半宽
            // ——设置里存了个 157 DIP 这种被弄坏的值时，窗口会小到只剩侧边栏。
            var width = DpiHelper.ToPhysical(Math.Max(720, settings.WindowWidth), scale);
            var height = DpiHelper.ToPhysical(Math.Max(480, settings.WindowHeight), scale);

            var hasPosition = !double.IsNaN(settings.WindowX) && !double.IsNaN(settings.WindowY);
            var x = hasPosition ? DpiHelper.ToPhysical(settings.WindowX, scale) : 0;
            var y = hasPosition ? DpiHelper.ToPhysical(settings.WindowY, scale) : 0;

            if (hasPosition && IsOnScreen(x, y, width, height))
            {
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(width, height));
            }

            if (settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "exdir.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }

            _sidebarWidth = settings.SidebarWidth;

            Diagnostics.Log.Write($"恢复窗口位置: 设置={settings.WindowWidth}x{settings.WindowHeight} DIP@{settings.WindowX},{settings.WindowY}, 缩放={scale:N2} → 实际={width}x{height} 物理像素");
        }
        catch (Exception)
        {
            // 位置恢复失败时使用系统默认窗口大小
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var settings = ViewModel.Settings;
            var scale = DpiHelper.GetScale(this);
            var presenter = AppWindow.Presenter as OverlappedPresenter;

            settings.WindowMaximized = presenter?.State == OverlappedPresenterState.Maximized;

            // 最小化时 AppWindow.Position 报的是 (-32000,-32000) 这类哨兵值，
            // 照抄会把窗口位置写坏（下次启动算出来在屏幕外，只能拿到默认位置），所以跳过
            var minimized = presenter?.State == OverlappedPresenterState.Minimized;
            var position = AppWindow.Position;
            var positionUsable = position.X > -32000 && position.Y > -32000;

            if (settings.WindowMaximized != true && !minimized && positionUsable)
            {
                var size = AppWindow.Size;

                settings.WindowWidth = DpiHelper.ToDips(size.Width, scale);
                settings.WindowHeight = DpiHelper.ToDips(size.Height, scale);
                settings.WindowX = DpiHelper.ToDips(position.X, scale);
                settings.WindowY = DpiHelper.ToDips(position.Y, scale);
            }

            settings.SidebarWidth = _sidebarWidth;
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static bool IsOnScreen(int x, int y, int width, int height)
    {
        // 简单校验：窗口左上角必须落在主显示器可视范围内
        var display = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Primary);
        var bounds = display.WorkArea;
        return x >= bounds.X - 32
               && y >= bounds.Y - 32
               && x + width <= bounds.X + bounds.Width + 32
               && y + height <= bounds.Y + bounds.Height + 32;
    }

    private void SyncSidebarSelection()
    {
        if (!string.IsNullOrEmpty(ViewModel.ActiveDirectoryPath))
        {
            Sidebar.SyncToPath(ViewModel.ActiveDirectoryPath);
        }
    }
}
