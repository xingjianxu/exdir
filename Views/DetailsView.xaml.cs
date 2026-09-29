using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Exdir.Controls;
using Exdir.Helpers;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace Exdir.Views;

/// <summary>
/// 详细信息列表：列头（可拖动列宽、点击列头排序）+ 可展开目录的树形行列表。
/// 列宽是“共享的 ColumnLayout + 视图内实测可用宽度”算出来的，列头与数据行绑定同一对象，因此永远对齐。
/// </summary>
public sealed partial class DetailsView : UserControl
{
    private readonly ColumnResizeHandle[] _handles = new ColumnResizeHandle[ColumnLayout.ColumnCount];

    private ColumnLayout? _layout;
    private ScrollViewer? _scrollViewer;
    private ObservableCollection<FileItemViewModel>? _items;
    private bool _fitQueued;
    private int _dragIndex = -1;
    private double _dragStartWidth;
    private double _dragDelta;

    public DetailsView()
    {
        InitializeComponent();

        CreateResizeHandles();

        // 方向键用 PreviewKeyDown（隧道）而不是 KeyDown：
        // ListView 内部的 ScrollViewer 会先吃掉左右键做横向滚动，必须在它之前拦住。
        EntryList.PreviewKeyDown += EntryList_PreviewKeyDown;
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(FolderTabViewModel),
        typeof(DetailsView),
        new PropertyMetadata(null, OnViewModelChanged));

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

        view.AttachLayout((e.NewValue as FolderTabViewModel)?.Columns);
        view.AttachItems((e.NewValue as FolderTabViewModel)?.Items);
        view.ApplyListAnimations();
    }

    /// <summary>
    /// 监听条目集合：行数变化会影响内部滚动条的占位，进而影响名称列“自动填满”的宽度，
    /// 所以要重新算一次列宽（展开目录是增量插入，走 CollectionChanged）。
    /// </summary>
    private void AttachItems(ObservableCollection<FileItemViewModel>? items)
    {
        if (_items is not null)
        {
            _items.CollectionChanged -= OnItemsCollectionChanged;
        }

        _items = items;

        if (_items is not null)
        {
            _items.CollectionChanged += OnItemsCollectionChanged;
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueFit();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderTabViewModel.EnableListAnimations))
        {
            ApplyListAnimations();
            return;
        }

        if (e.PropertyName == nameof(FolderTabViewModel.Items))
        {
            AttachItems(ViewModel?.Items);

            // 整体替换列表会清掉选中项（ListView 的行为），这里按 ViewModel 给的路径恢复
            DispatcherQueue.TryEnqueue(RestoreSelection);
            QueueFit();
        }
    }

    /// <summary>把重新算列宽合并到一次（展开大目录时会连续插入很多行）。</summary>
    private void QueueFit()
    {
        if (_fitQueued)
        {
            return;
        }

        _fitQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _fitQueued = false;
            FitColumns();
        });
    }

    // ------------------------------------------------------------------ 列宽

    private void CreateResizeHandles()
    {
        for (var i = 0; i < _handles.Length; i++)
        {
            // 直接放在列头 Grid 里（垂直拉伸撑满表头），水平位置用 RenderTransform 推，
            // 这样不依赖 Canvas 的实测高度，也不影响列宽计算。
            var handle = new ColumnResizeHandle
            {
                Tag = i,
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = new TranslateTransform(),
            };

            AutomationProperties.SetName(handle, $"调整列宽：{ColumnLayout.GetColumnName(i)}");

            handle.DragStarted += OnHandleDragStarted;
            handle.DeltaChanged += OnHandleDeltaChanged;
            handle.DragCompleted += OnHandleDragCompleted;
            handle.ResetRequested += OnHandleResetRequested;

            _handles[i] = handle;
            HeaderContent.Children.Add(handle);
        }
    }

    private void AttachLayout(ColumnLayout? layout)
    {
        if (ReferenceEquals(_layout, layout))
        {
            return;
        }

        if (_layout is not null)
        {
            _layout.RenderedChanged -= OnRenderedChanged;
        }

        _layout = layout;

        if (_layout is not null)
        {
            _layout.RenderedChanged += OnRenderedChanged;
        }

        FitColumns();
        UpdateSplitterPositions();
    }

    private void OnRenderedChanged(object? sender, EventArgs e)
    {
        UpdateSplitterPositions();

        // 列集合本身变了（显示/隐藏“状态”列）也会走到这里：名称列要重新吃掉多出来的宽度
        QueueFit();
    }

    private void FitColumns()
    {
        if (_layout is null)
        {
            return;
        }

        var available = ColumnAreaWidth();
        if (available <= 0)
        {
            return;
        }

        _layout.FitTo(available);
        UpdateSplitterPositions();
    }

    /// <summary>列区可用宽度：ListView 视口宽度 − 左右内边距（滚动条占位不能算进去）。</summary>
    private double ColumnAreaWidth()
    {
        _scrollViewer ??= FindDescendant<ScrollViewer>(EntryList);

        var available = _scrollViewer is { ViewportWidth: > 0 } viewer
            ? viewer.ViewportWidth
            : EntryList.ActualWidth;

        var padding = HeaderRow.Padding;
        return available - padding.Left - padding.Right;
    }

    /// <summary>把把手摆到各列右边界的中心。</summary>
    private void UpdateSplitterPositions()
    {
        if (_layout is null)
        {
            return;
        }

        var x = 0d;

        for (var i = 0; i < _handles.Length; i++)
        {
            var width = _layout.GetRenderedWidth(i);
            x += width;

            // 宽度为 0 的列（非云目录里的“状态”列）不摆把手：
            // 否则它会压在名称列的左边界上，把本该点到名称列那几像素抢走
            _handles[i].Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_handles[i].RenderTransform is TranslateTransform transform)
            {
                transform.X = Math.Round(x - (ColumnResizeHandle.HandleWidth / 2));
            }
        }
    }

    private void OnHandleDragStarted(object? sender, EventArgs e)
    {
        if (sender is not ColumnResizeHandle { Tag: int index } || _layout is null)
        {
            return;
        }

        // 以“当前渲染宽度”为基准：名称列可能是自动填满的，requested 值并不等于看到的宽度
        _dragIndex = index;
        _dragDelta = 0;
        _dragStartWidth = _layout.GetRenderedWidth(index);
    }

    private void OnHandleDeltaChanged(object? sender, double delta)
    {
        if (_layout is null || _dragIndex < 0)
        {
            return;
        }

        _dragDelta += delta;
        _layout.SetRequestedWidth(_dragIndex, _dragStartWidth + _dragDelta);
        FitColumns();
    }

    private void OnHandleDragCompleted(object? sender, EventArgs e) => _dragIndex = -1;

    private void OnHandleResetRequested(object? sender, EventArgs e)
    {
        if (sender is not ColumnResizeHandle { Tag: int index } || _layout is null)
        {
            return;
        }

        _layout.ResetColumn(index);
        FitColumns();
    }

    /// <summary>列头固定高度也不变，这里只是为了把列头内容裁剪在窗格内（横向滚动时会平移出去）。</summary>
    private void HeaderRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        HeaderRow.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };

        UpdateSplitterPositions();
    }

    /// <summary>列头跟着列表的横向偏移一起平移（WinUI 的手动同步表头）。</summary>
    private void OnListScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer viewer)
        {
            HeaderTransform.X = -viewer.HorizontalOffset;
        }
    }

    // ------------------------------------------------------------------ 列表

    private void EntryList_Loaded(object sender, RoutedEventArgs e)
    {
        AttachScrollViewer();
        FitColumns();
        UpdateSplitterPositions();

        // 容器是加载后才生成的，这里再同步一次过渡设置（让“关闭动画”对已经显示的行也立即生效）
        ApplyListAnimations();
    }

    // ------------------------------------------------------------------ 过渡动画

    /// <summary>
    /// “配置 → 文件列表 → 过渡动画”开关。关闭后列表不再播放入场/重排动画
    /// （换目录、插行、排序都直接到位），打开时回到 ListView 样式自带的过渡。
    /// </summary>
    private void ApplyListAnimations()
    {
        var enabled = ViewModel?.EnableListAnimations != false;

        if (enabled)
        {
            // 清掉本地值即可回到样式默认，不自己复制一份过渡集合（免得与系统主题不一致）
            EntryList.ClearValue(ItemsControl.ItemContainerTransitionsProperty);
            EntryList.ClearValue(UIElement.TransitionsProperty);
        }
        else
        {
            EntryList.ItemContainerTransitions = new TransitionCollection();
            EntryList.Transitions = new TransitionCollection();
        }

        SyncContainerTransitions(enabled);
    }

    /// <summary>
     /// 已经生成的行容器不会跟随 <see cref="ItemsControl.ItemContainerTransitions"/> 变化，
     /// 所以逐个同步；否则开关要等容器被回收重建后才看得出来。
     /// </summary>
    private void SyncContainerTransitions(bool enabled)
    {
        if (EntryList.ItemsPanelRoot is not { } panel)
        {
            return;
        }

        // 样式里的过渡集合是多个控件共享的同一个实例，直接拿来复用即可
        var effective = EntryList.ItemContainerTransitions;

        foreach (var child in panel.Children)
        {
            if (child is not ListViewItem container)
            {
                continue;
            }

            if (!enabled)
            {
                container.Transitions = new TransitionCollection();
            }
            else if (effective is { Count: > 0 })
            {
                container.Transitions = effective;
            }
            else
            {
                container.ClearValue(UIElement.TransitionsProperty);
            }
        }
    }

    /// <summary>取 ListView 内部的 ScrollViewer，用来同步表头横向偏移。</summary>
    private void AttachScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnListScrollChanged;
        }

        _scrollViewer = FindDescendant<ScrollViewer>(EntryList);

        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged += OnListScrollChanged;
            HeaderTransform.X = -_scrollViewer.HorizontalOffset;
        }
    }

    private void EntryList_SizeChanged(object sender, SizeChangedEventArgs e) => FitColumns();

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel?.SetSelection(EntryList.SelectedItems.OfType<FileItemViewModel>());

    /// <summary>整体重建列表后按路径恢复选中项（排序、刷新用）。</summary>
    private void RestoreSelection()
    {
        var paths = ViewModel?.PendingSelection;
        if (paths is null || paths.Count == 0 || EntryList.ItemsSource is null)
        {
            return;
        }

        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        EntryList.SelectedItems.Clear();

        if (EntryList.ItemsSource is not IEnumerable rows)
        {
            return;
        }

        foreach (var item in rows.OfType<FileItemViewModel>())
        {
            if (wanted.Contains(item.FullPath))
            {
                EntryList.SelectedItems.Add(item);
            }
        }
    }

    /// <summary>双击一行：目录进入，文件交给默认程序（与列表行为一致）。</summary>
    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileItemViewModel item } row)
        {
            return;
        }

        // 点在行首展开箭头上不算双击行（否则会既折叠又进目录）
        if (IsInsideInteractivePart(e.OriginalSource as DependencyObject, row))
        {
            e.Handled = true;
            return;
        }

        ViewModel?.OpenItem(item);
        e.Handled = true;
    }

    private async void Expander_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileItemViewModel item } && ViewModel is { } viewModel)
        {
            await viewModel.ToggleExpandAsync(item);
        }
    }

    /// <summary>左右方向键：展开/折叠当前行（与资源管理器一致）。</summary>
    private void EntryList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || EntryList.SelectedItem is not FileItemViewModel item)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Right when item.IsDirectory && !item.IsExpanded:
                _ = viewModel.ToggleExpandAsync(item);
                e.Handled = true;
                break;

            case VirtualKey.Left when item.IsExpanded:
                viewModel.Collapse(item);
                e.Handled = true;
                break;
        }
    }

    private static bool IsInsideInteractivePart(DependencyObject? source, DependencyObject rowRoot)
    {
        while (source is not null && !ReferenceEquals(source, rowRoot))
        {
            if (source is Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
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
}
