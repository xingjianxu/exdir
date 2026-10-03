using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Models;

namespace Exdir.ViewModels;

/// <summary>
/// 设置窗口「远程」页里的一行（一个 SFTP / FTP 位置）。
///
/// <para>
/// 它只是 <see cref="SettingsViewModel" /> 手里那份**副本**的显示壳：真正写回
/// <c>config.json</c> 只发生在 <c>MainViewModel.ApplySettings</c> 一处（见 AGENTS.md 第 5 节）。
/// </para>
/// </summary>
public sealed partial class RemoteLocationItemViewModel : ObservableObject
{
    private RemoteLocation _location;

    public RemoteLocationItemViewModel(RemoteLocation location)
    {
        _location = location;
    }

    /// <summary>这份配置（编辑对话框直接改的就是它；密码存的是 DPAPI 密文）。</summary>
    public RemoteLocation Location
    {
        get => _location;
        private set
        {
            if (SetProperty(ref _location, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(EditButtonName));
                OnPropertyChanged(nameof(DeleteButtonName));
            }
        }
    }

    /// <summary>行标题：用户填的名字，没填时用 <c>user@host</c>。</summary>
    public string Title => Location.DisplayName;

    /// <summary>「编辑…」按钮的 UIA 名字（带上位置名，脚本 / 屏幕阅读器才分得清哪一行）。</summary>
    public string EditButtonName => $"编辑 {Title}";

    /// <summary>「删除」按钮的 UIA 名字。</summary>
    public string DeleteButtonName => $"删除 {Title}";

    /// <summary>
    /// 行副标题：协议 + 主机 + 用户名 + 认证方式 + 起始目录。
    /// UIA 名字用的也是它，回归脚本据此断言“这一行是哪个位置”。
    /// </summary>
    public string Subtitle
    {
        get
        {
            var auth = Location.Auth switch
            {
                RemoteAuthMethod.PrivateKey => "私钥",
                RemoteAuthMethod.Anonymous => "匿名",
                _ => "密码",
            };

            var start = string.IsNullOrWhiteSpace(Location.StartPath) ? "/" : Location.StartPath;
            return $"{Location.DisplayTarget} · {auth} · 起始目录 {start}";
        }
    }

    /// <summary>编辑对话框点「确定」后换掉这份配置（触发标题 / 副标题刷新）。</summary>
    public void Update(RemoteLocation location) => Location = location;
}
