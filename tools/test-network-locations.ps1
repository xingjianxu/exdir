# 侧边栏「此电脑」里显示 Windows「网络位置」的回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-network-locations.ps1
#   pwsh -NoProfile -File tools\test-network-locations.ps1 -Exe dist\win-x64\exdir.exe
#
# 思路：Windows 的「网络位置」（资源管理器里「添加一个网络位置」造出来的东西）在磁盘上就是
# %APPDATA%\Microsoft\Windows\Network Shortcuts 下的一个子目录，里面放一个 target.lnk。
# 脚本自己造一个这样的目录（目标指向一个临时目录，不用真的联网），断言：
#   1. 启动时它出现在侧边栏「此电脑」分组里，显示名 = 目录名，且排在磁盘之后；
#   2. 点它（UIA InvokePattern）能导航到 target.lnk 的目标目录（文件列表里出现目标里的文件）；
#   3. 收到 WM_DEVICECHANGE 刷新后它还在（差量刷新不会把网络位置误删）；
#   4. 把网络位置目录删掉后再刷新，它就消失了。
#
# 全程 UIA + SendMessage（与 test-drive-hotplug.ps1 同一套路），不需要交互桌面。
# 跑完会删掉伪造的网络位置与目标目录，并还原 settings.json。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeNetLoc {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVNODES_CHANGED = 0x0007;
}
'@

[void][NativeNetLoc]::SetProcessDpiAwarenessContext([IntPtr][NativeNetLoc]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$settingsPath = Join-Path $env:LOCALAPPDATA 'exdir\settings.json'

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ 伪造一个网络位置

$locationName = 'exdir-netloc-test'
$shortcutRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Network Shortcuts'
$locationDir = Join-Path $shortcutRoot $locationName
$targetDir = Join-Path $env:TEMP 'exdir-netloc-target'
$markerName = 'marker-netloc.txt'

function Remove-TestNetworkLocation {
    if (Test-Path $script:locationDir) {
        Remove-Item $script:locationDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Remove-TestNetworkLocation
if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
New-Item -ItemType Directory -Path $targetDir | Out-Null
Set-Content (Join-Path $targetDir $markerName) 'exdir network location test' -Encoding utf8

New-Item -ItemType Directory -Path $locationDir | Out-Null
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut((Join-Path $locationDir 'target.lnk'))
$sc.TargetPath = $targetDir
$sc.Save()
Write-Host "伪造网络位置: $locationDir -> $targetDir"

# ------------------------------------------------------------------ settings.json

if (-not (Test-Path $settingsPath)) {
    $boot = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $settingsPath)) { Start-Sleep -Milliseconds 500 }
    try { if (-not $boot.HasExited) { $boot.Kill() } } catch { }
    if (-not (Test-Path $settingsPath)) { throw "启动一次后仍然没有 settings.json: $settingsPath" }
}

$originalSettings = Get-Content $settingsPath -Raw

$json = $originalSettings | ConvertFrom-Json
$json.IsSidebarVisible = $true
$json.SidebarShowComputer = $true
$json.ShowToolbar = $true
$json.IsDualPane = $false
$json.WindowMaximized = $false
$json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

# ------------------------------------------------------------------ UIA 小工具

function Start-Session {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 500

    $proc = Start-Process -FilePath $script:exePath -WorkingDirectory (Split-Path $script:exePath) -PassThru
    $handle = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $proc.MainWindowHandle; break }
    }
    if ($handle -eq [IntPtr]::Zero) { throw '未出现主窗口' }

    Start-Sleep -Seconds 5
    return [pscustomobject]@{
        Proc   = $proc
        Handle = $handle
        Root   = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    }
}

function Stop-Session {
    param($Session)
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 800
}

function Find-ByType {
    param($From, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# 侧边栏树里的每一行（名字 + Y 坐标）。WinUI 的 TreeView 在 UIA 里是平铺的，按 Y 排序就是视觉顺序。
function Get-SidebarRows {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::TreeItem) |
        ForEach-Object { [pscustomobject]@{ Name = $_.Current.Name; Y = $_.Current.BoundingRectangle.Y; Element = $_ } })
}

function Find-SidebarRow {
    param($Session, [string]$Name)
    return @(Get-SidebarRows -Session $Session | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)
}

function Send-DeviceChange {
    param($Session)
    [void][NativeNetLoc]::SendMessage(
        $Session.Handle, [NativeNetLoc]::WM_DEVICECHANGE, [IntPtr][NativeNetLoc]::DBT_DEVNODES_CHANGED, [IntPtr]::Zero)
    # 刷新是“合并成一次稍后再刷”+“再补一次兜底”，等过第一轮
    Start-Sleep -Seconds 4
}

# ================================================================== 开跑

$session = $null

try {
    $session = Start-Session

    $rows = Get-SidebarRows -Session $session
    Write-Host ("--- 侧边栏: {0}" -f (($rows | Sort-Object Y | ForEach-Object { $_.Name }) -join ' / '))

    Write-Host '--- 用例 1：网络位置出现在「此电脑」里，排在磁盘之后 ---'
    $row = Find-SidebarRow -Session $session -Name $locationName
    Assert ($null -ne $row) "侧边栏里出现了网络位置「$locationName」（目标 $targetDir）"

    $lastDriveY = @($rows | Where-Object { $_.Name -match '^[A-Za-z]:' } | ForEach-Object { $_.Y } | Measure-Object -Maximum).Maximum
    if ($null -ne $row) {
        Assert ($row.Y -gt $lastDriveY) '网络位置排在磁盘之后的「此电脑」分组里'
    }

    Write-Host '--- 用例 2：点它能导航到 target.lnk 的目标目录 ---'
    if ($null -ne $row) {
        $invoke = $row.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Start-Sleep -Seconds 3

        $listItems = @(Find-ByType -From $session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
            ForEach-Object { $_.Current.Name })
        Write-Host ("  文件列表: {0}" -f (($listItems | Select-Object -First 8) -join ' / '))
        Assert ($listItems -contains $markerName) "文件列表里有目标目录中的 $markerName（说明进入的是 target.lnk 的目标）"
    }

    Write-Host '--- 用例 3：收到 WM_DEVICECHANGE 刷新后网络位置仍在（差量刷新不误删） ---'
    Send-DeviceChange -Session $session
    $rowAfter = Find-SidebarRow -Session $session -Name $locationName
    Assert ($null -ne $rowAfter) '设备变化刷新后网络位置仍在侧边栏里'

    Write-Host '--- 用例 4：网络位置被移除后再刷新就消失 ---'
    Remove-TestNetworkLocation
    Send-DeviceChange -Session $session
    $rowGone = Find-SidebarRow -Session $session -Name $locationName
    Assert ($null -eq $rowGone) '网络位置目录删掉、刷新后它从侧边栏消失'
}
finally {
    if ($null -ne $session) { Stop-Session -Session $session }

    Remove-TestNetworkLocation
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }

    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 settings.json 并清掉伪造的网络位置'
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
