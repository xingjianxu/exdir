using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services.Native;

namespace Exdir.Services;

/// <inheritdoc cref="IEverythingSearchService" />
public sealed class EverythingSearchService : IEverythingSearchService
{
    private const uint FileAttributeHidden = 0x00000002;
    private const uint FileAttributeSystem = 0x00000004;

    private readonly IArchiveService _archive;

    // SDK 的搜索状态是进程级全局的一份，绝不能有两个查询交叉设置它
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _loggedAttributesMissing;

    // 可用性探一次就记住（含失败）。每次读都去 EnsureLoaded 会抢 EverythingInterop 的锁 ——
    // 那个锁在查询期间是持着的（一个 Everything 往返），而 SearchHint 这类绑定是在 UI 线程上读它。
    private bool? _available;
    private string? _unavailableReason;

    public EverythingSearchService(IArchiveService archive) => _archive = archive;

    public bool IsAvailable => Probe();

    public string? UnavailableReason => Probe() ? null : _unavailableReason;

    private bool Probe()
    {
        if (_available is null)
        {
            _available = EverythingInterop.EnsureLoaded(out var failure);
            _unavailableReason = _available.Value ? null : failure;
        }

        return _available.Value;
    }

    public async Task<EverythingSearchResult> SearchAsync(
        string text,
        string? directory,
        bool includeHidden,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!EverythingInterop.EnsureLoaded(out var failure))
        {
            return new EverythingSearchResult
            {
                Status = EverythingSearchStatus.NotAvailable,
                Message = failure,
            };
        }

        var query = EverythingQuery.Build(text, directory);
        var max = (uint)Math.Clamp(maxResults, 1, 100_000);

        var (raw, elapsed) = await RunQueryAsync(query, max, cancellationToken).ConfigureAwait(false);

        if (!raw.Available)
        {
            return new EverythingSearchResult
            {
                Status = EverythingSearchStatus.NotAvailable,
                Message = raw.Failure,
            };
        }

        if (!raw.Ok)
        {
            if (raw.Error == EverythingInterop.ErrorIpc)
            {
                Log.Write("Everything 搜索：Everything 客户端没有在运行");
                return new EverythingSearchResult
                {
                    Status = EverythingSearchStatus.NotRunning,
                    Message = "Everything 没有在运行：先启动 Everything（托盘里那个放大镜），再回来搜索。",
                };
            }

            Log.Write($"Everything 搜索失败：错误码 {raw.Error}，查询 {query}");
            return new EverythingSearchResult
            {
                Status = EverythingSearchStatus.Failed,
                Message = $"Everything 查询失败（错误码 {raw.Error}）。",
            };
        }

        if (!raw.AttributesAvailable && !includeHidden && !_loggedAttributesMissing)
        {
            _loggedAttributesMissing = true;
            Log.Write("Everything 搜索：结果里读不到文件属性（Everything 的“索引文件属性”没打开），"
                + "“显示隐藏文件”这一项对搜索结果不生效");
        }

        var entries = new List<FileSystemEntry>(raw.Hits.Count);
        foreach (var hit in raw.Hits)
        {
            // 属性读不到时（全是 0）绝不能当成“非隐藏”，否则打开“显示隐藏文件”也没有什么变化
            var hidden = raw.AttributesAvailable && (hit.Attributes & (FileAttributeHidden | FileAttributeSystem)) != 0;
            if (hidden && !includeHidden)
            {
                continue;
            }

            entries.Add(CreateEntry(hit));
        }

        var status = entries.Count > 0 ? EverythingSearchStatus.Ok : EverythingSearchStatus.NoResults;

        Log.Write($"Everything 搜索：{query} → {entries.Count} 项（Everything 共 {raw.TotalCount} 项），"
            + $"取回 {raw.Hits.Count} 项，耗时 {(long)elapsed.TotalMilliseconds} ms");

        return new EverythingSearchResult
        {
            Status = status,
            Entries = entries,
            TotalCount = raw.TotalCount,
            IsTruncated = raw.TotalCount > (uint)raw.Hits.Count,
        };
    }

    /// <inheritdoc />
    public async Task<bool> IsDirectoryIndexedAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !EverythingInterop.EnsureLoaded(out _))
        {
            return false;
        }

        var scope = EverythingQuery.BuildScope(directory);
        if (scope.Length == 0)
        {
            return false;
        }

        // 只要这个目录下**有任何一条**进了索引就算覆盖到了 —— 取 1 条就够，别把整棵子树拉回来
        var (raw, _) = await RunQueryAsync(scope, 1, cancellationToken).ConfigureAwait(false);
        var indexed = raw.Available && raw.Ok && raw.TotalCount > 0;

        Log.Write(raw.Ok
            ? $"Everything 索引覆盖检查：{scope} → 索引里共 {raw.TotalCount} 项（{(indexed ? "覆盖到了" : "没覆盖")}）"
            : $"Everything 索引覆盖检查：{scope} → 查询失败（错误码 {raw.Error}）");

        return indexed;
    }

    /// <summary>
    /// 真正的那一次 Everything 往返：同一把锁串行化 + 丢到线程池 + 计时。
    /// 搜索与“索引覆盖检查”共用（SDK 的搜索状态是进程级全局的一份，不能交叉设置）。
    /// </summary>
    private async Task<(EverythingQueryResult Raw, TimeSpan Elapsed)> RunQueryAsync(
        string query,
        uint maxResults,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Everything 的查询是同步阻塞的 SendMessage（客户端没运行时立刻返回错误码 2）。
            // 放到线程池上，UI 线程永远不会卡在这一步。
            var raw = await Task.Run(() => EverythingInterop.Query(query, maxResults), CancellationToken.None)
                .ConfigureAwait(false);

            stopwatch.Stop();
            return (raw, stopwatch.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private FileSystemEntry CreateEntry(EverythingHit hit) => new()
    {
        FullPath = hit.FullPath,
        Name = Path.GetFileName(hit.FullPath),
        IsDirectory = hit.IsDirectory,
        Size = hit.IsDirectory ? 0 : hit.Size,
        LastWriteTime = hit.LastWriteTime,
        CreationTime = hit.CreationTime == default ? hit.LastWriteTime : hit.CreationTime,
        TypeName = FileTypeHelper.GetTypeName(hit.FullPath, hit.IsDirectory),
        IsHidden = (hit.Attributes & (FileAttributeHidden | FileAttributeSystem)) != 0,

        // 搜索结果里的压缩包仍然是压缩包：行首照样能就地展开（见 AGENTS.md 第 4 节）
        IsArchive = !hit.IsDirectory && _archive.IsArchiveFile(hit.FullPath),
    };
}
