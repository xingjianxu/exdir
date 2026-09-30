using System.Collections.Generic;

namespace Exdir.Services;

/// <summary>
/// 剪贴板上的“一批文件”及其意图（复制还是剪切）。
/// 读到的内容可能来自 exdir 自己，也可能来自资源管理器 / 7-Zip 等。
/// </summary>
/// <param name="Paths">文件或目录的完整路径。</param>
/// <param name="IsMove">true = 剪切（粘贴时应移动），false = 复制。</param>
public sealed record ClipboardFiles(IReadOnlyList<string> Paths, bool IsMove);

/// <summary>
/// 文件剪贴板。实现走系统的 <c>CF_HDROP</c> + <c>CF_PREFERREDDROPEFFECT</c>，
/// 因此与资源管理器互通（见 <c>Services/Native/ClipboardInterop</c>）。
/// </summary>
public interface IClipboardService
{
    /// <summary>把一批路径放到剪贴板上；<paramref name="move" /> 为 true 表示“剪切”。</summary>
    bool SetFiles(IReadOnlyList<string> paths, bool move);

    /// <summary>读出剪贴板上的文件列表与意图；剪贴板上没有文件时返回 null。</summary>
    ClipboardFiles? GetFiles();

    /// <summary>剪贴板上有没有文件（只查格式，不读内容；给菜单项的可用状态用）。</summary>
    bool HasFiles();

    /// <summary>清空剪贴板（移动成功之后调用，避免同一批文件被移动两次）。</summary>
    void Clear();
}
