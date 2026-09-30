using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Exdir.Services.Native;

/// <summary>
/// 剪贴板互操作：把“一批文件”按资源管理器用的那套系统格式放上剪贴板，也要能从别的程序手里读回来。
/// <para>
/// 用的是两条标准格式：<c>CF_HDROP</c>（文件列表）与 <c>CF_PREFERREDDROPEFFECT</c>
/// （一个 DWORD，1 = 复制、2 = 剪切）。资源管理器、7-Zip 等都用这一对格式，
/// 所以 exdir 与它们之间可以互相复制 / 剪切 / 粘贴。
/// </para>
/// <para>
/// 写入走 Win32 的 <c>SetClipboardData</c>：内存所有权交给系统，进程退出后内容仍然有效
/// （不必像 OLE 那样再调 <c>OleFlushClipboard</c>）。
/// 读取先走 Win32 的 <c>GetClipboardData</c>（延迟渲染的格式系统会替我们向所有者索要），
/// 拿不到再退回 OLE 的 <c>OleGetClipboard</c> + <c>IDataObject</c>
/// —— 有些程序只注册 OLE 数据对象，Win32 这一侧看不到格式。
/// </para>
/// 全部调用都在 UI 线程上（剪贴板属于当前线程的窗口站，跨线程读要自己 <c>OpenClipboard</c>，
/// 这里简单起见只在 UI 线程用）。
/// </summary>
internal static class ClipboardInterop
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const uint DROPFILES_HEADER_SIZE = 20; // DROPFILES 在 x86/x64 上都是 20 字节

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int x;
        public int y;
        public int fNC;
        public int fWide;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FORMATETC
    {
        public ushort cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STGMEDIUM
    {
        public uint tymed;
        public IntPtr unionmember;
        public IntPtr pUnkForRelease;
    }

    [ComImport]
    [Guid("0000010e-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataObject
    {
        // 只用到 GetData，但 vtable 的槽位必须按 COM 定义排下去（顺序错就会调错函数）
        void GetData(ref FORMATETC format, out STGMEDIUM medium);

        void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium);

        int QueryGetData(ref FORMATETC format);

        int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut);

        void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard(out IDataObject dataObject);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern int ReleaseStgMedium(ref STGMEDIUM medium);

    private static bool _oleReady;

    /// <summary>
    /// “复制 / 剪切”标志的剪贴板格式 id。
    /// 注意**不能**写死 <c>0x000C</c>：那是 <c>CF_WAVE</c>；
    /// “Preferred DropEffect” 是运行时注册的格式（外壳用 <c>RegisterClipboardFormat</c> 注册），
    /// 所以这里也按名字注册一次拿同一个 id。
    /// </summary>
    private static readonly uint PreferredDropEffectFormat = RegisterClipboardFormat("Preferred DropEffect");

    /// <summary>剪贴板上有没有文件列表（只查格式，不真读内容，用于菜单项的可用状态）。</summary>
    public static bool HasFileDrop()
    {
        try
        {
            return IsClipboardFormatAvailable(CF_HDROP);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把一批路径以 <c>CF_HDROP</c> + <c>CF_PREFERREDDROPEFFECT</c> 写入剪贴板。</summary>
    public static bool SetFileDrop(IReadOnlyList<string> paths, bool move)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var drop = BuildDropHandle(paths);
        if (drop == IntPtr.Zero)
        {
            return false;
        }

        var effect = AllocInt32(move ? DROPEFFECT_MOVE : DROPEFFECT_COPY);
        if (effect == IntPtr.Zero)
        {
            GlobalFree(drop);
            return false;
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(drop);
            GlobalFree(effect);
            return false;
        }

        try
        {
            EmptyClipboard();

            var ok = true;

            // SetClipboardData 成功之后内存归剪贴板所有，不能再 GlobalFree
            if (SetClipboardData(CF_HDROP, drop) == IntPtr.Zero)
            {
                GlobalFree(drop);
                ok = false;
            }

            if (SetClipboardData(PreferredDropEffectFormat, effect) == IntPtr.Zero)
            {
                GlobalFree(effect);
            }

            return ok;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>清空剪贴板（移动成功后调用，与资源管理器一样：剪切粘贴只生效一次）。</summary>
    public static void Clear()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return;
        }

        try
        {
            EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>读出剪贴板上的文件列表与“复制 / 剪切”标志；没有文件时返回 null。</summary>
    public static (List<string> Paths, bool Move)? GetFileDrop()
    {
        var fromClipboard = ReadWin32();
        if (fromClipboard is { Paths.Count: > 0 })
        {
            return fromClipboard;
        }

        return ReadOle();
    }

    // ------------------------------------------------------------------ 写入辅助

    private static IntPtr BuildDropHandle(IReadOnlyList<string> paths)
    {
        // DROPFILES 头 + 宽字符文件列表：每一项以 '\0' 结尾，整串再补一个 '\0'
        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var total = DROPFILES_HEADER_SIZE + list.Length;

        var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)total);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.StructureToPtr(
                new DROPFILES { pFiles = DROPFILES_HEADER_SIZE, fWide = 1 },
                pointer,
                fDeleteOld: false);

            Marshal.Copy(list, 0, pointer + (int)DROPFILES_HEADER_SIZE, list.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static IntPtr AllocInt32(uint value)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)sizeof(int));
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }

        Marshal.WriteInt32(pointer, unchecked((int)value));
        GlobalUnlock(handle);
        return handle;
    }

    // ------------------------------------------------------------------ 读取

    private static (List<string> Paths, bool Move)? ReadWin32()
    {
        if (!IsClipboardFormatAvailable(CF_HDROP) || !OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            var drop = GetClipboardData(CF_HDROP);
            if (drop == IntPtr.Zero)
            {
                return null;
            }

            var paths = ReadDropFiles(drop);
            return paths.Count == 0 ? null : (paths, ReadWin32MoveFlag());
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool ReadWin32MoveFlag()
    {
        var handle = GetClipboardData(PreferredDropEffectFormat);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return (unchecked((uint)Marshal.ReadInt32(pointer)) & DROPEFFECT_MOVE) != 0;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static (List<string> Paths, bool Move)? ReadOle()
    {
        try
        {
            EnsureOle();

            var hr = OleGetClipboard(out var data);
            if (hr != 0 || data is null)
            {
                return null;
            }

            try
            {
                var paths = ReadOleDropFiles(data);
                return paths.Count == 0 ? null : (paths, ReadOleMoveFlag(data));
            }
            finally
            {
                Marshal.ReleaseComObject(data);
            }
        }
        catch (Exception)
        {
            // OLE 没初始化 / 剪贴板所有者已经退出：当作剪贴板上没有文件
            return null;
        }
    }

    private static List<string> ReadOleDropFiles(IDataObject data)
    {
        var format = new FORMATETC
        {
            cfFormat = (ushort)CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED_HGLOBAL,
        };

        data.GetData(ref format, out var medium);

        if (medium.unionmember == IntPtr.Zero)
        {
            return new List<string>();
        }

        try
        {
            return ReadDropFiles(medium.unionmember);
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static bool ReadOleMoveFlag(IDataObject data)
    {
        var name = PreferredDropEffectFormat;
        if (name == 0)
        {
            return false;
        }

        var format = new FORMATETC
        {
            cfFormat = (ushort)name,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED_HGLOBAL,
        };

        data.GetData(ref format, out var medium);
        if (medium.unionmember == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // 这里是 CF_PREFERREDDROPEFFECT 的实际存储（一个 DWORD），不是 HDROP，要自己锁
            var pointer = GlobalLock(medium.unionmember);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return (unchecked((uint)Marshal.ReadInt32(pointer)) & DROPEFFECT_MOVE) != 0;
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>从一个 HDROP（也可能是 OLE 交出来的 HGLOBAL）读出文件名列表。</summary>
    private static List<string> ReadDropFiles(IntPtr drop)
    {
        var paths = new List<string>();

        var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFile(drop, i, null, 0);
            if (length == 0)
            {
                continue;
            }

            var buffer = new StringBuilder((int)length + 1);
            if (DragQueryFile(drop, i, buffer, (uint)buffer.Capacity) > 0)
            {
                paths.Add(buffer.ToString());
            }
        }

        return paths;
    }

    private static void EnsureOle()
    {
        if (_oleReady)
        {
            return;
        }

        // UI 线程通常已经被 WinUI 初始化过 OLE（返回 S_FALSE），重复调用无害；
        // 万一 OLE 不是这个线程的模式，也只是 OleGetClipboard 失败后退回 Win32 那条路
        OleInitialize(IntPtr.Zero);
        _oleReady = true;
    }
}
