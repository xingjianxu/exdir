using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Exdir.Services.Native;

/// <summary>
/// 复制 / 移动 / 删除文件的 Win32 互操作：`SHFileOperation`（外壳自己的文件操作引擎）。
/// <para>
/// 用它而不是自己写 <c>File.Copy</c> 走路，是因为它能免费得到资源管理器同款的东西：
/// 进度对话框（可取消）、同名冲突的是 / 否 / 全部对话框、删除确认框（含回收站与“只能永久删”警告）、
/// 只读属性询问、长路径与只读介质处理。
/// 代价是它必须在 STA 线程上调用（见 <c>FileOperationService</c>）。
/// 代价是它必须在 STA 线程上调用（见 <c>FileOperationService</c>）。
/// </para>
/// </summary>
internal static class FileOperationInterop
{
    private const uint FO_MOVE = 0x0001;
    private const uint FO_COPY = 0x0002;
    private const uint FO_DELETE = 0x0003;

    /// <summary>目标目录不存在时直接建，不要弹“是否新建文件夹”的确认框。</summary>
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    /// <summary>删除时把文件放进回收站（而不是从磁盘上抹掉）。</summary>
    private const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>
    /// 回收站装不下（文件太大、卷没有回收站）而只能永久删除时，仍然弹一次警告框。
    /// 不带这个标志的话，<c>FOF_ALLOWUNDO</c> 会让这类文件被静默抹掉，
    /// 而用户以为只是“丢进回收站” —— 这是删除里唯一不可逆的分支，必须问一次。
    /// </summary>
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;

        public uint wFunc;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pTo;

        public ushort fFlags;

        public int fAnyOperationsAborted;

        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>一次文件操作的结果。</summary>
    /// <param name="ErrorCode">外壳返回的错误码（0 = 成功）。</param>
    /// <param name="Canceled">用户是否在弹出的确认框 / 进度对话框里点了取消（或者否）。</param>
    public readonly record struct Result(int ErrorCode, bool Canceled)
    {
        public bool Success => ErrorCode == 0;
    }

    /// <summary>复制一批文件 / 目录到目标目录。</summary>
    public static Result Copy(IntPtr owner, IReadOnlyList<string> sources, string destinationDirectory)
        => Run(owner, FO_COPY, sources, destinationDirectory);

    /// <summary>移动一批文件 / 目录到目标目录。</summary>
    public static Result Move(IntPtr owner, IReadOnlyList<string> sources, string destinationDirectory)
        => Run(owner, FO_MOVE, sources, destinationDirectory);

    /// <summary>
    /// 删除一批文件 / 目录。<paramref name="permanent" /> 为 false 时进回收站（可撤销），
    /// 为 true 时永久删除。两种都会由外壳弹出确认框（用户关了“删除确认”设置时不弹）。
    /// </summary>
    public static Result Delete(IntPtr owner, IReadOnlyList<string> sources, bool permanent)
    {
        if (sources.Count == 0)
        {
            return new Result(0, false);
        }

        var operation = new SHFILEOPSTRUCTW
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = BuildFrom(sources),

            // FO_DELETE 不看 pTo；给 null 而不是空串（外壳会把非法目标当成参数错误返回）
            pTo = null!,
            fFlags = permanent ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING),
        };

        var result = SHFileOperation(ref operation);
        return new Result(result, operation.fAnyOperationsAborted != 0);
    }

    private static Result Run(IntPtr owner, uint function, IReadOnlyList<string> sources, string destinationDirectory)
    {
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return new Result(0, false);
        }

        var operation = new SHFILEOPSTRUCTW
        {
            hwnd = owner,
            wFunc = function,
            pFrom = BuildFrom(sources),
            pTo = destinationDirectory.EndsWith('\\') || destinationDirectory.EndsWith('/')
                ? destinationDirectory + "\0\0"
                : destinationDirectory + "\\\0\0",
            fFlags = FOF_NOCONFIRMMKDIR,
        };

        var result = SHFileOperation(ref operation);
        return new Result(result, operation.fAnyOperationsAborted != 0);
    }

    /// <summary>
    /// pFrom 是“多字符串”：每项以 '\0' 结尾，整串再来一个 '\0' 收尾；
    /// marshaler 还会补上最后一个 '\0'，所以这里只要再补一个就够（多出来也无害）。
    /// </summary>
    private static string BuildFrom(IReadOnlyList<string> sources) => string.Join('\0', sources) + "\0\0";
}
