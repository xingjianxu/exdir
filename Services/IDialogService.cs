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
}
