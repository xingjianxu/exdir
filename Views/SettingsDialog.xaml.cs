using Exdir.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// 统一的设置对话框（底部「保存 / 取消」）。
/// 内容绑定的是调用方传进来的 <see cref="SettingsViewModel" /> 快照：
/// <c>ShowAsync</c> 返回 <see cref="ContentDialogResult.Primary" /> 才由调用方应用改动。
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(SettingsViewModel viewModel)
    {
        // 和主窗口一样：x:Bind 在 InitializeComponent 期间求值，必须在此之前赋值
        ViewModel = viewModel;

        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
