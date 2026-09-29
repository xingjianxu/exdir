using System;
using System.ComponentModel;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Exdir.Views;

/// <summary>一个文件管理窗格：标签页集合，每个标签页内部自带导航条。</summary>
public sealed partial class PaneView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(PanelViewModel),
        typeof(PaneView),
        new PropertyMetadata(null, OnViewModelChanged));

    public PaneView()
    {
        InitializeComponent();
    }

    public PanelViewModel? ViewModel
    {
        get => (PanelViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PaneView view)
        {
            return;
        }

        if (e.OldValue is PanelViewModel old)
        {
            old.PropertyChanged -= view.OnViewModelPropertyChanged;
        }

        if (e.NewValue is PanelViewModel current)
        {
            current.PropertyChanged += view.OnViewModelPropertyChanged;
        }

        view.UpdateActiveVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.IsActive))
        {
            UpdateActiveVisual();
        }
    }

    /// <summary>活动窗格有一套强调色边框，便于区分 1/2 窗格布局。</summary>
    private void UpdateActiveVisual()
    {
        if (ViewModel is null)
        {
            return;
        }

        PaneRoot.BorderBrush = ViewModel.IsActive
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void PaneRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        => ViewModel?.RequestActivate();

    private void PaneRoot_GotFocus(object sender, RoutedEventArgs e)
        => ViewModel?.RequestActivate();

    private void Tabs_AddTabButtonClick(TabView sender, object args)
    {
        if (ViewModel is not null)
        {
            _ = ViewModel.NewTabAsync();
        }
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (ViewModel is not null && args.Item is FolderTabViewModel tab)
        {
            ViewModel.RemoveTab(tab);
        }
    }
}
