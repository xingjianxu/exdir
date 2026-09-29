using System;
using System.Runtime.InteropServices;

namespace Exdir.Services.Native;

/// <summary>
/// 读取云文件同步状态所需的 Win32 / COM 互操作。
///
/// 云文件（OneDrive、WPS 云盘、坚果云等 CFAPI 同步提供程序）的状态由 Windows 属性系统提供：
/// <list type="bullet">
/// <item><c>System.StorageProviderState</c>（<c>PKEY_StorageProviderState</c>，UInt32）——
///       资源管理器“状态 / 可用性”列用的就是它，能区分“仅在云端 / 本机可用 / 始终保留 /
///       同步挂起 / 已排除”等；</item>
/// <item><c>System.FilePlaceholderStatus</c>（<c>PKEY_FilePlaceholderStatus</c>，UInt32）——占位符状态位，
///       老客户端不一定写 StorageProviderState，这时用它和文件属性位兜底。</item>
/// </list>
/// 两者都是“固有属性”（innate），读的是占位符元数据，不会触发云端下载。
/// PKEY 的定义见 Windows SDK 的 <c>propkey.h</c>（property system 里没有它们的 schema 页，
/// 属性名解析不一定成功，因此这里直接写死 FMTID/PID）。
/// </summary>
internal static class ShellPropertyStore
{
    /// <summary>PKEY_FilePlaceholderStatus：B2F9B9D6-FEC4-4DD5-94D7-8957488C807B / 2。</summary>
    private static readonly PropertyKey FilePlaceholderStatusKey =
        new(new Guid("B2F9B9D6-FEC4-4DD5-94D7-8957488C807B"), 2);

    /// <summary>PKEY_StorageProviderState：E77E90DF-6271-4F5B-834F-2DD1F245DDA4 / 3。</summary>
    private static readonly PropertyKey StorageProviderStateKey =
        new(new Guid("E77E90DF-6271-4F5B-834F-2DD1F245DDA4"), 3);

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private static bool _compatibilityModeApplied;

    /// <summary>
    /// 把本进程切到“暴露占位符”模式（PHCM_EXPOSE_PLACEHOLDERS）。
    /// 系统可能因为兼容性把某些进程的占位符伪装成普通文件（看不到 reparse / sparse / offline 位，
    /// 属性系统也可能给出固定的“同步挂起”），显式设置一次可以拿到真实状态。
    /// 这是幂等的，失败（老系统 / 导出被移除）不影响功能，只影响兜底判断的精度。
    /// </summary>
    public static void EnsurePlaceholdersExposed()
    {
        if (_compatibilityModeApplied)
        {
            return;
        }

        _compatibilityModeApplied = true;

        try
        {
            RtlSetProcessPlaceholderCompatibilityMode(PhcmExposePlaceholders);
        }
        catch (Exception)
        {
            // ntdll 导出不可用：忽略
        }
    }

    /// <summary>
    /// 一次性取出占位符状态位与同步提供程序状态。
    /// 返回 false 表示这个路径建不起属性存储（例如老客户端不支持、权限不足）。
    /// </summary>
    public static bool TryGetCloudStates(string path, out uint placeholderStatus, out uint providerState)
    {
        placeholderStatus = 0;
        providerState = 0;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        IPropertyStore? store = null;

        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store) < 0 || store is null)
            {
                return false;
            }

            // 两个键都从同一个属性存储里取：每次打开属性存储比读一个值贵得多
            var hasPlaceholder = TryGetUInt32(store, FilePlaceholderStatusKey, out placeholderStatus);
            var hasProviderState = TryGetUInt32(store, StorageProviderStateKey, out providerState);
            return hasPlaceholder || hasProviderState;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (store is not null)
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    private static bool TryGetUInt32(IPropertyStore store, PropertyKey key, out uint value)
    {
        value = 0;
        var variant = default(PropVariant);

        try
        {
            var localKey = key;
            if (store.GetValue(ref localKey, out variant) < 0)
            {
                return false;
            }

            switch (variant.VariantType)
            {
                case VariantTypeUi4:
                    value = variant.UInt32Value;
                    return true;

                case VariantTypeUi8:
                    // PKEY_StorageProviderStatus 是 UInt64，低 32 位即状态
                    value = (uint)variant.UInt64Value;
                    return true;

                default:
                    return false;
            }
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

    private const ushort VariantTypeUi4 = 19; // VT_UI4
    private const ushort VariantTypeUi8 = 21; // VT_UI8

    private const sbyte PhcmExposePlaceholders = 2;

    [DllImport("ntdll.dll")]
    private static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

    // CharSet.Unicode 让运行时自动解析到 SHGetPropertyStoreFromParsingNameW
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath,
        IntPtr pbc,
        uint flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;

        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>
    /// PROPVARIANT 的最小可用视图：vt 在 0，联合体在 8（x86/x64 都是这个偏移）。
    /// 只读 UI4/UI8，不涉及需要释放的指针型值。
    /// 注意 <c>Size = 24</c>：x64 上 PROPVARIANT 整整 24 字节（8 字节头 + 16 字节联合体），
    /// 按字段算只有 16 字节，native 写回时会把栈踩坏（表现为整个进程无日志猝死）。
    /// x86 上它是 16 字节，给大一点无害。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public uint UInt32Value;

        [FieldOffset(8)]
        public ulong UInt64Value;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }
}

