using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Services.Native;
using WinRT.Interop;

namespace Exdir.Services;

/// <summary>
/// <inheritdoc cref="IFileOperationService" />
/// <para>
/// <c>SHFileOperation</c> 必须在 STA 线程上调用（它要自己跑一个模态进度对话框），
/// 而线程池线程是 MTA，所以每次操作都开一个专用的 STA 线程，
/// 这样主窗口的消息循环不被阻塞（只有进度对话框是模态的，和资源管理器一样）。
/// </para>
/// </summary>
public sealed class FileOperationService : IFileOperationService
{
    public event EventHandler<FileOperationCompletedEventArgs>? Completed;

    public Task<FileOperationResult> CopyAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory)
        => RunAsync(sourcePaths, destinationDirectory, move: false);

    public Task<FileOperationResult> MoveAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory)
        => RunAsync(sourcePaths, destinationDirectory, move: true);

    public Task<FileOperationResult> DeleteAsync(IReadOnlyList<string> sourcePaths, bool permanent = false)
        => RunDeleteAsync(sourcePaths, permanent);

    private async Task<FileOperationResult> RunAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool move)
    {
        if (sourcePaths.Count == 0 || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return new FileOperationResult(true, false, 0);
        }

        var sources = new List<string>(sourcePaths);
        var destination = destinationDirectory;
        var owner = OwnerHandle;

        var result = await RunOnStaThread(() => move
            ? FileOperationInterop.Move(owner, sources, destination)
            : FileOperationInterop.Copy(owner, sources, destination)).ConfigureAwait(true);

        Log.Write(
            $"文件操作：{(move ? "移动" : "复制")} {sources.Count} 项 → {destination}"
            + $"（错误码 {result.ErrorCode}{(result.Canceled ? "，已取消" : string.Empty)}）");

        if (result.Success)
        {
            Completed?.Invoke(this, new FileOperationCompletedEventArgs
            {
                IsMove = move,
                SourcePaths = sources,
                DestinationDirectory = destination,
            });
        }

        return new FileOperationResult(result.Success, result.Canceled, result.ErrorCode);
    }

    private async Task<FileOperationResult> RunDeleteAsync(IReadOnlyList<string> sourcePaths, bool permanent)
    {
        if (sourcePaths.Count == 0)
        {
            return new FileOperationResult(true, false, 0);
        }

        var sources = new List<string>(sourcePaths);
        var owner = OwnerHandle;

        var result = await RunOnStaThread(() => FileOperationInterop.Delete(owner, sources, permanent))
            .ConfigureAwait(true);

        Log.Write(
            $"文件操作：{(permanent ? "永久删除" : "删除到回收站")} {sources.Count} 项"
            + $"（错误码 {result.ErrorCode}{(result.Canceled ? "，已取消" : string.Empty)}）");

        if (result.Success)
        {
            Completed?.Invoke(this, new FileOperationCompletedEventArgs
            {
                IsMove = false,
                IsDelete = true,
                SourcePaths = sources,
            });
        }

        return new FileOperationResult(result.Success, result.Canceled, result.ErrorCode);
    }

    /// <summary>进度对话框要有属主窗口才不会飘到别的窗口后面；没有主窗口时交给系统自己摆。</summary>
    private static IntPtr OwnerHandle
        => App.MainWindow is { } window ? WindowNative.GetWindowHandle(window) : IntPtr.Zero;

    /// <summary>在一条专用的 STA 线程上跑外壳文件操作；线程池线程是 MTA，用不了。</summary>
    private static Task<FileOperationInterop.Result> RunOnStaThread(Func<FileOperationInterop.Result> work)
    {
        var completion = new TaskCompletionSource<FileOperationInterop.Result>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "exdir-file-operation",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }
}
