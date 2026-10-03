namespace Exdir.Services;

/// <summary>
/// 需要用户当场回答的少数几个问题。放在服务里是为了让 ViewModel 不直接建 UI
/// （压缩包密码是目前唯一一个入口，见 <see cref="IArchiveService" />）。
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// 问用户要一个压缩包的密码。用户取消（或当时没有可用的 XamlRoot）时返回 null。
    /// </summary>
    /// <param name="archiveDisplayName">压缩包显示名（只用于提示文案）。</param>
    Task<string?> RequestPasswordAsync(string archiveDisplayName);

    /// <summary>
    /// 弹系统文件夹选择器，返回选中的本地目录；用户取消 / 没有宿主窗口时返回 null。
    /// 只有「远程位置的下载到…」在用（需要一个本地落点）。
    ///
    /// <para>
    /// 它在 UI 线程上是**模态**的（对话框开着的时候不抽消息），所以只能从 UI 线程调，
    /// 而且调用期间界面不响应 —— 与设置窗口里的「浏览…」是同一个行为。
    /// </para>
    /// </summary>
    Task<string?> PickFolderAsync(string? initialDirectory = null);

    /// <summary>弹系统文件选择器（SFTP 私钥文件用），返回选中的文件；取消 / 无宿主窗口时返回 null。</summary>
    Task<string?> PickFileAsync(string? initialFilePath = null);
}
