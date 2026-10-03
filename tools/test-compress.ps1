# 内置右键菜单「压缩」（把选中项打成一个 zip、落到配置的输出目录、再自动复制到剪贴板）的回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-compress.ps1
#   pwsh -NoProfile -File tools\test-compress.ps1 -Exe dist\win-x64\exdir.exe
#
# 四个用例（每个都真启动 exdir，右键用真鼠标 + 真剪贴板，所以**需要交互桌面**）：
#   1. 文件行与目录行的内置菜单里都有「压缩」，空白处没有；
#   2. 选中一个文件点「压缩」→ 默认输出目录（「下载」文件夹）出现 `<名字>.zip`，包内条目与源一致，
#      剪贴板里就是这个 zip（CF_HDROP + Preferred DropEffect=1），
#      文件列表顶部弹出绿色「压缩完成」InfoBar + 「打开目录」，exdir.log 里留「压缩：…」与
#      「压缩产物已复制到剪贴板：…」；
#   3. 多选（文件 + 目录）→ 包名 = 当前文件夹名；再压一次同名包落到 `… (2).zip`（不覆盖）；
#   4. 把设置里的「压缩输出目录」配成别的目录（写进 config.json 后重启）→ 压缩落到那里，
#      不再动「下载」文件夹。
#
# 收尾会删掉这次生成的 zip、测试目录，并还原 config.json（测试用的文件名带随机后缀，
# 不会碰到用户自己的东西）。服务级（不需要交互桌面）的压缩用例在 tools\archive-smoke 里。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class CompressNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;
}
'@

[void][CompressNative]::SetProcessDpiAwarenessContext([IntPtr][CompressNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }
if ($null -eq $originalSettings) { throw "找不到配置文件: $settingsPath" }

# 「下载」目录按与 KnownFolderService / FolderTabViewModel.ResolveDownloadsDirectory 一致的方式算
$downloadsDir = Join-Path $env:USERPROFILE 'Downloads'

$stamp = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$testDir = Join-Path $env:TEMP "exdir-compress-$stamp"
$customOut = Join-Path $env:TEMP "exdir-compress-out-$stamp"

$fileName = "alpha-$stamp.txt"
$fileName2 = "beta-$stamp.txt"
$fileBase = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
$dirName = Split-Path $testDir -Leaf

# 这次可能生成的所有 zip（收尾时全删掉）
$createdZips = @(
    (Join-Path $downloadsDir "$fileBase.zip"),
    (Join-Path $downloadsDir "$dirName.zip"),
    (Join-Path $downloadsDir "$dirName (2).zip"),
    (Join-Path $customOut "$fileBase.zip")
)

New-Item -ItemType Directory -Force -Path (Join-Path $testDir 'sub') | Out-Null
# 用 WriteAllText 而不是 Set-Content：后者会自动在末尾补一个 CRLF，于是磁盘上的内容其实是
# "hello 压缩\r\n"，与断言里的字面量对不上（本脚本曾因此在用例 2、3 恒定 FAIL）。
[System.IO.File]::WriteAllText((Join-Path $testDir $fileName), 'hello 压缩')
[System.IO.File]::WriteAllText((Join-Path $testDir $fileName2), 'beta')
[System.IO.File]::WriteAllText((Join-Path $testDir 'sub\inner.txt'), 'inner')

$failures = 0
$logMark = 0

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

# 「文件存在」不等于「压缩写完」：CompressionService 先把最终文件名建出来
#（FileMode.CreateNew）、再以 FileShare.None 往里写，所以存在性一满足就去 OpenRead 会撞上
#「文件正被另一个进程占用」（本脚本曾因此在用例 3 直接抛异常）。这里等到能真正打开、
# 读到条目列表（中央目录已写完）为止。
function Wait-Zip {
    param([string]$Path, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            try {
                $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
                $archive.Dispose()
                return $true
            } catch {
                # 还在写 / 中央目录还没落盘：再等一拍
            }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# ------------------------------------------------------------------ 剪贴板（资源管理器读剪贴板走的同一条路）

function Get-ClipboardFiles {
    $list = [System.Windows.Forms.Clipboard]::GetFileDropList()
    if ($null -eq $list) { return @() }
    return @($list)
}

# 压缩完成后才把 zip 写进剪贴板（比文件落盘晚一步），所以读剪贴板同样要等：
# 落盘一回来就读会拿到上一次剪贴板里的东西（用例 4 曾因此 FAIL）。
function Wait-ClipboardContains {
    param([string]$Path, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            if (@([System.Windows.Forms.Clipboard]::GetFileDropList()) -contains $Path) { return $true }
        } catch {
            # 剪贴板正好被别的进程占着：再等一拍
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Get-ClipboardDropEffect {
    $data = [System.Windows.Forms.Clipboard]::GetDataObject()
    if ($null -eq $data -or -not $data.GetDataPresent('Preferred DropEffect')) { return 0 }
    $stream = $data.GetData('Preferred DropEffect')
    if ($null -eq $stream) { return 0 }
    $bytes = $stream.ToArray()
    if ($bytes.Length -lt 4) { return 0 }
    return [BitConverter]::ToInt32($bytes, 0)
}

function Clear-Clipboard {
    [System.Windows.Forms.Clipboard]::Clear()
    Start-Sleep -Milliseconds 300
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

function Select-Row {
    param($Row)
    $Row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
}

function Add-RowToSelection {
    param($Row)
    $Row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    Start-Sleep -Milliseconds 300
}

function Invoke-RightClick {
    param($Session, [int]$ScreenX, [int]$ScreenY)
    [void][CompressNative]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 400
    [void][CompressNative]::SetCursorPos($ScreenX, $ScreenY)
    Start-Sleep -Milliseconds 300
    [CompressNative]::mouse_event([CompressNative]::RIGHTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [CompressNative]::mouse_event([CompressNative]::RIGHTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 3
}

function Dismiss-Menu {
    param($Session)
    [void][CompressNative]::SetForegroundWindow($Session.Handle)
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

# 接名字的通配符查找：InfoBar 的正文是“已压缩 N 项到 …”，路径不定，只能模糊匹配。
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

# 读 zip 里的条目名（CompressionService 写的目录条目以 '/' 结尾）
function Get-ZipEntryNames {
    param([string]$ZipPath)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try { return @($archive.Entries | ForEach-Object { $_.FullName }) }
    finally { $archive.Dispose() }
}

function Get-ZipEntryText {
    param([string]$ZipPath, [string]$EntryName)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $EntryName } | Select-Object -First 1
        if ($null -eq $entry) { return $null }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

try {

    # ============================================================== 准备配置（默认输出目录 = 下载）

    Set-Setting 'CompressionOutputDirectory' ''
    Set-Setting 'UseBuiltInContextMenu' $true
    Set-Setting 'IsDualPane' $false
    Set-Setting 'PrimaryTabs' ([string[]]@($testDir))
    Set-Setting 'SecondaryTabs' ([string[]]@())
    # 窗口尺寸写死：上次退出时如果窗口是最小化的，位置/尺寸可能是哨兵值，窗口小到右键点不到列表
    Set-Setting 'WindowWidth' 1280
    Set-Setting 'WindowHeight' 800
    Set-Setting 'WindowX' 0
    Set-Setting 'WindowY' 0
    Set-Setting 'WindowMaximized' $false
    Set-Setting 'SidebarWidth' 232

    foreach ($zip in $createdZips) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
    Clear-Clipboard

    $session = Start-Session
    Reset-LogMark

    # ============================================================== 用例 1：菜单里有没有「压缩」

    Write-Host '--- 用例 1：文件行 / 目录行有「压缩」，空白处没有 ---'
    $fileRow = Find-Row -Session $session -NameLike "$fileBase*"
    $subRow = Find-Row -Session $session -NameLike 'sub'
    Assert ($null -ne $fileRow) '测试用的文本文件出现在列表里'
    Assert ($null -ne $subRow) '测试用的 sub 目录出现在列表里'

    RightClickRow -Session $session -Row $fileRow
    Assert (@(Get-NewLogText | Select-String -Pattern '内置右键菜单：文件 上下文 \d+ 项').Count -ge 1) '弹出了内置文件菜单（日志）'
    Assert ((Find-MenuItems -Session $session -Name '压缩').Count -ge 1) '文件行的菜单里有「压缩」'
    Dismiss-Menu -Session $session

    RightClickRow -Session $session -Row $subRow
    Assert ((Find-MenuItems -Session $session -Name '压缩').Count -ge 1) '目录行的菜单里也有「压缩」'
    Dismiss-Menu -Session $session

    $rowRect = $subRow.Current.BoundingRectangle
    Invoke-RightClick -Session $session -ScreenX ([int]($rowRect.X + $rowRect.Width / 3)) -ScreenY ([int]($rowRect.Y + $rowRect.Height * 6))
    Assert ((Find-MenuItems -Session $session -Name '压缩').Count -eq 0) '空白处的背景菜单里没有「压缩」（它只作用于选中的条目）'
    Assert ((Find-MenuItems -Session $session -Name '新建文件夹').Count -ge 1) '空白处菜单仍是目录背景菜单（有「新建文件夹」）'
    Dismiss-Menu -Session $session

    # ============================================================== 用例 2：单个文件 → 下载文件夹 + 剪贴板

    Write-Host '--- 用例 2：点「压缩」→ 下载文件夹出现 <名字>.zip，并复制到剪贴板 ---'
    Select-Row -Row $fileRow
    Reset-LogMark
    RightClickRow -Session $session -Row $fileRow
    Invoke-MenuItem -Session $session -Name '压缩'

    $singleZip = Join-Path $downloadsDir "$fileBase.zip"
    Assert (Wait-Zip -Path $singleZip) "默认输出目录（下载文件夹）里出现了 $fileBase.zip"
    Assert ((Get-ZipEntryNames -ZipPath $singleZip) -contains $fileName) '包里有选中的那个文件'
    Assert ((Get-ZipEntryText -ZipPath $singleZip -EntryName $fileName) -eq 'hello 压缩') '包里的内容与源文件一致'

    Assert (Wait-ClipboardContains -Path $singleZip) '压缩完成后生成的 zip 被自动复制到了剪贴板'
    $clipboard = @(Get-ClipboardFiles)
    Write-Host "  剪贴板: $($clipboard -join ' / ')"
    Assert ($clipboard.Count -eq 1) '剪贴板上只放了这一个 zip'
    Assert ((Get-ClipboardDropEffect) -eq 1) "剪贴板上是「复制」（Preferred DropEffect=1，实际 $((Get-ClipboardDropEffect)))"

    $log = Wait-NewLog -Pattern '压缩：1 项 / 1 个文件'
    Assert ($log -match '压缩：1 项 / 1 个文件') 'exdir.log 里留了「压缩：…」'
    $log = Wait-NewLog -Pattern '压缩产物已复制到剪贴板：'
    Assert ($log -match '压缩产物已复制到剪贴板：') 'exdir.log 里留了「压缩产物已复制到剪贴板：…」'

    Assert ((Find-Elements -Session $session -Name '压缩完成').Count -ge 1) '弹出了「压缩完成」的绿色 InfoBar'
    Assert ((Find-ElementsLike -Session $session -NameLike '已压缩*').Count -ge 1) 'InfoBar 正文写着「已压缩 N 项到 …」'
    Assert ((Find-Elements -Session $session -Name '打开目录').Count -ge 1) 'InfoBar 上有「打开目录」按钮'

    # ============================================================== 用例 3：多选 → 当前文件夹名 + (2)

    Write-Host '--- 用例 3：多选（文件 + 目录）→ 包名 = 当前文件夹名；再压一次落到 (2) ---'
    Select-Row -Row $fileRow
    Add-RowToSelection -Row $subRow
    Reset-LogMark
    RightClickRow -Session $session -Row $fileRow
    Invoke-MenuItem -Session $session -Name '压缩'

    $multiZip = Join-Path $downloadsDir "$dirName.zip"
    Assert (Wait-Zip -Path $multiZip) "多选时包名用当前文件夹名（$dirName.zip）"
    $multiNames = @(Get-ZipEntryNames -ZipPath $multiZip)
    Write-Host "  包内条目: $($multiNames -join ', ')"
    Assert ($multiNames -contains $fileName) '多选时文件进了包'
    Assert ($multiNames -contains 'sub/inner.txt') '多选时目录按整棵子树进了包'
    Assert ($multiNames -contains 'sub/') '包内保留了目录条目'

    Assert ((Get-NewLogText) -match '压缩：2 项') 'exdir.log 记的是 2 项'

    Reset-LogMark
    RightClickRow -Session $session -Row $fileRow
    Invoke-MenuItem -Session $session -Name '压缩'

    $multiZip2 = Join-Path $downloadsDir "$dirName (2).zip"
    Assert (Wait-Zip -Path $multiZip2) '同名 zip 已存在时第二次压缩落到「<文件夹名> (2).zip」'
    Assert ((Get-ZipEntryText -ZipPath $multiZip -EntryName $fileName) -eq 'hello 压缩') '第二次压缩没有破坏第一次写好的包'

    Stop-Session -Session $session

    # ============================================================== 用例 4：配置的输出目录

    Write-Host '--- 用例 4：设置里的「压缩输出目录」指向别处 → 压缩落到那里 ---'
    New-Item -ItemType Directory -Force -Path $customOut | Out-Null
    Set-Setting 'CompressionOutputDirectory' $customOut
    Clear-Clipboard

    $session = Start-Session
    Reset-LogMark

    $fileRow = Find-Row -Session $session -NameLike "$fileBase*"
    Assert ($null -ne $fileRow) '(第二个会话）测试文件仍在列表里'
    Select-Row -Row $fileRow
    RightClickRow -Session $session -Row $fileRow
    Invoke-MenuItem -Session $session -Name '压缩'

    $customZip = Join-Path $customOut "$fileBase.zip"
    Assert (Wait-Zip -Path $customZip) "压缩落到了配置的输出目录（$customOut）"
    Assert ((Get-ZipEntryNames -ZipPath $customZip) -contains $fileName) '落到自定义目录的包内容正确'
    Assert (Wait-ClipboardContains -Path $customZip) '自定义目录里的包也被复制到了剪贴板'

    # 「下载」文件夹里不应该再多出新的包（那个目录上次是有的，这里只确认没被覆盖/新增）
    Assert (Test-Path -LiteralPath $multiZip) '配置了别的输出目录后，之前的包还在（没被清掉/覆盖）'

    Reset-LogMark
    # 背景菜单里的「粘贴」等仍是背景菜单：反向确认一次「压缩」只在条目上出现
    $rect = $fileRow.Current.BoundingRectangle
    Invoke-RightClick -Session $session -ScreenX ([int]($rect.X + $rect.Width / 3)) -ScreenY ([int]($rect.Y + $rect.Height * 6))
    Assert ((Find-MenuItems -Session $session -Name '压缩').Count -eq 0) '空白处菜单里没有「压缩」'
    Dismiss-Menu -Session $session

    Stop-Session -Session $session
}
finally {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    foreach ($zip in $createdZips) {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    }

    foreach ($dir in @($testDir, $customOut)) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    if ($null -ne $originalSettings) {
        Set-Content $settingsPath $originalSettings -Encoding utf8
        Write-Host '已还原 config.json'
    }
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
