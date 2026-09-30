using System;
using System.ComponentModel;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Exdir.Views;

/// <summary>
/// 设置窗口（「配置 → 设置…」）。界面在 <see cref="SettingsView" />（左导航 + 设置卡片），
/// 这里只做三件事：给正文接上编辑模型、按默认尺寸居中打开、把“改动即时生效”接到主 ViewModel 上。
///
/// 设置是即时生效的：编辑模型任何一项被改动，就调 <see cref="MainViewModel.ApplySettings" />
/// 写回并立即落盘，没有“保存 / 取消”。窗口由 <see cref="MainWindow" /> 持有并复用（同一时刻只开一个）。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>主窗口的 ViewModel（只读它的 Theme，用来跟主窗口同步主题）。</summary>
    private readonly MainViewModel _owner;

    /// <summary>
    /// 默认宽高（DIP）：860×800 —— 左导航 180 + 右侧正文约 630。
    /// 宽度不能小：Windows 11 的 SettingsCard 在卡片宽度小于 476 DIP 时会把右侧控件
    /// 换行到标题下方（工具包里的 SettingsCardWrapThreshold），那就不是标准的 Win11 观感了。
    /// </summary>
    private const double DefaultWidthDips = 860;

    private const double DefaultHeightDips = 800;

    public SettingsWindow(MainViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _owner = owner;

        InitializeComponent();

        Title = "设置";

        ViewModel = owner.CreateSettingsEditor();
        View.ViewModel = ViewModel;

        // 即时生效：任何一项改动都由这里写回并落盘
        ViewModel.Changed += (_, _) => owner.ApplySettings(ViewModel);

        // 主题是主窗口与本窗口共享的：标题栏那个太阳 / 月亮开关一拨，本窗口也得当场换色
        // （本窗口是另一个 Window，不继承主窗口的 RequestedTheme）。
        // 关窗时必须退订：owner 是单例（活到进程结束），不退订会让关掉的窗口一直被它引用。
        owner.PropertyChanged += OnOwnerPropertyChanged;
        Closed += (_, _) => owner.PropertyChanged -= OnOwnerPropertyChanged;

        ApplyTheme();
        ApplyDefaultPlacement();
    }

    /// <summary>这一份编辑模型（窗口关闭即丢弃）。</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>把主窗口当前的主题推给本窗口（含左导航 / 设置卡片，以及「主题」下拉框的选中项）。</summary>
    public void ApplyTheme()
    {
        var theme = _owner.Theme;

        View.RequestedTheme = ThemeHelper.ToElementTheme(theme);
        ViewModel.SyncTheme(theme);
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Theme))
        {
            ApplyTheme();
        }
    }

    /// <summary>
    /// 默认尺寸 860×800 DIP，工作区放不下就退让，并在显示器工作区居中。
    /// 用主窗口的缩放因子换算 DIP（此刻本窗口还没有 XamlRoot，量不到 DPI）。
    /// </summary>
    private void ApplyDefaultPlacement()
    {
        try
        {
            var scale = App.MainWindow is { } main ? DpiHelper.GetScale(main) : 1.0;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

            var width = Math.Min(DpiHelper.ToPhysical(DefaultWidthDips, scale), area.Width);
            var height = Math.Min(DpiHelper.ToPhysical(DefaultHeightDips, scale), area.Height);

            AppWindow.MoveAndResize(new RectInt32(
                area.X + ((area.Width - width) / 2),
                area.Y + ((area.Height - height) / 2),
                width,
                height));
        }
        catch (Exception ex)
        {
            // 放不下也不影响使用：窗口还在，用户可以自己拖
            Log.Exception("设置窗口的位置/尺寸", ex);
        }
    }
}
