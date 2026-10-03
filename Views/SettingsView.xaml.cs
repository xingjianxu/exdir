using Exdir.Helpers;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// 设置窗口的正文（左导航 + 右侧设置卡片）。
///
/// <see cref="ViewModel" /> 是依赖属性：父级（<see cref="SettingsWindow" />）在
/// <c>InitializeComponent</c> 之后才赋值，普通 CLR 属性此时已经错过了 x:Bind 的求值时机。
/// </summary>
public sealed partial class SettingsView : UserControl
{
    /// <summary>编辑模型（设置窗口的那一份）。</summary>
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(SettingsViewModel),
        typeof(SettingsView),
        new PropertyMetadata(null, OnViewModelChanged));

    public SettingsView()
    {
        InitializeComponent();

        // 滑块右边的数值文本由这里刷新（纯展示，没必要为它在 ViewModel 上再开一个属性）
        RowHeightSlider.ValueChanged += (_, _) => UpdateRowHeightText();

        Loaded += OnLoaded;
    }

    /// <summary>编辑模型；由 <see cref="SettingsWindow" /> 赋值。</summary>
    public SettingsViewModel? ViewModel
    {
        get => (SettingsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// 承载本控件的窗口（同样由 <see cref="SettingsWindow" /> 赋值）。
    /// 弹文件夹选择器时要拿它的 HWND 当属主，否则非打包进程里的外壳对话框起不来。
    /// </summary>
    public Window? HostWindow { get; set; }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SettingsView)d;

        // 默认停在第一个分类；之后 NavigationView 自己会在点击时更新选中项
        view.Nav.SelectedItem = view.ViewModel?.SelectedCategory;

        view.UpdateRowHeightText();
    }

    /// <summary>
    /// 分类切换。用 <c>SelectionChanged</c> 而不是 <c>ItemInvoked</c>：后者只在“用户点了 / 按了”
    /// 这一项时才触发，键盘方向键切换与程序化选中（自动化脚本的 SelectionItemPattern、
    /// 或直接赋 SelectedItem）都不会触发它，那样右侧页面就不会跟着换。
    /// </summary>
    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ViewModel is { } viewModel && args.SelectedItem is SettingsCategoryViewModel category)
        {
            viewModel.SelectedCategory = category;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // UserControl 被重新挂载时会再触发一次，只做一次就够
        Loaded -= OnLoaded;

        // 「右键菜单」页的清单要现枚举（建 COM 对象、QueryContextMenu），几十到几百毫秒：
        // 先让窗口画出来（显示已记下来的那份清单），再放到下一轮消息循环里补全
        DispatcherQueue.TryEnqueue(() => ViewModel?.RefreshShellMenuItems());
    }

    private void UpdateRowHeightText()
    {
        if (ViewModel is { } viewModel)
        {
            RowHeightValue.Text = $"{viewModel.RowHeight:0}";
        }
    }

    /// <summary>
    /// 「压缩输出目录」旁边那个「浏览…」：弹外壳的文件夹选择器，选完直接写回绑定属性
    ///（写回会触发 <see cref="SettingsViewModel.Changed" /> → 即时落盘）。
    /// 用户取消选择时什么也不做，文本框里的值保持不变。
    /// </summary>
    private void BrowseCompressionOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || HostWindow is null)
        {
            return;
        }

        var picked = FolderPicker.PickFolder(
            WinRT.Interop.WindowNative.GetWindowHandle(HostWindow),
            viewModel.CompressionOutputDirectory);

        if (!string.IsNullOrWhiteSpace(picked))
        {
            viewModel.CompressionOutputDirectory = picked;
        }
    }
}
