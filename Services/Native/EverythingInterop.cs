using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Exdir.Helpers;

namespace Exdir.Services.Native;

/// <summary>一次 Everything 查询的原样结果（还没变成 <c>FileSystemEntry</c>）。</summary>
public sealed class EverythingQueryResult
{
    /// <summary>Everything64.dll 找得到并且加载起来了。</summary>
    public bool Available { get; init; }

    /// <summary>加载失败的原因（没找到 DLL / 位数不对 / 缺导出）；成功时为 null。</summary>
    public string? Failure { get; init; }

    /// <summary><c>Everything_QueryW</c> 返回 TRUE。</summary>
    public bool Ok { get; init; }

    /// <summary><c>Everything_GetLastError</c>；<see cref="EverythingInterop.ErrorIpc" /> = 客户端没在运行。</summary>
    public uint Error { get; init; }

    /// <summary>匹配总数（不受本次取回上限限制）：界面上显示“前 N 项 / 共 M 项”。</summary>
    public uint TotalCount { get; init; }

    /// <summary>
    /// 结果里真的读到了文件属性（有任意一条的属性不为 0）。
    /// Everything 只有在“索引文件属性”打开时才报属性；关着的话可能全部是 0，
    /// 那时就不能拿它判断隐藏项（否则会把所有结果都当成非隐藏）。
    /// 注意不能只看“目录结果带 DIRECTORY 位”：同一条查询完全可能只命中文件。
    /// </summary>
    public bool AttributesAvailable { get; init; }

    public IReadOnlyList<EverythingHit> Hits { get; init; } = Array.Empty<EverythingHit>();
}

/// <summary>Everything 结果里的一项。</summary>
public readonly record struct EverythingHit(
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTime,
    DateTime CreationTime,
    uint Attributes);

/// <summary>
/// Everything SDK（<c>Everything64.dll</c>）的互操作层：只负责“调 Win32 导出 + 把结果读出来”。
///
/// 与 <see cref="SevenZipInterop" /> 同一套路：用 <c>NativeLibrary.TryLoad</c> 显式从**我们找到的那个路径**
/// 加载，再用 <c>Marshal.GetDelegateForFunctionPointer</c> 取导出 —— 不用 <c>[DllImport]</c>，
/// 因为 DLL 的位置是运行期探测出来的（exe 旁边 / 注册表 / 常见目录），
/// 而且这样“找不到 DLL”只是 TryLoad 返回 false，不会在第一次调用时抛异常。
///
/// 线程模型：SDK 的搜索状态是**进程级全局**的一份，两个线程同时设置就会互相覆盖，
/// 所以这里所有导出都在同一把锁里调用（服务层另有自己的串行化）。
/// Everything 自己会开一个线程 + 消息窗口来收结果，所以调用方可以是任意线程（包括线程池）。
/// </summary>
public static class EverythingInterop
{
    // ---- include/Everything.h 里的常量（改 SDK 时对着那份头文件复核） ----

    /// <summary>Everything 客户端没在运行。</summary>
    public const uint ErrorIpc = 2;

    public const uint RequestFileName = 0x00000001;
    public const uint RequestPath = 0x00000002;
    public const uint RequestSize = 0x00000010;
    public const uint RequestDateCreated = 0x00000020;
    public const uint RequestDateModified = 0x00000040;
    public const uint RequestAttributes = 0x00000100;

    /// <summary>EVERYTHING_SORT_NAME_ASCENDING：按名称升序（默认排序，保证结果集稳定、可截断）。</summary>
    public const uint SortNameAscending = 1;

    private const uint PathBufferLength = 1024;
    private const uint LongPathBufferLength = 32768;

    private static readonly object Gate = new();

    private static bool _initialized;
    private static IntPtr _module;
    private static string? _failure;

    private static SetSearchWDelegate? _setSearchW;
    private static SetBoolDelegate? _setMatchCase;
    private static SetBoolDelegate? _setMatchPath;
    private static SetBoolDelegate? _setRegex;
    private static SetUIntDelegate? _setMax;
    private static SetUIntDelegate? _setRequestFlags;
    private static SetUIntDelegate? _setSort;
    private static QueryDelegate? _query;
    private static GetUIntDelegate? _getLastError;
    private static GetUIntDelegate? _getNumResults;
    private static GetUIntDelegate? _getTotResults;
    private static GetFullPathDelegate? _getResultFullPathNameW;
    private static IsFolderDelegate? _isFolderResult;
    private static GetSizeDelegate? _getResultSize;
    private static GetFileTimeDelegate? _getResultDateModified;
    private static GetFileTimeDelegate? _getResultDateCreated;
    private static GetIndexedUIntDelegate? _getResultAttributes;
    private static ResetDelegate? _reset;
    private static GetUIntDelegate? _getMajorVersion;

    /// <summary>
    /// 确保 DLL 加载并绑好导出；失败时给出人话原因。
    /// 结果（含失败）只算一次 —— 每个按键都去翻注册表 / 加载 DLL 不值当。
    /// </summary>
    public static bool EnsureLoaded(out string? failure)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                _initialized = true;
                Load();
            }

            failure = _failure;
            return _failure is null;
        }
    }

    /// <summary>Everything 的大版本号（1.4 / 1.5）；没加载起来时返回 0。</summary>
    public static uint MajorVersion
    {
        get
        {
            lock (Gate)
            {
                return _getMajorVersion?.Invoke() ?? 0;
            }
        }
    }

    /// <summary>
    /// 执行一次同步查询（Everything 在本机时通常 1~10 ms；客户端没运行时**立刻**返回错误码 2）。
    /// 调用方负责放在后台线程上，别在 UI 线程里等。
    /// </summary>
    public static EverythingQueryResult Query(string query, uint maxResults)
    {
        if (!EnsureLoaded(out var failure))
        {
            return new EverythingQueryResult { Available = false, Failure = failure };
        }

        lock (Gate)
        {
            // 全局状态：先清干净，再逐项设置（不依赖上一次查询留下的任何东西）
            _reset!();
            _setSearchW!(query);
            _setMatchCase!(false);
            _setMatchPath!(false);
            _setRegex!(false);
            _setMax!(maxResults);
            _setSort!(SortNameAscending);
            _setRequestFlags!(
                RequestFileName | RequestPath | RequestSize | RequestDateCreated | RequestDateModified | RequestAttributes);

            var ok = _query!(true);
            var error = _getLastError!();

            var hits = new List<EverythingHit>();
            var attributesAvailable = false;

            for (uint i = 0; ok && i < _getNumResults!(); i++)
            {
                var fullPath = ReadFullPath(i);
                if (string.IsNullOrEmpty(fullPath))
                {
                    continue;
                }

                var isDirectory = _isFolderResult!(i);
                var attributes = _getResultAttributes!(i);

                if (attributes != 0)
                {
                    attributesAvailable = true;
                }

                long size = 0;
                if (!isDirectory)
                {
                    try
                    {
                        _getResultSize!(i, out size);
                    }
                    catch (Exception)
                    {
                        // 属性没索引时读不到大小：保持 0，不影响其余列
                    }
                }

                hits.Add(new EverythingHit(
                    fullPath,
                    isDirectory,
                    size,
                    ReadFileTime(_getResultDateModified!, i),
                    ReadFileTime(_getResultDateCreated!, i),
                    attributes));
            }

            return new EverythingQueryResult
            {
                Available = true,
                Ok = ok,
                Error = error,
                TotalCount = ok ? _getTotResults!() : 0,
                AttributesAvailable = attributesAvailable,
                Hits = hits,
            };
        }
    }

    private static void Load()
    {
        var path = EverythingLocator.DllPath;
        if (path is null)
        {
            _failure = $"没有找到 {EverythingLocator.DllName}（Everything SDK 的 IPC 客户端）；"
                + "可以装一份 Everything（https://www.voidtools.com/），或把 DLL 放到 exe 旁边";
            return;
        }

        try
        {
            if (!NativeLibrary.TryLoad(path, out _module))
            {
                _failure = $"加载 {EverythingLocator.DllName} 失败：{path}（多半是位数不对，exdir 只带 x64 的那一份）";
                return;
            }

            _setSearchW = Export<SetSearchWDelegate>("Everything_SetSearchW");
            _setMatchCase = Export<SetBoolDelegate>("Everything_SetMatchCase");
            _setMatchPath = Export<SetBoolDelegate>("Everything_SetMatchPath");
            _setRegex = Export<SetBoolDelegate>("Everything_SetRegex");
            _setMax = Export<SetUIntDelegate>("Everything_SetMax");
            _setRequestFlags = Export<SetUIntDelegate>("Everything_SetRequestFlags");
            _setSort = Export<SetUIntDelegate>("Everything_SetSort");
            _query = Export<QueryDelegate>("Everything_QueryW");
            _getLastError = Export<GetUIntDelegate>("Everything_GetLastError");
            _getNumResults = Export<GetUIntDelegate>("Everything_GetNumResults");
            _getTotResults = Export<GetUIntDelegate>("Everything_GetTotResults");
            _getResultFullPathNameW = Export<GetFullPathDelegate>("Everything_GetResultFullPathNameW");
            _isFolderResult = Export<IsFolderDelegate>("Everything_IsFolderResult");
            _getResultSize = Export<GetSizeDelegate>("Everything_GetResultSize");
            _getResultDateModified = Export<GetFileTimeDelegate>("Everything_GetResultDateModified");
            _getResultDateCreated = Export<GetFileTimeDelegate>("Everything_GetResultDateCreated");
            _getResultAttributes = Export<GetIndexedUIntDelegate>("Everything_GetResultAttributes");
            _reset = Export<ResetDelegate>("Everything_Reset");
            _getMajorVersion = Export<GetUIntDelegate>("Everything_GetMajorVersion");
        }
        catch (Exception ex)
        {
            _failure = $"{EverythingLocator.DllName} 里缺少需要的导出（版本太旧或文件损坏）：{ex.Message}";
        }
    }

    private static T Export<T>(string name)
        where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

    private static string ReadFullPath(uint index)
    {
        var buffer = new StringBuilder((int)PathBufferLength);
        var length = _getResultFullPathNameW!(index, buffer, PathBufferLength);

        // 超长路径（>260）：返回值达到缓冲区上限就换个大缓冲再来一次
        if (length >= PathBufferLength - 1)
        {
            buffer = new StringBuilder((int)LongPathBufferLength);
            _getResultFullPathNameW(index, buffer, LongPathBufferLength);
        }

        return buffer.ToString();
    }

    private static DateTime ReadFileTime(GetFileTimeDelegate read, uint index)
    {
        try
        {
            if (!read(index, out var fileTime) || fileTime <= 0)
            {
                return default;
            }

            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (Exception)
        {
            // Everything 没索引这个时间：留默认值
            return default;
        }
    }

    // ------------------------------------------------------------------ 导出签名（见 SDK 的 include/Everything.h）

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate void SetSearchWDelegate(string text);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetBoolDelegate([MarshalAs(UnmanagedType.Bool)] bool enable);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetUIntDelegate(uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool QueryDelegate([MarshalAs(UnmanagedType.Bool)] bool wait);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetUIntDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate uint GetFullPathDelegate(uint index, [Out] StringBuilder buffer, uint bufferLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool IsFolderDelegate(uint index);

    /// <summary>LARGE_INTEGER* 在 x64 上就是 8 字节，直接出 <c>long</c>。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetSizeDelegate(uint index, out long size);

    /// <summary>FILETIME* 也是 8 字节（低 DWORD 在前），按 <c>long</c> 读正好是那个 64 位时间值。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetFileTimeDelegate(uint index, out long fileTime);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetIndexedUIntDelegate(uint index);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ResetDelegate();
}
