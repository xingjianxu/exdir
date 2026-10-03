using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// 设置窗口「远程」页：SFTP / FTP 位置的增删改。
///
/// <para>
/// 编辑对话框是用代码搭的（不是 XAML）：字段多、要按“协议 / 登录方式”互相联动置灰，
/// 而且这里的密码框要在打开时**解密填回去**（用户改端口时不用重新输密码）。
/// 对话框只写 <see cref="SettingsViewModel" /> 手里那份副本，真正落盘走
/// <c>MainViewModel.ApplySettings</c>（见 AGENTS.md 第 5 节）。
/// </para>
/// </summary>
public sealed partial class SettingsView
{
    private static readonly string[] ProtocolNames = { "SFTP（SSH）", "FTP", "FTPS（FTP over TLS）" };

    private static readonly string[] AuthNames = { "密码", "私钥文件", "匿名（仅 FTP）" };

    private void AddRemoteLocation_Click(object sender, RoutedEventArgs e)
        => _ = EditRemoteLocationAsync(null);

    private void EditRemoteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is { } row)
        {
            _ = EditRemoteLocationAsync(row);
        }
    }

    private void DeleteRemoteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is { } row)
        {
            _ = DeleteRemoteLocationAsync(row);
        }
    }

    /// <summary>按钮所在那一行的数据项（模板里 x:Bind 是直接传数据项的，Tag 是兜底）。</summary>
    private static RemoteLocationItemViewModel? RowFrom(object sender)
        => sender is FrameworkElement element
            ? element.DataContext as RemoteLocationItemViewModel ?? element.Tag as RemoteLocationItemViewModel
            : null;

    private async Task DeleteRemoteLocationAsync(RemoteLocationItemViewModel row)
    {
        if (ViewModel is not { } viewModel || XamlRoot is null)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除远程位置",
            Content = $"确定要删除“{row.Title}”吗？保存的密码会一起删掉。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary)
        {
            viewModel.RemoveRemoteLocation(row);
        }
    }

    private async Task EditRemoteLocationAsync(RemoteLocationItemViewModel? row)
    {
        if (ViewModel is not { } viewModel || XamlRoot is null)
        {
            return;
        }

        var location = row?.Location.Clone() ?? new RemoteLocation
        {
            Protocol = RemoteProtocol.Sftp,
            Auth = RemoteAuthMethod.Password,
            StartPath = "/",
        };

        // 解不开（换了 Windows 用户 / 换了机器）就当没存过密码：留空让用户重填
        var password = SecretProtector.Unprotect(location.ProtectedPassword) ?? string.Empty;
        var passphrase = SecretProtector.Unprotect(location.ProtectedPassphrase) ?? string.Empty;

        var nameBox = new TextBox
        {
            Header = "名称（可留空，默认用 用户名@主机）",
            Text = location.Name,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(nameBox, "远程位置名称");

        var protocolBox = new ComboBox
        {
            Header = "协议",
            ItemsSource = ProtocolNames,
            SelectedIndex = (int)location.Protocol,
        };
        AutomationProperties.SetName(protocolBox, "远程协议");

        var hostBox = new TextBox { Header = "主机", Text = location.Host };
        AutomationProperties.SetName(hostBox, "远程主机");

        var portBox = new TextBox
        {
            Header = "端口（留空用默认：SFTP 22 / FTP 21）",
            Text = location.Port > 0 ? location.Port.ToString(CultureInfo.InvariantCulture) : string.Empty,
        };
        AutomationProperties.SetName(portBox, "远程端口");

        var userBox = new TextBox { Header = "用户名", Text = location.EffectiveUserName };
        AutomationProperties.SetName(userBox, "远程用户名");

        var authBox = new ComboBox
        {
            Header = "登录方式",
            ItemsSource = AuthNames,
            SelectedIndex = (int)location.Auth,
        };
        AutomationProperties.SetName(authBox, "远程登录方式");

        var passwordBox = new PasswordBox { Header = "密码", Password = password };
        AutomationProperties.SetName(passwordBox, "远程密码");

        var keyPathBox = new TextBox { Header = "私钥文件", Text = location.PrivateKeyPath };
        AutomationProperties.SetName(keyPathBox, "远程私钥文件");

        var passphraseBox = new PasswordBox { Header = "私钥口令（可留空）", Password = passphrase };
        AutomationProperties.SetName(passphraseBox, "远程私钥口令");

        var browseKey = new Button { Content = "浏览…" };
        AutomationProperties.SetName(browseKey, "浏览私钥文件");
        browseKey.Click += (_, _) =>
        {
            if (HostWindow is null)
            {
                return;
            }

            var picked = FolderPicker.PickFile(
                WinRT.Interop.WindowNative.GetWindowHandle(HostWindow),
                keyPathBox.Text);

            if (!string.IsNullOrWhiteSpace(picked))
            {
                keyPathBox.Text = picked;
            }
        };

        // 私钥文件那行：输入框跟着卡片宽度缩，按钮永远占住自己的位置（见 AGENTS.md 第 93 条）
        var keyRow = new Grid { ColumnSpacing = 8 };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(keyPathBox, 0);
        Grid.SetColumn(browseKey, 1);
        keyRow.Children.Add(keyPathBox);
        keyRow.Children.Add(browseKey);

        var startPathBox = new TextBox
        {
            Header = "起始目录（默认 /，打开这个位置时先进这里）",
            Text = location.StartPath,
        };
        AutomationProperties.SetName(startPathBox, "远程起始目录");

        var passiveSwitch = new ToggleSwitch
        {
            Header = "被动模式（FTP / FTPS；主动模式在 NAT 后面基本连不上）",
            IsOn = location.UsePassive,
            OffContent = string.Empty,
            OnContent = string.Empty,
        };
        AutomationProperties.SetName(passiveSwitch, "远程被动模式");

        var certSwitch = new ToggleSwitch
        {
            Header = "允许无效证书（FTPS；自签名证书的内网服务器需要打开）",
            IsOn = location.AllowInvalidCertificate,
            OffContent = string.Empty,
            OnContent = string.Empty,
        };
        AutomationProperties.SetName(certSwitch, "远程允许无效证书");

        var errorText = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        // 按协议 / 登录方式互相置灰：SFTP 用不到被动模式，密码登录用不到私钥文件……
        void Sync()
        {
            var isPrivateKey = authBox.SelectedIndex == (int)RemoteAuthMethod.PrivateKey;
            var isAnonymous = authBox.SelectedIndex == (int)RemoteAuthMethod.Anonymous;
            var isSftp = protocolBox.SelectedIndex == (int)RemoteProtocol.Sftp;
            var isFtps = protocolBox.SelectedIndex == (int)RemoteProtocol.Ftps;

            keyPathBox.IsEnabled = isPrivateKey;
            browseKey.IsEnabled = isPrivateKey;
            passphraseBox.IsEnabled = isPrivateKey;
            passwordBox.IsEnabled = !isPrivateKey;
            userBox.IsEnabled = !isAnonymous;
            passiveSwitch.IsEnabled = !isSftp;
            certSwitch.IsEnabled = isFtps;
        }

        authBox.SelectionChanged += (_, _) => Sync();
        protocolBox.SelectionChanged += (_, _) => Sync();
        Sync();

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(errorText);
        content.Children.Add(nameBox);
        content.Children.Add(protocolBox);
        content.Children.Add(hostBox);
        content.Children.Add(portBox);
        content.Children.Add(userBox);
        content.Children.Add(authBox);
        content.Children.Add(passwordBox);
        content.Children.Add(keyRow);
        content.Children.Add(passphraseBox);
        content.Children.Add(startPathBox);
        content.Children.Add(passiveSwitch);
        content.Children.Add(certSwitch);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = row is null ? "添加远程位置" : "编辑远程位置",
            Content = new ScrollViewer { Content = content, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 校验不通过就留在对话框里（把 args.Cancel 置上），别把写坏的值存进去
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var protocol = (RemoteProtocol)Math.Clamp(protocolBox.SelectedIndex, 0, ProtocolNames.Length - 1);
            var auth = (RemoteAuthMethod)Math.Clamp(authBox.SelectedIndex, 0, AuthNames.Length - 1);

            var host = hostBox.Text.Trim();
            var user = userBox.Text.Trim();
            var keyPath = keyPathBox.Text.Trim();
            var portText = portBox.Text.Trim();

            var port = 0;
            if (portText.Length > 0
                && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    || port <= 0
                    || port > 65535))
            {
                args.Cancel = true;
                errorText.Text = "端口要填 1～65535 之间的数字，或者留空用默认端口。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            if (host.Length == 0)
            {
                args.Cancel = true;
                errorText.Text = "主机不能为空。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            if (auth != RemoteAuthMethod.Anonymous && user.Length == 0)
            {
                args.Cancel = true;
                errorText.Text = "用户名不能为空（匿名登录请把登录方式改成「匿名」）。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            if (auth == RemoteAuthMethod.PrivateKey && keyPath.Length == 0)
            {
                args.Cancel = true;
                errorText.Text = "私钥登录要指定私钥文件。";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            var updated = new RemoteLocation
            {
                Id = location.Id,
                Name = nameBox.Text.Trim(),
                Protocol = protocol,
                Host = host,
                Port = port,
                UserName = user,
                Auth = auth,

                // 私钥登录不需要密码；匿名登录的密码框几乎总是空的（服务器也不看）
                ProtectedPassword = auth == RemoteAuthMethod.PrivateKey
                    ? string.Empty
                    : SecretProtector.Protect(passwordBox.Password),
                PrivateKeyPath = keyPath,
                ProtectedPassphrase = SecretProtector.Protect(passphraseBox.Password),
                StartPath = string.IsNullOrWhiteSpace(startPathBox.Text) ? "/" : startPathBox.Text.Trim(),
                UsePassive = passiveSwitch.IsOn,
                AllowInvalidCertificate = certSwitch.IsOn,
            };

            if (row is null)
            {
                viewModel.AddRemoteLocation(updated);
            }
            else
            {
                viewModel.UpdateRemoteLocation(row, updated);
            }
        };

        await dialog.ShowAsync().AsTask().ConfigureAwait(true);
    }
}
