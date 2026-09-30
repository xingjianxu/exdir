using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Exdir.Services;

/// <summary>一次文件操作的结果。</summary>
/// <param name="Success">是否全部成功。</param>
/// <param name="Canceled">用户是否在进度对话框里取消了。</param>
/// <param name="ErrorCode">外壳返回的错误码（0 = 成功）。</param>
public sealed record FileOperationResult(bool Success, bool Canceled, int ErrorCode)
{
    /// <summary>给界面看的失败原因（成功时为 null）。</summary>
    public string? ErrorMessage => Success
        ? null
        : Canceled
            ? "操作已取消"
            : $"操作失败（错误码 {ErrorCode}）";
}

/// <summary>一次文件操作完成后的通知（界面据此刷新受影响的目录）。</summary>
public sealed class FileOperationCompletedEventArgs : EventArgs
{
    /// <summary>true = 移动，false = 复制（删除看 <see cref="IsDelete" />）。</summary>
    public required bool IsMove { get; init; }

    /// <summary>true = 删除操作（此时没有目标目录）。</summary>
    public bool IsDelete { get; init; }

    /// <summary>本次操作的源路径（文件或目录）。</summary>
    public required IReadOnlyList<string> SourcePaths { get; init; }

    /// <summary>目标目录；删除操作没有目标目录，为 null。</summary>
    public string? DestinationDirectory { get; init; }
}

/// <summary>
/// 文件操作（复制 / 移动 / 删除）。实现走外壳自己的 <c>SHFileOperation</c>，
/// 因此进度对话框、同名冲突询问、只读介质处理都是资源管理器同款。
/// </summary>
public interface IFileOperationService
{
    /// <summary>操作成功完成后触发（在 UI 线程上）。</summary>
    event EventHandler<FileOperationCompletedEventArgs>? Completed;

    /// <summary>把一批文件 / 目录复制到目标目录。</summary>
    Task<FileOperationResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory);

    /// <summary>把一批文件 / 目录移动到目标目录。</summary>
    Task<FileOperationResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory);

    /// <summary>
    /// 删除一批文件 / 目录。<paramref name="permanent" /> 为 false（默认）时丢进回收站，
    /// true 时永久删除。确认框由外壳弹出，用户点“否”时返回 <see cref="FileOperationResult.Canceled" />。
    /// </summary>
    Task<FileOperationResult> DeleteAsync(
        IReadOnlyList<string> sourcePaths,
        bool permanent = false);
}
