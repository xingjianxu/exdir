using System;
using System.Collections.Generic;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
