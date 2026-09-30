# 磁盘热插拔（U 盘插入 / 拔出）实时刷新侧边栏与工具条的回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-drive-hotplug.ps1
#   pwsh -NoProfile -File tools\test-drive-hotplug.ps1 -Exe dist\win-x64\exdir.exe
#
# 思路：用 `subst` 造一个临时盘符当“U 盘”（Windows 的 DriveInfo.GetDrives() 会把它列出来，
# 卷标取目标卷的卷标），再给主窗口发一条 WM_DEVICECHANGE —— 真 U 盘插拔走的也是这条消息
# （exdir 的监听在 Services/Native/VolumeChangeWatcher）。
#
# 为什么发 DBT_DEVNODES_CHANGED（wParam=7、lParam=0）而不是带 DEV_BROADCAST_VOLUME 的
# DBT_DEVICEARRIVAL：后者的 lParam 是**指针**，跨进程发消息会让目标进程去读自己地址空间里的
# 野指针（可能直接把 exdir 打死）。设备树变化事件没有 lParam，安全且同样会触发刷新。
#
# 三个用例：
#   1. 插入：发消息前侧边栏 / 工具条里都没有这个盘符；发消息后两处都出现新盘，
#      并且 exdir.log 里多了一行「安排刷新磁盘」（证明消息真的被 exdir 处理了，不是碰巧刷到的）；
#   2. 拔出：subst /d 后再发一条消息，两处都消失；
#   3. 无副作用：侧边栏节点清单与插入前完全一致（差量刷新没有重建整棵树），
#      收藏夹子项仍与 settings.json 的 PinnedFolders 一致。
#
# 全程 UIA + SendMessage，不需要交互桌面。跑完会删掉 subst 映射、临时目录，并还原 settings.json。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // WM_DEVICECHANGE / DBT_DEVNODES_CHANGED：设备树变了（插拔 U 盘一定会带上它）
    public const uint WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVNODES_CHANGED = 0x0007;
}
'@

[void][Native]::SetProcessDpiAwarenessContext([IntPtr][Native]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$settingsPath = Join-Path $env:LOCALAPPDATA 'exdir\settings.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ 临时“U 盘”

$volumeDir = Join-Path $env:TEMP 'exdir-hotplug'
if (Test-Path $volumeDir) { Remove-Item $volumeDir -Recurse -Force }
New-Item -ItemType Directory -Path $volumeDir | Out-Null
Set-Content (Join-Path $volumeDir 'readme.txt') 'exdir hotplug test' -Encoding utf8

# 上一次跑崩了可能留下同一个目标目录的映射，先摘掉
function Remove-StaleSubst {
    param([string]$Target)
    foreach ($line in (subst)) {
        if ($line -match '^([A-Za-z]):\\: => (.+)$' -and $Matches[2].TrimEnd('\') -ieq $Target.TrimEnd('\')) {
            subst "$($Matches[1]):" /d
        }
    }
}
Remove-StaleSubst -Target $volumeDir

$usedLetters = @([System.IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1) })
$letter = @('E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z') |
    Where-Object { $usedLetters -notcontains $_ } |
    Select-Object -First 1
if (-not $letter) { throw '没有可用的盘符（E: ~ Z: 全被占了）' }

$drivePrefix = "${letter}:"
Write-Host "临时盘符 $drivePrefix → $volumeDir"

# ------------------------------------------------------------------ settings.json

if (-not (Test-Path $settingsPath)) {
    # 从没跑过 exdir：先启动一次让它把默认设置写出来
    $boot = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $settingsPath)) { Start-Sleep -Milliseconds 500 }
    try { if (-not $boot.HasExited) { $boot.Kill() } } catch { }
    if (-not (Test-Path $settingsPath)) { throw "启动一次后仍然没有 settings.json: $settingsPath" }
}

$originalSettings = Get-Content $settingsPath -Raw

# 让会话确定地打开一个有内容的目录，并且保证侧边栏「此电脑」分组是显示的（不然断言不到盘符）
$json = $originalSettings | ConvertFrom-Json
$json.PrimaryTabs = @($volumeDir)
$json.PrimaryActiveTab = 0
$json.IsSidebarVisible = $true
$json.ShowToolbar = $true
$json.SidebarShowComputer = $true
$json.SidebarShowFavorites = $true
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
    # exdir 关窗口只是隐藏到托盘（隐藏前已落盘），收尾直接 Kill
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 800
}

function Find-ByType {
    param($From, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# 侧边栏树的每一行（分组 + 已展开的子节点）
function Get-SidebarItems {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::TreeItem) |
        ForEach-Object { $_.Current.Name })
}

# 「收藏夹」分组的子项。WinUI 的 TreeView 在 UIA 里是**平铺**的（子行不是父行的后代），
# 所以按 Y 坐标排序后从「收藏夹」那一行往下读，碰到下一个分组标题就停。
function Get-FavoriteItems {
    param($Session)

    $groups = @('主目录', '收藏夹', '云存储', '此电脑')
    $rows = @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::TreeItem) |
        ForEach-Object { [pscustomobject]@{ Name = $_.Current.Name; Y = $_.Current.BoundingRectangle.Y } } |
        Sort-Object Y)

    $start = -1
    for ($i = 0; $i -lt $rows.Count; $i++) {
        if ($rows[$i].Name -eq '收藏夹') { $start = $i; break }
    }

    if ($start -lt 0) { return @() }

    $result = @()
    for ($i = $start + 1; $i -lt $rows.Count; $i++) {
        if ($groups -contains $rows[$i].Name) { break }
        $result += $rows[$i].Name
    }

    return $result
}

# 工具条左侧的磁盘按钮（AutomationProperties.Name = DriveModel.ToolbarText）
function Get-ToolbarButtons {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::Button) |
        ForEach-Object { $_.Current.Name })
}

function Test-HasDrive {
    param([string[]]$Names)
    return @($Names | Where-Object { $_ -like "${drivePrefix}*" }).Count
}

function Send-DeviceChange {
    param($Session)
    [void][Native]::SendMessage(
        $Session.Handle, [Native]::WM_DEVICECHANGE, [IntPtr][Native]::DBT_DEVNODES_CHANGED, [IntPtr]::Zero)

    # 消息处理器是“合并成一次稍后再刷”+“再补一次兜底刷新”，这里等过第一轮
    Start-Sleep -Seconds 3
}

function Get-DeviceChangeLogCount {
    if (-not (Test-Path $script:logPath)) { return 0 }
    return @(Select-String -Path $script:logPath -Pattern '安排刷新磁盘' -ErrorAction SilentlyContinue).Count
}

# ================================================================== 开跑

$session = $null
$substCreated = $false

try {
    $session = Start-Session

    $beforeSidebar = Get-SidebarItems -Session $session
    Write-Host ("--- 启动时的侧边栏: {0}" -f ($beforeSidebar -join ' / '))

    $expectedFavorites = @($json.PinnedFolders | ForEach-Object { [System.IO.Path]::GetFileName($_.TrimEnd('\')) })
    $actualFavorites = Get-FavoriteItems -Session $session
    Write-Host ("  收藏夹: {0}" -f ($actualFavorites -join ' / '))
    Assert (($actualFavorites -join '|') -eq ($expectedFavorites -join '|')) `
        "侧边栏「收藏夹」与 settings.json 的 $($expectedFavorites.Count) 项固定目录一致"

    Write-Host '--- 用例 1：插入（subst 一个盘 + 发设备变化消息） ---'
    Assert ((Test-HasDrive -Names $beforeSidebar) -eq 0) "插入前侧边栏里没有 $drivePrefix"
    Assert ((Test-HasDrive -Names (Get-ToolbarButtons -Session $session)) -eq 0) "插入前工具条里没有 $drivePrefix"

    subst "$drivePrefix" $volumeDir
    $substCreated = $true

    $logBefore = Get-DeviceChangeLogCount
    Send-DeviceChange -Session $session
    $logAfter = Get-DeviceChangeLogCount

    Assert ($logAfter -gt $logBefore) "发消息后 exdir 收到了 WM_DEVICECHANGE（exdir.log：$logBefore → $logAfter 行）"

    $sidebar = Get-SidebarItems -Session $session
    $buttons = Get-ToolbarButtons -Session $session
    Write-Host ("  侧边栏: {0}" -f ($sidebar -join ' / '))
    Assert ((Test-HasDrive -Names $sidebar) -ge 1) "侧边栏「此电脑」里出现了 $drivePrefix"
    Assert ((Test-HasDrive -Names $buttons) -ge 1) "工具条磁盘区里出现了 $drivePrefix"

    Write-Host '--- 用例 2：拔出（subst /d + 再发一条消息） ---'
    subst "$drivePrefix" /d
    $substCreated = $false

    $logBefore = Get-DeviceChangeLogCount
    Send-DeviceChange -Session $session
    Assert ((Get-DeviceChangeLogCount) -gt $logBefore) '拔出消息同样被收到'

    $sidebar = Get-SidebarItems -Session $session
    $buttons = Get-ToolbarButtons -Session $session
    Write-Host ("  侧边栏: {0}" -f ($sidebar -join ' / '))
    Assert ((Test-HasDrive -Names $sidebar) -eq 0) "拔出后侧边栏里没有 $drivePrefix"
    Assert ((Test-HasDrive -Names $buttons) -eq 0) "拔出后工具条里没有 $drivePrefix"

    Write-Host '--- 用例 3：一圈插拔之后其它东西没被动过 ---'
    $afterSidebar = Get-SidebarItems -Session $session
    $diff = @(Compare-Object -ReferenceObject ($beforeSidebar | Sort-Object) -DifferenceObject ($afterSidebar | Sort-Object))
    if ($diff.Count -gt 0) { Write-Host ("  差异: {0}" -f (($diff | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join ' / ')) }
    Assert ($diff.Count -eq 0) '侧边栏节点清单与插入前完全一致（差量刷新没有重建整棵树）'

    Assert (@($afterSidebar | Where-Object { $_ -like 'C:*' -or $_ -like 'D:*' }).Count -ge 2) '原有的 C: / D: 仍在侧边栏里'
    $actualFavorites = Get-FavoriteItems -Session $session
    Assert (($actualFavorites -join '|') -eq ($expectedFavorites -join '|')) '收藏夹仍是那几项（刷新没有清掉收藏）'
}
finally {
    if ($substCreated) { subst "$drivePrefix" /d 2>$null }
    if ($null -ne $session) { Stop-Session -Session $session }

    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 settings.json'

    if (Test-Path $volumeDir) { Remove-Item $volumeDir -Recurse -Force }
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
