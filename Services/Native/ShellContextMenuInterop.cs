using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Exdir.Services.Native;

/// <summary>
/// 托管“系统右键菜单”所需的 Win32 / COM 互操作。
///
/// 资源管理器的右键菜单来自外壳的 <c>IContextMenu</c>：外壳把
/// <c>IShellFolder.GetUIObjectOf</c>（选中项）或 <c>IShellFolder.CreateViewObject</c>（目录背景）
/// 拿到的对象 QI 成 <c>IContextMenu</c>，让它把菜单项 <c>QueryContextMenu</c> 加进一张空菜单，
/// 再把这张 <c>HMENU</c> 交给 <c>TrackPopupMenu</c>；用户选中哪一项会以命令 id 的形式返回，
/// 最后用 <c>IContextMenu::InvokeCommand</c> 把偏移（id − idCmdFirst）交给外壳执行。
///
/// 第三条路（把菜单项读出来自己用 WinUI 重画）虽然好看，但拿不到 <c>WM_INITMENUPOPUP</c> 那类
/// 运行时才填内容的子菜单、也没法让 owner-draw 项自己画自己，所以这里老老实实弹出真菜单：
/// 第三方扩展（7-Zip / Git / 杀软）与“发送到”“打开方式”全都原样可用。
/// </summary>
internal static class ShellContextMenuInterop
{
    // ---------------------------------------------------------------- 常量

    /// <summary>RemoveMenu / GetMenuItemInfo 的“按位置”标志。</summary>
    public const uint MfByPosition = 0x00000400;

    /// <summary>MENUITEMINFO.fType 里的分隔符位。</summary>
    public const uint MfSeparator = 0x00000800;

    public const uint MiimId = 0x00000002;
    public const uint MiimSubMenu = 0x00000004;
    public const uint MiimFType = 0x00000100;

    /// <summary>GetCommandString 的“要规范动词（Unicode）”标志。</summary>
    public const uint GcsVerbW = 0x00000004;

    public const uint TpmRightButton = 0x0002;
    public const uint TpmReturnCmd = 0x0100;

    public const uint CmicMaskUnicode = 0x00004000;
    public const uint CmicMaskPtInvoke = 0x20000000;

    public const uint WmNull = 0x0000;
    public const uint WmDrawItem = 0x002B;
    public const uint WmMeasureItem = 0x002C;
    public const uint WmInitMenuPopup = 0x0117;

    /// <summary>DRAWITEMSTRUCT / MEASUREITEMSTRUCT 的第一个字段（CtlType），ODT_MENU = 1。</summary>
    private const int OdtMenu = 1;

    public const int SwShowNormal = 1;

    public static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IidContextMenu = new("000214E4-0000-0000-C000-000000000046");

    // ---------------------------------------------------------------- 结构体

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// MENUITEMINFO 的“只读几项”视图：只要 id / 子菜单 / 类型时不需要字符串字段，
    /// 因此 <c>dwTypeData</c> 保持 IntPtr.Zero（文本另外用 GetMenuString 取，省得折腾缓冲区）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    /// <summary>
    /// CMINVOKECOMMANDINFOEX（末尾比 ANSI 版多一个 POINT）。
    /// 用 <c>lpVerb = (IntPtr)偏移</c>（即 MAKEINTRESOURCE）而不是动词字符串：
    /// 地址栏那套“把菜单项读出来再执行”的路径不需要，直接按索引最省事。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public int ptInvokeX;
        public int ptInvokeY;
    }

    // ---------------------------------------------------------------- COM 接口

    /// <summary>只声明到 <c>GetUIObjectOf</c> 为止用得到的方法；vtable 顺序必须与 native 完全一致。</summary>
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr hwnd,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten,
            out IntPtr ppidl,
            ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner,
            uint cidl,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
            ref Guid riid,
            IntPtr rgfReserved,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        [PreserveSig]
        int SetNameOf(
            IntPtr hwnd,
            IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags,
            out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(
            IntPtr idCmd,
            uint uType,
            IntPtr pReserved,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            uint cchMax);
    }

    /// <summary>C# 里 COM 接口的“继承”不会重复基类声明，所以 IContextMenu 的三个方法要再写一遍。</summary>
    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(
            IntPtr idCmd,
            uint uType,
            IntPtr pReserved,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCEF0396-2AD7-41C4-A2B0-91E9D0B8CFA7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(
            IntPtr idCmd,
            uint uType,
            IntPtr pReserved,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    public static extern int SHBindToParent(
        IntPtr pidl,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv,
        out IntPtr ppidlLast);

    /// <summary>从桌面（psf 传 null）开始把绝对 pidl 绑定成某个接口（这里用来拿“这个目录本身”的 IShellFolder）。</summary>
    [DllImport("shell32.dll")]
    public static extern int SHBindToObject(
        IntPtr psf,
        IntPtr pidl,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int cchMax, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMenuItemInfo(IntPtr hMenu, uint uItem, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    // ---------------------------------------------------------------- 窗口子类化（转发菜单消息）

    public delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    /// <summary>判断 owner-draw 消息是不是菜单发出来的（CtlType 是结构体的第一个字段）。</summary>
    public static bool IsMenuDrawMessage(IntPtr lParam)
    {
        try
        {
            return lParam != IntPtr.Zero && Marshal.ReadInt32(lParam) == OdtMenu;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 菜单弹出期间挂在宿主窗口上的“消息转发器”。
    ///
    /// 有些外壳扩展要等子菜单即将弹出时才知道该往里塞什么（<c>WM_INITMENUPOPUP</c>，
    /// 典型是“打开方式”），还有些扩展的菜单项是 owner-draw 的（<c>WM_DRAWITEM</c> /
    /// <c>WM_MEASUREITEM</c>）。这些消息系统会发给菜单的宿主窗口，宿主必须转交给
    /// <c>IContextMenu2/3</c>，否则子菜单会是空的、owner-draw 项会画成空白。
    /// 只在 TrackPopupMenu 那一小段时间里挂着，弹完就摘掉。
    /// </summary>
    internal sealed class ShellMenuHost : IDisposable
    {
        private static readonly IntPtr SubclassId = new(0x6578 /* "ex" */);

        private readonly IntPtr _hwnd;
        private readonly IContextMenu2? _menu2;
        private readonly IContextMenu3? _menu3;
        private readonly SubclassProc _proc;
        private bool _installed;

        public ShellMenuHost(IntPtr hwnd, IContextMenu contextMenu)
        {
            _hwnd = hwnd;
            _proc = OnMessage;

            _menu3 = contextMenu as IContextMenu3;
            _menu2 = contextMenu as IContextMenu2;

            if (_menu2 is null && _menu3 is null)
            {
                return;
            }

            try
            {
                _installed = SetWindowSubclass(_hwnd, _proc, SubclassId, IntPtr.Zero);
            }
            catch (Exception)
            {
                _installed = false;
            }
        }

        private IntPtr OnMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            try
            {
                switch (uMsg)
                {
                    case WmInitMenuPopup:
                        if (Forward(uMsg, wParam, lParam, out var initResult))
                        {
                            return initResult;
                        }

                        break;

                    case WmDrawItem:
                    case WmMeasureItem:
                        if (IsMenuDrawMessage(lParam) && Forward(uMsg, wParam, lParam, out var drawResult))
                        {
                            return drawResult;
                        }

                        break;
                }
            }
            catch (Exception)
            {
                // 转发失败就退回默认处理，不能让菜单弹不出来
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private bool Forward(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;

            if (_menu3 is not null && _menu3.HandleMenuMsg2(msg, wParam, lParam, out result) >= 0)
            {
                return true;
            }

            if (_menu2 is not null && _menu2.HandleMenuMsg(msg, wParam, lParam) >= 0)
            {
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (!_installed)
            {
                return;
            }

            _installed = false;

            try
            {
                RemoveWindowSubclass(_hwnd, _proc, SubclassId);
            }
            catch (Exception)
            {
                // 窗口已经销毁时也就算了
            }
        }
    }
}
