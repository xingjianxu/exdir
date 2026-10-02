# 开机自启（--preload 预热启动）的回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-autostart.ps1
#   pwsh -NoProfile -File tools\test-autostart.ps1 -Exe dist\win-x64\exdir.exe
#
# 三个用例（全程靠进程句柄 + exdir.log 断言，不需要交互桌面、不需要前台窗口）：
#   1. `exdir --preload` 是“预热启动”：进程活着、**没有可见的主窗口**（MainWindowHandle = 0）、
#      exdir.log 里有「预热启动：不显示主窗口」与「预热完成：… 目录=… 图标=…」；
#   2. 预热进程在跑的时候再启动一次 `exdir`（无参数）：第二个进程几十~几百毫秒就退出
#      （单实例闸门把“唤回窗口”转交给它），预热进程的窗口真的显示出来了，而且只有一个 exdir 进程；
#   3. 预热进程里照样能按命令行参数导航（`exdir <目录>` → 新标签页打开该目录），
#      证明“藏起来的预热进程”就是一个正常的 exdir。
#
# 这个脚本不改注册表、不改 config.json 的语义（只在用例 3 里把会话目录临时指到一个测试目录，
# 结束时原样还原），也不弹任何窗口。
#
# 与 tools\test-settings.ps1 用例 10 的分工：那边验证“设置里拨一下开关真的会写 / 删 HKCU 的 Run 项”，
# 这里验证“带 --preload 启动的那个进程的行为”。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$exeDir = Split-Path $exePath
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# exdir 是常驻托盘的单实例程序：收尾一律直接 Kill（隐藏时已经落过盘，见 tool\*.ps1 的 Stop-Session）
function Stop-AllExdir {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    # 等真的退出，否则下一次 Start-Process 会因为“已有实例在运行”直接退出去
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Process -Name 'exdir' -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
}

function Read-LogTail {
    param([int]$Lines = 200)
    if (-not (Test-Path $logPath)) { return @() }
    return @(Get-Content $logPath -Tail $Lines)
}

function Wait-LogContains {
    param([string]$Pattern, [int]$Tail = 200, [int]$TimeoutMs = 30000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (@(Read-LogTail -Lines $Tail | Where-Object { $_ -like "*$Pattern*" }).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# 只用 Win32 判断窗口是否可见：Eh 的 Process.MainWindowHandle 只认“可见的顶层窗口”，
# 所以预热进程应该是 0，而唤回之后应该非 0。
function Get-VisibleWindowHandle {
    param($Process)
    try { $Process.Refresh() } catch { }
    return $Process.MainWindowHandle
}

# 启动一份 exdir（-Preload 时带 --preload），返回进程对象
function Start-Exdir {
    param([switch]$Preload)
    $arguments = if ($Preload) { @('--preload') } else { @() }
    return Start-Process -FilePath $exePath -ArgumentList $arguments -WorkingDirectory $exeDir -PassThru
}

# 等一个进程的可见主窗口出现
function Wait-VisibleWindow {
    param($Process, [int]$TimeoutMs = 30000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) { return [IntPtr]::Zero }
        $handle = Get-VisibleWindowHandle -Process $Process
        if ($handle -ne [IntPtr]::Zero) { return $handle }
        Start-Sleep -Milliseconds 300
    }
    return [IntPtr]::Zero
}

$testDir = Join-Path ([System.IO.Path]::GetTempPath()) ('exdir-autostart-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

try {
    # ================================================================== 用例 1：预热启动 = 不显示主窗口
    Write-Host '--- 用例 1：exdir --preload（预热启动，不显示主窗口） ---'
    Stop-AllExdir

    $preload = Start-Exdir -Preload
    Start-Sleep -Seconds 2
    Assert (-not $preload.HasExited) '--preload 进程起来了并保持驻留'

    Assert (Wait-LogContains -Pattern '命令行：--preload') '日志记下了「命令行：--preload」'
    Assert (Wait-LogContains -Pattern '预热启动：不显示主窗口') '日志记下了「预热启动：不显示主窗口」'

    $preheated = Wait-LogContains -Pattern '预热完成：' -TimeoutMs 60000
    Assert $preheated '日志记下了「预热完成：…」（会话恢复 + 首屏图标都做完了）'
    if ($preheated) {
        $line = @(Read-LogTail -Lines 200 | Where-Object { $_ -like '*预热完成：*' })[-1]
        Write-Host ("  " + $line.Trim())
        Assert ($line -match '目录=' -and $line -match '图标=') '预热日志里有目录与图标个数'
    }

    Start-Sleep -Seconds 2
    Assert ((Get-VisibleWindowHandle -Process $preload) -eq [IntPtr]::Zero) '预热进程没有可见的主窗口'
    Assert (@(Get-Process -Name 'exdir' -ErrorAction SilentlyContinue).Count -eq 1) '只有一个 exdir 进程'

    # ================================================================== 用例 2：第二次启动把窗口唤出来
    Write-Host '--- 用例 2：预热进程在跑时再启动一次 exdir（应瞬时唤回窗口） ---'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $second = Start-Exdir
    $second.WaitForExit(20000) | Out-Null
    $sw.Stop()
    Write-Host ("  第二次启动的进程用时 {0} ms 退出" -f $sw.ElapsedMilliseconds)

    Assert $second.HasExited '第二次启动的进程自己退出了（请求转交给了已在运行的实例）'
    Assert (@(Get-Process -Name 'exdir' -ErrorAction SilentlyContinue).Count -eq 1) '仍然只有一个 exdir 进程（单实例闸门生效）'
    Assert (Wait-LogContains -Pattern '收到其它实例的请求：唤回主窗口') '日志记下了「收到其它实例的请求：唤回主窗口」'
    Assert (Wait-LogContains -Pattern '窗口已从托盘唤回') '日志记下了「窗口已从托盘唤回」'
    Assert ((Wait-VisibleWindow -Process $preload) -ne [IntPtr]::Zero) '预热进程的主窗口真的显示出来了（不用重建任何东西）'

    # ================================================================== 用例 3：预热进程照样能按参数导航
    Write-Host '--- 用例 3：预热进程收下 exdir <目录> 的请求并新开标签页 ---'
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null

    $third = Start-Process -FilePath $exePath -ArgumentList @($testDir) -WorkingDirectory $exeDir -PassThru
    $third.WaitForExit(20000) | Out-Null
    Assert $third.HasExited '带路径的第二次启动也自己退出了'
    Assert (Wait-LogContains -Pattern "命令行：路径参数 → $testDir") '日志记下了转发过来的路径参数'
    Assert (Wait-LogContains -Pattern '命令行：在新标签页打开') '日志记下了「在新标签页打开 <测试目录>」'
}
finally {
    Stop-AllExdir

    if ($null -ne $originalSettings) {
        Set-Content $settingsPath $originalSettings -Encoding utf8
        Write-Host '已还原 config.json'
    }

    if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
