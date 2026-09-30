using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Exdir.Services.Native;

/// <summary>
/// 解析 <c>.lnk</c> 快捷方式的目标路径（<c>IShellLinkW</c> + <c>IPersistFile</c>）。
/// Windows 的「网络位置」就是一个快捷方式容器目录：<c>%APPDATA%\Microsoft\Windows\Network Shortcuts</c>
/// 下的每一项都带一个隐藏的 <c>target.lnk</c>，这个快捷方式的目标（UNC 或本地路径）才是要打开的位置。
/// </summary>
internal static class ShellLinkInterop
{
    /// <summary>SLGP_UNCPRIORITY：目标存的是映射盘符时优先返回 UNC 路径。</summary>
    private const uint SlgpUncPriority = 0x0002;

    /// <summary>STGM_READ：只读打开，不改动快捷方式。</summary>
    private const uint StgmRead = 0;

    /// <summary>目标路径缓冲区大小。MAX_PATH 在长路径场景下不够，给宽一点。</summary>
    private const int MaxPath = 1024;

    /// <summary>
    /// 读快捷方式的目标路径；文件不存在、不是快捷方式、或链接指向 shell 虚拟项（没有文件路径）时返回 null。
    /// </summary>
    public static string? ResolveTarget(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return null;
        }

        object? shellLink = null;
        try
        {
            shellLink = new ShellLinkCoClass();
            var link = (IShellLinkW)shellLink;
            ((IPersistFile)link).Load(shortcutPath, StgmRead);

            var buffer = new StringBuilder(MaxPath);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpUncPriority);

            var target = buffer.ToString().Trim();
            return target.Length == 0 ? null : target;
        }
        catch (Exception)
        {
            // 快捷方式损坏 / COM 不可用 / 没有读取权限：当作“不是网络位置”跳过
            return null;
        }
        finally
        {
            if (shellLink is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(shellLink);
                }
                catch (Exception)
                {
                    // 释放失败不影响结果
                }
            }
        }
    }

    /// <summary>Shell 链接 COM 类的 coclass（<c>CLSID_ShellLink</c>）。</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    /// <summary>
    /// <c>IShellLinkW</c> 的方法必须按 vtable 顺序完整声明（只看前几个也要把后面补齐），
    /// 具体用到的只有 <see cref="GetPath" />。所有字符串参数都显式标 <c>LPWStr</c>：
    /// 不标的话 COM 默认按 ANSI 传，中文路径会乱码。
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary><c>IPersistFile</c>（第一个方法是继承自 <c>IPersist</c> 的 <c>GetClassID</c>）。</summary>
    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
    }
}
