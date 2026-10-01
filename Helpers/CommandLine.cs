using System;
using System.IO;
using Exdir.Diagnostics;

namespace Exdir.Helpers;

/// <summary>
/// 命令行请求：<c>exdir [path]</c>。
///
/// 解析必须在**进程最早**的时刻做（<see cref="Program.Main" /> 的第一件事）：
///   * 不带路径参数时用的是**本进程**的工作目录 —— 用户在终端里 <c>cd</c> 到哪个目录，
///     应该打开的就是哪个目录（不是已经在跑的那个实例的工作目录）；
///   * 带相对路径时也要按本进程的工作目录解析成绝对路径（转发给已有实例时，
///     对方的工作目录完全是另一回事）。
///
/// 解析结果是一段字符串载荷（<see cref="Request" />）：非空 = 要打开的路径（绝对路径，
/// 可能是文件，见 <c>MainViewModel.HandleActivationAsync</c>）；空串 = 不导航，只把已有窗口切到前台。
/// </summary>
internal static class CommandLine
{
    /// <summary>本次启动的请求载荷（空串 = 只唤回窗口，不导航）。</summary>
    public static string Request { get; private set; } = string.Empty;

    /// <summary>
    /// 解析命令行参数。
    /// 注意：<c>Main(string[] args)</c> 收到的是**不含 exe 路径**的参数（与
    /// <c>Environment.GetCommandLineArgs()</c> 差一位），所以从 0 开始就是用户写的参数。
    /// </summary>
    public static void Parse(string[] args)
    {
        var (request, note) = Resolve(args);
        Request = request;
        Log.Write($"命令行：{note}");
    }

    /// <summary>把命令行解析成“要打开的路径”（空串 = 只唤回窗口）与一句写进日志的说明。</summary>
    private static (string Request, string Note) Resolve(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
            {
                // 目前没有别的选项；"-" 开头的参数一律当选项忽略（Windows 路径不会以 '-' 开头）
                continue;
            }

            var full = ToFullPath(arg);
            return full is null
                ? (arg, $"路径参数 {arg} 算不出合法路径，交给窗口显示「无法打开」")
                : (full, $"路径参数 → {full}");
        }

        // 没有路径参数：用本进程的工作目录。双击 exe / 点任务栏图标时工作目录是 exe 所在目录或
        // C:\Windows\System32 这类地方，那显然不是用户想看的目录 —— 这时只唤回窗口，不动浏览位置。
        // 从终端里执行（cd 到项目目录再敲 exdir）才是“打开当前工作目录”的本意。
        var cwd = CurrentDirectory();
        if (cwd is null)
        {
            return (string.Empty, "没有路径参数，也读不到工作目录 → 只唤回窗口");
        }

        if (LooksLikeLauncherDirectory(cwd))
        {
            return (string.Empty, $"没有路径参数，工作目录 {cwd} 是程序/系统目录（多半是双击 exe 或任务栏启动）→ 只唤回窗口");
        }

        return (cwd, $"没有路径参数，打开工作目录 → {cwd}");
    }

    /// <summary>相对路径按本进程的工作目录解析成绝对路径；非法路径（非法字符 / 过长）返回 null。</summary>
    private static string? ToFullPath(string raw)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            return string.IsNullOrWhiteSpace(expanded) ? null : Path.GetFullPath(expanded);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? CurrentDirectory()
    {
        try
        {
            var cwd = Environment.CurrentDirectory;
            return string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFullPath(cwd);
        }
        catch (Exception)
        {
            // 工作目录已经被删掉 / 盘符已经拔掉时读它会抛
            return null;
        }
    }

    /// <summary>
    /// 这个工作目录像不像“启动器给的”而不是用户自己所在的目录：
    /// exe 所在目录（双击 / 快捷方式默认的工作目录）与 Windows 系统目录（任务栏 / 开始菜单启动时的常见值）。
    /// </summary>
    private static bool LooksLikeLauncherDirectory(string cwd)
    {
        var path = Path.TrimEndingDirectorySeparator(cwd);

        return IsSameOrUnder(path, AppContext.BaseDirectory)
               || IsSameOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
               || IsSameOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.System))
               || IsSameOrUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
    }

    /// <summary><paramref name="path" /> 等于、或位于 <paramref name="ancestor" /> 之下。</summary>
    private static bool IsSameOrUnder(string path, string ancestor)
    {
        if (string.IsNullOrEmpty(ancestor))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(ancestor);
        if (path.Length < root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 必须是整段相同：C:\Windows 与 C:\Windows.old 不是同一个目录
        return path.Length == root.Length || path[root.Length] == '\\';
    }
}
