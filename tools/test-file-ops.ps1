# 复制 / 剪切 / 粘贴与“拖到目录里移动”的回归脚本（真鼠标 + 真键盘 + 真实剪贴板）。
#
# 用法:
#   pwsh -NoProfile -File tools\test-file-ops.ps1
#   pwsh -NoProfile -File tools\test-file-ops.ps1 -Exe dist\win-x64\exdir.exe
#
# 用例（每次都真启动 exdir，窗口坐标写死在 (0,0)、1280×800）：
#   A. 内置右键菜单里文件行有「剪切 / 复制 / 粘贴」，空白处有「粘贴」；
#   B. 选中文件按 Ctrl+C：剪贴板上真的出现了 CF_HDROP（用 System.Windows.Forms 读回来，
#      和资源管理器读剪贴板是同一条路），Preferred DropEffect = 1（复制）；
#   C. 进入子目录按 Ctrl+V：文件被复制过去（源还在），exdir.log 有“文件操作：复制”；
#   D. 选中文件按 Ctrl+X（剪贴板 DropEffect = 2）→ 进子目录 Ctrl+V：文件被移动过去（源没了）；
#   E. 把文件行拖到目录行上：文件被移动过去（exdir.log 有“拖放落下”），
#      拖拽过程中有整行强调色高亮（截图 .artifacts\file-ops-drag.png）；
#   F. 外部来源（本脚本往剪贴板放的 CF_HDROP）→ Ctrl+V：文件被复制进来；
#   G. 外部来源 + Preferred DropEffect = 2 → Ctrl+V：文件被移动进来；
#   H. 内置右键菜单里有「删除」；按 Delete → 外壳确认框 → 文件真的进了回收站
#      （用 Shell.Application 的「回收站」名字空间读回来，与资源管理器看到的是同一份），
#      exdir.log 记下“文件操作：删除到回收站”；
#   I. Shift+Delete → 确认后永久删除（不进回收站），exdir.log 记下“文件操作：永久删除”。
#
# 需要交互桌面（真实鼠标拖动 + SendKeys + 截图）；跑完会还原 settings.json 并删掉测试目录。

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
public static class FileOpsNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint MOVE = 0x0001;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
    public const uint ABSOLUTE = 0x8000;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;
}
'@

[void][FileOpsNative]::SetProcessDpiAwarenessContext([IntPtr][FileOpsNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$shotDir = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\.artifacts")
$settingsPath = Join-Path $env:LOCALAPPDATA 'exdir\settings.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

$testDir = Join-Path $env:TEMP 'exdir-file-ops-test'
$elsewhere = Join-Path $env:TEMP 'exdir-file-ops-elsewhere'
Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $elsewhere -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $testDir 'sub') | Out-Null
New-Item -ItemType Directory -Force -Path $elsewhere | Out-Null
foreach ($name in 'alpha.txt', 'beta.txt', 'gamma.txt') {
    Set-Content (Join-Path $testDir $name) $name -Encoding utf8
}
# 删除用例的文件名每次都不一样：回收站是全系统共用的，同名旧项会让“真的进了回收站”假通过
$delName = "exdir-del-$([Guid]::NewGuid().ToString('N').Substring(0, 8)).txt"
$nukeName = "exdir-nuke-$([Guid]::NewGuid().ToString('N').Substring(0, 8)).txt"
Set-Content (Join-Path $testDir $delName) 'del' -Encoding utf8
Set-Content (Join-Path $testDir $nukeName) 'nuke' -Encoding utf8
Set-Content (Join-Path $elsewhere 'zeta.txt') 'zeta' -Encoding utf8
Set-Content (Join-Path $elsewhere 'eta.txt') 'eta' -Encoding utf8

$failures = 0
$logMark = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ settings.json / 日志

function Get-Setting {
    param([string]$Name)
    return (Get-Content $script:settingsPath -Raw | ConvertFrom-Json).$Name
}

function Set-Setting {
    param([string]$Name, $Value)
    $json = Get-Content $script:settingsPath -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content $script:settingsPath -Encoding utf8
}

function Get-LogTail {
    param([int]$Lines = 40)
    if (-not (Test-Path $script:logPath)) { return @() }
    $all = @(Get-Content $script:logPath)
    if ($script:logMark -le 0 -or $script:logMark -ge $all.Count) { return @() }
    return @($all[$script:logMark..($all.Count - 1)])
}

# 只取本次会话之后新写进去的日志行：同一份 exdir.log 是所有运行共用的，
# 上一次运行/调试脚本的「拖放落下：1 项」也会被 -Tail 40 读到，断言会假通过。
function Set-LogMark {
    $script:logMark = if (Test-Path $script:logPath) { @(Get-Content $script:logPath).Count } else { 0 }
}

# 把 exdir 提到前台：提权脚本给普通权限进程调 SetForegroundWindow 可能失败，重试几次。
function Focus-Window {
    param($Session, [int]$Retries = 6)

    for ($i = 0; $i -lt $Retries; $i++) {
        [void][FileOpsNative]::SetForegroundWindow($Session.Handle)
        Start-Sleep -Milliseconds 200
        if ([FileOpsNative]::GetForegroundWindow() -eq $Session.Handle) { return }
    }
}

# ------------------------------------------------------------------ 剪贴板（就是资源管理器用的那条路）

function Get-ClipboardFiles {
    $list = [System.Windows.Forms.Clipboard]::GetFileDropList()
    if ($null -eq $list) { return @() }
    return @($list)
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

# 模拟“别的程序”（资源管理器）往剪贴板上放一批文件
function Set-ClipboardFiles {
    param([string[]]$Paths, [int]$DropEffect = 1)
    $collection = New-Object System.Collections.Specialized.StringCollection
    foreach ($path in $Paths) { [void]$collection.Add($path) }

    $data = New-Object System.Windows.Forms.DataObject
    $data.SetFileDropList($collection)
    $data.SetData('Preferred DropEffect', (New-Object System.IO.MemoryStream (, [BitConverter]::GetBytes($DropEffect))))

    # copy: true -> OleFlushClipboard，读的时候不依赖本进程还活着
    [System.Windows.Forms.Clipboard]::SetDataObject($data, $true)
    Start-Sleep -Milliseconds 300
}

function Clear-Clipboard {
    [System.Windows.Forms.Clipboard]::Clear()
    Start-Sleep -Milliseconds 300
}

# 回收站里有没有叫这个名字的项：用 Shell.Application 的 10 号名字空间，
# 和资源管理器左侧「回收站」是同一条路（比去翻 $Recycle.Bin 目录靠得住）。
function Test-InRecycleBin {
    param([string]$FileName)
    $shell = New-Object -ComObject Shell.Application
    $bin = $shell.NameSpace(10)
    if ($null -eq $bin) { return $false }
    foreach ($item in $bin.Items()) {
        if ($item.Name -eq $FileName) { return $true }
    }
    return $false
}

# 删除的确认框是外壳（在本进程的 STA 线程上）弹的模态 MessageBox（#32770，标题“删除文件”）：
# UIA 里它并不是桌面下的直接子窗口（是被主窗口 owned 的，挂在主窗口的 Descendants 里），
# 所以必须用 Descendants 找。找到后点它的“是(Y)”；实在找不到按钮就用回车（默认按钮就是“是”）。
# 注意：删除确认框是模态的，它一弹出来 shell 就在那个 STA 线程上等，不进下去文件是不会被删的。
function Confirm-ShellDialog {
    param($Session, [int]$TimeoutMs = 10000)

    $pidCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Session.Proc.Id)
    $buttonCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)

    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    $seenDialog = $false

    while ((Get-Date) -lt $deadline) {
        foreach ($win in $Session.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond)) {
            if ($win.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
            if ($win.Current.NativeWindowHandle -eq [int]$Session.Handle) { continue }
            if ($win.Current.BoundingRectangle.Width -le 0) { continue }

            if (-not $seenDialog) {
                Write-Host "  确认框：$($win.Current.Name)"
                $seenDialog = $true
            }

            foreach ($button in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond)) {
                if ($button.Current.Name -match '^是|^Yes') {
                    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    Start-Sleep -Seconds 1
                    return $true
                }
            }

            # 找不到按钮就用回车（“是”本来就是默认按钮）。这里不能先 Focus-Window：
            # 模态框已经占着前台，把主窗口抢回来反而会把回车送给列表。
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Start-Sleep -Seconds 1
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    Write-Host '  没有出现确认框（系统关掉了删除确认），继续' -ForegroundColor Yellow
    return $false
}

# ------------------------------------------------------------------ 会话

function Start-Session {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    # 当前 shell 是管理员时，Start-Process 拉起来的 exdir 也会是提权进程，
    # 而 Windows 直接禁止提权进程参与拖放（拖拽会“开始”但永远收不到 DragOver/Drop），
    # 走 explorer.exe 让子进程降到普通权限（同 tools\test-pin-drag.ps1）
    $elevated = (New-Object System.Security.Principal.WindowsPrincipal(
            [System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
            [System.Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($elevated) {
        Write-Host '  当前 shell 已提权：用 explorer.exe 以普通权限启动 exdir（拖放测试需要）' -ForegroundColor Yellow
        Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$script:exePath`""

        $proc = $null
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $proc = Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($proc) { break }
        }
        if ($null -eq $proc) { throw 'explorer.exe 没有把 exdir 拉起来' }
    }
    else {
        $proc = Start-Process -FilePath $script:exePath -WorkingDirectory (Split-Path $script:exePath) -PassThru
    }

    Set-LogMark

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
    Start-Sleep -Milliseconds 500
}

# ------------------------------------------------------------------ UIA

function Find-Rows {
    param($Session)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($Session.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

# 文件列表里的行（排除侧边栏树节点：那些的父级是树，这里按“名字 + 有实际矩形”再筛一遍）
function Find-RowByName {
    param($Session, [string]$Name)
    $rows = Find-Rows -Session $Session
    return $rows | Where-Object {
        $_.Current.Name -like $Name -and
        $_.Current.BoundingRectangle.Width -gt 0 -and
        $_.Current.BoundingRectangle.Height -gt 0
    } | Select-Object -First 1
}

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

# ------------------------------------------------------------------ 鼠标 / 键盘

function Move-Mouse {
    param([int]$X, [int]$Y)
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $nx = [uint32][math]::Round($X * 65535 / ($screen.Width - 1))
    $ny = [uint32][math]::Round($Y * 65535 / ($screen.Height - 1))
    [FileOpsNative]::mouse_event([FileOpsNative]::MOVE -bor [FileOpsNative]::ABSOLUTE, $nx, $ny, 0, [IntPtr]::Zero)
}

function Invoke-Click {
    param($Session, [int]$X, [int]$Y, [string]$Button = 'Left')
    Focus-Window -Session $Session
    Start-Sleep -Milliseconds 200
    Move-Mouse $X $Y
    Start-Sleep -Milliseconds 250

    $down = if ($Button -eq 'Right') { [FileOpsNative]::RIGHTDOWN } else { [FileOpsNative]::LEFTDOWN }
    $up = if ($Button -eq 'Right') { [FileOpsNative]::RIGHTUP } else { [FileOpsNative]::LEFTUP }

    [FileOpsNative]::mouse_event($down, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [FileOpsNative]::mouse_event($up, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1400
}

function Invoke-DoubleClick {
    param($Session, [int]$X, [int]$Y)
    Focus-Window -Session $Session
    Start-Sleep -Milliseconds 200
    Move-Mouse $X $Y
    Start-Sleep -Milliseconds 250

    for ($i = 0; $i -lt 2; $i++) {
        [FileOpsNative]::mouse_event([FileOpsNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 40
        [FileOpsNative]::mouse_event([FileOpsNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }

    Start-Sleep -Seconds 3
}

function Invoke-DragTo {
    param($Session, [int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY, [string]$ShotDuring)

    Focus-Window -Session $Session
    Start-Sleep -Milliseconds 400

    # 窗口矩形提前取（拖拽期间不做 UIA 调用：那时应用在拖拽循环里，跨进程 UIA 调用会干扰拖拽）
    $windowRect = $Session.Root.Current.BoundingRectangle

    Move-Mouse $FromX $FromY
    Start-Sleep -Milliseconds 250

    [FileOpsNative]::mouse_event([FileOpsNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150

    # 逐步移动：一次跳跃会被当成“瞬移”，拖拽根本不会启动
    $steps = 24
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($FromX + ($ToX - $FromX) * $i / $steps)
        $y = [int]($FromY + ($ToY - $FromY) * $i / $steps)
        Move-Mouse $x $y
        Start-Sleep -Milliseconds 40
    }

    Start-Sleep -Milliseconds 800

    $cursorNow = New-Object FileOpsNative+POINT
    [void][FileOpsNative]::GetCursorPos([ref]$cursorNow)
    Write-Host ("  拖拽 ({0},{1}) -> ({2},{3})，当前光标 ({4},{5})" -f $FromX, $FromY, $ToX, $ToY, $cursorNow.X, $cursorNow.Y)

    if ($ShotDuring) { Save-Shot -Session $Session -Name $ShotDuring -RectOverride $windowRect }

    [FileOpsNative]::mouse_event([FileOpsNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 3
}

function Send-Keys {
    param($Session, [string]$Keys, [int]$WaitMs = 1500)
    Focus-Window -Session $Session
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $WaitMs
}

function Save-Shot {
    param($Session, [string]$Name, $RectOverride)
    New-Item -ItemType Directory -Force -Path $script:shotDir | Out-Null
    $shot = Join-Path $script:shotDir ($Name + '.png')

    $rect = if ($RectOverride) { $RectOverride } else { $Session.Root.Current.BoundingRectangle }
    $left = [Math]::Max(0, [int]$rect.X); $top = [Math]::Max(0, [int]$rect.Y)
    $w = [Math]::Max(1, [int]$rect.Width); $h = [Math]::Max(1, [int]$rect.Height)

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $gfx.Dispose()
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  截图 $shot"
}

function RowCenter {
    param($Row)
    $rect = $Row.Current.BoundingRectangle
    return @([int]($rect.X + $rect.Width / 3), [int]($rect.Y + $rect.Height / 2))
}

# ------------------------------------------------------------------ 主流程

try {
    if ([FileOpsNative]::GetForegroundWindow() -eq [IntPtr]::Zero) {
        Write-Host '没有交互桌面（GetForegroundWindow 返回 0）：无法模拟鼠标/键盘，只能人工验证。' -ForegroundColor Yellow
        return
    }

    Set-Setting 'PrimaryTabs' ([string[]]@($testDir))
    Set-Setting 'SecondaryTabs' ([string[]]@())
    Set-Setting 'IsDualPane' $false
    Set-Setting 'UseBuiltInContextMenu' $true
    Set-Setting 'ShowHiddenFiles' $false
    Set-Setting 'FoldersFirst' $true
    Set-Setting 'WindowWidth' 1280
    Set-Setting 'WindowHeight' 800
    Set-Setting 'WindowX' 0
    Set-Setting 'WindowY' 0
    Set-Setting 'WindowMaximized' $false
    Set-Setting 'SidebarWidth' 232

    Clear-Clipboard
    $session = Start-Session

    # 预热：第一次鼠标事件常常只用来激活窗口（不送到内容里），先空白处点一下
    Start-Sleep -Seconds 1
    $warmRect = $Session.Root.Current.BoundingRectangle
    Invoke-Click -Session $session -X ([int]($warmRect.X + 600)) -Y ([int]($warmRect.Y + 700))
    Start-Sleep -Seconds 1

    # ============================================================== A. 内置菜单里的三个命令

    Write-Host '--- 用例 A：内置右键菜单里有复制 / 剪切 / 粘贴 ---'
    $alpha = Find-RowByName -Session $session -Name 'alpha*'
    Assert ($null -ne $alpha) '测试目录里的 alpha.txt 出现在列表里'
    Assert ($null -ne (Find-RowByName -Session $session -Name 'sub')) '测试目录里的 sub 目录出现在列表里'

    $point = RowCenter -Row $alpha
    Invoke-Click -Session $session -X $point[0] -Y $point[1] -Button 'Right'
    Assert ((Find-MenuItems -Session $session -Name '剪切').Count -ge 1) '文件行菜单里有「剪切」'
    Assert ((Find-MenuItems -Session $session -Name '复制').Count -ge 1) '文件行菜单里有「复制」'
    Assert ((Find-MenuItems -Session $session -Name '粘贴').Count -ge 1) '文件行菜单里有「粘贴」'
    Assert (@(Get-LogTail | Where-Object { $_ -match '内置右键菜单：文件 上下文 \d+ 项' }).Count -ge 1) '日志记下了内置菜单的文件上下文'
    Send-Keys -Session $session -Keys '{ESC}' -WaitMs 900

    $rowRect = $alpha.Current.BoundingRectangle
    $blankX = [int]($rowRect.X + $rowRect.Width / 3)
    $blankY = [int]($rowRect.Y + $rowRect.Height * 6)
    Invoke-Click -Session $session -X $blankX -Y $blankY -Button 'Right'
    Assert ((Find-MenuItems -Session $session -Name '粘贴').Count -ge 1) '空白处菜单里有「粘贴」'
    Assert ((Find-MenuItems -Session $session -Name '新建文件夹').Count -ge 1) '空白处菜单里仍保留「新建文件夹」'
    Send-Keys -Session $session -Keys '{ESC}' -WaitMs 900

    # ============================================================== B. Ctrl+C

    Write-Host '--- 用例 B：Ctrl+C 把文件放进剪贴板（复制） ---'
    $point = RowCenter -Row $alpha
    Invoke-Click -Session $session -X $point[0] -Y $point[1]
    Send-Keys -Session $session -Keys '^c'

    $clipboard = @(Get-ClipboardFiles)
    Assert ($clipboard.Count -eq 1) "剪贴板里有 1 个文件（实际 $($clipboard.Count)：$($clipboard -join ' / ')"
    Assert ($clipboard.Count -eq 1 -and $clipboard[0] -like '*alpha.txt') "剪贴板里就是 alpha.txt（实际：$($clipboard -join ' / ')"
    Assert ((Get-ClipboardDropEffect) -eq 1) "Preferred DropEffect = 1（复制），实际 $((Get-ClipboardDropEffect))"
    Assert (@(Get-LogTail | Where-Object { $_ -match '复制到剪贴板：1 项' }).Count -ge 1) 'exdir.log 记下了“复制到剪贴板：1 项”'

    # ============================================================== C. Ctrl+V（复制）

    Write-Host '--- 用例 C：进入子目录 Ctrl+V，文件被复制过去 ---'
    $sub = Find-RowByName -Session $session -Name 'sub'
    $point = RowCenter -Row $sub
    Invoke-DoubleClick -Session $session -X $point[0] -Y $point[1]
    Assert ($null -eq (Find-RowByName -Session $session -Name 'sub')) '双击 sub 之后列表里没有 sub 行了（说明已经进入该目录）'

    Send-Keys -Session $session -Keys '^v' -WaitMs 3000
    Assert (Test-Path (Join-Path $testDir 'sub\alpha.txt')) 'sub 目录里出现了 alpha.txt'
    Assert (Test-Path (Join-Path $testDir 'alpha.txt')) '源文件 alpha.txt 还在（复制不是移动）'
    Assert (@(Get-LogTail | Where-Object { $_ -match '文件操作：复制 1 项' }).Count -ge 1) 'exdir.log 记下了“文件操作：复制 1 项”'

    # ============================================================== D. Ctrl+X + Ctrl+V（移动）

    Write-Host '--- 用例 D：Ctrl+X 再 Ctrl+V，文件被移动 ---'
    Send-Keys -Session $session -Keys '%{UP}' -WaitMs 2500

    $beta = Find-RowByName -Session $session -Name 'beta*'
    Assert ($null -ne $beta) '回到上级后能看见 beta.txt'
    $point = RowCenter -Row $beta
    Invoke-Click -Session $session -X $point[0] -Y $point[1]
    Send-Keys -Session $session -Keys '^x'

    $clipboard = @(Get-ClipboardFiles)
    Assert ($clipboard.Count -eq 1 -and $clipboard[0] -like '*beta.txt') "剪贴板里是 beta.txt（实际：$($clipboard -join ' / ')"
    Assert ((Get-ClipboardDropEffect) -eq 2) "Preferred DropEffect = 2（剪切），实际 $((Get-ClipboardDropEffect))"
    Assert (@(Get-LogTail | Where-Object { $_ -match '剪切到剪贴板：1 项' }).Count -ge 1) 'exdir.log 记下了“剪切到剪贴板：1 项”'

    $sub = Find-RowByName -Session $session -Name 'sub'
    $point = RowCenter -Row $sub
    Invoke-DoubleClick -Session $session -X $point[0] -Y $point[1]
    Send-Keys -Session $session -Keys '^v' -WaitMs 3000

    Assert (Test-Path (Join-Path $testDir 'sub\beta.txt')) 'sub 目录里出现了 beta.txt'
    Assert (-not (Test-Path (Join-Path $testDir 'beta.txt'))) '源文件 beta.txt 已经不在了（是移动）'
    Assert (@(Get-LogTail | Where-Object { $_ -match '文件操作：移动 1 项' }).Count -ge 1) 'exdir.log 记下了“文件操作：移动 1 项”'

    # ============================================================== E. 拖到目录行上移动

    Write-Host '--- 用例 E：把文件拖到 sub 目录行上，文件被移动过去 ---'
    Send-Keys -Session $session -Keys '%{UP}' -WaitMs 2500

    $gamma = Find-RowByName -Session $session -Name 'gamma*'
    $sub = Find-RowByName -Session $session -Name 'sub'
    Assert ($null -ne $gamma) '回到上级后能看见 gamma.txt'
    Assert ($null -ne $sub) '回到上级后能看见 sub 目录'

    $from = RowCenter -Row $gamma
    $to = RowCenter -Row $sub

    # 拖拽在“提权 shell 驱动普通权限窗口 + 模拟鼠标”下偶尔会没启动（按下的那一下只用来激活窗口），
    # 所以失败就重试几次；产品行为用一个断言（文件真的进了 sub）验证
    $moved = $false
    for ($attempt = 1; $attempt -le 3 -and -not $moved; $attempt++) {
        Invoke-DragTo -Session $session -FromX $from[0] -FromY $from[1] -ToX $to[0] -ToY $to[1] -ShotDuring 'file-ops-drag'
        Start-Sleep -Seconds 1
        $moved = Test-Path (Join-Path $testDir 'sub\gamma.txt')
        if (-not $moved) { Write-Host "  第 $attempt 次拖拽没生效，重试" -ForegroundColor Yellow }
    }

    Assert (Test-Path (Join-Path $testDir 'sub\gamma.txt')) 'sub 目录里出现了 gamma.txt（拖放是移动）'
    Assert (-not (Test-Path (Join-Path $testDir 'gamma.txt'))) '源文件 gamma.txt 已经不在了'
    Assert (@(Get-LogTail | Where-Object { $_ -match '拖放(落下|兜底)：1 项' }).Count -ge 1) 'exdir.log 记下了这次拖放'
    Assert (@(Get-LogTail | Where-Object { $_ -match '拖放(落下|兜底)：1 项.*移动' }).Count -ge 1) '这次拖放是“移动”，不是复制'

    # ============================================================== F. 外部来源（复制）

    Write-Host '--- 用例 F：别的程序复制进来的文件，Ctrl+V 复制到当前目录 ---'
    Clear-Clipboard
    Set-ClipboardFiles -Paths @((Join-Path $elsewhere 'zeta.txt')) -DropEffect 1

    Send-Keys -Session $session -Keys '^v' -WaitMs 3000
    Assert (Test-Path (Join-Path $testDir 'zeta.txt')) '别的程序复制的 zeta.txt 被粘贴进了当前目录'
    Assert (Test-Path (Join-Path $elsewhere 'zeta.txt')) '源文件还在（剪贴板上是复制）'

    # ============================================================== G. 外部来源（剪切）

    Write-Host '--- 用例 G：别的程序剪切进来的文件，Ctrl+V 移动进来 ---'
    Set-ClipboardFiles -Paths @((Join-Path $elsewhere 'eta.txt')) -DropEffect 2

    Send-Keys -Session $session -Keys '^v' -WaitMs 3000
    Assert (Test-Path (Join-Path $testDir 'eta.txt')) '别的程序剪切的 eta.txt 被粘贴进了当前目录'
    Assert (-not (Test-Path (Join-Path $elsewhere 'eta.txt'))) '源文件没了（剪贴板上是剪切，粘贴后移动）'

    # ============================================================== H. Delete → 回收站

    Write-Host '--- 用例 H：Delete 把文件丢进回收站 ---'
    Clear-Clipboard

    $del = Find-RowByName -Session $session -Name "$delName*"
    Assert ($null -ne $del) "测试目录里有 $delName"

    $point = RowCenter -Row $del
    Invoke-Click -Session $session -X $point[0] -Y $point[1] -Button 'Right'
    Assert ((Find-MenuItems -Session $session -Name '删除').Count -ge 1) '文件行菜单里有「删除」'
    Send-Keys -Session $session -Keys '{ESC}' -WaitMs 900

    $point = RowCenter -Row $del
    Invoke-Click -Session $session -X $point[0] -Y $point[1]
    Send-Keys -Session $session -Keys '{DEL}' -WaitMs 1500
    [void](Confirm-ShellDialog -Session $session)
    Start-Sleep -Seconds 2

    Assert (-not (Test-Path (Join-Path $testDir $delName))) "$delName 已经从磁盘上消失"
    Assert (Test-InRecycleBin -FileName $delName) "$delName 出现在回收站里（Delete 是“丢进回收站”，不是永久删除）"
    Assert (@(Get-LogTail | Where-Object { $_ -match '文件操作：删除到回收站 1 项' }).Count -ge 1) 'exdir.log 记下了“文件操作：删除到回收站 1 项”'
    Assert ($null -eq (Find-RowByName -Session $session -Name "$delName*")) "删完之后列表里没有 $delName 行了"

    # ============================================================== I. Shift+Delete → 永久删除

    Write-Host '--- 用例 I：Shift+Delete 永久删除（不进回收站）---'
    $nuke = Find-RowByName -Session $session -Name "$nukeName*"
    Assert ($null -ne $nuke) "测试目录里有 $nukeName"

    $point = RowCenter -Row $nuke
    Invoke-Click -Session $session -X $point[0] -Y $point[1]
    Send-Keys -Session $session -Keys '+{DEL}' -WaitMs 1500
    [void](Confirm-ShellDialog -Session $session)
    Start-Sleep -Seconds 2

    Assert (-not (Test-Path (Join-Path $testDir $nukeName))) "$nukeName 已经被永久删除"
    Assert (-not (Test-InRecycleBin -FileName $nukeName)) "$nukeName 没有进回收站（Shift+Delete 是永久删除）"
    Assert (@(Get-LogTail | Where-Object { $_ -match '文件操作：永久删除 1 项' }).Count -ge 1) 'exdir.log 记下了“文件操作：永久删除 1 项”'

    Save-Shot -Session $session -Name 'file-ops-done'
    Stop-Session -Session $session
}
finally {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $elsewhere -Recurse -Force -ErrorAction SilentlyContinue

    if ($null -ne $originalSettings) {
        Set-Content $settingsPath $originalSettings -Encoding utf8
        Write-Host '已还原 settings.json'
    }
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }

