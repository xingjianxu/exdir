using System;
using System.Collections.Generic;
using System.Linq;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Exdir.Views;

/// <summary>左侧文件夹树。</summary>
public sealed partial class SidebarView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(SidebarViewModel),
        typeof(SidebarView),
        new PropertyMetadata(null));

    public SidebarView()
    {
        InitializeComponent();

        // 行模板上的 DragOver 负责“认出鼠标压在哪一行”；TreeView 这一层要在整条路由的最后
        // 再确认一次 AcceptedOperation —— TreeViewList 会把自己的判断（重排已关闭 → None）
        // 写回去，不覆盖的话外面已接受的操作会被改回“禁止”，松手时根本不会有 Drop。
        // 这两个监听用 handledEventsToo 才收得到（TreeViewList 会标 Handled）。
        FolderTree.AddHandler(DragOverEvent, new DragEventHandler(FolderTree_DragOver), true);
        FolderTree.AddHandler(DropEvent, new DragEventHandler(FolderTree_Drop), true);
    }

    public SidebarViewModel? ViewModel
    {
        get => (SidebarViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is SidebarNodeViewModel node && node.IsNavigable)
        {
            ViewModel?.RequestNavigate(node.FullPath);
        }
    }

    private async void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (ViewModel is null)
        {
            return;
        }

        var node = args.Item as SidebarNodeViewModel
            ?? args.Node?.Content as SidebarNodeViewModel;

        if (node is null)
        {
            return;
        }

        try
        {
            await ViewModel.ExpandAsync(node);
        }
        catch (Exception)
        {
            // 展开失败时保持节点折叠，不打断交互
        }
    }

    /// <summary>
    /// 把树节点（已加载的目录）作为拖放内容：分组标题节点（云存储 / 此电脑 / 收藏夹 / 远程）没有路径，
    /// 拖它们不产生任何数据，直接取消；**远程位置的路径也不能拖** —— 它的落点（工具条固定目录 /
    /// 侧边栏收藏）只接受真实的本地目录，拖过去也只会被拒（还不如根本不让它开始拖）。
    /// </summary>
    private void FolderTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        var nodes = args.Items.OfType<SidebarNodeViewModel>().ToList();

        if (nodes.Any(static node => node.Kind == SidebarNodeKind.Remote
                                     || RemotePath.LooksRemote(node.FullPath)))
        {
            args.Cancel = true;
            return;
        }

        var paths = nodes
            .Select(node => node.FullPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToList();

        if (paths.Count == 0)
        {
            args.Cancel = true;
            return;
        }

        DragDropHelper.SetPaths(args.Data, paths);
        Log.Write($"拖拽开始（侧边栏）：{paths.Count} 个目录");
    }

    // ------------------------------------------------------------------ 拖放（收藏到侧边栏）

    /// <summary>当前高亮的收藏落点节点（拖拽离开或落下后清掉）。</summary>
    private SidebarNodeViewModel? _dropTargetNode;

    /// <summary>
    /// 拖目录停在某个树节点上时：只有「收藏夹」分组本身或它的子项才接受，
    /// 其它节点一律拒绝（拖到别的目录上不该变成收藏）。
    /// 拖动工具条上的固定目录按钮（排序格式）也不接受：那是排序，不是收藏。
    ///
    /// 事件挂在每个 <see cref="TreeViewItem" />（模板根）上，不是挂在 TreeView 上：
    /// 拖放的落点是“带 AllowDrop 的那个元素”，挂在 TreeView 上时 e.OriginalSource 永远
    /// 是 TreeView 自己，认不出鼠标压在哪一行（见 AGENTS.md 第 6 节第 54 条）。
    /// 下面空白处没有 AllowDrop，自然显示“禁止”光标，不用额外拦。
    /// </summary>
    private void SidebarItem_DragOver(object sender, DragEventArgs e)
    {
        var node = NodeFromContainer(sender);

        if (node is null || !IsFavoritesTarget(node) || !DragDropHelper.MayContainFolder(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;

            // 从收藏夹行移到别的行上时必须立刻取消旧目标，否则外层 DragOver 仍会按它接受
            SetDropTarget(null);
            return;
        }

        // DragOver 里只能做同步判断：await 之后事件参数会失效（见 AGENTS.md 第 6 节第 22 条）
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "收藏到侧边栏";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;

        SetDropTarget(node);
    }

    /// <summary>TreeView 这一层的拖拽确认 / 落点处理（见构造函数里的说明）。</summary>
    private void FolderTree_DragOver(object sender, DragEventArgs e)
    {
        if (_dropTargetNode is { } node
            && IsFavoritesTarget(node)
            && DragDropHelper.MayContainFolder(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "收藏到侧边栏";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void SidebarItem_DragLeave(object sender, DragEventArgs e)
        => ClearDropTarget(NodeFromContainer(sender));

    /// <summary>
    /// 真正落下：目标行由 <see cref="SidebarItem_DragOver" /> 记在 <see cref="_dropTargetNode" /> 里。
    /// 这里不能看 <c>e.OriginalSource</c> —— 拖放事件的源是带 AllowDrop 的那个元素，
    /// TreeView 这一层拿到的永远是 TreeViewList，分不出落在哪一行。
    /// </summary>
    private async void FolderTree_Drop(object sender, DragEventArgs e)
    {
        var node = _dropTargetNode;
        SetDropTarget(null);

        if (node is null || !IsFavoritesTarget(node) || ViewModel is not { } viewModel)
        {
            return;
        }

        // 先把 DataView 取到局部变量：await 之后不能再碰事件参数
        var data = e.DataView;

        try
        {
            var paths = await DragDropHelper.GetPathsAsync(data);
            viewModel.RequestPin(paths);
        }
        catch (Exception ex)
        {
            Log.Exception("侧边栏收藏拖放", ex);
        }
    }

    /// <summary>收藏项右键菜单：取消收藏（工具条被隐藏时这是唯一的移除入口）。</summary>
    private void SidebarItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (NodeFromContainer(sender) is not { Kind: SidebarNodeKind.Favorite } favorite
            || ViewModel is not { } viewModel
            || sender is not FrameworkElement anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var unpin = new MenuFlyoutItem
        {
            Text = "取消收藏",
            Icon = new FontIcon { Glyph = "\uE77A" },
        };

        unpin.Click += (_, _) => viewModel.RequestUnpin(favorite.FullPath);
        flyout.Items.Add(unpin);

        if (args.TryGetPosition(anchor, out var position))
        {
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        }
        else
        {
            flyout.ShowAt(anchor);
        }

        args.Handled = true;
    }

    /// <summary>
    /// 模板根就是 <see cref="TreeViewItem" />（即容器本身），所以 sender 就是那一行；
    /// 用 <see cref="TreeView.ItemFromContainer" /> 反查数据项（不依赖 DataContext：
    /// DataTemplate 里的 x:Bind 是直接把数据项传给生成的绑定代码的）。
    /// </summary>
    private SidebarNodeViewModel? NodeFromContainer(object? sender)
        => sender is TreeViewItem item ? FolderTree.ItemFromContainer(item) as SidebarNodeViewModel : null;

    private static bool IsFavoritesTarget(SidebarNodeViewModel node)
        => node.Kind is SidebarNodeKind.FavoritesGroup or SidebarNodeKind.Favorite;

    private void SetDropTarget(SidebarNodeViewModel? node)
    {
        if (ReferenceEquals(_dropTargetNode, node))
        {
            return;
        }

        if (_dropTargetNode is not null)
        {
            _dropTargetNode.IsDropTarget = false;
        }

        _dropTargetNode = node;

        if (node is not null)
        {
            node.IsDropTarget = true;
        }
    }

    private void ClearDropTarget(SidebarNodeViewModel? node)
    {
        if (node is not null)
        {
            node.IsDropTarget = false;
        }

        if (ReferenceEquals(_dropTargetNode, node))
        {
            _dropTargetNode = null;
        }
    }

    /// <summary>尽力把树的选中项同步到某个路径（仅限已加载的节点）。</summary>
    public void SyncToPath(string path)
    {
        if (ViewModel is null || string.IsNullOrEmpty(path))
        {
            return;
        }

        var target = ViewModel.FindNode(path);
        if (target is null)
        {
            return;
        }

        var treeNode = FindTreeNode(FolderTree.RootNodes, target);
        if (treeNode is not null)
        {
            FolderTree.SelectedNode = treeNode;
        }
    }

    private static TreeViewNode? FindTreeNode(IEnumerable<TreeViewNode> nodes, SidebarNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node.Content, target))
            {
                return node;
            }

            var found = FindTreeNode(node.Children, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
