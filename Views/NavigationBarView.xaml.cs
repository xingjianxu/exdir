using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Exdir.Views;

/// <summary>
/// 标签页内部的导航条（后退/前进/上一级/刷新 + 可编辑路径框）。
/// 每个标签页各有一份实例，因此路径输入与历史不会在标签页之间串台。
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

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is not { } tab)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                tab.NavigatePathCommand.Execute(null);
                break;

            case VirtualKey.Escape:
                e.Handled = true;
                tab.PathInput = tab.CurrentPath;
                break;
        }
    }
}
