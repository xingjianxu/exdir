using System;
using System.IO;
using System.Text;

namespace Exdir.Diagnostics;

/// <summary>
/// 极简文件日志。用途：非打包 WinUI 应用崩溃时不会有控制台输出，
/// 把异常落到 <c>%LOCALAPPDATA%\exdir\exdir.log</c> 便于排查。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

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
                File.AppendAllText(
                    FilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
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
