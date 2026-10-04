using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exdir.Diagnostics;
using Exdir.Models;
using Exdir.Services;

namespace Exdir.ViewModels;

/// <summary>
/// 在线更新的状态与命令（<see cref="Views.UpdateWindow" /> 与主窗口顶部那条「发现新版本」提示条共用这一份）。
///
/// 三步分别对应 <see cref="IUpdateService" /> 的三个方法：
///   检查（<see cref="CheckAsync" /> / <see cref="CheckInBackgroundAsync" />）→
///   下载（<see cref="DownloadCommand" />）→ 重启替换（<see cref="RestartCommand" />）。
/// 「重启替换」在启动替换脚本之后发 <see cref="RestartRequested" />，由 <see cref="MainWindow" /> 真的退出进程。
///
/// 属性一律手写（不用 <c>[ObservableProperty]</c> 字段版）：那个写法在 WinUI 3 里会生成
/// “跨 WinRT ABI 但缺 CsWinRT 编组代码”的属性，MVVMTK0045 警告（见 AGENTS.md 第 6 节第 71 条同一类问题），
/// 仓库里其它 ViewModel 也都是手写 + <c>SetProperty</c>。
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly IShellService _shell;

    /// <summary>下载用的取消源（窗口上的「取消下载」/ 关掉窗口都能停下）。</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>解好的新版本目录（<see cref="RestartCommand" /> 用它）。</summary>
    private string? _payloadDirectory;

    private UpdateInfo? _available;
    private bool _isUpdateAvailable;
    private bool _isChecking;
    private bool _isDownloading;
    private double _downloadProgress;
    private string _statusText = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isReadyToRestart;

    public UpdateViewModel(IUpdateService updates, IShellService shell)
    {
        _updates = updates;
        _shell = shell;
    }

    /// <summary>用户点了「重启并完成更新」：替换脚本已经起来了，主窗口该退出进程了。</summary>
    public event EventHandler? RestartRequested;

    /// <summary>当前版本，形如 <c>0.0.20261001</c>。</summary>
    public string CurrentVersion => _updates.CurrentVersion;

    /// <summary>
    /// 启动时要不要允许“自动查一次”（当前目录是发布版目录）。
    /// 开发目录不自动查（见 <see cref="IUpdateService.IsReleaseLayout" />），手动检查不受影响。
    /// </summary>
    public bool CanCheckOnStartup => _updates.IsReleaseLayout;

    /// <summary>界面上的“当前版本：0.0.20261001”。</summary>
    public string CurrentVersionText => $"当前版本：{CurrentVersion}";

    /// <summary>发现的更新（没有就是 null）。</summary>
    public UpdateInfo? Available
    {
        get => _available;
        set
        {
            if (SetProperty(ref _available, value))
            {
                RaiseAvailableDependent();
            }
        }
    }

    /// <summary>主窗口顶部那条提示条是否显示（启动时后台查到新版本时打开，用户关掉 / 开始更新就关）。</summary>
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set => SetProperty(ref _isUpdateAvailable, value);
    }

    /// <summary>正在检查（按钮置灰 + 转圈）。</summary>
    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (SetProperty(ref _isChecking, value))
            {
                RaiseBusyDependent();
            }
        }
    }

    /// <summary>正在下载。</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(ShowDownloadButton));
                RaiseBusyDependent();
            }
        }
    }

    /// <summary>下载进度 0~1。</summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            if (SetProperty(ref _downloadProgress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    /// <summary>状态行文字（“正在检查…”/“已是最新版本”/“下载完成…”）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>出错原因（空 = 没出错）。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>包已经下好解好，就等重启。</summary>
    public bool IsReadyToRestart
    {
        get => _isReadyToRestart;
        set
        {
            if (SetProperty(ref _isReadyToRestart, value))
            {
                OnPropertyChanged(nameof(ShowDownloadButton));
                OnPropertyChanged(nameof(ShowRestartButton));
                RestartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>有没有发现更新。</summary>
    public bool HasUpdate => Available is not null;

    /// <summary>有没有报错（错误那一行是否显示）。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>检查或下载当中（界面该显示忙碌状态）。</summary>
    public bool IsBusy => IsChecking || IsDownloading;

    /// <summary>“最新版本：v0.0.20261002”。</summary>
    public string LatestVersionText => Available is null ? string.Empty : $"最新版本：{Available.Tag}";

    /// <summary>提示条上的一句话。</summary>
    public string UpdateMessage => Available is null
        ? string.Empty
        : $"发现新版本 {Available.Tag}（当前 {CurrentVersion}）";

    /// <summary>不能自动替换时给用户看的原因（能自动替换时为空串）。</summary>
    public string SelfUpdateHint => Available?.SelfUpdateBlockedReason ?? string.Empty;

    public bool HasSelfUpdateHint => !string.IsNullOrEmpty(SelfUpdateHint);

    /// <summary>这个更新有没有可下载的安装包。</summary>
    public bool CanDownload => Available?.CanDownload == true;

    /// <summary>「立即更新」（下载）按钮是否显示。</summary>
    public bool ShowDownloadButton => HasUpdate && CanDownload && !IsReadyToRestart && !IsDownloading;

    /// <summary>「重启并完成更新」按钮是否显示。</summary>
    public bool ShowRestartButton => IsReadyToRestart;

    /// <summary>进度的百分比文字。</summary>
    public string ProgressText => $"{Math.Round(DownloadProgress * 100)}%";

    /// <summary>发现的新版本说明（Release 的正文，markdown 原样显示）。</summary>
    public string ReleaseNotes => Available?.ReleaseNotes ?? string.Empty;

    /// <summary>发行说明为空时给一个占位文案（那个区域一直留着，不然窗口会跳一下）。</summary>
    public string ReleaseNotesText => string.IsNullOrWhiteSpace(Available?.ReleaseNotes)
        ? "（这个版本没有填写发行说明）"
        : Available!.ReleaseNotes;

    /// <summary>
    /// 手动检查（「帮助 → 检查更新…」与更新窗口里的「重新检查」）。
    /// 返回是否发现了新版本；出错时把原因写进 <see cref="ErrorMessage" /> 并返回 false。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task<bool> CheckAsync()
    {
        if (IsBusy)
        {
            return HasUpdate;
        }

        IsChecking = true;
        ErrorMessage = string.Empty;
        StatusText = "正在检查更新…";

        try
        {
            var info = await _updates.CheckAsync();

            Available = info;
            IsReadyToRestart = false;
            _payloadDirectory = null;

            if (info is null)
            {
                StatusText = $"当前已是最新版本（{CurrentVersion}）";
                return false;
            }

            StatusText = "可以更新到新版本。";
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception("检查更新", ex);
            ErrorMessage = ex.Message;
            StatusText = "检查更新失败。";
            return false;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanCheck() => !IsBusy;

    /// <summary>
    /// 启动时的后台检查：只在窗口顶部弹一条提示条，**不**动状态行、不弹窗、出错也不打扰用户。
    /// </summary>
    public async Task CheckInBackgroundAsync()
    {
        try
        {
            var info = await _updates.CheckAsync();
            if (info is null)
            {
                return;
            }

            Available = info;
            IsUpdateAvailable = true;
            Log.Write($"在线更新：后台检查发现 {info.Tag}，已在窗口顶部提示");
        }
        catch (Exception ex)
        {
            // 启动时后台检查失败不是用户的问题（没网、接口限流…），只记日志
            Log.Write($"在线更新：后台检查失败（{ex.Message}）");
        }
    }

    /// <summary>下载并解好新版本（窗口里的「立即更新」）。</summary>
    [RelayCommand(CanExecute = nameof(CanStartDownload))]
    private async Task DownloadAsync()
    {
        if (Available is not { } info || IsDownloading)
        {
            return;
        }

        IsDownloading = true;
        IsUpdateAvailable = false; // 提示条让位给更新窗口里的进度
        ErrorMessage = string.Empty;
        DownloadProgress = 0;
        StatusText = "正在下载更新包…";

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<double>(value => DownloadProgress = value);

        try
        {
            _payloadDirectory = await _updates.DownloadAsync(info, progress, _downloadCts.Token);
            IsReadyToRestart = true;
            StatusText = "下载完成，点「重启并完成更新」即可换上新版本。";
            Log.Write($"在线更新：{info.Tag} 下载完成，等待重启替换");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消下载。";
        }
        catch (Exception ex)
        {
            Log.Exception("下载更新", ex);
            ErrorMessage = ex.Message;
            StatusText = "下载更新失败。";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private bool CanStartDownload() => !IsBusy;

    /// <summary>取消正在进行的下载（窗口上的「取消下载」）。</summary>
    [RelayCommand]
    private void CancelDownload()
    {
        try
        {
            _downloadCts?.Cancel();
        }
        catch (Exception)
        {
            // 已经结束了，忽略
        }
    }

    /// <summary>用系统默认浏览器打开发布页（自动替换不可用时的兜底，也是“看看到底改了什么”）。</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = Available?.ReleasePageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = _updates.ReleasesPageUrl;
        }

        if (!_shell.OpenUrl(url))
        {
            ErrorMessage = $"打不开浏览器，请手动访问 {url}";
        }
    }

    /// <summary>启动替换脚本并请求退出进程（真正的退出由 <see cref="RestartRequested" /> 的订阅者做）。</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart()
    {
        if (_payloadDirectory is not { } payload)
        {
            return;
        }

        try
        {
            _updates.ApplyAndRestart(payload);
        }
        catch (Exception ex)
        {
            Log.Exception("启动更新替换", ex);
            ErrorMessage = $"无法启动更新程序：{ex.Message}";
            return;
        }

        StatusText = "正在重启并替换文件…";
        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanRestart() => IsReadyToRestart;

    /// <summary>窗口关掉时收尾：下到一半的直接取消（半份包留着没用，<c>UpdateService.CleanupTemp</c> 会清）。</summary>
    public void OnWindowClosed() => CancelDownload();

    // ------------------------------------------------------------------ 内部

    private void RaiseAvailableDependent()
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(LatestVersionText));
        OnPropertyChanged(nameof(UpdateMessage));
        OnPropertyChanged(nameof(SelfUpdateHint));
        OnPropertyChanged(nameof(HasSelfUpdateHint));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(ReleaseNotes));
        OnPropertyChanged(nameof(ReleaseNotesText));
        OnPropertyChanged(nameof(ShowDownloadButton));
    }

    private void RaiseBusyDependent()
    {
        OnPropertyChanged(nameof(IsBusy));
        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
    }
}
