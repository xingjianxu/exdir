using System;
using System.Runtime.InteropServices;

namespace Exdir.Services.Native;

/// <summary>
/// DPAPI（<c>CryptProtectData</c> / <c>CryptUnprotectData</c>）：用**当前 Windows 用户**的凭据
/// 加密一小段数据，只有同一个用户在同一台机器上解得开。
///
/// <para>
/// 为什么不用 <c>System.Security.Cryptography.ProtectedData</c>：那个类型在 net8.0 下要靠一个额外的
/// NuGet 包（<c>System.Security.Cryptography.ProtectedData</c>）才拿得到，而这里只需要两个 P/Invoke ——
/// 与仓库里其它 Win32 互操作一样放在 <c>Native/</c> 下，既少一个依赖，也少一个裁剪要照顾的程序集。
/// </para>
///
/// <para>
/// 坑：`DATA_BLOB` 在 x64 上不是“4 字节长度 + 指针”紧挨着那么简单 —— 托管侧用
/// <c>[StructLayout(LayoutKind.Sequential)]</c> 的 <c>int + IntPtr</c> 正好对上 C 的
/// <c>DWORD cbData; BYTE* pbData;</c>（结构体本身按指针宽度对齐），所以这里照着声明就行；
/// 真正要小心的是**输入缓冲区必须是原生的**（<c>Marshal.AllocHGlobal</c>），
/// 不能把托管数组的地址塞给系统（GC 随时可能搬走它）。
/// </para>
/// </summary>
internal static class DpapiInterop
{
    /// <summary>不弹任何 UI（我们总是在后台线程调用，弹窗会跑到别的桌面上去）。</summary>
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>加密；失败返回 null（调用方只记日志）。</summary>
    public static byte[]? Protect(byte[] plain)
    {
        if (plain.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var input = ToBlob(plain);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                return null;
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Free(input);
        }
    }

    /// <summary>解密；失败（换了用户 / 换了机器 / 数据坏了）返回 null。</summary>
    public static byte[]? Unprotect(byte[] encrypted)
    {
        if (encrypted.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var input = ToBlob(encrypted);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                return null;
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Free(input);
        }
    }

    /// <summary>把托管字节复制进原生内存（系统会读写这块内存，必须是原生分配）。</summary>
    private static DataBlob ToBlob(byte[] bytes)
    {
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);

        return new DataBlob { Size = bytes.Length, Data = buffer };
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[blob.Size];
        Marshal.Copy(blob.Data, result, 0, blob.Size);
        return result;
    }

    private static void Free(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }
}
