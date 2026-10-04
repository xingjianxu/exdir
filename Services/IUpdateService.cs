using System;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 在线更新：问 GitHub 要最新的 Release，把它的 win-x64 zip 下下来校验、解开，
/// 然后交给一个后台脚本在 exdir 退出后替换安装目录并重新启动。
///
/// 三件事分开，互不耦合：
///   * <see cref="CheckAsync" /> 只读网络（最多 20 秒超时），失败抛异常、没有新版本返回 null；
///   * <see cref="DownloadAsync" /> 只写 <c>%LOCALAPPDATA%\exdir\update\&lt;tag&gt;\</c>（不动安装目录）；
///   * <see cref="ApplyAndRestart" /> 只负责“退出后替换并重启”，调用方紧接着就要退出进程。
///
/// 界面在 <c>Views/UpdateWindow</c>（手动检查 / 点「立即更新」）与 <c>MainWindow</c> 顶部的提示条
/// （启动时的后台检查）。
/// </summary>
public interface IUpdateService
{
    /// <summary>当前版本，形如 <c>0.0.20261001</c>（来自 <see cref="Exdir.Helpers.AppVersion" />）。</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// 当前目录看起来是不是一个发布版安装目录（旁边有 <c>build-info.txt</c>）。
    /// 界面上**只用它决定要不要在启动时自动查**：“帮助 → 检查更新…”永远可以手动查。
    /// 开发目录（bin\ 下的 Debug / Release 输出）不自动查：既免得打扰正在写代码的人，
    /// 也免得提示条把布局顶下去、影响那些按坐标点击的回归脚本。
    /// </summary>
    bool IsReleaseLayout { get; }

    /// <summary>Release 列表页地址（「打开发布页」/ 手动下载时用）。</summary>
    string ReleasesPageUrl { get; }

    /// <summary>
    /// 检查有没有比当前版本更新的 Release。
    /// 返回 null = 已经是最新（或远端还没有发布任何 Release）；网络 / 接口出错则抛异常，由调用方显示原因。
    /// </summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载 <paramref name="info" /> 的 zip 并校验 / 解压，返回**解好的 payload 目录**（里面就是 exdir.exe 那一层）。
    /// <paramref name="progress" /> 收到 0~1 的进度（在调用方的线程上回调，UI 里直接绑进度条即可）。
    /// </summary>
    Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 起一个后台 PowerShell 脚本：等本进程退出 → robocopy 覆盖安装目录 → 重新启动 exdir。
    /// **调用后必须马上退出进程**，否则脚本只会一直等（超时后仍会尝试替换，但会失败）。
    /// </summary>
    void ApplyAndRestart(string payloadDirectory);

    /// <summary>清掉上次没下完 / 没装上留下的中转目录（超过一天的才删，见实现里的注释）。</summary>
    void CleanupTemp();
}
