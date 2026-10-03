using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Exdir.Services.Native;

/// <summary>
/// 随程序分发的 7z.dll（官方 7-Zip 的原生引擎，见 <c>native/README.md</c>）的最小**只读**互操作层。
///
/// 为什么自己写而不引 NuGet 包装包：exdir 只需要「打开 → 列举条目 → 解出条目（单个或一批）」，
/// 用到的接口只有 <c>IInArchive</c> 与打开/解压两个回调，自己写一层就没有额外的托管依赖要跟版本，
/// 也不影响裁剪（7z.dll 本来就是原生文件，见 <c>exdir.csproj</c>）。
///
/// 几个必须照着 7-Zip 源码来的细节：
/// <list type="bullet">
/// <item><c>7z.dll</c> 不是注册过的 COM 类，它导出的是普通 C 函数 <c>CreateObject(clsid, iid, out)</c>，
///       所以这里用 <see cref="NativeLibrary" /> 从 exe 目录显式加载（不走搜索路径，避免 DLL 劫持），
///       再用 <c>GetExport</c> 取函数指针；</item>
/// <item>格式表（扩展名 → 处理器）不写死：用 <c>GetNumberOfFormats</c> + <c>GetHandlerProperty2</c>
///       现读，升级 7z.dll 后新增的格式自动就有；</item>
/// <item>回调是**托管类实现 COM 接口**（CCW），依赖 <c>BuiltInComInteropSupport=true</c>
///       （exdir.csproj 里为裁剪已经开了）；</item>
/// <item><c>PROPVARIANT</c> 在 x64 上是 24 字节，不能按字段算大小 —— 见 AGENTS.md 第 6 节第 19 条。</item>
/// </list>
///
/// 线程：<c>IInArchive</c> 实例不是线程安全的，每次操作都重新 CreateObject + Open；
/// 静态状态只在第一次用时初始化一次。
/// </summary>
internal static class SevenZipInterop
{
    /// <summary><c>NExtract::NExtractMode::kExtract</c>：真正把数据解出来（不是 kTest / kSkip）。</summary>
    internal const int ExtractModeExtract = 0;

    internal const ushort VtEmpty = 0;
    internal const ushort VtBstr = 8;
    internal const ushort VtBool = 11;
    internal const ushort VtUi4 = 19;
    internal const ushort VtUi8 = 21;
    internal const ushort VtFileTime = 64;

    internal const int SFalse = 1;
    internal const int EAbort = unchecked((int)0x80004004);
    internal const int EFail = unchecked((int)0x80004005);

    /// <summary>第一次 <c>Open</c> 返回 S_FALSE（“像是我，但前置数据不够”）时允许扫描的最大字节数。</summary>
    private const ulong MaxCheckStartPositionRetry = 1u << 23; // 8 MB

    private const uint HandlerPropIdName = 0;
    private const uint HandlerPropIdClassId = 1;
    private const uint HandlerPropIdExtension = 2;

    /// <summary>
    /// <c>IID_IInArchive</c>。
    /// 注意 7-Zip 的接口 GUID 布局与“格式 CLSID”**不是**同一套（见 7-Zip 的 <c>IDecl.h</c>）：
    /// 接口是 <c>{23170F69-40C1-278A-0000-000G 00 SS 0000}</c>（G = 接口组，SS = 组内编号），
    /// 而格式 CLSID 是 <c>{23170F69-40C1-278A-1000-000110 ID 0000}</c>。
    /// 网上很多老例子用的 <c>...-1000-000110060000</c> 在 7-Zip 21.x 之后已经对不上了
    /// （<c>CreateObject</c> 会返回 E_NOINTERFACE）。
    /// </summary>
    private static readonly Guid IidIInArchive = new("23170F69-40C1-278A-0000-000600600000");

    private static readonly object Gate = new();
    private static bool _initialized;
    private static string _failure = string.Empty;
    private static IntPtr _module;
    private static CreateObjectDelegate? _createObject;
    private static GetNumberOfFormatsDelegate? _getNumberOfFormats;
    private static GetHandlerProperty2Delegate? _getHandlerProperty2;
    private static IReadOnlyList<SevenZipHandler> _handlers = Array.Empty<SevenZipHandler>();

    /// <summary>7z.dll 是否可用（缺失 / 加载失败时为 false，调用方退化成“双击交给默认程序”）。</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _createObject is not null;
        }
    }

    /// <summary>不可用的原因（只用于日志）。</summary>
    public static string FailureReason
    {
        get
        {
            EnsureInitialized();
            return _failure;
        }
    }

    /// <summary>7z.dll 支持的格式（名称 / CLSID / 扩展名列表）。</summary>
    public static IReadOnlyList<SevenZipHandler> Handlers
    {
        get
        {
            EnsureInitialized();
            return _handlers;
        }
    }

    private static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            // 显式从 exe 目录加载：既不受 DLL 搜索路径影响，也不必先 SetDllDirectory
            var path = Path.Combine(AppContext.BaseDirectory, "7z.dll");

            try
            {
                if (!File.Exists(path))
                {
                    _failure = $"找不到 {path}";
                    return;
                }

                if (!NativeLibrary.TryLoad(path, out _module))
                {
                    _failure = $"NativeLibrary.TryLoad 失败：{path}（多半是位数不对，exdir 只带 x64 的 7z.dll）";
                    return;
                }

                _createObject = GetExport<CreateObjectDelegate>("CreateObject");
                _getNumberOfFormats = GetExport<GetNumberOfFormatsDelegate>("GetNumberOfFormats");
                _getHandlerProperty2 = GetExport<GetHandlerProperty2Delegate>("GetHandlerProperty2");

                _handlers = ReadHandlers();
            }
            catch (Exception ex)
            {
                _failure = $"加载 7z.dll 失败：{ex.Message}";
                _createObject = null;
                _getNumberOfFormats = null;
                _getHandlerProperty2 = null;
                _handlers = Array.Empty<SevenZipHandler>();
                return;
            }

            if (_handlers.Count == 0)
            {
                _failure = "7z.dll 里一个格式处理器都没有（文件损坏或版本不匹配）";
            }
        }
    }

    private static T GetExport<T>(string name)
        where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

    /// <summary>现读 7z.dll 支持的格式表。</summary>
    private static IReadOnlyList<SevenZipHandler> ReadHandlers()
    {
        var list = new List<SevenZipHandler>();

        if (_getNumberOfFormats is null || _getHandlerProperty2 is null)
        {
            return list;
        }

        if (_getNumberOfFormats(out var count) != 0)
        {
            return list;
        }

        for (uint i = 0; i < count; i++)
        {
            var name = ReadHandlerString(i, HandlerPropIdName);
            var classId = ReadHandlerClassId(i);
            var extensions = ReadHandlerString(i, HandlerPropIdExtension);

            if (string.IsNullOrEmpty(name) || classId is null || extensions is null)
            {
                continue;
            }

            // kExtension 是空格分隔的裸扩展名（如 "zip zipx jar xpi"，也可能写成 ".zip"）
            var set = new List<string>();
            foreach (var raw in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var extension = raw.Trim().TrimStart('.');
                if (extension.Length > 0)
                {
                    set.Add(extension.ToLowerInvariant());
                }
            }

            list.Add(new SevenZipHandler(name, classId.Value, set));
        }

        return list;
    }

    /// <summary>
    /// 读格式的 CLSID。
    /// 注意 7-Zip 这里返回的 <c>VT_BSTR</c> 里装的**不是字符串，而是 16 字节的 GUID 原始内存**
    /// （按 BSTR 读会得到一串乱码 UTF-16），必须按字节取出来交给 <see cref="Guid(byte[])" />。
    /// </summary>
    private static Guid? ReadHandlerClassId(uint index)
    {
        if (_getHandlerProperty2 is null)
        {
            return null;
        }

        var variant = default(SevenZipPropVariant);

        try
        {
            if (_getHandlerProperty2(index, HandlerPropIdClassId, out variant) != 0
                || variant.VariantType != VtBstr
                || variant.PointerValue == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[16];
            Marshal.Copy(variant.PointerValue, bytes, 0, bytes.Length);
            return new Guid(bytes);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    private static string? ReadHandlerString(uint index, uint propId)
    {
        if (_getHandlerProperty2 is null)
        {
            return null;
        }

        var variant = default(SevenZipPropVariant);

        try
        {
            if (_getHandlerProperty2(index, propId, out variant) != 0)
            {
                return null;
            }

            return ReadBstr(ref variant);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    /// <summary>建一个格式处理器对象（<c>CreateObject</c>）；失败返回 null。</summary>
    public static IInArchive? CreateArchive(Guid classId, out string failure)
    {
        failure = string.Empty;
        EnsureInitialized();

        if (_createObject is null)
        {
            failure = $"7z.dll 不可用（{_failure}）";
            return null;
        }

        try
        {
            var iid = IidIInArchive;
            if (_createObject(ref classId, ref iid, out var archive) != 0 || archive is null)
            {
                failure = "CreateObject 没有返回 IInArchive";
                return null;
            }

            return archive;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}：{ex.Message}";
            return null;
        }
    }

    /// <summary>打开一个压缩包（<c>IInArchive.Open</c>），需要的话把密码交给回调。</summary>
    public static SevenZipArchive? TryOpen(
        string filePath,
        Guid classId,
        string? password,
        CancellationToken cancellationToken,
        out string failure)
    {
        failure = string.Empty;

        var archive = CreateArchive(classId, out failure);
        if (archive is null)
        {
            return null;
        }

        ManagedInStream? stream = null;
        var callback = new ManagedOpenCallback(password);

        try
        {
            stream = new ManagedInStream(File.Open(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete));

            cancellationToken.ThrowIfCancellationRequested();

            var maxCheck = 0ul;
            var hr = archive.Open(stream, ref maxCheck, callback);

            // S_FALSE = “格式像是我，但前置数据不够，再多给一点我就能确定”
            if (hr == SFalse)
            {
                maxCheck = MaxCheckStartPositionRetry;
                hr = archive.Open(stream, ref maxCheck, callback);
            }

            if (hr != 0)
            {
                failure = DescribeOpenFailure(hr, callback.AskedForPassword);
                return null;
            }

            if (archive.GetNumberOfItems(out var count) != 0)
            {
                failure = "GetNumberOfItems 失败";
                return null;
            }

            var result = new SevenZipArchive(archive, stream, count, filePath);
            stream = null;
            callback = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}：{ex.Message}";
            return null;
        }
        finally
        {
            if (stream is not null || callback is not null)
            {
                // 走到这里说明打开失败：把 native 那边交出来的东西放掉
                stream?.Dispose();
                callback?.Dispose();

                if (archive is not null)
                {
                    try
                    {
                        archive.Close();
                    }
                    catch (Exception)
                    {
                        // 已经失败在收拾现场了，Close 再抛就忽略
                    }

                    Marshal.ReleaseComObject(archive);
                }
            }
        }
    }

    private static string DescribeOpenFailure(int hr, bool askedForPassword)
        => askedForPassword
            ? "需要密码或密码错误"
            : hr switch
            {
                // S_FALSE = “这个格式处理器不是我”（我们把所有处理器都试了一遍）
                SFalse => "无法打开压缩包：格式不受支持或文件已损坏",
                EFail => "无法打开压缩包：格式不受支持或文件已损坏",
                EAbort => "无法打开压缩包：操作被取消或需要密码",
                unchecked((int)0x80070056) => "无法打开压缩包：密码错误",
                _ => $"无法打开压缩包（0x{hr:X8}）",
            };

    /// <summary>从 PROPVARIANT 里取 BSTR（不释放，调用方负责 <c>PropVariantClear</c>）。</summary>
    internal static string? ReadBstr(ref SevenZipPropVariant variant)
        => variant.VariantType == VtBstr && variant.PointerValue != IntPtr.Zero
            ? Marshal.PtrToStringBSTR(variant.PointerValue)
            : null;

    /// <summary>往 7-Zip 交出来的 <c>UInt32*</c> 里写值；指针允许为 <see cref="IntPtr.Zero" />。</summary>
    internal static void WriteUInt32(IntPtr target, uint value)
    {
        if (target != IntPtr.Zero)
        {
            Marshal.WriteInt32(target, unchecked((int)value));
        }
    }

    /// <summary>往 7-Zip 交出来的 <c>UInt64*</c> 里写值；指针允许为 <see cref="IntPtr.Zero" />。</summary>
    internal static void WriteUInt64(IntPtr target, ulong value)
    {
        if (target != IntPtr.Zero)
        {
            Marshal.WriteInt64(target, unchecked((long)value));
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateObjectDelegate(
        ref Guid classId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IInArchive outObject);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetNumberOfFormatsDelegate(out uint numFormats);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetHandlerProperty2Delegate(uint formatIndex, uint propId, out SevenZipPropVariant value);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref SevenZipPropVariant value);
}

/// <summary>7z.dll 支持的一种格式。</summary>
internal sealed record SevenZipHandler(string Name, Guid ClassId, IReadOnlyList<string> Extensions);

/// <summary>
/// PROPVARIANT 的最小可用视图：vt 在 0，联合体在 8（x86/x64 都是这个偏移）。
/// <c>Size = 24</c> 是**必须**的：x64 上它整整 24 字节，按字段算只有 16，
/// native 写回时会把栈踩坏（整个进程无日志猝死，见 AGENTS.md 第 6 节第 19 条）。
/// x86 上它是 16，给大一点无害。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct SevenZipPropVariant
{
    [FieldOffset(0)]
    public ushort VariantType;

    [FieldOffset(8)]
    public IntPtr PointerValue;

    [FieldOffset(8)]
    public uint UInt32Value;

    [FieldOffset(8)]
    public ulong UInt64Value;

    [FieldOffset(8)]
    public long FileTimeValue;

    [FieldOffset(8)]
    public short BoolValue;
}

// ---------------------------------------------------------------------------
// 7-Zip 的 COM 接口。vtable 顺序必须与 IArchive.h 完全一致（继承的槽位排在最前，
// 所以这里把 IProgress 的两个方法直接平铺在子接口里，避免再声明一层）。
// ---------------------------------------------------------------------------

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600600000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig]
    int Open(
        [MarshalAs(UnmanagedType.Interface)] IInStream stream,
        ref ulong maxCheckStartPosition,
        [MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback? openCallback);

    [PreserveSig]
    int Close();

    [PreserveSig]
    int GetNumberOfItems(out uint numItems);

    [PreserveSig]
    int GetProperty(uint index, uint propId, out SevenZipPropVariant value);

    [PreserveSig]
    int Extract(
        [MarshalAs(UnmanagedType.LPArray)] uint[]? indices,
        uint numItems,
        int testMode,
        [MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback extractCallback);

    [PreserveSig]
    int GetArchiveProperty(uint propId, out SevenZipPropVariant value);

    [PreserveSig]
    int GetNumberOfProperties(out uint numProperties);

    [PreserveSig]
    int GetPropertyInfo(uint index, [MarshalAs(UnmanagedType.BStr)] out string name, out uint propId, out ushort varType);

    [PreserveSig]
    int GetNumberOfArchiveProperties(out uint numProperties);

    [PreserveSig]
    int GetArchivePropertyInfo(uint index, [MarshalAs(UnmanagedType.BStr)] out string name, out uint propId, out ushort varType);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300010000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISequentialInStream
{
    [PreserveSig]
    int Read(IntPtr data, uint size, IntPtr processedSize);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300030000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInStream
{
    [PreserveSig]
    int Read(IntPtr data, uint size, IntPtr processedSize);

    [PreserveSig]
    int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300020000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISequentialOutStream
{
    [PreserveSig]
    int Write(IntPtr data, uint size, IntPtr processedSize);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300040000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOutStream
{
    [PreserveSig]
    int Write(IntPtr data, uint size, IntPtr processedSize);

    [PreserveSig]
    int Seek(long offset, uint seekOrigin, IntPtr newPosition);

    [PreserveSig]
    int SetSize(ulong newSize);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000000050000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IProgress
{
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveOpenCallback
{
    [PreserveSig]
    int SetTotal(IntPtr files, IntPtr bytes);

    [PreserveSig]
    int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000500100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICryptoGetTextPassword
{
    [PreserveSig]
    int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000600200000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IArchiveExtractCallback
{
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);

    [PreserveSig]
    int GetStream(
        uint index,
        [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? outStream,
        int askExtractMode);

    [PreserveSig]
    int PrepareOperation(int askExtractMode);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000500110000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICryptoGetTextPassword2
{
    [PreserveSig]
    int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string password);
}

// ---------------------------------------------------------------------------
// 托管回调实现（CCW）
// ---------------------------------------------------------------------------

/// <summary>
/// 把压缩包文件当只读流交给 7z.dll。
/// 同时实现 <see cref="IInStream" /> 与它的基接口 <see cref="ISequentialInStream" />：
/// 处理器既可能直接拿传进去的指针调方法，也可能 <c>QueryInterface</c>，两个 GUID 都在才不会扑空。
/// </summary>
[ComVisible(true)]
public sealed partial class ManagedInStream : IInStream, ISequentialInStream, IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];

    public ManagedInStream(FileStream stream) => _stream = stream;

    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        SevenZipInterop.WriteUInt32(processedSize, 0);

        try
        {
            var wanted = (int)Math.Min(size, (uint)_buffer.Length);
            var read = _stream.Read(_buffer, 0, wanted);

            if (read > 0)
            {
                Marshal.Copy(_buffer, 0, data, read);
                SevenZipInterop.WriteUInt32(processedSize, (uint)read);
            }

            return 0;
        }
        catch (Exception)
        {
            return SevenZipInterop.EFail;
        }
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            // STREAM_SEEK_SET / CUR / END
            var origin = seekOrigin switch
            {
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => SeekOrigin.Begin,
            };

            SevenZipInterop.WriteUInt64(newPosition, (ulong)Math.Max(0, _stream.Seek(offset, origin)));
            return 0;
        }
        catch (Exception)
        {
            return SevenZipInterop.EFail;
        }
    }

    public void Dispose() => _stream.Dispose();
}
/// <summary>解压出来的字节写到目标文件（同时实现 <see cref="IOutStream" />，有的处理器会 Seek/SetSize）。</summary>
[ComVisible(true)]
public sealed partial class ManagedOutStream : IOutStream, ISequentialOutStream, IDisposable
{
    private readonly FileStream _stream;
    private readonly CancellationToken _cancellationToken;

    public ManagedOutStream(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = File.Create(path);
        _cancellationToken = cancellationToken;
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            var origin = seekOrigin switch
            {
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => SeekOrigin.Begin,
            };

            SevenZipInterop.WriteUInt64(newPosition, (ulong)Math.Max(0, _stream.Seek(offset, origin)));
            return 0;
        }
        catch (Exception)
        {
            return SevenZipInterop.EFail;
        }
    }

    public int SetSize(ulong newSize)
    {
        try
        {
            _stream.SetLength((long)Math.Min(newSize, long.MaxValue));
            return 0;
        }
        catch (Exception)
        {
            return SevenZipInterop.EFail;
        }
    }

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        // 一开始就把已写字节数置 0：出错了 7-Zip 也会读它
        SevenZipInterop.WriteUInt32(processedSize, 0);

        if (_cancellationToken.IsCancellationRequested)
        {
            return SevenZipInterop.EAbort;
        }

        var written = 0u;

        try
        {
            const int Chunk = 256 * 1024;
            var remaining = (int)size;
            var offset = 0;

            while (remaining > 0)
            {
                var take = Math.Min(remaining, Chunk);
                var buffer = new byte[take];
                Marshal.Copy(IntPtr.Add(data, offset), buffer, 0, take);
                _stream.Write(buffer, 0, take);
                offset += take;
                remaining -= take;
                written += (uint)take;
            }

            SevenZipInterop.WriteUInt32(processedSize, written);
            return 0;
        }
        catch (Exception)
        {
            SevenZipInterop.WriteUInt32(processedSize, written);
            return SevenZipInterop.EFail;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>打开阶段的回调：只用来回答密码问题（进度回调忽略）。</summary>
[ComVisible(true)]
public sealed partial class ManagedOpenCallback : IArchiveOpenCallback, IProgress, ICryptoGetTextPassword, ICryptoGetTextPassword2, IDisposable
{
    private readonly string? _password;

    public ManagedOpenCallback(string? password) => _password = password;

    /// <summary>7z.dll 有没有向我们要过密码（用来区分“格式不对”和“需要密码”）。</summary>
    public bool AskedForPassword { get; private set; }

    public int SetTotal(IntPtr files, IntPtr bytes) => 0;

    public int SetCompleted(IntPtr files, IntPtr bytes) => 0;

    int IProgress.SetTotal(ulong total) => 0;

    int IProgress.SetCompleted(IntPtr completeValue) => 0;

    public int CryptoGetTextPassword(out string password)
    {
        AskedForPassword = true;
        password = _password ?? string.Empty;

        // 没有密码可用时返回 E_ABORT，7-Zip 会就此停止并让我们去问用户
        return _password is null ? SevenZipInterop.EAbort : 0;
    }

    public int CryptoGetTextPassword2(out int passwordIsDefined, out string password)
    {
        AskedForPassword = true;

        if (_password is null)
        {
            passwordIsDefined = 0;
            password = string.Empty;
            return SevenZipInterop.EAbort;
        }

        passwordIsDefined = 1;
        password = _password;
        return 0;
    }

    public void Dispose()
    {
    }
}

/// <summary>解压阶段的回调：只为请求的那几个条目开输出流，并回答密码问题。</summary>
[ComVisible(true)]
public sealed partial class ManagedExtractCallback : IArchiveExtractCallback, IProgress, ICryptoGetTextPassword, ICryptoGetTextPassword2, IDisposable
{
    /// <summary>7-Zip 条目号 → 落盘路径；不在这张表里的条目（含目录）直接跳过。</summary>
    private readonly IReadOnlyDictionary<uint, string> _outputs;

    private readonly string? _password;
    private readonly CancellationToken _cancellationToken;

    private ManagedOutStream? _stream;

    public ManagedExtractCallback(
        IReadOnlyDictionary<uint, string> outputs,
        string? password,
        CancellationToken cancellationToken)
    {
        _outputs = outputs;
        _password = password;
        _cancellationToken = cancellationToken;
    }

    /// <summary>7z.dll 报出来的操作结果（非 0 表示这一个条目解压失败）。</summary>
    public int OperationResult { get; private set; }

    /// <summary>7z.dll 向我们要过密码（用来区分“需要密码”与“用户取消”）。</summary>
    public bool AskedForPassword { get; private set; }

    public int SetTotal(ulong total) => 0;

    public int SetCompleted(IntPtr completeValue) => 0;

    public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
    {
        outStream = null;

        if (askExtractMode != SevenZipInterop.ExtractModeExtract || !_outputs.TryGetValue(index, out var outputPath))
        {
            // 目录条目由调用方自己建（ManagedOutStream 也会补父目录），这里连流都不用开
            return 0;
        }

        if (_cancellationToken.IsCancellationRequested)
        {
            return SevenZipInterop.EAbort;
        }

        // 7-Zip 是一个条目一个条目串行处理的：上一份的输出流在这里就可以关掉，
        // 免得一次解几百个文件时把几百个文件句柄攒到解完才放
        _stream?.Dispose();
        _stream = null;

        try
        {
            _stream = new ManagedOutStream(outputPath, _cancellationToken);
            outStream = _stream;
            return 0;
        }
        catch (Exception)
        {
            return SevenZipInterop.EFail;
        }
    }

    public int PrepareOperation(int askExtractMode) => 0;

    public int SetOperationResult(int operationResult)
    {
        if (operationResult != 0)
        {
            OperationResult = operationResult;
        }

        return 0;
    }

    public int CryptoGetTextPassword2(out int passwordIsDefined, out string password)
    {
        AskedForPassword = true;

        // 没有可用密码就中止：调用方据此去问用户（返回空密码会让 7z 白白重试一遍）
        if (_password is null)
        {
            passwordIsDefined = 0;
            password = string.Empty;
            return SevenZipInterop.EAbort;
        }

        passwordIsDefined = 1;
        password = _password;
        return 0;
    }

    /// <summary>老版接口（只给密码串）：Zip 解码器问的是这一版，两个都实现才不会扑空。</summary>
    public int CryptoGetTextPassword(out string password)
    {
        AskedForPassword = true;

        if (_password is null)
        {
            password = string.Empty;
            return SevenZipInterop.EAbort;
        }

        password = _password;
        return 0;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

// ---------------------------------------------------------------------------
// 已打开的压缩包
// ---------------------------------------------------------------------------

/// <summary>一个压缩包条目（只取浏览需要的几个属性）。</summary>
internal readonly record struct SevenZipEntry(
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastWriteTime,
    bool Encrypted);

/// <summary>已打开的压缩包：查询条目 + 解出单个文件。用完必须 <see cref="Dispose" />。</summary>
internal sealed partial class SevenZipArchive : IDisposable
{
    /// <summary><c>IInArchive::GetProperty</c> 用的属性 ID（7-Zip 的 PropID.h）。</summary>
    private const uint PropIdPath = 3;
    private const uint PropIdIsDirectory = 6;
    private const uint PropIdSize = 7;
    private const uint PropIdLastWriteTime = 12;
    private const uint PropIdEncrypted = 15;

    /// <summary><c>NArchive::NExtract::NOperationResult::kWrongPassword</c>。</summary>
    private const int WrongPassword = 9;

    private readonly IInArchive _archive;
    private readonly ManagedInStream _stream;
    private readonly string _filePath;
    private bool _disposed;

    internal SevenZipArchive(IInArchive archive, ManagedInStream stream, uint itemCount, string filePath)
    {
        _archive = archive;
        _stream = stream;
        _filePath = filePath;
        ItemCount = itemCount;
    }

    public uint ItemCount { get; }

    public string FilePath => _filePath;

    /// <summary>读一个条目的元数据（路径 / 是否目录 / 大小 / 修改时间 / 加密）。</summary>
    /// <remarks>
    /// 路径**为空串是合法的**：单文件压缩器（gzip / xz …）根本不提供条目名，
    /// 内层文件名由调用方按压缩包名派生（见 <c>ArchiveFormats.DeriveSingleEntryName</c>）。
    /// </remarks>
    public SevenZipEntry? GetEntry(uint index)
    {
        var path = ReadString(index, PropIdPath) ?? string.Empty;

        var isDirectory = ReadBool(index, PropIdIsDirectory);
        var size = ReadUInt64(index, PropIdSize) ?? 0;
        var encrypted = ReadBool(index, PropIdEncrypted);
        var modified = ReadFileTime(index, PropIdLastWriteTime);

        return new SevenZipEntry(
            path.Replace('/', '\\'),
            isDirectory,
            isDirectory ? 0 : (long)Math.Min(size, long.MaxValue),
            modified,
            encrypted);
    }

    /// <summary>
    /// 把第 <paramref name="index" /> 个条目解到 <paramref name="outputPath" />
    /// （父目录自动创建，同名覆盖）。失败返回 false，<paramref name="failure" /> 写明原因。
    /// </summary>
    public bool ExtractToFile(
        uint index,
        string outputPath,
        string? password,
        CancellationToken cancellationToken,
        out string failure)
        => ExtractFiles(
            new Dictionary<uint, string> { [index] = outputPath },
            password,
            cancellationToken,
            out failure);

    /// <summary>
    /// 一次解出多个条目（7-Zip 条目号 → 落盘路径；父目录自动创建，同名覆盖）。
    /// 比逐个调 <see cref="ExtractToFile" /> 好：固实压缩包（7z / rar）里逐条解会把同一个数据块重解 N 遍。
    /// 失败返回 false，<paramref name="failure" /> 写明原因。
    /// </summary>
    public bool ExtractFiles(
        IReadOnlyDictionary<uint, string> outputs,
        string? password,
        CancellationToken cancellationToken,
        out string failure)
    {
        failure = string.Empty;

        if (outputs.Count == 0)
        {
            return true;
        }

        // 条目号按升序交给 7-Zip（固实包里顺序读最省事）
        var indices = outputs.Keys.ToArray();
        Array.Sort(indices);

        var callback = new ManagedExtractCallback(outputs, password, cancellationToken);

        try
        {
            var hr = _archive.Extract(indices, (uint)indices.Length, SevenZipInterop.ExtractModeExtract, callback);

            if (hr != 0)
            {
                // 7-Zip 在“要密码但没有”时会先问回调、再中止（E_ABORT）；
                // 有的处理器则直接把 NOperationResult::kWrongPassword(9) 当 HRESULT 返回
                failure = callback.AskedForPassword || hr == WrongPassword
                    ? "需要密码或密码错误"
                    : hr switch
                    {
                        SevenZipInterop.EAbort => "操作已取消",
                        SevenZipInterop.EFail => "解压失败（文件已加密或数据损坏）",
                        _ => $"解压失败（0x{hr:X8}）",
                    };

                return false;
            }

            if (callback.OperationResult != 0)
            {
                failure = callback.AskedForPassword || callback.OperationResult == WrongPassword
                    ? "需要密码或密码错误"
                    : $"解压失败（0x{callback.OperationResult:X8}）";
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
        finally
        {
            callback.Dispose();
        }
    }

    // ------------------------------------------------------------------ 属性

    private string? ReadString(uint index, uint propId)
    {
        var variant = default(SevenZipPropVariant);

        try
        {
            if (_archive.GetProperty(index, propId, out variant) != 0)
            {
                return null;
            }

            return SevenZipInterop.ReadBstr(ref variant);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    private ulong? ReadUInt64(uint index, uint propId)
    {
        var variant = default(SevenZipPropVariant);

        try
        {
            if (_archive.GetProperty(index, propId, out variant) != 0)
            {
                return null;
            }

            return variant.VariantType switch
            {
                SevenZipInterop.VtUi8 => variant.UInt64Value,
                SevenZipInterop.VtUi4 => variant.UInt32Value,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    private bool ReadBool(uint index, uint propId)
    {
        var variant = default(SevenZipPropVariant);

        try
        {
            if (_archive.GetProperty(index, propId, out variant) != 0)
            {
                return false;
            }

            return variant.VariantType switch
            {
                SevenZipInterop.VtBool => variant.BoolValue != 0,
                SevenZipInterop.VtUi4 => variant.UInt32Value != 0,
                _ => false,
            };
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    /// <summary>
    /// 修改时间。7z.dll 交出来的是 FILETIME（自 1601 起的 100ns，UTC）；
    /// 极端值（0 / 超出 DateTime 范围 / 1970 以前）一律当成“没有时间”，避免界面上出现 1601 年。
    /// </summary>
    private DateTimeOffset ReadFileTime(uint index, uint propId)
    {
        var variant = default(SevenZipPropVariant);

        try
        {
            if (_archive.GetProperty(index, propId, out variant) != 0
                || variant.VariantType != SevenZipInterop.VtFileTime)
            {
                return default;
            }

            var fileTime = variant.FileTimeValue;
            if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
            {
                return default;
            }

            var utc = DateTime.FromFileTimeUtc(fileTime);
            return utc.Year < 1980 ? default : new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime();
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _archive.Close();
        }
        catch (Exception)
        {
            // 关闭失败无所谓，下面照样释放引用
        }

        _stream.Dispose();

        try
        {
            Marshal.ReleaseComObject(_archive);
        }
        catch (Exception)
        {
            // 已经断开连接时 Release 会抛，忽略
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref SevenZipPropVariant value);
}
