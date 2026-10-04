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
/// 更新窗口（「帮助 → 检查更新…」发现新版本时打开，或点主窗口顶部提示条上的「立即更新」）。
/// 界面在 <see cref="UpdateView" />，这里只做四件事：接上共用的 <see cref="UpdateViewModel" />、
/// 按默认尺寸居中打开、跟主窗口同步主题、把「重启并完成更新」转成本窗口的关闭。
///
/// **同一时刻只开一个**（由 <see cref="MainWindow" /> 持有并复用），它拿的也是主窗口那一份
/// UpdateViewModel —— 顶部提示条与这个窗口看到的状态永远是同一份。
/// </summary>
public sealed partial class UpdateWindow : Window
{
    /// <summary>默认宽高（DIP）：够放发行说明与进度条，又不至于遮住整个屏幕。</summary>
    private const double DefaultWidthDips = 620;

    private const double DefaultHeightDips = 560;

    /// <summary>主窗口的 ViewModel（只读它的 Theme，用来跟主窗口同步主题）。</summary>
    private readonly MainViewModel _owner;

    public UpdateWindow(MainViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _owner = owner;

        InitializeComponent();

        Title = "更新 exdir";

        View.ViewModel = owner.Update;
        View.HostWindow = this;

        // 主窗口的主题一变，本窗口也得跟着换（另一个 Window 不继承 RequestedTheme）。
        // 关窗必须退订：owner 是单例（活到进程结束），不退订会让关掉的窗口一直被它引用；
        // `RestartRequested` 同理（不退订的话第二次打开更新窗口时旧窗口也会被叫到）。
        owner.PropertyChanged += OnOwnerPropertyChanged;
        Closed += (_, _) =>
        {
            owner.PropertyChanged -= OnOwnerPropertyChanged;
            owner.Update.RestartRequested -= OnRestartRequested;

            // 下到一半就被关掉：取消下载（半份包留着没用，%LOCALAPPDATA%\exdir\update 会在下次启动时清）
            owner.Update.OnWindowClosed();
        };

        // 「重启并完成更新」之后 exdir 就该退出（替换脚本在等这个进程消失）
        owner.Update.RestartRequested += OnRestartRequested;

        ApplyTheme();
        ApplyDefaultPlacement();
    }

    /// <summary>把主窗口当前的主题推给本窗口。</summary>
    public void ApplyTheme() => View.RequestedTheme = ThemeHelper.ToElementTheme(_owner.Theme);

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Theme))
        {
            ApplyTheme();
        }
    }

    private void OnRestartRequested(object? sender, EventArgs e)
    {
        // 真正退出进程由 MainWindow 负责（它要摘托盘图标、保存会话与窗口位置）。
        // 退订交给 Closed —— 这里不再自己摘（摘了也行，重复 -= 是无害的）。
        _owner.RequestExitForUpdate();
    }

    /// <summary>
    /// 默认尺寸 620×560 DIP，工作区放不下就退让，并在显示器工作区居中。
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
            // 放不下也不影响使用
            Log.Exception("更新窗口的位置/尺寸", ex);
        }
    }
}
