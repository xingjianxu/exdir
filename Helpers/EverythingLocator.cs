using System;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace Exdir.Helpers;

/// <summary>
/// 找 Everything SDK 的 IPC 客户端 DLL（<c>Everything64.dll</c>）。查找顺序：
/// <list type="number">
/// <item><c>EXDIR_EVERYTHING_DLL</c> 环境变量 —— **指定了就以它为准**（找不到也算“没有”，不再回退），</item>
/// <item>exe 旁边（<c>native\x64\Everything64.dll</c> 随包分发的那份），</item>
/// <item>注册表 <c>InstallLocation</c> / <c>InstallPath</c>（HKLM / HKCU / WOW6432Node），</item>
/// <item><c>%ProgramFiles%\Everything</c> 等常见安装目录、以及 <c>PATH</c> 上的 <c>Everything.exe</c> 同目录。</item>
/// </list>
///
/// 为什么要有环境变量那条：DLL 与本机装没装 Everything 是两件事，回归脚本要能造出
/// “找不到 DLL”的场景（否则随包分发之后永远是找得到的），见 <c>tools\test-search.ps1</c>。
///
/// 结果只解析一次并缓存（含“没找到”）—— 每个按键都去翻注册表不值当。
/// 代价是运行期新装 Everything 不会被发现，重启 exdir 即可。
/// </summary>
public static class EverythingLocator
{
    /// <summary>SDK 的 IPC 客户端 DLL 文件名（只带 x64 那一份）。</summary>
    public const string DllName = "Everything64.dll";

    /// <summary>覆盖 DLL 路径的环境变量名（回归脚本用它验证“检测不到 Everything”的降级路径）。</summary>
    public const string OverrideVariable = "EXDIR_EVERYTHING_DLL";

    private static readonly Lazy<string?> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>DLL 的完整路径；null = 本机没找到。</summary>
    public static string? DllPath => Resolved.Value;

    /// <summary>DLL 找得到（不代表 Everything 客户端在运行，那个只有查一次才知道）。</summary>
    public static bool IsAvailable => Resolved.Value is not null;

    private static string? Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception)
            {
                // 非法路径（环境变量里塞了怪东西）就当这个候选不存在
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateCandidates()
    {
        // 1) 随包分发的那份（publish.ps1 会校验它真的在发布目录里）
        yield return Path.Combine(AppContext.BaseDirectory, DllName);

        // 2) 注册表里的安装位置（Everything 1.5 写 InstallLocation；1.4 的安装包写 InstallPath）
        foreach (var root in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var subKey in new[] { @"SOFTWARE\Everything", @"SOFTWARE\voidtools\Everything" })
                {
                    foreach (var valueName in new[] { "InstallLocation", "InstallPath", "Path" })
                    {
                        yield return Combine(ReadRegistry(root, view, subKey, valueName), DllName);
                    }
                }
            }
        }

        // 3) 常见安装目录
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
                         ? Path.Combine(local, "Programs")
                         : null,
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            yield return Path.Combine(programFiles, "Everything", DllName);
            yield return Path.Combine(programFiles, "Everything 1.5a", DllName);
        }

        // 4) PATH 上的 Everything.exe 同目录（scoop / winget 装的通常在这里）
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Combine(directory.Trim().Trim('"'), "Everything.exe");
            if (exe is not null && SafeFileExists(exe))
            {
                yield return Path.Combine(Path.GetDirectoryName(exe)!, DllName);
            }
        }
    }

    /// <summary>
    /// 迭代器里不能把 <c>yield</c> 写在带 <c>catch</c> 的 <c>try</c> 里（CS1626），
    /// 所以“路径非法 = 这个候选不存在”的容错单独放这里。
    /// </summary>
    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception)
        {
            // 没有权限 / 键不存在：都只是“这个候选不存在”
            return null;
        }
    }

    private static string? Combine(string? directory, string fileName)
        => string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, fileName);
}
