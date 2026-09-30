using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Exdir.Controls;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using WinRT.Interop;

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

        // 右键菜单挂在最外层 Grid 上，而不是挂在 ListView 上：
        // 列表下面的空白处（没生成行的地方）根本不会命中 ListView 内部的 ScrollViewer（它没有背景，
        // 不参与命中测试），事件会从外层 Grid 直接往上冒，挂在 ListView 上就永远收不到。
        // handledEventsToo：行里的图标 / 文字可能把事件标成 Handled。
        // RightTapped 负责鼠标右键，ContextRequested 负责键盘菜单键；两者不会同时触发，用时间戳防重复。
        DetailsRoot.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(DetailsRoot_RightTapped), true);
        DetailsRoot.AddHandler(UIElement.ContextRequestedEvent, new TypedEventHandler<UIElement, ContextRequestedEventArgs>(DetailsRoot_ContextRequested), true);

        // 左键单击空白处取消选择：行上的点击虽然由 ListView 自己处理，但它可能把事件标记成 Handled，
        // 所以这里也必须 handledEventsToo（靠 FindRowItem 把“落在某一行上”的点击排除掉）。
        DetailsRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(DetailsRoot_PointerPressed), true);

        // 双击一行：目录进入、文件用默认程序打开。
        // 同样挂在最外层 Grid 上（handledEventsToo）：行里绝大部分区域（文字右侧、日期/类型/大小的空白处、
        // 行内上下留白）本来没有可命中的元素，命中的是 ListViewItem 自己 —— 事件从 ListViewItem 直接往上冒，
        // 永远经过不了行模板里那个 Grid，于是只有双击到文字/图标上才有效。挂在这一层再用 FindRowItem
        // 反查行，整条高亮区（= 鼠标悬停会高亮的那一块）就都能双击了。
        DetailsRoot.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(DetailsRoot_DoubleTapped), true);
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
            // 放在 HeaderLayer（整宽、无列定义）里，垂直拉伸撑满表头，水平位置用 RenderTransform 推，
            // 这样不依赖 Canvas 的实测高度，也不影响列宽计算。
            // 不能放在带列定义的 HeaderContent 里：它的第 0 列是“状态”列，非云目录里宽度为 0，
            // 零宽单元格里的子元素收不到指针事件（把手会变成完全点不到、也拖不动的装饰）。
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
            HeaderLayer.Children.Add(handle);
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

        UpdateHeaderInsets();
    }

    /// <summary>
    /// 列头按钮的左右外扩：最左那一列往左、最右的“大小”列往右各多铺一个行内边距（6 DIP），
    /// 于是“从窗格边缘到列边界的整条表头”都能点、都有悬停高亮，与数据行的行高亮范围一致
    /// （数据行的高亮是整条 ListViewItem，从窗格左边缘一直到右边）；
    /// 外扩量同时补成同侧的内边距，所以标签文字位置不变（列头依旧与数据行对齐）。
    /// 最左可见列会随“状态”列的显隐变化，因此每次重排列宽都重算一遍。
    /// </summary>
    private void UpdateHeaderInsets()
    {
        if (_layout is null)
        {
            return;
        }

        var inset = HeaderRow.Padding.Left;
        var syncFirst = _layout.ShowSyncColumn;

        ExtendHeader(SyncStateHeader, syncFirst ? inset : 0);
        ExtendHeader(NameHeader, syncFirst ? 0 : inset);
        ExtendHeader(DateHeader, 0);
        ExtendHeader(TypeHeader, 0);
        ExtendHeader(SizeHeader, 0, inset);
    }

    /// <summary>
    /// 把列头按钮往左/右各扩大 <paramref name="left" /> / <paramref name="right" />，
    /// 并用等量内边距把内容顶回原位（内容位置与不做外扩时完全一样）。
    /// 用负 Margin 而不是改列宽：列宽是列头与数据行共用的对齐基准，动了它会连数据行一起错位。
    /// </summary>
    private static void ExtendHeader(Button button, double left, double right = 0)
    {
        button.Margin = new Thickness(-left, 0, -right, 0);
        button.Padding = new Thickness(left, 0, right, 0);
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

    /// <summary>
    /// 拖拽的起点：把选中的目录作为拖放内容（工具条“固定目录”是接受方）。
    /// 文件目前没有可拖拽的语义（复制/移动尚未实现），所以直接取消拖拽，
    /// 免得拖出去后落到哪儿都没反应。
    /// </summary>
    private void EntryList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var directories = e.Items
            .OfType<FileItemViewModel>()
            .Where(item => item.IsDirectory)
            .Select(item => item.FullPath)
            .ToList();

        if (directories.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        DragDropHelper.SetPaths(e.Data, directories);
        Log.Write($"拖拽开始（文件列表）：{directories.Count} 个目录");
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

    /// <summary>
    /// 行容器被生成时才去取它的真实图标：列表是虚拟化的，所以只有真正显示出来的行
    /// （以及预取的那几行）才会付出一次 <c>SHGetFileInfo</c>；滚动不会再重复取（服务里有缓存）。
    /// </summary>
    private void EntryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not FileItemViewModel item || ViewModel is not { } viewModel)
        {
            return;
        }

        _ = viewModel.EnsureIconAsync(item);
    }

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

    // ------------------------------------------------------------------ 选择

    /// <summary>
    /// 单击列表空白处（列头以下、任何一行之外）取消当前选择。
    /// 行首箭头、行内文字这些仍算“行上”，交给 ListView 自己处理选择，这里不插手。
    /// </summary>
    private void DetailsRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(DetailsRoot);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 列头有自己的行为（排序 / 拖列宽），不算空白处
        if (point.Position.Y <= HeaderRow.ActualHeight || FindRowItem(e.OriginalSource) is not null)
        {
            return;
        }

        if (EntryList.SelectedItems.Count > 0)
        {
            EntryList.SelectedItems.Clear();
        }

        // 空白处自身不可聚焦，不在这儿抢一把的话焦点会留在点空白之前的控件上
        // （紧接着按 Ctrl+A / 方向键就不作用于文件列表了）
        EntryList.Focus(FocusState.Pointer);
    }

    /// <summary>
    /// Ctrl+A：全选列表里当前可见的行。
    /// 加速器挂在 <c>DetailsRoot</c> 上，焦点在列表 / 列头 / 行内箭头时都生效。
    /// </summary>
    private void SelectAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel is null)
        {
            return;
        }

        EntryList.SelectAll();
        EntryList.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------ 右键菜单

    /// <summary>
    /// 右键 / 菜单键：点在行上 → 该（批）条目的菜单；点在空白处 → 当前目录的背景菜单。
    /// 用哪一种菜单（exdir 自建的轻量菜单 / 系统外壳菜单）由设置决定，见 <see cref="ShowContextMenu" />。
    /// </summary>
    private void DetailsRoot_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var position = args.TryGetPosition(DetailsRoot, out var local) ? local : default;
        ShowContextMenu(viewModel, args.OriginalSource, position);
        args.Handled = true;
    }

    /// <summary>鼠标右键：<c>RightTapped</c> 比 <c>ContextRequested</c> 更可靠（后者在有些控件上不冒泡）。</summary>
    private void DetailsRoot_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        ShowContextMenu(viewModel, args.OriginalSource, args.GetPosition(DetailsRoot));
        args.Handled = true;
    }

    /// <summary>
    /// 按设置决定弹哪一种菜单：内置（现搭的 WinUI <c>MenuFlyout</c>，弹出瞬时）还是系统外壳菜单。
    /// 两种菜单内容不要求一致 —— 内置只含 exdir 自己实现的命令，系统菜单内容完整但慢。
    /// </summary>
    private void ShowContextMenu(FolderTabViewModel viewModel, object? source, Point position)
    {
        // 列头上右键不弹菜单（它的排序按钮有自己的行为，弹一个目录背景菜单只会让人困惑）
        if (position.Y <= HeaderRow.ActualHeight)
        {
            return;
        }

        // RightTapped 与 ContextRequested 可能为同一次右键都冒上来：系统菜单是模态弹出的，
        // 第一个处理完（用户关掉菜单）之后第二个才会被调用；而内置菜单的 ShowAt 立即返回，
        // 第二个事件只差几毫秒，所以两种都要靠时间戳挡一下
        if (Environment.TickCount64 - _lastContextMenuTicks < DuplicateContextMenuGuardMs)
        {
            return;
        }

        var item = FindRowItem(source);

        if (item is not null && !EntryList.SelectedItems.Contains(item))
        {
            // 右键点到没选中的行：先把选择换成它（与资源管理器一致），菜单作用于刚刚选中的这批
            EntryList.SelectedItem = item;
        }

        if (viewModel.UseBuiltInContextMenu)
        {
            ShowBuiltInContextMenu(viewModel, item is not null, position);
        }
        else
        {
            ShowShellContextMenu(viewModel, position, item);
        }

        // 记在“菜单弹过之后”，这样紧跟着来的重复事件（间隔约 0 ms）会被挡掉，
        // 而用户过一会儿真的再点一次右键不受影响
        _lastContextMenuTicks = Environment.TickCount64;
    }

    /// <summary>系统外壳菜单：位置要换算成屏幕物理像素（<c>TrackPopupMenuEx</c> 用的是屏幕坐标）。</summary>
    private void ShowShellContextMenu(FolderTabViewModel viewModel, Point position, FileItemViewModel? item)
    {
        var screen = DpiHelper.ToScreenPoint(DetailsRoot, MainWindowHandle, position);

        if (item is not null)
        {
            var paths = EntryList.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
            if (paths.Count > 0)
            {
                viewModel.ShowShellContextMenu(paths, isBackground: false, (int)screen.X, (int)screen.Y);
            }
        }
        else if (!string.IsNullOrEmpty(viewModel.CurrentPath))
        {
            viewModel.ShowShellContextMenu(
                new[] { viewModel.CurrentPath },
                isBackground: true,
                (int)screen.X,
                (int)screen.Y);
        }
    }

    /// <summary>
    /// 内置菜单：现场搭一个 <c>MenuFlyout</c>（几个静态项，几毫秒的事），命令直接绑到标签页 VM 上，
    /// 不碰 COM、不问外壳，所以弹出几乎瞬时。
    /// 菜单项集合故意与系统菜单不同 —— 这里只有 exdir 自己实现的命令（见 AGENTS.md 第 4 节）。
    /// </summary>
    private void ShowBuiltInContextMenu(FolderTabViewModel viewModel, bool onRow, Point position)
    {
        // “此电脑”这种没有路径的标签页里没有可用的背景命令
        if (!onRow && string.IsNullOrEmpty(viewModel.CurrentPath))
        {
            return;
        }

        var flyout = new MenuFlyout();

        if (onRow)
        {
            AddContextMenuItem(flyout, "打开", viewModel.OpenSelectionCommand);
            AddContextMenuItem(flyout, "在资源管理器中显示", viewModel.RevealInExplorerCommand);
            flyout.Items.Add(new MenuFlyoutSeparator());
            AddContextMenuItem(flyout, "复制路径", viewModel.CopySelectionPathCommand);
            AddContextMenuItem(flyout, "属性", viewModel.ShowPropertiesCommand);
        }
        else
        {
            AddContextMenuItem(flyout, "新建文件夹", viewModel.CreateNewFolderCommand);
            AddContextMenuItem(flyout, "刷新", viewModel.RefreshCommand);
            AddContextMenuAction(flyout, "全选", SelectAllRows);
            flyout.Items.Add(new MenuFlyoutSeparator());
            AddContextMenuItem(flyout, "复制当前路径", viewModel.CopyCurrentPathCommand);
            AddContextMenuItem(flyout, "在此处打开终端", viewModel.OpenTerminalCommand);
        }

        // 回归脚本的断言依据（内置菜单在 UIA 里读得到，不像系统菜单那样只能看 #32768）
        Log.Write($"内置右键菜单：{(onRow ? "文件" : "背景")} 上下文 {flyout.Items.Count} 项");

        // Position 是相对 DetailsRoot 的 DIP 坐标，ShowAt 自己会换算，所以这里不做 DPI 换算
        flyout.ShowAt(DetailsRoot, new FlyoutShowOptions { Position = position });
    }

    private static void AddContextMenuItem(MenuFlyout flyout, string text, ICommand command)
        => flyout.Items.Add(new MenuFlyoutItem { Text = text, Command = command });

    private static void AddContextMenuAction(MenuFlyout flyout, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        flyout.Items.Add(item);
    }

    /// <summary>内置菜单里的「全选」：和 Ctrl+A 走同一条路（要顺手把焦点抢回列表）。</summary>
    private void SelectAllRows()
    {
        EntryList.SelectAll();
        EntryList.Focus(FocusState.Programmatic);
    }

    /// <summary>同一次右键里两个事件都冒上来时，用来去重的时间窗（毫秒）。</summary>
    private const long DuplicateContextMenuGuardMs = 400;

    private long _lastContextMenuTicks;

    /// <summary>从命中的最深层元素往上找它所属的那一行；没找到（空白处 / 列头）返回 null。</summary>
    private FileItemViewModel? FindRowItem(object? source)
    {
        var node = source as DependencyObject;

        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: FileItemViewModel item })
            {
                return item;
            }

            if (ReferenceEquals(node, DetailsRoot))
            {
                return null;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static IntPtr MainWindowHandle
        => App.MainWindow is { } window ? WindowNative.GetWindowHandle(window) : IntPtr.Zero;

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

    /// <summary>
    /// 双击一行：目录进入、文件交给默认程序。
    /// 整行高亮区内任意位置都算（行内边距、名称文字右侧、日期/类型/大小的空白处都行），
    /// 只有行首那个展开箭头除外（它自己负责折叠/展开）。
    /// </summary>
    private void DetailsRoot_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var item = FindRowItem(e.OriginalSource);
        if (item is null)
        {
            return;
        }

        // 点在行首展开箭头上不算双击行（否则会既折叠又进目录）
        if (IsInsideRowButton(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        viewModel.OpenItem(item);
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

    /// <summary>
    /// 命中的元素是不是行内某个按钮（目前只有名称列那个 18px 展开箭头）的一部分。
    /// 往上走到文件列表为止：列表外面（列头排序按钮等）不在判断范围里，那些位置 FindRowItem 本来就返回 null。
    /// </summary>
    private bool IsInsideRowButton(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, EntryList) && !ReferenceEquals(source, DetailsRoot))
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
