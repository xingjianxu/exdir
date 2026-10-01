using System;
using Exdir.Diagnostics;
using Microsoft.Win32;

namespace Exdir.Helpers;

/// <summary>
/// 开机自启（登录时自动启动 exdir）。
///
/// 实现是往 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 里写一个字符串值
/// （值名 <c>exdir</c>，数据 = <c>"&lt;exe 路径&gt;" --preload</c>）：
/// 非打包应用没有包标识，用不了 <c>Windows.ApplicationModel.StartupTask</c>；
/// 写 HKCU 不需要管理员权限，也是绝大多数桌面程序的做法。
///
/// 为什么要带 <c>--preload</c>：登录时起来的这一份**不显示主窗口**，
/// 只把进程、DI 容器、上次打开的目录（会话）与首屏图标先备好
/// （见 <c>MainWindow.StartPreload</c>）。用户之后双击 exe / 点托盘图标时，
/// 单实例闸门会把请求转给这份已经在跑的进程，它只要 ShowWindow 一下就能出现 ——
/// 这才是“加快后续窗口弹出”的意义所在。
///
/// 注册表项是“当前 exe 的绝对路径”，所以换目录 / 升级后启动时会重写一遍（见
/// <see cref="Exdir.ViewModels.MainViewModel" /> 构造里的自愈调用）。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "exdir";

    /// <summary>
    /// 把当前状态同步到注册表：<c>true</c> = 写入自启项，<c>false</c> = 删除。
    /// 失败只记日志（不能因为“登记不上开机自启”影响正常使用）。
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                Log.Write("自动启动：打不开 HKCU 的 Run 项，已跳过");
                return;
            }

            if (enabled)
            {
                var command = BuildCommandLine();
                key.SetValue(ValueName, command, RegistryValueKind.String);
                Log.Write($"自动启动：已登记 {command}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write("自动启动：已取消");
            }
        }
        catch (Exception ex)
        {
            Log.Exception("自动启动", ex);
        }
    }

    /// <summary>自启项里记的命令行：<c>"&lt;exe 路径&gt;" --preload</c>（路径含空格时引号是必须的）。</summary>
    private static string BuildCommandLine()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            exe = System.IO.Path.Combine(AppContext.BaseDirectory, "exdir.exe");
        }

        return $"\"{exe}\" {CommandLine.PreloadArgument}";
    }
}
