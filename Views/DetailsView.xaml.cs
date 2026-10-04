using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Exdir.Controls;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;
using Exdir.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
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

        // 左键单击空白处取消选择（行上的点击交给 ListView，但它可能把事件标记成 Handled，
        // 所以这里也必须 handledEventsToo，靠 FindRowItem 区分）。
        DetailsRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(DetailsRoot_PointerPressed), true);

        // 拖拽手势是自己识别的（按下 + 移动超过阈值 → StartDragAsync）：
        // ListView 自带的 CanDragItems 拖拽在模拟鼠标下只能走到 DragItemsStarting 就没了下文，
        // 而 StartDragAsync 是本仓库验证过能用的（工具条上的固定目录拖拽就是它）。
        DetailsRoot.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(DetailsRoot_PointerMoved), true);

        // 注意不要把 PointerCaptureLost 也接到 PointerEnded 上：
        // ListViewItem 在按下时会把指针捕获过去，随即发一次 PointerCaptureLost，
        // 而那时拖拽还没开始 —— 拿它当“松手”会把 _dragCandidate 清掉，拖拽永远启动不了
        // （表现为“有时能拖、有时拖不动”）。捕获丢失只意味着事件不再往上传，不代表用户松手。
        DetailsRoot.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(DetailsRoot_PointerEnded), true);

        // 双击一行：目录进入、文件用默认程序打开。
        // 同样挂在最外层 Grid 上（handledEventsToo）：行里绝大部分区域（文字右侧、日期/类型/大小的空白处、
        // 行内上下留白）本来没有可命中的元素，命中的是 ListViewItem 自己 —— 事件从 ListViewItem 直接往上冒，
        // 永远经过不了行模板里那个 Grid，于是只有双击到文字/图标上才有效。挂在这一层再用 FindRowItem
        // 反查行，整条高亮区（= 鼠标悬停会高亮的那一块）就都能双击了。
        DetailsRoot.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(DetailsRoot_DoubleTapped), true);

        // 拖放：把文件 / 目录拖到某个目录行上（或列表空白处 = 当前目录）就移动 / 复制过去。
        // 同样用 handledEventsToo：ListViewItem 这些容器自己也参与拖放，会把“不接受”写进 AcceptedOperation，
        // 必须在整条路由的最后再确认一次，否则鼠标下的行高亮着、松手却什么都不发生。
        DetailsRoot.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DetailsRoot_DragOver), true);
        DetailsRoot.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(DetailsRoot_DragLeave), true);
        DetailsRoot.AddHandler(UIElement.DropEvent, new DragEventHandler(DetailsRoot_Drop), true);
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

        // 新建出来的标签页可能是在“导航完成之后”才挂上 ViewModel 的（TabView 的容器要等布局那一拍才生成），
        // 那样它就错过了 Items 整体替换的通知，按路径恢复选中项的那一拍也跟着丢了
        //（命令行 `exdir <文件>` 给一个刚建出来的标签页选文件正好撞在这上面）。
        // 这里补一次，排在队列里等 x:Bind 把 ItemsSource 接上之后再执行。
        view.DispatcherQueue.TryEnqueue(view.RestoreSelection);
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
    /// 拖拽的起点：把选中的项（目录与文件）作为拖放内容。
    /// <para>
    /// 拖拽手势是自己识别的（<c>EntryList</c> 的 <c>CanDragItems</c> 关掉，见
    /// <see cref="DetailsRoot_PointerMoved" />）：<c>ListView</c> 自带的那个拖拽在模拟鼠标下
    /// 只能走到 <c>DragItemsStarting</c> 就没了下文（拖拽循环收不到移动），而
    /// <c>StartDragAsync</c> 这条路是本仓库验证过能用的（工具条上的固定目录拖拽就是它）。
    /// </para>
    /// 同一个数据包既是“固定到工具条”的来源（只有目录时才显示那个提示，见
    /// DragDropHelper.FoldersOnlyProperty），也是“拖到目录行 / 另一个窗格里移动”的来源；
    /// 允许的效果里带上 Move：同盘拖动默认就是移动（资源管理器的习惯）。
    /// </summary>
    private void DetailsRoot_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        // 取消的拖拽不能留下上一次的路径（否则拖拽收尾的兜底会拿旧路径做一次移动）
        _draggingPaths.Clear();
        _draggingFromStaging = false;

        var items = SelectedDragItems();
        if (items.Count == 0)
        {
            args.Cancel = true;
            return;
        }

        _dragStarted = true;
        _internalDropHandled = false;

        // 压缩包里的条目 / 远程位置上的条目没有本地真实路径（写不出 CF_HDROP）：
        // 真实文件已经在手势开始前由 <see cref="PrepareStagingDragAsync" /> 解出来 / 下下来，
        // 并放进了 <see cref="_stagingDrag" />（拖拽过程中不能再 await —— 这里必须同步把数据包写完）
        if (items.Any(static item => item.IsInArchive || RemotePath.LooksRemote(item.FullPath)))
        {
            var ready = _stagingDrag;
            _stagingDrag = null;

            if (ready is null || ready.StorageItems.Count == 0)
            {
                Log.Write("拖拽：待中转的条目还没准备好（没有预解出 / 预下载的数据包），取消这次拖拽");
                args.Cancel = true;
                return;
            }

            _draggingPaths = ready.Payload.Paths.ToList();
            _draggingFromStaging = true;

            DragDropHelper.SetArchiveDrag(args.Data, ready.StorageItems, ready.Payload.StagingRoots);

            Log.Write($"拖拽开始（包内 / 远程条目）：{items.Count} 项 → 交给外壳 {ready.StorageItems.Count} 个真实文件（临时副本 {ready.Payload.StagingRoots.Count} 处）");
            return;
        }

        _draggingPaths = items.Select(item => item.FullPath).ToList();

        var foldersOnly = items.All(item => item.IsDirectory);

        DragDropHelper.SetPaths(
            args.Data,
            items.Select(item => item.FullPath),
            DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link,
            foldersOnly);

        Log.Write($"拖拽开始（文件列表）：{items.Count} 项（全是目录={foldersOnly}）");
    }

    /// <summary>
    /// 要拖走的行。按在没选中的行上时 ListView 为了支持“拖动已选中的多项”会把选择推迟到鼠标松开，
    /// 而那时拖拽已经开始了 —— 这里自己补上（与 <see cref="DetailsRoot_PointerPressed" /> 一致）。
    /// </summary>
    private List<FileItemViewModel> SelectedDragItems()
    {
        var items = EntryList.SelectedItems.OfType<FileItemViewModel>().ToList();

        if (_dragCandidate is { } candidate && !items.Contains(candidate))
        {
            EntryList.SelectedItem = candidate;
            items = new List<FileItemViewModel> { candidate };
        }

        return items;
    }

    /// <summary>
    /// 拖拽手势的第一步：选中项里有**压缩包内部条目 / 远程位置上的条目**时，
    /// 先把它们解到 / 下到临时目录（<c>archive-cache\drag</c> / <c>remote-cache\drag</c>）、拿到真实文件
    /// （那两类路径写不进 <c>CF_HDROP</c>，也交不给别的程序），才能开始拖拽。
    ///
    /// <para>
    /// 解包可能很慢（大条目要解几秒）、还可能弹密码框，所以放在 <c>StartDragAsync</c> **之前**做：
    /// 一来拖拽数据包能同步写完（拖拽一开始就不能再改了），
    /// 二来解包失败 / 用户取消时干脆不启动这一次拖拽（见 AGENTS.md 第 6 节第 95 条）。
    /// </para>
    /// </summary>
    /// <returns>false = 这一次不该开始拖拽。</returns>
    private async Task<bool> PrepareStagingDragAsync()
    {
        _stagingDrag = null;

        if (ViewModel is not { } viewModel)
        {
            return false;
        }

        var items = SelectedDragItems();
        if (items.Count == 0)
        {
            return false;
        }

        // 真实路径不需要预先下载 / 解包（数据包里直接写它们的真实路径）
        if (!items.Any(static item => item.IsInArchive || RemotePath.LooksRemote(item.FullPath)))
        {
            return true;
        }

        var payload = await viewModel.BuildDragPayloadAsync(items).ConfigureAwait(true);
        if (payload is null || payload.Paths.Count == 0)
        {
            Log.Write("拖拽：包内 / 远程条目没能取出来（原因见上面的日志 / InfoBar），取消这次拖拽");
            return false;
        }

        var storageItems = await CreateStorageItemsAsync(payload.Paths).ConfigureAwait(true);
        if (storageItems.Count == 0)
        {
            Log.Write("拖拽：解出来 / 下下来的临时副本没能交给外壳（StorageItems 为空），取消这次拖拽");
            return false;
        }

        _stagingDrag = new StagingDragReady(payload, storageItems);
        return true;
    }

    /// <summary>
    /// 把解出来的真实路径包成 <see cref="IStorageItem" />：资源管理器只认 <c>CF_HDROP</c>，
    /// 而 WinRT 会把 StorageItems 换成它。取不到的对象跳过（极端情况：解出来的临时文件刚好被清掉）。
    /// </summary>
    private static async Task<List<IStorageItem>> CreateStorageItemsAsync(IReadOnlyList<string> paths)
    {
        var items = new List<IStorageItem>();

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
                else if (File.Exists(path))
                {
                    items.Add(await StorageFile.GetFileFromPathAsync(path));
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"拖拽：路径无法交给外壳（{path}）", ex);
            }
        }

        return items;
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

        var row = FindRowItem(e.OriginalSource);
        if (row is not null)
        {
            // 按在没选中的行上（没按 Ctrl/Shift）就先把选择换成它：
            // ListView 为了支持“拖动已选中的多项”会把选择推迟到鼠标松开，而那时拖拽已经开始了
            // （`DragStarting` 要用当前选择当拖拽内容）—— 这里自己补上。
            if ((e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)) == 0
                && !EntryList.SelectedItems.Contains(row))
            {
                EntryList.SelectedItem = row;
            }

            _dragCandidate = row;
            _dragPressPosition = point.Position;
            _dragStarted = false;
            return;
        }

        // 列头有自己的行为（排序 / 拖列宽），不算空白处
        if (point.Position.Y <= HeaderRow.ActualHeight)
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

    // ------------------------------------------------------------------ 拖拽手势

    /// <summary>按下时鼠标所在的行（可能要拖的那一行）。</summary>
    private FileItemViewModel? _dragCandidate;

    /// <summary>按下的位置，用来算拖拽阈值。</summary>
    private Point _dragPressPosition;

    /// <summary>本次按下已经启动过拖拽，避免重复调 <c>StartDragAsync</c>。</summary>
    private bool _dragStarted;

    /// <summary>本次拖拽真正拖走的路径（DragStarting 时定下来）。</summary>
    private List<string> _draggingPaths = new();

    /// <summary>本次拖拽拖的是“交出去的临时副本”（从压缩包里解出来 / 从远程下下来的；收尾兜底据此强制按复制）。</summary>
    private bool _draggingFromStaging;

    /// <summary>
    /// 这次手势已经准备好的临时副本：<see cref="PrepareStagingDragAsync" /> 填、
    /// <see cref="DetailsRoot_DragStarting" /> 取（拖拽数据包必须在 DragStarting 里同步写完，
    /// 所以解包 / 下载与建 StorageItems 都得在开始拖拽之前做完）。
    /// </summary>
    private StagingDragReady? _stagingDrag;

    /// <summary>“把压缩包内条目 / 远程条目拖出去”这次手势准备好的东西。</summary>
    /// <param name="Payload">解出来 / 下下来的真实路径 + 临时副本的根目录。</param>
    /// <param name="StorageItems">交给外壳的 StorageItems（资源管理器只认它转出来的 CF_HDROP）。</param>
    private sealed record StagingDragReady(DragPayload Payload, List<IStorageItem> StorageItems);

    /// <summary>本次拖拽的 Drop 有没有落到本列表上（落下处理用它防重复）。</summary>
    private bool _internalDropHandled;

    /// <summary>按下后移动超过这个距离（DIP）才算拖拽，否则还是点击。</summary>
    private const double DragThreshold = 4;

    private void DetailsRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCandidate is null || _dragStarted || ViewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(DetailsRoot);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }

        if (Math.Abs(point.Position.X - _dragPressPosition.X) < DragThreshold
            && Math.Abs(point.Position.Y - _dragPressPosition.Y) < DragThreshold)
        {
            return;
        }

        _dragStarted = true;
        _ = StartRowDragAsync(point);
    }

    /// <summary>松手：结束本次“按下”（拖拽已经启动时由 <see cref="StartRowDragAsync" /> 自己收尾）。</summary>
    private void DetailsRoot_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragStarted)
        {
            _dragCandidate = null;
        }
    }

    private async Task StartRowDragAsync(Microsoft.UI.Input.PointerPoint point)
    {
        try
        {
            // 选中项里有包内条目时先把它们解到临时目录（可能要等几秒）、建好 StorageItems，
            // 再开始拖拽 —— 拖拽数据包必须在 DragStarting 里同步写完，那里不能再 await
            if (!await PrepareStagingDragAsync().ConfigureAwait(true))
            {
                return;
            }

            await DetailsRoot.StartDragAsync(point);
        }
        catch (Exception ex)
        {
            // 提权进程不支持 StartDragAsync（Windows 的安全策略），只记日志
            Log.Exception("文件列表拖拽", ex);
            return;
        }
        finally
        {
            // 拖拽结束后才能再开始下一次；拖拽期间收不到 PointerReleased，得在这里清
            _dragStarted = false;
            _dragCandidate = null;
            _stagingDrag = null;
        }

        // 兜底：WinUI 有时不会把 Drop 冒泡到列表上（行高亮着、松手却什么都没发生），
        // 那就按“松开时鼠标落在哪儿”自己把这次移动做完。
        // 左键还按着 = 用户按 Esc 取消了拖拽，什么都不做；
        // 光标不在本列表里 = 已经落到别的窗格 / 工具条 / 别的程序上了，不用管。
        if (_internalDropHandled || IsKeyDown(VkLeftButton))
        {
            return;
        }

        await CompleteInternalDropAsync();
    }

    /// <summary>
    /// 拖拽结束的兜底：把 <see cref="_draggingPaths" /> 移到松开鼠标时所指向的目录
    /// （目录行，或列表空白处 = 当前目录）。
    /// </summary>
    private async Task CompleteInternalDropAsync()
    {
        if (ViewModel is not { } viewModel || _draggingPaths.Count == 0)
        {
            return;
        }

        var position = DpiHelper.GetCursorPosition(DetailsRoot, MainWindowHandle);
        if (double.IsNaN(position.X) || position.X < 0 || position.Y < 0
            || position.X > DetailsRoot.ActualWidth || position.Y > DetailsRoot.ActualHeight)
        {
            return;
        }

        var row = RowAt(position);

        // 包内 / 远程目录行都不接受拖放：包内是虚拟路径（写不进去），远程位置只读
        if (row?.IsInArchive == true || row is not null && RemotePath.LooksRemote(row.FullPath))
        {
            return;
        }

        // 当前目录在压缩包里 / 在远程位置上时不接受拖放：
        // 压缩包那种情况 CurrentPath 是虚拟路径，退回去当落点会经 NormalizeDirectoryPath 落到
        // **压缩包所在的目录**（见 AGENTS.md 第 6 节第 83 条）；远程位置则是根本不能写。
        // 「最新访问」同理：当前路径是 <c>exdir://recent</c>，不是一个能写的目录。
        if (viewModel.IsInsideArchive || viewModel.IsRemote || viewModel.IsRecentView)
        {
            return;
        }

        var target = row is { IsDirectory: true } ? row.FullPath : viewModel.CurrentPath;

        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        // 和 DragOver 里的判定一致（默认移动，按住 Ctrl 是复制；从压缩包里拖出来的永远是复制）
        var move = !_draggingFromStaging && !IsKeyDown(VkControl);

        Log.Write($"拖放兜底：{_draggingPaths.Count} 项 → {target}（{(move ? "移动" : "复制")}）");
        await viewModel.DropFilesAsync(_draggingPaths, target, move).ConfigureAwait(true);
    }

    /// <summary>
    /// Shift 现在是不是按下的。用 <see cref="InputKeyboardSource" />（因为线程消息队列里的键状态）
    /// 而不是 <c>GetAsyncKeyState</c>：后者是“此刻的真实物理状态”，等到本进程拿到这条键盘消息时，
    /// 用户（或自动化脚本）可能已经把 Shift 松开了。
    /// </summary>
    private static bool IsShiftDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down)
           == CoreVirtualKeyStates.Down;

    private const int VkLeftButton = 0x01;
    private const int VkControl = 0x11;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>某个键（或鼠标键）现在是不是按下状态；拖拽期间收不到指针事件，只能这样问系统。</summary>
    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

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

    /// <summary>Ctrl+C：把选中项放到剪贴板（与资源管理器、7-Zip 互通）。</summary>
    private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel is { } viewModel && viewModel.CopySelectionCommand.CanExecute(null))
        {
            viewModel.CopySelectionCommand.Execute(null);
        }
    }

    /// <summary>Ctrl+X：放到剪贴板，粘贴时是移动。</summary>
    private void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel is { } viewModel && viewModel.CutSelectionCommand.CanExecute(null))
        {
            viewModel.CutSelectionCommand.Execute(null);
        }
    }

    /// <summary>Ctrl+V：把剪贴板上的文件复制 / 移动进当前目录。</summary>
    private void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel is { } viewModel)
        {
            _ = viewModel.PasteCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Delete / Shift+Delete：删除选中项（默认丢进回收站，按住 Shift 是永久删除）。
    /// <para>
    /// 为什么不用 <c>KeyboardAccelerator</c>：带 Shift 的加速器对 Delete 这种键在本仓库的
    /// WinUI 下根本不会 Invoke（实测 <c>Modifiers="Shift" + Key="Delete"</c> 完全没反应，
    /// 同一次按键的普通 Delete 却正常）——Shift 的修饰键匹配不可靠。
    /// <c>PreviewKeyDown</c> 是隧道事件，一定先于列表/列头拿到这个键，再由自己查 Shift 的按下状态，
    /// 不依赖任何修饰键匹配。
    /// </para>
    /// 只作用于文件列表：地址栏在 <c>NavigationBarView</c> 里、不是本控件子树，所以那里的 Delete
    /// 仍然是文本框自己的行为。
    /// </summary>
    private async void DetailsRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || ViewModel is not { } viewModel)
        {
            return;
        }

        var permanent = IsShiftDown();
        var command = permanent ? viewModel.DeleteSelectionPermanentlyCommand : viewModel.DeleteSelectionCommand;

        if (!command.CanExecute(null))
        {
            return;
        }

        e.Handled = true;

        try
        {
            await command.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Log.Exception("删除", ex);
        }
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

        if (viewModel.UseBuiltInContextMenu || viewModel.IsInsideArchive || viewModel.IsRemote || viewModel.IsRecentView || item?.IsInArchive == true)
        {
            ShowBuiltInContextMenu(viewModel, item, position);
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
    private void ShowBuiltInContextMenu(FolderTabViewModel viewModel, FileItemViewModel? item, Point position)
    {
        var onRow = item is not null;

        // “此电脑”这种没有路径的标签页里没有可用的背景命令
        if (!onRow && string.IsNullOrEmpty(viewModel.CurrentPath))
        {
            return;
        }

        var flyout = new MenuFlyout();

        // 「最新访问」是虚拟视图：行是真实文件（能打开 / 复制 / 删除），但当前“目录”不是真目录，
        // 所以背景菜单里的写操作（粘贴 / 新建文件夹 / 终端）根本没给，行上给看得懂的那几项。
        // 键盘入口另有守卫（FolderTabViewModel.RefuseInRecentView）。
        if (viewModel.IsRecentView)
        {
            if (onRow)
            {
                AddContextMenuItem(flyout, "打开", viewModel.OpenSelectionCommand);
                AddContextMenuItem(flyout, "打开所在文件夹", viewModel.OpenContainingFolderCommand);
                AddContextMenuItem(flyout, "在资源管理器中显示", viewModel.RevealInExplorerCommand);

                flyout.Items.Add(new MenuFlyoutSeparator());
                AddContextMenuItem(flyout, "复制", viewModel.CopySelectionCommand, "Ctrl+C");
                AddContextMenuItem(flyout, "复制路径", viewModel.CopySelectionPathCommand);
                AddContextMenuItem(flyout, "属性", viewModel.ShowPropertiesCommand);
            }
            else
            {
                AddContextMenuItem(flyout, "刷新", viewModel.RefreshCommand);
                AddContextMenuAction(flyout, "全选", SelectAllRows);
            }

            Log.Write($"内置右键菜单：「最新访问」{(onRow ? "文件" : "背景")} 上下文 {flyout.Items.Count} 项");
            flyout.ShowAt(DetailsRoot, new FlyoutShowOptions { Position = position });
            return;
        }

        // 远程位置（SFTP / FTP）也是**只读**的：留看得懂的（打开 / 下载到… / 复制到本地），
        // 写操作的入口（剪切 / 删除 / 粘贴 / 新建文件夹 / 压缩 / 属性 / 在资源管理器中显示）根本不给；
        // 键盘入口由 FolderTabViewModel 里的守卫挡（RefuseInRemote）。
        if (viewModel.IsRemote)
        {
            if (onRow)
            {
                AddContextMenuItem(flyout, "打开", viewModel.OpenSelectionCommand);
                AddContextMenuItem(flyout, "下载到…", viewModel.DownloadSelectionCommand);

                flyout.Items.Add(new MenuFlyoutSeparator());
                AddContextMenuItem(flyout, "复制", viewModel.CopySelectionCommand, "Ctrl+C");

                flyout.Items.Add(new MenuFlyoutSeparator());
                AddContextMenuItem(flyout, "复制路径", viewModel.CopySelectionPathCommand);
            }
            else
            {
                AddContextMenuItem(flyout, "刷新", viewModel.RefreshCommand);
                AddContextMenuAction(flyout, "全选", SelectAllRows);
                flyout.Items.Add(new MenuFlyoutSeparator());
                AddContextMenuItem(flyout, "复制当前路径", viewModel.CopyCurrentPathCommand);
            }

            Log.Write($"内置右键菜单：远程位置{(onRow ? "文件" : "背景")} 上下文 {flyout.Items.Count} 项（只读，可下载 / 复制到本地）");
            flyout.ShowAt(DetailsRoot, new FlyoutShowOptions { Position = position });
            return;
        }

        // 压缩包内部是**只读**的：只留看得懂、做得了的那几项，写操作的入口根本不给
        //（Ctrl+X/V/Delete 这些键盘入口由 ViewModel 里的守卫挡，见 FolderTabViewModel.RefuseSelectionInArchive）
        // 这里同时也盖住“在真实目录里就地展开了压缩包、右键点在包内行上”的情况。
        if (viewModel.IsInsideArchive || item?.IsInArchive == true)
        {
            if (onRow)
            {
                AddContextMenuItem(flyout, "打开", viewModel.OpenSelectionCommand);

                // 包内条目没有真实路径，"复制"只把“压缩包 + 包内路径”记在内存里，
                // 到真实目录里粘贴时才解出来（见 AGENTS.md 第 4 节）
                AddContextMenuItem(flyout, "复制", viewModel.CopySelectionCommand, "Ctrl+C");

                flyout.Items.Add(new MenuFlyoutSeparator());
                AddContextMenuItem(flyout, "复制路径", viewModel.CopySelectionPathCommand);
            }
            else
            {
                AddContextMenuItem(flyout, "刷新", viewModel.RefreshCommand);
                AddContextMenuAction(flyout, "全选", SelectAllRows);
                flyout.Items.Add(new MenuFlyoutSeparator());
                AddContextMenuItem(flyout, "复制当前路径", viewModel.CopyCurrentPathCommand);
            }

            Log.Write($"内置右键菜单：压缩包{(onRow ? "文件" : "背景")} 上下文 {flyout.Items.Count} 项（只读，可复制到外部目录）");
            flyout.ShowAt(DetailsRoot, new FlyoutShowOptions { Position = position });
            return;
        }

        if (onRow)
        {
            // 搜索结果：双击是“打开所在文件夹并选中”，所以菜单里把这两件事都写清楚。
            // 其余项（剪切 / 复制 / 删除 / 压缩 / 复制路径 / 属性）本来就是拿条目的完整路径干活，直接可用。
            if (viewModel.IsSearchMode)
            {
                AddContextMenuItem(flyout, "打开所在文件夹", viewModel.OpenContainingFolderCommand);
                AddContextMenuItem(flyout, "打开", viewModel.OpenWithDefaultAppCommand);
            }
            else
            {
                AddContextMenuItem(flyout, "打开", viewModel.OpenSelectionCommand);
            }

            // 真实压缩包文件（可多选）多两个入口：交给系统的 7-Zip 打开 / 解压到「下载」文件夹。
            // 没装 7-Zip 时那一项留着但置灰、标题里写明原因 —— 直接不显示会让人以为功能没做。
            if (viewModel.CanUseArchiveCommands)
            {
                AddContextMenuItem(
                    flyout,
                    viewModel.HasSevenZip ? "使用 7-Zip 打开" : "使用 7-Zip 打开（未找到 7-Zip）",
                    viewModel.OpenWithSevenZipCommand,
                    isEnabled: viewModel.HasSevenZip);

                AddContextMenuItem(flyout, "解压到下载文件夹", viewModel.ExtractToDownloadsCommand);
            }

            // 任意选中项都能打包成 zip（目录含整棵子树）：生成后落到设置里的输出目录
            //（默认「下载」文件夹）并自动复制到剪贴板
            AddContextMenuItem(flyout, "压缩", viewModel.CompressSelectionCommand);

            AddContextMenuItem(flyout, "在资源管理器中显示", viewModel.RevealInExplorerCommand);
            flyout.Items.Add(new MenuFlyoutSeparator());

            // 复制 / 剪切 / 粘贴：与 Ctrl+C / X / V 是同一条命令
            AddContextMenuItem(flyout, "剪切", viewModel.CutSelectionCommand, "Ctrl+X");
            AddContextMenuItem(flyout, "复制", viewModel.CopySelectionCommand, "Ctrl+C");
            AddContextMenuItem(flyout, "粘贴", viewModel.PasteCommand, "Ctrl+V", viewModel.HasFileClipboard);

            // 删除进回收站（与资源管理器一致：Shift+Delete 才是永久删除，菜单里不单列一项）
            AddContextMenuItem(flyout, "删除", viewModel.DeleteSelectionCommand, "Del");

            flyout.Items.Add(new MenuFlyoutSeparator());
            AddContextMenuItem(flyout, "复制路径", viewModel.CopySelectionPathCommand);
            AddContextMenuItem(flyout, "属性", viewModel.ShowPropertiesCommand);
        }
        else
        {
            AddContextMenuItem(flyout, "粘贴", viewModel.PasteCommand, "Ctrl+V", viewModel.HasFileClipboard);
            AddContextMenuItem(flyout, "新建文件夹", viewModel.CreateNewFolderCommand);
            AddContextMenuItem(flyout, "刷新", viewModel.RefreshCommand);
            AddContextMenuAction(flyout, "全选", SelectAllRows);
            flyout.Items.Add(new MenuFlyoutSeparator());
            AddContextMenuItem(flyout, "复制当前路径", viewModel.CopyCurrentPathCommand);
            AddContextMenuItem(flyout, "在此处打开终端", viewModel.OpenTerminalCommand);
        }

        // 合并一份系统菜单项（设置里打开才做）：平铺到末尾、按规范动词与内置项去重。
        // 读一遍系统菜单要把第三方 shell 扩展 Load 进本进程，所以默认关（见 AppSettings）。
        var shellItemCount = AppendShellMenuItems(flyout, viewModel, position, isBackground: !onRow);

        // 回归脚本的断言依据（内置菜单在 UIA 里读得到，不像系统菜单那样只能看 #32768）
        Log.Write(
            $"内置右键菜单：{(onRow ? "文件" : "背景")} 上下文 {flyout.Items.Count} 项"
            + (shellItemCount > 0 ? $"（含系统菜单项 {shellItemCount} 项）" : string.Empty));

        // Position 是相对 DetailsRoot 的 DIP 坐标，ShowAt 自己会换算，所以这里不做 DPI 换算
        flyout.ShowAt(DetailsRoot, new FlyoutShowOptions { Position = position });
    }

    /// <summary>
    /// 把系统菜单项平铺合并进内置菜单的末尾（见 <see cref="AppSettings.BuiltInMenuIncludeShellItems" />）：
    /// 先接一个分隔符，后面就是外壳给这一批选中项的菜单项（<c>发送到</c> / <c>7-Zip</c> 这类子菜单原样保留）。
    ///
    /// 为什么是“这一批选中项”：外壳给的内容本来就跟着**选中项与目录**走（<c>.zip</c> 才有「解压到」、
    /// 仓库里才有 Git 那几项、目录背景的「新建」），而执行的偏移也只在那一次建出来的 HMENU 上有意义。
    /// </summary>
    /// <returns>真的加进去的系统菜单项个数（不含分隔符）。</returns>
    private int AppendShellMenuItems(
        MenuFlyout flyout,
        FolderTabViewModel viewModel,
        Point position,
        bool isBackground)
    {
        if (!viewModel.ShowShellItemsInBuiltInMenu)
        {
            return 0;
        }

        var paths = isBackground
            ? new[] { viewModel.CurrentPath }
            : EntryList.SelectedItems.OfType<FileItemViewModel>().Select(static i => i.FullPath).ToArray();

        if (paths.Length == 0 || (isBackground && string.IsNullOrEmpty(viewModel.CurrentPath)))
        {
            return 0;
        }

        var snapshot = viewModel.GetShellMenuItems(paths, isBackground);
        if (snapshot is null)
        {
            return 0;
        }

        // 执行要用屏幕物理像素（ptInvoke），错一次的代价很小，所以在弹出前算一次就行
        var screen = DpiHelper.ToScreenPoint(DetailsRoot, MainWindowHandle, position);

        var pending = new List<MenuFlyoutItemBase>();

        // 只去重“这次真的已经给过”的那些动词（行菜单与背景菜单给的东西不一样，见上面两个集合）
        var provided = isBackground ? BackgroundProvidedVerbs : RowProvidedVerbs;

        foreach (var entry in snapshot.Items)
        {
            // 内置项已经给过的动词不再重复一份（「打开」只留 exdir 自己那个）
            if (entry.Verb is not null && provided.Contains(entry.Verb))
            {
                continue;
            }

            var item = BuildShellMenuItem(flyout, viewModel, snapshot, entry, screen);
            if (item is not null)
            {
                pending.Add(item);
            }
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var added = 0;
        foreach (var item in pending)
        {
            flyout.Items.Add(item);

            if (item is not MenuFlyoutSeparator)
            {
                added++;
            }
        }

        // 菜单关掉之前这份快照（连同它背后的 HMENU / IContextMenu）得留着：缓存自己管释放，
        // 这里只记下“本次菜单拿着它”，释放时只清引用
        _builtInShellMenu = snapshot;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_builtInShellMenu, snapshot))
            {
                _builtInShellMenu = null;
            }
        };

        return added;
    }

    /// <summary>
    /// 把一个系统菜单项变成 WinUI 菜单项：子菜单递归展开，勾选 / 置灰 / 默认动词（加粗）照抄，
    /// 点击交回外壳执行（exdir 自己不解任何系统命令）。
    /// </summary>
    private static MenuFlyoutItemBase? BuildShellMenuItem(
        MenuFlyout flyout,
        FolderTabViewModel viewModel,
        ShellMenuSnapshot snapshot,
        ShellMenuEntry entry,
        Point screen)
    {
        if (entry.IsSeparator)
        {
            return new MenuFlyoutSeparator();
        }

        if (string.IsNullOrEmpty(entry.Text))
        {
            return null;
        }

        if (entry.Children.Count > 0)
        {
            var subMenu = new MenuFlyoutSubItem { Text = entry.Text, IsEnabled = entry.IsEnabled };

            if (entry.IsChecked)
            {
                subMenu.Icon = CheckIcon();
            }

            foreach (var child in entry.Children)
            {
                var childItem = BuildShellMenuItem(flyout, viewModel, snapshot, child, screen);
                if (childItem is not null)
                {
                    subMenu.Items.Add(childItem);
                }
            }

            // 一个子项都放不进去的空子菜单不如不显示
            return subMenu.Items.Count == 0 ? null : subMenu;
        }

        var item = new MenuFlyoutItem { Text = entry.Text, IsEnabled = entry.IsEnabled };

        if (entry.IsChecked)
        {
            item.Icon = CheckIcon();
        }

        if (entry.IsDefault)
        {
            // 外壳把默认动词（双击 / 回车执行的那个）标了出来，照抄它的加粗
            item.FontWeight = FontWeights.SemiBold;
        }

        item.Click += (_, _) =>
        {
            // 先把菜单收掉：外壳命令可能弹自己的模态对话框，菜单还挂着会抢焦点。
            // Hide 本身可能因为“框架已经关过了”而失败，不能让它挡住下面这行执行命令。
            try
            {
                flyout.Hide();
            }
            catch (Exception)
            {
                // 忽略：菜单已经关掉了
            }

            viewModel.InvokeShellMenuEntry(snapshot, entry, (int)screen.X, (int)screen.Y);
        };

        return item;
    }

    /// <summary>系统菜单里被勾上的项（「查看 → 大图标」这类）前面画一个勾。</summary>
    private static FontIcon CheckIcon() => new() { Glyph = "\uE73E", FontSize = 12 };

    private static void AddContextMenuItem(
        MenuFlyout flyout,
        string text,
        ICommand command,
        string? acceleratorText = null,
        bool isEnabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Command = command, IsEnabled = isEnabled };

        if (acceleratorText is not null)
        {
            item.KeyboardAcceleratorTextOverride = acceleratorText;
        }

        flyout.Items.Add(item);
    }

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

    /// <summary>关掉“操作结果”提示条时同步清掉 VM 里的状态，下次解压才能再弹出来。</summary>
    private void StatusInfoBar_CloseButtonClick(InfoBar sender, object args) => ViewModel?.ClearStatus();

    /// <summary>关掉错误 / 提示条时同样要清掉 VM 里的文案，否则下一次换了内容也弹不出来（见 AGENTS.md 第 90 条）。</summary>
    private void ErrorInfoBar_CloseButtonClick(InfoBar sender, object args) => ViewModel?.ClearError();

    /// <summary>“操作结果”提示条上的「打开目录」：在资源管理器里打开这次解压到的目录。</summary>
    private void StatusInfoBar_OpenDirectory(object sender, RoutedEventArgs args) => ViewModel?.OpenStatusTarget();

    // ------------------------------------------------------------------ 拖放（移动 / 复制到目录）

    /// <summary>本次拖放要落到的目录（目录行，或列表空白处对应的当前目录）。</summary>
    private string? _dropTargetDirectory;

    /// <summary>落下时是移动还是复制（同盘拖动默认移动，按住 Ctrl 是复制）。</summary>
    private bool _dropIsMove;

    /// <summary>鼠标正压着的那一行（只记录有效的目录行，用于整行高亮）。</summary>
    private FileItemViewModel? _dropRow;

    /// <summary>
    /// 拖到列表上：认出目标目录（鼠标下的目录行，或列表空白处 = 当前目录），
    /// 把“移动 / 复制”的结论写进 <see cref="DragEventArgs.AcceptedOperation" /> 与拖拽提示。
    /// </summary>
    private void DetailsRoot_DragOver(object sender, DragEventArgs e) => HandleDragOver(e);

    private void HandleDragOver(DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || !TryResolveDrop(e, viewModel, out var target, out var row, out var move))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SetDropRow(null);
            _dropTargetDirectory = null;
            return;
        }

        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = (move ? "移动到 " : "复制到 ") + LeafName(target);
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;

        if (!string.Equals(_dropTargetDirectory, target, StringComparison.OrdinalIgnoreCase) || _dropIsMove != move)
        {
            Log.Write($"拖放经过：{target}（{(move ? "移动" : "复制")}）行={row?.DisplayName ?? "(背景)"}");
        }

        _dropTargetDirectory = target;
        _dropIsMove = move;
        SetDropRow(row);
    }

    /// <summary>光拖着离开列表（跑到别的窗格/程序上）就把高亮与目标清掉。</summary>
    private void DetailsRoot_DragLeave(object sender, DragEventArgs e)
    {
        // 用光标位置而不是 e.GetPosition（后者在拖拽过程中的坐标不可靠，见 DpiHelper.GetCursorPosition）
        var position = DpiHelper.GetCursorPosition(DetailsRoot, MainWindowHandle);
        if (!double.IsNaN(position.X)
            && position.X >= 0 && position.Y >= 0
            && position.X <= DetailsRoot.ActualWidth && position.Y <= DetailsRoot.ActualHeight)
        {
            // 还在自己身上（子元素之间的移动也会发 DragLeave），不当作离开
            return;
        }

        SetDropRow(null);
        _dropTargetDirectory = null;
    }

    /// <summary>
    /// 真正落下：按 <see cref="DetailsRoot_DragOver" /> 记下的目标目录与移动/复制做操作。
    /// 操作本身交给标签页 ViewModel（视图不做 I/O），完成后由服务发事件让受影响的目录重枚举。
    /// </summary>
    private async void DetailsRoot_Drop(object sender, DragEventArgs e) => await HandleDropAsync(e);

    private async Task HandleDropAsync(DragEventArgs e)
    {
        _internalDropHandled = true;

        var viewModel = ViewModel;
        SetDropRow(null);

        if (viewModel is null)
        {
            return;
        }

        // 落下这一刻按光标位置再算一次落点：拖拽期间鼠标可能已经停在别处，
        // 而 DragOver 里记下的目标只代表“最后一次 DragOver 时”的位置。
        // 只有实在算不出来时才退回 DragOver 记下的那个。
        if (!TryResolveDrop(e, viewModel, out var target, out _, out var move))
        {
            target = _dropTargetDirectory ?? string.Empty;
            move = _dropIsMove;
        }

        _dropTargetDirectory = null;

        if (string.IsNullOrEmpty(target))
        {
            Log.Write("拖放落下：没有可用的目标目录");
            return;
        }

        // 先把 DataView 取到局部变量：await 之后不能再碰事件参数（见 AGENTS.md 第 6 节第 22 条）
        var data = e.DataView;

        try
        {
            var paths = await DragDropHelper.GetPathsAsync(data);
            if (paths.Count == 0)
            {
                Log.Write("拖放落下：数据包里没有路径");
                return;
            }

            Log.Write($"拖放落下：{paths.Count} 项 → {target}（{(move ? "移动" : "复制")}）");
            await viewModel.DropFilesAsync(paths, target, move).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception("文件拖放", ex);
        }
    }

    /// <summary>
    /// 算“这次拖放落在哪个目录 / 是移动还是复制”，不是文件拖放（例如工具条固定目录排序）时返回 false。
    /// <para>
    /// 落点用“鼠标下的行 + 已生成容器的实际矩形”算，而不是看 <c>e.OriginalSource</c>：
    /// 拖放事件的源永远是带 <c>AllowDrop</c> 的那个元素（这里是 <c>DetailsRoot</c>），
    /// 反查不出鼠标压在哪一行（见 AGENTS.md 第 6 节第 54 条）。
    /// </para>
    /// </summary>
    private bool TryResolveDrop(
        DragEventArgs e,
        FolderTabViewModel viewModel,
        out string target,
        out FileItemViewModel? row,
        out bool move)
    {
        target = string.Empty;
        row = null;
        move = false;

        if (string.IsNullOrEmpty(viewModel.CurrentPath)
            || viewModel.IsInsideArchive
            || viewModel.IsRemote
            || viewModel.IsRecentView
            || e.DataView.Contains(DragDropHelper.PinnedReorderFormat))
        {
            return false;
        }

        // 同进程拖拽（我们自己写的格式）与外部拖入（资源管理器）都接受；其它一律不接。
        // 从压缩包里拖出来的那些同时带着 StorageItems，也算内部拖拽。
        var archiveDrag = e.DataView.Contains(DragDropHelper.ArchiveDragFormat);
        var internalDrag = e.DataView.Contains(DragDropHelper.PathsFormat) || archiveDrag;
        if (!internalDrag && !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return false;
        }

        var point = DpiHelper.GetCursorPosition(DetailsRoot, MainWindowHandle);
        if (double.IsNaN(point.X))
        {
            return false;
        }

        var hit = RowAt(point);

        // 包内的目录行与远程位置的行都不能当落点：前者是虚拟路径，后者只读。
        // 不能退回到“当前目录” —— 用户盯的是那一行，退回去会搬错地方。
        if (hit?.IsInArchive == true || (hit is not null && RemotePath.LooksRemote(hit.FullPath)))
        {
            return false;
        }

        row = hit is { IsDirectory: true } ? hit : null;
        target = row?.FullPath ?? viewModel.CurrentPath;

        var control = (e.Modifiers & DragDropModifiers.Control) != 0;
        var shift = (e.Modifiers & DragDropModifiers.Shift) != 0;

        // 自己拖的：默认移动（资源管理器在同盘内的习惯），按住 Ctrl 变成复制。
        // 从资源管理器拖进来的：默认复制（不客气地搬走别人窗口里的文件太危险），按住 Shift 才是移动。
        // 从压缩包里拖出来的：永远复制 —— 包本身只读，交出去的也只是临时副本，
        // “移动”它没有任何意义（按 Shift 也不该变）。
        move = archiveDrag ? false : internalDrag ? !control : shift && !control;

        return true;
    }

    /// <summary>鼠标下的那一行：按已生成行容器的实际矩形判断（虚拟化下已实现的容器就是屏幕上的那些）。</summary>
    private FileItemViewModel? RowAt(Point positionInRoot)
    {
        if (EntryList.ItemsPanelRoot is not { } panel)
        {
            return null;
        }

        foreach (var child in panel.Children)
        {
            if (child is not ListViewItem container)
            {
                continue;
            }

            // 行模板用的是 x:Bind，容器的 DataContext 不保证是数据项（实测为 null），
            // 要用 ItemFromContainer 反查（同 SidebarView 的 TreeView.ItemFromContainer）
            if (EntryList.ItemFromContainer(container) is not FileItemViewModel item)
            {
                continue;
            }

            var origin = container.TransformToVisual(DetailsRoot).TransformPoint(new Point(0, 0));
            var bounds = new Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);

            if (bounds.Contains(positionInRoot))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>高亮当前拖放落点那一行（只对目录行）。</summary>
    private void SetDropRow(FileItemViewModel? row)
    {
        if (ReferenceEquals(_dropRow, row))
        {
            return;
        }

        if (_dropRow is not null)
        {
            _dropRow.IsDropTarget = false;
        }

        _dropRow = row;

        if (row is not null)
        {
            row.IsDropTarget = true;
        }
    }

    /// <summary>提示文本里的目录名（根目录这种没有名字的就用完整路径）。</summary>
    private static string LeafName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var name = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        return name.Length == 0 ? path : name;
    }

    /// <summary>同一次右键里两个事件都冒上来时，用来去重的时间窗（毫秒）。</summary>
    private const long DuplicateContextMenuGuardMs = 400;

    private long _lastContextMenuTicks;

    /// <summary>
    /// 行菜单自己已经提供的命令对应的**规范动词**：把系统菜单项平铺合并进内置菜单时按它去重
    /// —— 外壳的「打开 / 剪切 / 复制 / 粘贴 / 删除 / 属性 / 复制文件地址」与内置项是同一件事，只留内置的。
    /// 内置没有实现的功能（重命名 / 打印 / 以管理员身份运行 / 发送到 / 新建 / 7-Zip……）不在这里，照常显示。
    /// </summary>
    private static readonly HashSet<string> RowProvidedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open",
        "cut",
        "copy",
        "paste",
        "delete",
        "properties",
        "copyaspath",
    };

    /// <summary>
    /// 目录背景菜单自己已经提供的命令只有「粘贴」。
    /// **不能拿行菜单那份集合来套**：背景菜单里本来没有「属性」，去重后把外壳的「属性」也去掉，
    /// 用户就再也点不到文件夹属性了。
    /// </summary>
    private static readonly HashSet<string> BackgroundProvidedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "paste",
    };

    /// <summary>
    /// 本次内置菜单合并进来的那份系统菜单项。缓存负责释放它，这里只是“菜单还开着”的一个引用，
    /// 免得菜单还挂在屏幕上的时候它背后的 HMENU 被淘汰掉（按偏移执行命令需要它活着）。
    /// </summary>
    private ShellMenuSnapshot? _builtInShellMenu;

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
            case VirtualKey.Right when item.IsExpandable && !item.IsExpanded:
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
