using System;
using System.ComponentModel;
using System.IO;
using Exdir.Helpers;
using Exdir.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Exdir;

/// <summary>
/// 主窗口外壳：顶部菜单栏（含窗口按钮）、title 栏、侧边栏与 1~2 个文件窗格。
/// </summary>
public sealed partial class MainWindow : Window
{
    private double _sidebarWidth = 232;
    private bool _loaded;

    public MainWindow(MainViewModel viewModel)
    {
        // x:Bind 在 InitializeComponent 期间求值，因此必须先赋值
        ViewModel = viewModel;

        InitializeComponent();

        Title = "exdir";

        // 隐藏系统标题栏，改用 WinUI TitleBar 控件（保留系统窗口按钮与贴靠布局）
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.ExitRequested += (_, _) => Close();

        AppWindow.Closing += OnAppWindowClosing;

        // 窗口位置/尺寸必须等首次布局完成后才能恢复：
        // 在那之前 XamlRoot 和窗口 DPI 都取不到，会把 DIP 当成物理像素。
        RootGrid.Loaded += OnRootGridLoaded;
    }

    public MainViewModel ViewModel { get; }

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

    // ------------------------------------------------------------------ 窗口位置

    private void RestoreWindowPlacement()
    {
        try
        {
            var settings = ViewModel.Settings;
            var scale = DpiHelper.GetScale(this);

            var width = Math.Max(720, DpiHelper.ToPhysical(settings.WindowWidth, scale));
            var height = Math.Max(480, DpiHelper.ToPhysical(settings.WindowHeight, scale));

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

            if (settings.WindowMaximized != true)
            {
                var size = AppWindow.Size;
                var position = AppWindow.Position;

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
