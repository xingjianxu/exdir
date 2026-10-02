using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Services;

/// <inheritdoc cref="IDialogService" />
public sealed class DialogService : IDialogService
{
    public async Task<string?> RequestPasswordAsync(string archiveDisplayName)
    {
        // 主窗口的内容就是 RootGrid；拿它当 XamlRoot（非打包应用里 ContentDialog 必须显式指定）
        if (App.MainWindow?.Content is not FrameworkElement root || root.XamlRoot is null)
        {
            return null;
        }

        var box = new PasswordBox
        {
            PlaceholderText = "密码",
            // 回归脚本按这个名字找输入框
            Name = "ArchivePasswordBox",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "压缩包已加密",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"“{archiveDisplayName}”需要密码才能打开。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        return result == ContentDialogResult.Primary ? box.Password : null;
    }
}
