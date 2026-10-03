using System;
using System.IO;
using System.Runtime.InteropServices;
using Exdir.Diagnostics;

namespace Exdir.Helpers;

/// <summary>
/// 文件夹选择器（设置窗口「压缩输出目录」旁边的「浏览…」用）。
///
/// <para>
/// 直接调外壳的 <c>IFileOpenDialog</c> + <c>FOS_PICKFOLDERS</c>：
/// 非打包（unpackaged）进程里照样能用，而且 <c>SetFolder</c> 可以让对话框**从当前配置的目录开始**——
/// WinRT 的 <c>FolderPicker</c> 只能给一个 <c>PickerLocationId</c> 枚举，指不了具体路径。
/// </para>
/// <para>
/// 必须从有 COM 的 STA 线程（WinUI 的 UI 线程）调用；用户在对话框里按「取消」或出任何错都返回 null，
/// 调用方照旧用文本框里的值，不会因为选择器不好用而让设置项失效。
/// </para>
/// </summary>
public static class FolderPicker
{
    /// <summary>SIGDN_FILESYSPATH：要真实文件系统路径（shell 虚拟项没有路径时给空串）。</summary>
    private const uint SigdnFileSystemPath = 0x80058000;

    /// <summary>FOS_PICKFOLDERS：选目录而不是选文件。</summary>
    private const uint FosPickFolders = 0x00000020;

    /// <summary>FOS_FORCEFILESYSTEM：只让选真实文件系统里的项（否则会返回“库”这种没有路径的项）。</summary>
    private const uint FosForceFileSystem = 0x00000040;

    /// <summary>FOS_FILEMUSTEXIST：只能选已经存在的文件。</summary>
    private const uint FosFileMustExist = 0x00001000;

    /// <summary>FOS_PATHMUSTEXIST：只能选已经存在的目录。</summary>
    private const uint FosPathMustExist = 0x00000800;

    /// <summary>FOS_DONTADDTORECENT：选个目录不必进“最近使用”列表。</summary>
    private const uint FosDontAddToRecent = 0x02000000;

    /// <summary>
    /// 弹出「选择文件夹」。返回选中的目录全路径；用户取消 / 失败返回 null。
    /// <paramref name="initialDirectory" /> 存在时对话框从它开始。
    /// </summary>
    public static string? PickFolder(nint ownerWindow, string? initialDirectory)
        => Show(ownerWindow, initialDirectory, pickFolders: true, "选择文件夹");

    /// <summary>
    /// 弹出「选择文件」（远程位置的 SFTP 私钥文件用）。返回选中的文件全路径；取消 / 失败返回 null。
    /// 不做扩展名过滤 —— OpenSSH 的私钥文件（<c>id_rsa</c>、<c>id_ed25519</c>、<c>*.pem</c>…）没有统一后缀。
    /// </summary>
    public static string? PickFile(nint ownerWindow, string? initialFilePath)
        => Show(ownerWindow, initialFilePath, pickFolders: false, "选择私钥文件");

    private static string? Show(nint ownerWindow, string? initialPath, bool pickFolders, string title)
    {
        object? dialogObject = null;
        IShellItem? startFolder = null;
        IShellItem? result = null;

        try
        {
            dialogObject = new FileOpenDialogCoClass();
            var dialog = (IFileOpenDialog)dialogObject;

            dialog.GetOptions(out var options);
            dialog.SetOptions(pickFolders
                ? options | FosPickFolders | FosForceFileSystem | FosPathMustExist | FosDontAddToRecent
                : options | FosForceFileSystem | FosFileMustExist | FosDontAddToRecent);
            dialog.SetTitle(title);

            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                // 选文件时把起点定在它所在的目录，并把文件名填进输入框
                var directory = pickFolders ? initialPath : Path.GetDirectoryName(initialPath);

                if (!pickFolders && !string.IsNullOrWhiteSpace(Path.GetFileName(initialPath)))
                {
                    dialog.SetFileName(Path.GetFileName(initialPath));
                }

                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    var shellItemIid = typeof(IShellItem).GUID;

                    if (SHCreateItemFromParsingName(directory, nint.Zero, ref shellItemIid, out startFolder) == 0
                        && startFolder is not null)
                    {
                        dialog.SetFolder(startFolder);
                    }
                }
            }

            // Show 返回 S_OK(0) 表示确认（“选择文件夹”/“打开”）；取消 / 失败统一当“没选”
            if (dialog.Show(ownerWindow) != 0)
            {
                return null;
            }

            dialog.GetResult(out result);
            if (result is null)
            {
                return null;
            }

            result.GetDisplayName(SigdnFileSystemPath, out var path);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            // COM 不可用 / 对话框起不来：只记日志，调用方保留原值（设置项本身可手填）
            Log.Exception("文件夹 / 文件选择器", ex);
            return null;
        }
        finally
        {
            if (result is not null)
            {
                Marshal.ReleaseComObject(result);
            }

            if (startFolder is not null)
            {
                Marshal.ReleaseComObject(startFolder);
            }

            if (dialogObject is not null)
            {
                Marshal.ReleaseComObject(dialogObject);
            }
        }
    }
    // ---------------------------------------------------------------- COM 声明

    /// <summary>CLSID_FileOpenDialog 的 coclass。</summary>
    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass
    {
    }

    /// <summary>
    /// <c>IFileOpenDialog</c> 的方法必须按 vtable 顺序完整声明（vtable 从 <c>IModalWindow::Show</c> 开始）。
    /// 只用到 Show / SetOptions / SetFolder / GetResult，但后面的槽位也要写出来，否则调错函数。
    /// </summary>
    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig]
        int Show(nint parent);

        // IFileDialog
        void SetFileTypes(uint cFileTypes, nint rgFilterSpec);

        void SetFileTypeIndex(uint iFileType);

        void GetFileTypeIndex(out uint piFileType);

        void Advise(nint pfde, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOptions(uint fos);

        void GetOptions(out uint pfos);

        void SetDefaultFolder(IShellItem psi);

        void SetFolder(IShellItem psi);

        void GetFolder(out IShellItem ppsi);

        void GetCurrentSelection(out IShellItem ppsi);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

        void GetResult(out IShellItem ppsi);

        void AddPlace(IShellItem psi, int fdap);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

        void Close(int hr);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(nint pFilter);
    }

    /// <summary><c>IShellItem</c>：只用到 <c>GetDisplayName</c>，其余槽位照样补齐。</summary>
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        nint pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
