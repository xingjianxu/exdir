using System;
using Exdir.Models;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// title 栏下方的工具条：左侧磁盘、右侧固定目录与快捷菜单。
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
