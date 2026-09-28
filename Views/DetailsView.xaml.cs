using System;
using System.ComponentModel;
using System.Linq;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Exdir.Views;

/// <summary>详细列表布局：列头 + 多选列表。目前唯一的布局形态。</summary>
public sealed partial class DetailsView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(FolderTabViewModel),
        typeof(DetailsView),
        new PropertyMetadata(null, OnViewModelChanged));

    public DetailsView()
    {
        InitializeComponent();
    }

    public FolderTabViewModel? ViewModel
    {
        get => (FolderTabViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DetailsView view)
        {
            return;
        }

        if (e.OldValue is FolderTabViewModel old)
        {
            old.PropertyChanged -= view.OnViewModelPropertyChanged;
        }

        if (e.NewValue is FolderTabViewModel current)
        {
            current.PropertyChanged += view.OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderTabViewModel.Items))
        {
            // 条目集合变化后滚动条可能出现/消失，列头内边距需要重新对齐
            DispatcherQueue.TryEnqueue(UpdateHeaderAlignment);
        }
    }

    private void EntryList_Loaded(object sender, RoutedEventArgs e) => UpdateHeaderAlignment();

    private void EntryList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeaderAlignment();

    /// <summary>
    /// 让列头与数据行严格对齐。
    /// 数据行的可用宽度是“ListView 宽度 - 滚动条占用”，而列头是同级元素，
    /// 因此需要把滚动条占用的宽度补偿到列头的右内边距上。
    /// 注意：WinUI 3 的滚动条是否占位取决于当前模板，所以这里实测而不是写死常量。
    /// </summary>
    private void UpdateHeaderAlignment()
    {
        var reserved = 0d;
        var scrollViewer = FindDescendant<ScrollViewer>(EntryList);

        if (scrollViewer is not null && scrollViewer.ViewportWidth > 0)
        {
            reserved = Math.Max(0, scrollViewer.ActualWidth - scrollViewer.ViewportWidth);
        }

        var padding = HeaderRow.Padding;
        var target = new Thickness(padding.Left, padding.Top, padding.Left + reserved, padding.Bottom);

        if (Math.Abs(target.Right - padding.Right) > 0.5)
        {
            HeaderRow.Padding = target;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel?.SetSelection(EntryList.SelectedItems.OfType<FileItemViewModel>());
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileItemViewModel item })
        {
            ViewModel?.OpenItem(item);
            e.Handled = true;
        }
    }
}
