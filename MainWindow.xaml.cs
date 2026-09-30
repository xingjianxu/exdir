using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.ViewModels;
using Exdir.Views;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

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

    private double _sidebarWidth = 232;
    private bool _loaded;

    /// <summary>用户是否点了「退出」（而不是关窗口）：决定 AppWindow.Closing 里拦不拦。</summary>
    private bool _exitRequested;

    /// <summary>隐藏到托盘那一刻窗口是不是最大化的（ShowWindow(SW_SHOWNORMAL) 会把最大化还原掉）。</summary>
    private bool _restoreMaximized;

    public MainWindow(MainViewModel viewModel)
    {
        // x:Bind 在 InitializeComponent 期间求值，因此必须先赋值
        ViewModel = viewModel;

        // 托盘菜单是 H.NotifyIcon 转成 Win32 弹出菜单再执行的，只能走 Command（不能挂 Click）
        ShowWindowCommand = new RelayCommand(ShowFromTray);
        HideWindowCommand = new RelayCommand(HideToTray);

        InitializeComponent();

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

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.ExitRequested += (_, _) => RequestExit();

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

        RestoreWindowPlacement();
        BuildLocationsMenu();

        await ViewModel.InitializeAsync();

        ApplyLayout();
        SyncSidebarSelection();
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
        }
        catch (Exception ex)
        {
            // 隐藏失败（极少见）就保持窗口可见：不能让用户点了关闭之后既看不到窗口、也不在托盘里
            Log.Exception("隐藏到托盘", ex);
            return;
        }

        Log.Write($"窗口已隐藏到托盘（进程继续驻留，托盘图标={TrayIcon.IsCreated}）");
    }

    /// <summary>把窗口从托盘里叫回来。第二次双击 exe 也走这里（由单实例闸门转过来）。</summary>
    public void ShowFromTray()
    {
        if (_exitRequested)
        {
            return;
        }

        try
        {
            // 扩展方法：ShowWindow(SW_SHOWNORMAL) + 关掉效率模式
            this.Show();

            if (_restoreMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
                _restoreMaximized = false;
            }

            // ShowWindow 不抢前台；从托盘唤回本来就是要“跳到眼前”，必须自己激活
            Activate();
            Log.Write("窗口已从托盘唤回");
        }
        catch (Exception ex)
        {
            Log.Exception("唤回窗口", ex);
        }
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
