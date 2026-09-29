using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// 标签页内部的导航条（后退/前进/上一级/刷新 + 地址栏）。
/// 每个标签页各有一份实例，因此地址栏状态与历史不会在标签页之间串台。
/// </summary>
public sealed partial class NavigationBarView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(FolderTabViewModel),
        typeof(NavigationBarView),
        new PropertyMetadata(null));

    public NavigationBarView()
    {
        InitializeComponent();
    }

    public FolderTabViewModel? ViewModel
    {
        get => (FolderTabViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
