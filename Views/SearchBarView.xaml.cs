using System;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Exdir.Views;

/// <summary>
/// 导航条右侧的 Everything 搜索框（每个标签页一份）。
/// 搜索状态全在 <see cref="FolderTabViewModel" /> 上，所以这里只做三件事：
/// 把按键翻译成命令（回车立即搜、Esc 退出搜索）、订阅 <c>SearchFocusRequested</c> 去聚焦
/// （Ctrl+F 从窗口级加速器打进来）、点「×」清空。
/// </summary>
public sealed partial class SearchBarView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(FolderTabViewModel),
        typeof(SearchBarView),
        new PropertyMetadata(null, OnViewModelChanged));

    public SearchBarView()
    {
        InitializeComponent();
    }

    public FolderTabViewModel? ViewModel
    {
        get => (FolderTabViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// 标签页关闭时视图与 ViewModel 一起被丢弃，所以这里不退订也不会泄漏
    /// （与 <see cref="PathBreadcrumb" /> 同一条约定）。
    /// </summary>
    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SearchBarView)d;

        if (e.OldValue is FolderTabViewModel old)
        {
            old.SearchFocusRequested -= view.OnSearchFocusRequested;
        }

        if (e.NewValue is FolderTabViewModel tab)
        {
            tab.SearchFocusRequested += view.OnSearchFocusRequested;
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e)
    {
        // 等本帧落定再聚焦：刚显示出来的元素拿不到焦点
        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        });
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
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
                tab.SearchQuery = SearchBox.Text;
                tab.SearchNowCommand.Execute(null);
                break;

            case VirtualKey.Escape:
                e.Handled = true;
                tab.ClearSearchCommand.Execute(null);
                break;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearSearchCommand.Execute(null);
}
