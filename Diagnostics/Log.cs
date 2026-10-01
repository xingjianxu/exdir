using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Exdir.Diagnostics;

/// <summary>
/// 极简文件日志。用途：非打包 WinUI 应用崩溃时不会有控制台输出，
/// 把异常落到 <c>%LOCALAPPDATA%\exdir\exdir.log</c> 便于排查。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>
    /// 跨进程的写日志锁。现在会同时有两个 exdir 进程往同一份日志里写
    /// （第二个实例一启动就写一行“命令行：…”，紧接着第一个实例也在写），
    /// 两个进程各写各的会把两行日志交错到一起（诊断时看着像乱码），所以用命名互斥体串起来。
    /// </summary>
    private const string MutexName = @"Local\exdir.log";

    /// <summary>等不到锁就别记了（记日志绝不能拖住主流程）。</summary>
    private const int LockTimeoutMs = 2000;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "exdir",
        "exdir.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // File.AppendAllText 用的是 FileShare.Read：另一个进程正在写时后到的会被共享冲突挡掉，
                // 然后被下面的 catch 静静吐掉（症状是“日志里少了一行”）。
                // 所以自己开文件并允许别人同时读写；追加是 FILE_APPEND_DATA，不会互相覆盖。
                using var mutex = new Mutex(false, MutexName);
                var acquired = false;

                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(LockTimeoutMs);
                    }
                    catch (AbandonedMutexException)
                    {
                        // 上一个持有者被强杀了：锁归我们，接着写
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        return;
                    }

                    using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                    // 直接写字节，不用 StreamWriter/UTF8Encoding：
                    // 交付版是裁剪过的，而 System.Text.Encoding.Extensions（公开的 UTF8Encoding 类型就在那里）
                    // 谁都没引用时会被裁掉，一旦被裁，new UTF8Encoding(false) 就在 JIT 这一拍抛
                    // FileNotFoundException——try/catch 拦不住（异常发生在方法编译时，还没进 try），
                    // 症状是整个进程一启动就无日志猝死。Encoding.UTF8 的 GetBytes 不带 BOM，正好。
                    var bytes = Encoding.UTF8.GetBytes(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                    stream.Write(bytes, 0, bytes.Length);
                }
                finally
                {
                    if (acquired)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }
        catch (Exception)
        {
            // 日志失败不能影响主流程
        }
    }

    public static void Exception(string context, Exception exception)
        => Write($"异常 @ {context}{Environment.NewLine}{exception}");
}
