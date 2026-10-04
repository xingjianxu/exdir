using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Exdir.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace Exdir.Services;

/// <inheritdoc cref="IShellService" />
public sealed class ShellService : IShellService
{
    private static readonly string? WindowsTerminalPath = ProbeWindowsTerminal();

    public void OpenWithDefaultApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 没有关联程序或权限不足：静默失败，后续可改为提示对话框
        }
    }

    public bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"打开网址失败：{url}，{ex.Message}");
            return false;
        }
    }

    public void OpenAll(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            OpenWithDefaultApp(path);
        }
    }

    public bool OpenWithProgram(string programPath, IReadOnlyList<string> paths)
    {
        if (string.IsNullOrWhiteSpace(programPath) || paths.Count == 0)
        {
            return false;
        }

        try
        {
            // 每个路径都带引号：压缩包路径里完全可以有空格（Windows 的命令行解析自己处理带引号的参数）
            var arguments = string.Join(' ', paths.Select(path => $"\"{path}\""));

            Process.Start(new ProcessStartInfo(programPath, arguments) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"启动外部程序失败：{programPath}（{ex.Message}）");
            return false;
        }
    }

    public void RevealInFileExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    public void ShowProperties(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // “properties” 是外壳给文件 / 文件夹注册的标准谓词，直接跑它就能拿到属性对话框，
            // 不必为此建一个 IContextMenu（那才是系统菜单慢的原因）
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "properties" });
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    public void OpenTerminal(string directory, bool asAdministrator = false, bool preferWindowsTerminal = true)
    {
        var workingDirectory = Directory.Exists(directory) ? directory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            if (asAdministrator)
            {
                // -WorkingDirectory 需要引号；runas 不接受 UseShellExecute=false
                var arguments = $"-NoExit -WorkingDirectory \"{workingDirectory}\"";
                Process.Start(new ProcessStartInfo(ResolvePowerShellPath(), arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                return;
            }

            if (preferWindowsTerminal && WindowsTerminalPath is not null)
            {
                Process.Start(new ProcessStartInfo(WindowsTerminalPath, $"-d \"{workingDirectory}\"") { UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo(ResolvePowerShellPath(), $"-NoExit -WorkingDirectory \"{workingDirectory}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = workingDirectory,
            });
        }
        catch (Exception)
        {
            // 用户取消 UAC 或终端缺失
        }
    }

    public void CopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception)
        {
            // 剪贴板被其它进程占用时忽略
        }
    }

    public void RunCommand(string commandLine, string workingDirectory, bool asAdministrator = false)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return;
        }

        var cwd = Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", $"/c {commandLine}")
            {
                UseShellExecute = true,
                WorkingDirectory = cwd,
            };

            if (asAdministrator)
            {
                startInfo.Verb = "runas";
            }

            Process.Start(startInfo);
        }
        catch (Exception)
        {
            // 用户取消 UAC 或命令不存在
        }
    }

    /// <summary>优先使用 PowerShell 7，回退到 Windows PowerShell。</summary>
    private static string ResolvePowerShellPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh))
        {
            return pwsh;
        }

        var localPwsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "pwsh.exe");
        if (File.Exists(localPwsh))
        {
            return localPwsh;
        }

        return "powershell.exe";
    }

    private static string? ProbeWindowsTerminal()
    {
        // Windows Terminal 以应用执行别名方式注册在 %LOCALAPPDATA%\Microsoft\WindowsApps 下
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");

        return File.Exists(alias) ? alias : null;
    }
}
