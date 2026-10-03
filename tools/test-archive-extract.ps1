# 压缩包右键「使用 7-Zip 打开 / 解压到下载文件夹」的回归脚本（真鼠标右键 + UIA 断言）。
#
# 用法:
#   pwsh -NoProfile -File tools\test-archive-extract.ps1
#   pwsh -NoProfile -File tools\test-archive-extract.ps1 -Exe dist\win-x64\exdir.exe
#
# 四个用例（每个都真的启动 exdir，右键用真鼠标，所以**需要交互桌面**）：
#   1. 压缩包文件行的内置菜单里有「使用 7-Zip 打开」与「解压到下载文件夹」，
#      而且「使用 7-Zip 打开」的可点状态与本机到底装没装 7-Zip 一致（没装时置灰、标题里写明原因）；
#   2. 普通文件行（.txt）的菜单里没有这两项；
#   3. 点「解压到下载文件夹」→ `%USERPROFILE%\Downloads\<包名>\` 里真的出现了包内文件
#      （含子目录），文件列表顶部弹出绿色「解压完成」InfoBar + 「打开目录」按钮，exdir.log 里留一行「解压：…」；
#   4. 再解一次同名压缩包 → 落到 `<包名> (2)\`（不往已经解出来的目录里混）。
#
# 收尾会删掉这次解压出来的目录并还原 config.json —— 用例用的是带随机后缀的包名，不会碰到用户的东西。
#
# 服务级（不需要交互桌面）的解压用例在 tools\archive-smoke 里，见 AGENTS.md 第 2 节。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ExtractMenuNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;
}
'@

[void][ExtractMenuNative]::SetProcessDpiAwarenessContext([IntPtr][ExtractMenuNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }
if ($null -eq $originalSettings) { throw "找不到配置文件: $settingsPath" }

# 「下载」目录按与 KnownFolderService 一致的方式算（%USERPROFILE%\Downloads；用户重定向过也一样，
# 因为 KnownFolderService 自己也是这么拼的，见 Services/KnownFolderService.cs）
$downloadsDir = Join-Path $env:USERPROFILE 'Downloads'
$stamp = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$archiveName = "exdir-extract-test-$stamp"
$unpackedDirs = @((Join-Path $downloadsDir $archiveName), (Join-Path $downloadsDir "$archiveName (2)"))

# 测试现场：一个压缩包（里面有子目录）+ 一个普通文本文件
$testDir = Join-Path $env:TEMP "exdir-extract-test-$stamp"
$sourceDir = Join-Path $testDir 'src'
New-Item -ItemType Directory -Force -Path (Join-Path $sourceDir 'sub') | Out-Null
Set-Content (Join-Path $sourceDir 'hello.txt') 'hello 世界' -Encoding utf8
Set-Content (Join-Path $sourceDir 'sub\inner.txt') 'inner' -Encoding utf8
Set-Content (Join-Path $testDir 'plain.txt') 'not an archive' -Encoding utf8

$zipPath = Join-Path $testDir "$archiveName.zip"
Compress-Archive -Path (Join-Path $sourceDir '*') -DestinationPath $zipPath -Force

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ config.json / 日志

function Get-Setting {
    param([string]$Name)
    return (Get-Content $script:settingsPath -Raw | ConvertFrom-Json).$Name
}

function Set-Setting {
    param([string]$Name, $Value)
    $json = Get-Content $script:settingsPath -Raw | ConvertFrom-Json
    # 老版本的 config.json 里可能还没有这个字段，用 Add-Member -Force 更稳
    $json | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content $script:settingsPath -Encoding utf8
}

function Get-LogText {
    if (-not (Test-Path $script:logPath)) { return '' }
    return (Get-Content $script:logPath -Raw)
}

function Reset-LogMark { $script:logMark = (Get-LogText).Length }

function Get-NewLogText {
    $text = Get-LogText
    if ($text.Length -le $script:logMark) { return '' }
    return $text.Substring($script:logMark)
}

function Wait-NewLog {
    param([string]$Pattern, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $text = Get-NewLogText
        if ($text -match $Pattern) { return $text }
        Start-Sleep -Milliseconds 400
    }
    return (Get-NewLogText)
}

function Wait-File {
    param([string]$Path, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return (Test-Path -LiteralPath $Path)
}

# ------------------------------------------------------------------ 会话

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
    # exdir 关窗口只是隐藏到托盘（隐藏时已统一落盘），收尾直接 Kill
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 500
}

# ------------------------------------------------------------------ UIA / 鼠标

function Find-Rows {
    param($Session)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($Session.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

function Find-Row {
    param($Session, [string]$NameLike)
    return Find-Rows -Session $Session | Where-Object { $_.Current.Name -like $NameLike } | Select-Object -First 1
}

function Invoke-RightClick {
    param($Session, [int]$ScreenX, [int]$ScreenY)
    [void][ExtractMenuNative]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 400
    [void][ExtractMenuNative]::SetCursorPos($ScreenX, $ScreenY)
    Start-Sleep -Milliseconds 300
    [ExtractMenuNative]::mouse_event([ExtractMenuNative]::RIGHTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [ExtractMenuNative]::mouse_event([ExtractMenuNative]::RIGHTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 3
}

function Dismiss-Menu {
    param($Session)
    [void][ExtractMenuNative]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 2
}

function RightClickRow {
    param($Session, $Row)
    $rect = $Row.Current.BoundingRectangle
    Invoke-RightClick -Session $Session -ScreenX ([int]($rect.X + $rect.Width / 3)) -ScreenY ([int]($rect.Y + $rect.Height / 2))
}

# 内置（自建）右键菜单是 WinUI MenuFlyout：不在主窗口的 UIA 子树里（见 AGENTS.md 第 6 节第 24 条），
# 要从桌面往下找、按进程过滤，并且只要还没被关掉的（上次关掉的项仍留在树里、只是 offscreen）。
function Find-MenuItems {
    param($Session, [string]$Name)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $items = @()

    foreach ($el in $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($el.Current.ProcessId -ne $Session.Proc.Id) { continue }
        if ($el.Current.ControlType -ne [System.Windows.Automation.ControlType]::MenuItem) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $items += $el
    }

    return $items
}

function Find-Elements {
    param($Session, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return @($Session.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

# 接名字的通配符查找：InfoBar 的正文是“已解压 N 个文件到 …”，路径不定，只能模糊匹配。
function Find-ElementsLike {
    param($Session, [string]$NameLike)
    $all = $Session.Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    return @($all | Where-Object { $_.Current.Name -like $NameLike })
}

function Invoke-MenuItem {
    param($Session, [string]$Name)
    $item = Find-MenuItems -Session $Session -Name $Name | Select-Object -First 1
    if ($null -eq $item) { throw "内置菜单里找不到「$Name」" }
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# 本机到底装没装 7-Zip 的界面程序（与 Helpers/SevenZipLocator.cs 的查找顺序一致：注册表 → 常见目录 → PATH）
function Test-SevenZipInstalled {
    $keys = @(
        'HKCU:\SOFTWARE\7-Zip',
        'HKLM:\SOFTWARE\7-Zip',
        'HKLM:\SOFTWARE\WOW6432Node\7-Zip'
    )

    foreach ($key in $keys) {
        if (-not (Test-Path $key)) { continue }
        $dir = (Get-ItemProperty -Path $key -Name 'Path' -ErrorAction SilentlyContinue).'Path'
        if ($dir -and (Test-Path (Join-Path $dir '7zFM.exe'))) { return $true }
    }

    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if ($root -and (Test-Path (Join-Path $root '7-Zip\7zFM.exe'))) { return $true }
    }

    foreach ($entry in ($env:PATH -split ';')) {
        $trimmed = $entry.Trim().Trim('"')
        if ($trimmed -and (Test-Path (Join-Path $trimmed '7zFM.exe'))) { return $true }
    }

    return $false
}

try {

    # ============================================================== 准备配置

    Set-Setting 'UseBuiltInContextMenu' $true
    Set-Setting 'IsDualPane' $false
    Set-Setting 'PrimaryTabs' ([string[]]@($testDir))
    # 窗口尺寸写死：上次退出时如果窗口是最小化的，位置/尺寸可能是哨兵值，窗口小到右键点不到列表
    Set-Setting 'WindowWidth' 1280
    Set-Setting 'WindowHeight' 800
    Set-Setting 'WindowX' 0
    Set-Setting 'WindowY' 0
    Set-Setting 'WindowMaximized' $false
    Set-Setting 'SidebarWidth' 232

    $hasSevenZip = Test-SevenZipInstalled
    $sevenZipItemName = if ($hasSevenZip) { '使用 7-Zip 打开' } else { '使用 7-Zip 打开（未找到 7-Zip）' }
    Write-Host ("本机 7-Zip 界面程序: {0}" -f $(if ($hasSevenZip) { '有' } else { '没有' }))

    $session = Start-Session
    Reset-LogMark

    # ============================================================== 用例 1 / 3 / 4：压缩包行

    Write-Host '--- 用例 1：压缩包行的菜单里有「使用 7-Zip 打开」与「解压到下载文件夹」 ---'
    $zipRow = Find-Row -Session $session -NameLike "$archiveName*"
    Assert ($null -ne $zipRow) '测试用的压缩包出现在列表里'

    RightClickRow -Session $session -Row $zipRow
    Assert (@(Get-NewLogText | Select-String -Pattern '内置右键菜单：文件 上下文 \d+ 项').Count -ge 1) '弹出了内置文件菜单（日志）'
    Assert ((Find-MenuItems -Session $session -Name '打开').Count -ge 1) '压缩包行的菜单里有「打开」'
    Assert ((Find-MenuItems -Session $session -Name '解压到下载文件夹').Count -ge 1) '压缩包行的菜单里有「解压到下载文件夹」'

    $sevenZipItem = Find-MenuItems -Session $session -Name $sevenZipItemName | Select-Object -First 1
    Assert ($null -ne $sevenZipItem) "压缩包行的菜单里有「$sevenZipItemName」"

    if ($null -ne $sevenZipItem) {
        Assert ($sevenZipItem.Current.IsEnabled -eq $hasSevenZip) '「使用 7-Zip 打开」的可点状态与本机装没装 7-Zip 一致（没装时置灰）'
    }

    Dismiss-Menu -Session $session
    Assert ((Find-MenuItems -Session $session -Name '解压到下载文件夹').Count -eq 0) 'Esc 之后内置菜单关掉了'

    # ============================================================== 用例 2：普通文件行

    Write-Host '--- 用例 2：普通文件行的菜单里没有这两个入口 ---'
    $plainRow = Find-Row -Session $session -NameLike 'plain*'
    Assert ($null -ne $plainRow) '测试用的 plain.txt 出现在列表里'

    RightClickRow -Session $session -Row $plainRow
    Assert ((Find-MenuItems -Session $session -Name '打开').Count -ge 1) '普通文件行的菜单里有「打开」'
    Assert ((Find-MenuItems -Session $session -Name '解压到下载文件夹').Count -eq 0) '普通文件行没有「解压到下载文件夹」'
    $sevenZipOnPlain = @(Find-MenuItems -Session $session -Name '使用 7-Zip 打开').Count + @(Find-MenuItems -Session $session -Name '使用 7-Zip 打开（未找到 7-Zip）').Count
    Assert ($sevenZipOnPlain -eq 0) '普通文件行没有「使用 7-Zip 打开」'
    Dismiss-Menu -Session $session

    # ============================================================== 用例 3：真的解到下载目录

    Write-Host '--- 用例 3：点「解压到下载文件夹」真的解到 Downloads\<包名>\ ---'
    Get-ChildItem -Path $downloadsDir -Directory -Filter "$archiveName*" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

    Reset-LogMark
    RightClickRow -Session $session -Row $zipRow
    Invoke-MenuItem -Session $session -Name '解压到下载文件夹'

    $targetDir = Join-Path $downloadsDir $archiveName
    Assert (Wait-File -Path (Join-Path $targetDir 'hello.txt')) '下载目录里出现了解压出来的 hello.txt'
    Assert (Wait-File -Path (Join-Path $targetDir 'sub\inner.txt')) '包内的子目录结构也解出来了'
    Assert ((Get-Content (Join-Path $targetDir 'hello.txt') -Raw).Trim() -eq 'hello 世界') '解压出来的文件内容正确'

    $log = Wait-NewLog -Pattern '解压：'
    Assert ($log -match '解压：') 'exdir.log 里留了一行「解压：…」'
    Assert ($log -match '→') '日志里写了解压到的目录'

    Start-Sleep -Seconds 2
    Assert ((Find-ElementsLike -Session $session -NameLike '已解压*').Count -ge 1) '解压完成后弹出了「已解压 …」的绿色 InfoBar'
    Assert ((Find-Elements -Session $session -Name '解压完成').Count -ge 1) 'InfoBar 的标题是「解压完成」'
    Assert ((Find-Elements -Session $session -Name '打开目录').Count -ge 1) 'InfoBar 上有「打开目录」按钮'

    # ============================================================== 用例 4：同名压缩包再解一次

    Write-Host '--- 用例 4：再解一次同名压缩包落到「<包名> (2)」 ---'
    Reset-LogMark
    RightClickRow -Session $session -Row $zipRow
    Invoke-MenuItem -Session $session -Name '解压到下载文件夹'

    $secondDir = Join-Path $downloadsDir "$archiveName (2)"
    Assert (Wait-File -Path (Join-Path $secondDir 'hello.txt')) '同名压缩包第二次解压落到「<包名> (2)」'
    Assert ((Get-Content (Join-Path $targetDir 'hello.txt') -Raw).Trim() -eq 'hello 世界') '第二次解压没有动第一次解出来的目录'

    Stop-Session -Session $session
}
finally {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    # 清掉这次解压出来的目录与测试现场（包名带随机后缀，不会碰到用户自己的东西）
    foreach ($dir in $unpackedDirs) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    if (Test-Path -LiteralPath $testDir) { Remove-Item -LiteralPath $testDir -Recurse -Force -ErrorAction SilentlyContinue }

    if ($null -ne $originalSettings) {
        Set-Content $settingsPath $originalSettings -Encoding utf8
        Write-Host '已还原 config.json'
    }
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
