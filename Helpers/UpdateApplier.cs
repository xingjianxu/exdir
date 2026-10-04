using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Exdir.Diagnostics;

namespace Exdir.Helpers;

/// <summary>
/// 把“替换安装目录并重启”这一步交给一个**独立的后台 PowerShell 脚本**（在线更新的最后一拍）。
///
/// 为什么不能自己替换：正在运行的进程，它加载过的 exe / dll 都写着“删除挂起”（delete-pending），
/// robocopy 覆盖会失败；而且 exdir 是常驻托盘的单窗口程序，更新完必须真的重启一次。
/// 所以顺序只能是：写脚本 → 启动脚本 → 自己退出 → 脚本等本进程消失 → robocopy 覆盖 → 重启 exdir。
///
/// 脚本放在更新中转目录（<c>%LOCALAPPDATA%\exdir\update\apply-update.ps1</c>），
/// 日志与它同目录的 <c>apply.log</c>（替换没成功时用户 / 回归脚本都能看出来发生了什么）。
/// 用系统自带的 Windows PowerShell 5.1（<c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>），
/// 不依赖用户自己装过 PowerShell 7。
/// </summary>
internal static class UpdateApplier
{
    /// <summary>脚本文件名（放在更新中转目录根下，不在要删的 <c>&lt;tag&gt;</c> 子目录里）。</summary>
    public const string ScriptFileName = "apply-update.ps1";

    /// <summary>脚本写的日志文件名。</summary>
    public const string LogFileName = "apply.log";

    /// <summary>
    /// 启动替换脚本。**返回后调用方必须马上退出进程**。
    /// </summary>
    /// <param name="payloadDirectory">解好的新版本目录（里面是 exdir.exe 那一层）。</param>
    /// <param name="targetDirectory">当前安装目录（<see cref="AppContext.BaseDirectory" />）。</param>
    /// <param name="exeName">要重启的可执行文件名（<c>exdir.exe</c>）。</param>
    /// <param name="updateRoot">更新中转目录（脚本与日志放这里）。</param>
    public static void ApplyAndRestart(string payloadDirectory, string targetDirectory, string exeName, string updateRoot)
    {
        Directory.CreateDirectory(updateRoot);

        var scriptPath = Path.Combine(updateRoot, ScriptFileName);
        // 带 BOM 的 UTF-8：Windows PowerShell 5.1 没有 BOM 时按系统 ANSI 码页读脚本，中文会乱码
        File.WriteAllText(scriptPath, ScriptText, new UTF8Encoding(true));

        var logPath = Path.Combine(updateRoot, LogFileName);
        var powershell = FindPowerShell();

        var startInfo = new ProcessStartInfo(powershell)
        {
            // 脚本自己会等本进程退出，所以这里不能等它、也不能让它在自己的窗口里跑
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = targetDirectory,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass"); // 系统策略是 Restricted 时 -File 也会被拒
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-AppPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(payloadDirectory);
        startInfo.ArgumentList.Add("-Target");
        startInfo.ArgumentList.Add(targetDirectory);
        startInfo.ArgumentList.Add("-ExeName");
        startInfo.ArgumentList.Add(exeName);
        startInfo.ArgumentList.Add("-Log");
        startInfo.ArgumentList.Add(logPath);

        Process.Start(startInfo);

        Log.Write($"在线更新：替换脚本已启动（{scriptPath}），等待本进程退出后替换 {targetDirectory}");
    }

    /// <summary>真正的脚本正文。参数由命令行传（这里不用字符串插值，免得 C# 的花括号和 PowerShell 打架）。</summary>
    private const string ScriptText = """
        param(
            [int]$AppPid,
            [string]$Source,
            [string]$Target,
            [string]$ExeName,
            [string]$Log
        )

        # exdir 在线更新的最后一拍：等旧的 exdir 退出 -> robocopy 覆盖安装目录 -> 重新启动 exdir。
        # 由 exdir 自己生成并启动，跑完把自己删掉。日志见同目录的 apply.log。

        $ErrorActionPreference = 'Continue'
        $utf8 = New-Object System.Text.UTF8Encoding($false)

        function Write-UpdateLog([string]$Message) {
            try {
                $line = '[{0}] {1}{2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message, [Environment]::NewLine
                [System.IO.File]::AppendAllText($Log, $line, $utf8)
            } catch { }
        }

        Write-UpdateLog ('start: pid={0} source={1} target={2}' -f $AppPid, $Source, $Target)

        # 最多等 2 分钟（窗口 / 托盘 / 其它线程收尾慢的时候不至于卡太久）
        for ($i = 0; $i -lt 240; $i++) {
            if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }
        Start-Sleep -Milliseconds 700

        # /E 整棵树（含空目录）
        # /IS 连“时间戳与大小都一样”的文件也覆盖（下载解出来的时间戳可能与安装目录里的一致）
        # /R:3 /W:1 文件被占用时重试 3 次、每次等 1 秒
        $code = 16
        try {
            $output = & robocopy $Source $Target /E /IS /R:3 /W:1 /NFL /NDL /NJH /NJS /NP 2>&1
            $code = $LASTEXITCODE
            Write-UpdateLog ('robocopy exit={0}' -f $code)
            if ($code -ge 8) {
                Write-UpdateLog ('robocopy output: ' + (($output | Select-Object -First 20) -join ' | '))
            }
        } catch {
            Write-UpdateLog ('robocopy threw: ' + $_.Exception.Message)
        }

        # 不管替换成功与否都要把 exdir 起回来（半新半旧的目录总比“打不开”强，日志里能看出问题）
        Start-Sleep -Milliseconds 500
        try {
            Start-Process -FilePath $ExeName -WorkingDirectory $Target
            Write-UpdateLog 'restarted exdir'
        } catch {
            Write-UpdateLog ('restart failed: ' + $_.Exception.Message)
        }

        # 换成功了就把这次的中转目录（zip + 解出来的 payload）收掉；失败时留着便于排查
        if ($code -lt 8) {
            try { Remove-Item -LiteralPath (Split-Path -Parent $Source) -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        }

        try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
        """;

    /// <summary>
    /// 系统自带的 Windows PowerShell。找不到时退回 PATH 上的 powershell.exe
    /// （<see cref="Exdir.Services.UpdateService" /> 在“自更新能力探测”里会先用
    /// <see cref="Exists" /> 判一次，缺了就不给用户走自动替换这条路）。
    /// </summary>
    public static string FindPowerShell() => Exists() ? PowerShellPath! : "powershell.exe";

    /// <summary>Windows PowerShell 5.1 的绝对路径（拿不到系统目录时为 null）。</summary>
    private static string? PowerShellPath
    {
        get
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return string.IsNullOrEmpty(system)
                ? null
                : Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
        }
    }

    /// <summary>系统里有没有可用的 Windows PowerShell（自动替换要靠它）。</summary>
    public static bool Exists()
    {
        var path = PowerShellPath;
        if (path is null)
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
