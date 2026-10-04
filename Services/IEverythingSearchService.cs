using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>一次搜索的结局。</summary>
public enum EverythingSearchStatus
{
    /// <summary>查到了，而且有结果。</summary>
    Ok,

    /// <summary>查到了，但一个都没匹配上。</summary>
    NoResults,

    /// <summary>本机找不到 Everything64.dll（没装 Everything、也没把 DLL 放到 exe 旁边）。</summary>
    NotAvailable,

    /// <summary>DLL 在，但 Everything 客户端没在运行 —— SDK 是 IPC 客户端，必须有客户端进程。</summary>
    NotRunning,

    /// <summary>其它失败（查询出错、DLL 版本不匹配…）。</summary>
    Failed,
}

/// <summary>一次搜索的结果集。</summary>
public sealed class EverythingSearchResult
{
    public required EverythingSearchStatus Status { get; init; }

    /// <summary>已取回的条目（最多 <c>EverythingQuery.MaxResults</c> 条）。</summary>
    public IReadOnlyList<FileSystemEntry> Entries { get; init; } = System.Array.Empty<FileSystemEntry>();

    /// <summary>Everything 报的匹配总数（含被截断、以及被“隐藏文件”过滤掉的那些）。</summary>
    public uint TotalCount { get; init; }

    /// <summary>匹配总数比取回的多（结果被截断了）。</summary>
    public bool IsTruncated { get; init; }

    /// <summary>给用户看的原因（<see cref="EverythingSearchStatus.Failed" /> 等）。</summary>
    public string? Message { get; init; }
}

/// <summary>
/// 「基于 Everything 的快速搜索」（见 AGENTS.md 第 4 节）。
/// 只读、只查：不写 Everything 的索引，也不做任何文件操作。
/// </summary>
public interface IEverythingSearchService
{
    /// <summary>Everything64.dll 找得到并加载得起来（不代表客户端在运行）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用的原因（可用时为 null）。</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// 查询。<paramref name="directory" /> 为 null / 空 = 整机范围，否则限定在该目录（含子目录）。
    /// <paramref name="includeHidden" /> 为 false 时按 Everything 报的文件属性滤掉隐藏 / 系统项
    /// （Everything 没索引属性时这一项不起作用，日志里会留一行）。
    /// </summary>
    Task<EverythingSearchResult> SearchAsync(
        string text,
        string? directory,
        bool includeHidden,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything 的索引里有没有这个目录：用来区分“当前目录范围一个都没搜到”到底是
    /// **索引没覆盖这个目录**，还是这个目录里真的没有匹配。
    ///
    /// 做法是只问范围限定词（不带关键字）：索引里有任何一条这个目录下的条目就算覆盖到了。
    /// 只装/只启用了「文件夹索引」的 Everything 并不索引整块磁盘，这时它会是 false
    /// （见 AGENTS.md 第 4 节“Everything 快速搜索”）。
    /// </summary>
    Task<bool> IsDirectoryIndexedAsync(string directory, CancellationToken cancellationToken = default);
}
