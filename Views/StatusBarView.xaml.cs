using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// 文件列表区底部的状态栏：一行高、贴底，显示活动窗格的条目数 / 选中摘要 / 磁盘可用空间。
/// </summary>
public sealed partial class StatusBarView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(StatusBarViewModel),
        typeof(StatusBarView),
        new PropertyMetadata(null));

    public StatusBarView()
    {
        InitializeComponent();
    }

    public StatusBarViewModel? ViewModel
    {
        get => (StatusBarViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
