using System;
using System.ComponentModel;
using Exdir.ViewModels;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Exdir.Views;

/// <summary>
/// 地址栏：面包屑（分段可点击 + chevron 分隔）与“可直接输入路径”的输入框互相切换。
/// 编辑态保存在 <see cref="FolderTabViewModel.IsPathEditing"/> 上而不是视图里，
/// 这样 Ctrl+L 之类的窗口级入口也能把地址栏切到编辑态。
/// </summary>
public sealed partial class PathBreadcrumb : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(FolderTabViewModel),
        typeof(PathBreadcrumb),
        new PropertyMetadata(null, OnViewModelChanged));

    public PathBreadcrumb()
    {
        InitializeComponent();
        SyncEditState();
    }

    public FolderTabViewModel? ViewModel
    {
        get => (FolderTabViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // ------------------------------------------------------------------ 视图模型联动

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (PathBreadcrumb)d;

        // 标签页关闭时视图与 ViewModel 一起被丢弃，所以这里不退订也不会泄漏
        if (e.OldValue is FolderTabViewModel old)
        {
            old.PropertyChanged -= view.OnTabPropertyChanged;
        }

        if (e.NewValue is FolderTabViewModel tab)
        {
            tab.PropertyChanged += view.OnTabPropertyChanged;
        }

        view.SyncEditState();
        view.RequestScrollToEnd();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FolderTabViewModel.IsPathEditing):
                SyncEditState();
                break;

            case nameof(FolderTabViewModel.PathSegments):
                RequestScrollToEnd();
                break;
        }
    }

    /// <summary>按编辑态在“面包屑 / 输入框”之间切换。</summary>
    private void SyncEditState()
    {
        var editing = ViewModel?.IsPathEditing == true;

        CrumbScroll.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        PathBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        OverflowIndicator.Visibility = Visibility.Collapsed;
        BlankArea.Visibility = Visibility.Collapsed;

        if (!editing)
        {
            RequestScrollToEnd();
            return;
        }

        // 等本帧把输入框显示出来再聚焦/全选（隐藏的元素拿不到焦点）
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel?.IsPathEditing != true)
            {
                return;
            }

            PathBox.Focus(FocusState.Programmatic);
            PathBox.SelectAll();
        });
    }

    // ------------------------------------------------------------------ 面包屑布局

    /// <summary>滚到最右侧，保证“当前目录”这一段永远可见。</summary>
    private void RequestScrollToEnd()
    {
        // 低优先级派发 = 等本帧布局跑完再改，此时量到的宽度才是新值
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            CrumbScroll.ChangeView(CrumbScroll.ScrollableWidth, null, null, disableAnimation: true);
            UpdateOverflowIndicator();
            UpdateBlankArea();
        });
    }

    /// <summary>路径比地址栏宽时，左端露出的省略号（表示上面还有被裁掉的分段）。</summary>
    private void UpdateOverflowIndicator()
    {
        var overflow = ViewModel?.IsPathEditing == false && CrumbScroll.ScrollableWidth > 0.5;
        OverflowIndicator.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 把“空白区域”的点击目标贴到面包屑右侧。
    /// 面包屑自身宽度不能用 ExtentWidth：内容比视口窄时它会被拉伸成视口宽度，
    /// 因此 <see cref="Crumbs"/> 用 Left 对齐（宽 = 内容真实宽度），直接量它的 ActualWidth。
    /// 这个按钮盖在 ScrollViewer 之上，所以点空白处永远是它收到点击，不会被滚动器吞掉；
    /// 面包屑已经占满整条时收起（这时点“当前目录”段同样能进编辑态）。
    /// </summary>
    private void UpdateBlankArea()
    {
        if (ViewModel?.IsPathEditing != false)
        {
            BlankArea.Visibility = Visibility.Collapsed;
            return;
        }

        var crumbsWidth = CrumbScroll.Padding.Left + Crumbs.ActualWidth;
        var blank = CrumbScroll.ViewportWidth - crumbsWidth;
        if (Crumbs.ActualWidth <= 0 || blank < 4)
        {
            BlankArea.Visibility = Visibility.Collapsed;
            return;
        }

        BlankArea.Margin = new Thickness(crumbsWidth, 0, 0, 0);
        BlankArea.Width = blank;
        BlankArea.Visibility = Visibility.Visible;
    }

    private void CrumbScroll_SizeChanged(object sender, SizeChangedEventArgs e) => RequestScrollToEnd();

    private void CrumbScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdateOverflowIndicator();

    // ------------------------------------------------------------------ 点击

    /// <summary>点右侧空白区域切到编辑态。</summary>
    private void BlankArea_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsPathEditing: false } tab)
        {
            tab.BeginPathEdit();
        }
    }

    /// <summary>
    /// 点到分段之间的 chevron / 缝隙（地址栏内、但不是分段按钮）时也切到编辑态。
    /// <see cref="FolderTabViewModel.BeginPathEdit"/> 是幂等的，和上面的按钮重复触发也无副作用。
    /// </summary>
    private void AddressHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel is not { IsPathEditing: false } tab || IsInsideButton(e.OriginalSource))
        {
            return;
        }

        tab.BeginPathEdit();
    }

    /// <summary>点击的是面包屑分段按钮本身时不要抢（由它们自己的 Click 处理）。</summary>
    private bool IsInsideButton(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null && !ReferenceEquals(node, this))
        {
            if (node is ButtonBase)
            {
                return true;
            }

            node = node is UIElement element ? VisualTreeHelper.GetParent(element) : null;
        }

        return false;
    }

    /// <summary>点某一段：当前目录段 = 进编辑态，其它段 = 导航过去。</summary>
    private void Segment_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } tab ||
            sender is not FrameworkElement { DataContext: PathSegmentViewModel segment })
        {
            return;
        }

        if (segment.IsCurrent)
        {
            tab.BeginPathEdit();
            return;
        }

        if (tab.NavigateToSegmentCommand.CanExecute(segment.FullPath))
        {
            tab.NavigateToSegmentCommand.Execute(segment.FullPath);
        }
    }

    // ------------------------------------------------------------------ 编辑态

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is not { } tab)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;

                // 不依赖 TwoWay 的回写时机，直接以框里的文本为准
                tab.PathInput = PathBox.Text;
                await tab.NavigatePathCommand.ExecuteAsync(null);
                break;

            case VirtualKey.Escape:
                e.Handled = true;
                tab.CancelPathEdit();
                break;
        }
    }

    /// <summary>焦点离开地址栏 = 放弃这次输入（与资源管理器一致）。</summary>
    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsPathEditing: true } tab)
        {
            tab.CancelPathEdit();
        }
    }
}
