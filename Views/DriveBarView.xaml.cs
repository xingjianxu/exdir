using System;
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

/// <summary>
/// title 栏下方的工具条：左侧磁盘、右侧固定目录（可从文件列表/侧边栏拖目录过来固定）与快捷菜单。
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
        if (sender is FrameworkElement { DataContext: PinnedFolderViewModel folder })
        {
            ViewModel?.NavigateToCommand.Execute(folder.Path);
        }
    }

    /// <summary>右键固定目录按钮 → 取消固定（否则只能靠手改 settings.json 才能去掉一个）。</summary>
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

    private void PinnedDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !DragDropHelper.MayContainFolder(e.DataView))
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

    private void PinnedDropZone_DragLeave(object sender, DragEventArgs e)
        => DropHighlight.Visibility = Visibility.Collapsed;

    private async void PinnedDropZone_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;

        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        // 先把 DataView 取出来：Drop 处理里 await 之后不能再依赖事件参数
        var data = e.DataView;

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
