using System;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Exdir.Views;

/// <summary>
/// title 栏下方的工具条：左侧磁盘、右侧固定目录（可从文件列表/侧边栏拖目录过来固定，
/// 固定目录之间可以拖着换位）与快捷菜单。
/// </summary>
public sealed partial class DriveBarView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(MainViewModel),
        typeof(DriveBarView),
        new PropertyMetadata(null));

    public DriveBarView()
    {
        InitializeComponent();

        // Button 自己会把左键的 PointerPressed/PointerMoved 标成 Handled（普通路由监听收不到），
        // 拖拽手势必须看到“按下 + 移动”两个事件，所以只能在容器上用 handledEventsToo 监听
        // 被标记过的事件，再从 OriginalSource 反查是哪一个固定目录按钮。
        PinnedItemsHost.AddHandler(PointerPressedEvent, new PointerEventHandler(PinnedHost_PointerPressed), true);
        PinnedItemsHost.AddHandler(PointerMovedEvent, new PointerEventHandler(PinnedHost_PointerMoved), true);
        PinnedItemsHost.AddHandler(PointerReleasedEvent, new PointerEventHandler(PinnedHost_PointerEnded), true);
        PinnedItemsHost.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(PinnedHost_PointerEnded), true);
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void DriveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DriveModel drive })
        {
            ViewModel?.NavigateToCommand.Execute(drive.RootPath);
        }
    }

    private void PinnedButton_Click(object sender, RoutedEventArgs e)
    {
        // 松开鼠标前刚拖过（排序），这次 Click 是拖拽的余波，不该顺带导航
        if (Environment.TickCount64 - _lastPinDragTicks < PinDragClickGuardMs)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: PinnedFolderViewModel folder })
        {
            ViewModel?.NavigateToCommand.Execute(folder.Path);
        }
    }

    /// <summary>右键固定目录按钮 → 取消固定（否则只能靠手改 config.json 才能去掉一个）。</summary>
    private void PinnedButton_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: PinnedFolderViewModel folder } element
            || ViewModel is not { } viewModel)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var unpin = new MenuFlyoutItem
        {
            Text = "取消固定",
            Icon = new FontIcon { Glyph = "\uE77A" },
        };

        unpin.Click += (_, _) => viewModel.UnpinFolderCommand.Execute(folder);
        flyout.Items.Add(unpin);

        if (args.TryGetPosition(element, out var position))
        {
            flyout.ShowAt(element, new FlyoutShowOptions { Position = position });
        }
        else
        {
            flyout.ShowAt(element);
        }

        args.Handled = true;
    }

    // ------------------------------------------------------------------ 拖放（固定目录）

    /// <summary>拖拽排序后，短时间内忽略 Click（见 <see cref="PinnedButton_Click" />）。</summary>
    private const long PinDragClickGuardMs = 700;

    /// <summary>按下后移动超过这个距离（DIP）才算拖拽，否则还是点击。</summary>
    private const double PinDragThreshold = 4;

    /// <summary>正在拖动的固定目录路径，由 <c>DragStarting</c> 写入（拖放数据包只带一个路径字符串）。</summary>
    private string? _draggingPinnedPath;

    /// <summary>本次按下（尚未变成拖拽）的起始位置；松手/离开都会清掉。</summary>
    private Point? _pinPressPosition;

    /// <summary>本次按下已经启动过拖拽，避免 PointerMoved 重复调用 <c>StartDragAsync</c>。</summary>
    private bool _pinDragStarted;

    /// <summary>本次按下命中的固定目录按钮（也就是拖拽的源）。</summary>
    private Button? _pinDragButton;

    private long _lastPinDragTicks;

    /// <summary>
    /// 拖动工具条上的固定目录按钮：只写 <see cref="DragDropHelper.PinnedReorderFormat" />，
    /// 不带 <see cref="DragDropHelper.PathsFormat" />，否则拖到固定目录区会被当成“再固定一次”。
    /// </summary>
    private void PinnedButton_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: PinnedFolderViewModel folder })
        {
            args.Cancel = true;
            return;
        }

        _draggingPinnedPath = folder.Path;
        _lastPinDragTicks = Environment.TickCount64;

        args.Data.SetData(DragDropHelper.PinnedReorderFormat, folder.Path);
        args.Data.RequestedOperation = DataPackageOperation.Move;

        Log.Write($"拖拽开始（固定目录）：{folder.Name}");
    }

    private void PinnedButton_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _draggingPinnedPath = null;
        _pinDragStarted = false;
        HideInsertionIndicator();
    }

    // ---- 手势识别：Button 会把左键指针事件标成 Handled，所以这些监听都挂在固定目录容器上 ----

    private void PinnedHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pinDragStarted = false;
        _pinDragButton = FindPinnedButton(e.OriginalSource);

        // 上一次拖拽被 Esc 中断时可能留下提示条（DropCompleted 不保证触发）
        HideInsertionIndicator();

        if (_pinDragButton is not { } button)
        {
            _pinPressPosition = null;
            return;
        }

        var point = e.GetCurrentPoint(button);

        // 右键要留给“取消固定”菜单，不参与拖拽
        _pinPressPosition = point.Properties.IsRightButtonPressed ? null : point.Position;
    }

    /// <summary>
    /// 按下的指针移出阈值后手动调用 <c>StartDragAsync</c> 开始拖拽。
    /// WinUI 3 里 <c>CanDrag</c> 对 <c>Button</c> 无效（Button 自己吃掉了指针事件，
    /// 框架的拖拽手势识别根本不会启动），只能自己识别这个手势。
    /// </summary>
    private void PinnedHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pinDragButton is not { } button || _pinPressPosition is not { } origin || _pinDragStarted)
        {
            return;
        }

        var point = e.GetCurrentPoint(button);

        // IsInContact 对鼠标等价于“有键按住”，对触控/笔等价于“接触中”；
        // 松开后（含拖拽结束时）它会变成 false，顺便把残留的按下状态清掉
        if (!point.IsInContact)
        {
            _pinPressPosition = null;
            return;
        }

        if (Math.Abs(point.Position.X - origin.X) < PinDragThreshold
            && Math.Abs(point.Position.Y - origin.Y) < PinDragThreshold)
        {
            return;
        }

        _pinDragStarted = true;
        _pinPressPosition = null;
        _ = StartPinDragAsync(button, point);
    }

    private void PinnedHost_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        _pinPressPosition = null;
        _pinDragButton = null;
    }

    private async Task StartPinDragAsync(UIElement element, PointerPoint point)
    {
        try
        {
            await element.StartDragAsync(point);
        }
        catch (Exception ex)
        {
            // StartDragAsync 在提权进程里不被支持（Windows 的拖放限制），这里只记日志
            Log.Exception("固定目录拖拽排序", ex);
        }
    }

    /// <summary>从命中的最深层元素往上找它所属的固定目录按钮；不在固定目录区里就返回 null。</summary>
    private Button? FindPinnedButton(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null && !ReferenceEquals(node, PinnedItemsHost))
        {
            if (node is Button button)
            {
                return button;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void PinnedDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            HideDropFeedback();
            return;
        }

        // 拖的是工具条上已有的固定目录 → 调整顺序（数据包内容留到 Drop 再读，这里只做同步判断）
        if (e.DataView.Contains(DragDropHelper.PinnedReorderFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "调整固定目录顺序";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;

            DropHighlight.Visibility = Visibility.Collapsed;
            ShowInsertionIndicator(GetPinnedInsertionIndex(e.GetPosition(PinnedItems)));
            return;
        }

        HideInsertionIndicator();

        if (!DragDropHelper.MayContainFolder(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        // 用 Copy 而不是 Link：外部来源（资源管理器、浏览器地址栏等）不一定允许 Link，
        // 被系统过滤成 None 的话拖到这儿会直接显示“禁止”。
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "固定到工具条";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;

        DropHighlight.Visibility = Visibility.Visible;
    }

    private void PinnedDropZone_DragLeave(object sender, DragEventArgs e) => HideDropFeedback();

    private async void PinnedDropZone_Drop(object sender, DragEventArgs e)
    {
        HideDropFeedback();

        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        // 先把 DataView 与坐标取出来：Drop 处理里 await 之后不能再依赖事件参数
        var data = e.DataView;

        // 排序：不需要 await 就能拿到源路径，但改集合要避开“被拖动的那个按钮自己”的 Drop 处理
        if (data.Contains(DragDropHelper.PinnedReorderFormat))
        {
            var path = _draggingPinnedPath;
            var target = GetPinnedInsertionIndex(e.GetPosition(PinnedItems));

            if (!string.IsNullOrEmpty(path))
            {
                // 下一次消息循环再改集合：此刻还在拖放宿主的回调里，
                // 立刻把源按钮从可视树里摘掉会让拖放宿主提前失效
                DispatcherQueue.TryEnqueue(() => viewModel.MovePinnedFolder(path, target));
            }

            return;
        }

        try
        {
            var paths = await DragDropHelper.GetPathsAsync(data);
            var added = paths.Count == 0 ? 0 : viewModel.PinFolders(paths);
            Log.Write($"固定目录：拖入 {paths.Count} 项，新增 {added} 项");
        }
        catch (Exception ex)
        {
            Log.Exception("固定目录拖放", ex);
        }
    }

    private void HideDropFeedback()
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        HideInsertionIndicator();
    }

    /// <summary>
    /// 按鼠标横坐标算出“插到第几个之前”（<c>0</c> = 最左，<c>Count</c> = 最右）：
    /// 从左往右找第一个中点在自己右边的按钮，它前面就是插入位置。
    /// </summary>
    private int GetPinnedInsertionIndex(Point position)
    {
        if (PinnedItems.ItemsPanelRoot is not { } panel)
        {
            return ViewModel?.PinnedFolders.Count ?? 0;
        }

        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement child)
            {
                continue;
            }

            var origin = child.TransformToVisual(PinnedItems).TransformPoint(new Point(0, 0));
            if (position.X < origin.X + (child.ActualWidth / 2))
            {
                return i;
            }
        }

        return panel.Children.Count;
    }

    private void ShowInsertionIndicator(int index)
    {
        if (PinnedItems.ItemsPanelRoot is not { Children.Count: > 0 } panel)
        {
            HideInsertionIndicator();
            return;
        }

        // 提示条和按钮不在同一棵子树里，所以要把目标边界换算到提示条父容器的坐标系
        double x;
        if (index < panel.Children.Count && panel.Children[index] is FrameworkElement target)
        {
            x = target.TransformToVisual(PinnedItemsHost).TransformPoint(new Point(0, 0)).X;
        }
        else if (panel.Children[panel.Children.Count - 1] is FrameworkElement last)
        {
            x = last.TransformToVisual(PinnedItemsHost).TransformPoint(new Point(last.ActualWidth, 0)).X;
        }
        else
        {
            return;
        }

        InsertionIndicator.Margin = new Thickness(Math.Max(0, x - 1), 5, 0, 5);
        InsertionIndicator.Visibility = Visibility.Visible;
    }

    private void HideInsertionIndicator() => InsertionIndicator.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 快捷菜单暂时不内置任何命令（按需求留空），完全由设置里的
    /// <see cref="AppSettings.QuickCommands" /> 驱动。
    /// 集合为空时显示一条禁用提示，避免出现空白下拉框。
    /// </summary>
    private void QuickMenuFlyout_Opening(object sender, object e)
    {
        QuickMenuFlyout.Items.Clear();

        var viewModel = ViewModel;
        if (viewModel is null || viewModel.QuickCommands.Count == 0)
        {
            QuickMenuFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "（暂无快捷命令）",
                IsEnabled = false,
            });

            return;
        }

        foreach (var command in viewModel.QuickCommands)
        {
            var item = new MenuFlyoutItem
            {
                Text = command.Name,
                Command = viewModel.RunQuickCommandCommand,
                CommandParameter = command,
            };

            if (!string.IsNullOrEmpty(command.Glyph))
            {
                item.Icon = new FontIcon { Glyph = command.Glyph };
            }

            QuickMenuFlyout.Items.Add(item);
        }
    }
}
