# 真实外壳图标（plan.md S14）的回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-shell-icons.ps1
#   pwsh -NoProfile -File tools\test-shell-icons.ps1 -Exe dist\win-x64\exdir.exe
#
# 四个用例（每次都真的启动 exdir，把窗口置顶后截图到 .artifacts）：
#   1. 列表里每一行都有真实图标：UIA 里能按名字找到“程序图标”（Image）且尺寸 > 0；
#   2. 不同程序用各自的图标：几个不同 exe 在 exdir.log 里记到互不相同的图标哈希；
#      .lnk 的哈希与它指向的程序不同（多了一层“快捷方式小箭头”覆盖层）；
#   3. 同扩展名的文件只提取一次图标（3 个 .txt 只留 1 行日志）；
#   4. 滚动到列表末尾后，新出现的行同样有真实图标（虚拟化容器回收/重建的路径没漏掉）。
#
# 需要交互桌面（真鼠标点击 + SendKeys + 截图）。跑完会还原 config.json 的原始内容。
#
# 注意：本脚本会把 exdir 窗口设成 TOPMOST —— 终端窗口常常铺满屏幕，
# 不置顶的话 CopyFromScreen 拍到的是终端而不是 exdir。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$ShotDir = "$PSScriptRoot\..\.artifacts"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const int HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
    public const uint MOVE = 0x0001;
    public const uint ABSOLUTE = 0x8000;
}
'@

[void][Native]::SetProcessDpiAwarenessContext([IntPtr][Native]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

# “程序图标”是 DetailsView 行模板里那个 Image 的 AutomationProperties.Name
$iconName = '程序图标'

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ 临时目录（已知内容，便于断言）

$workDir = Join-Path $env:TEMP 'exdir-shell-icons'
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $workDir 'subdir') | Out-Null

# 4 个图标互不相同的程序（同名的 explorer 副本用 zz- 前缀排在最后，用来验证滚动后的行）
$programs = @{
    'notepad.exe'  = "$env:SystemRoot\System32\notepad.exe"
    'cmd.exe'      = "$env:SystemRoot\System32\cmd.exe"
    'explorer.exe' = "$env:SystemRoot\explorer.exe"
    'regedit.exe'  = "$env:SystemRoot\regedit.exe"
    'zz-last.exe'  = "$env:SystemRoot\explorer.exe"
}

foreach ($name in $programs.Keys) {
    Copy-Item $programs[$name] (Join-Path $workDir $name)
}

# 三个同扩展名的文件：图标只该提取一次
foreach ($name in 'a.txt', 'b.txt', 'c.txt') {
    Set-Content (Join-Path $workDir $name) 'hello'
}

# 一个指向 notepad.exe 的快捷方式
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $workDir 'notepad.lnk'))
$link.TargetPath = "$env:SystemRoot\System32\notepad.exe"
$link.Save()

$expected = @($programs.Keys) + @('notepad.lnk', 'subdir')

# 让会话确定地打开这个目录、显示扩展名（行名与日志里的文件名一致，方便对照）
#
# 先确保没有别的 exdir 实例在跑：残留实例退出时会把 config.json 写成它自己的会话，
# 正好盖掉下面刚写好的会话（症状：脚本断言的行全是用户主目录的内容）
Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Milliseconds 400

$json = Get-Content $settingsPath -Raw | ConvertFrom-Json
$json.PrimaryTabs = @($workDir)
$json.PrimaryActiveTab = 0
$json.ShowExtensions = $true
$json.ShowHiddenFiles = $false
$json.IsDualPane = $false
$json.WindowMaximized = $false
$json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

Remove-Item $logPath -ErrorAction SilentlyContinue

# ------------------------------------------------------------------ UIA 小工具

function Start-Session {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

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

    [void][Native]::SetWindowPos($handle, [IntPtr][Native]::HWND_TOPMOST, 0, 0, 0, 0,
        [Native]::SWP_NOMOVE -bor [Native]::SWP_NOSIZE -bor [Native]::SWP_SHOWWINDOW)
    [void][Native]::SetForegroundWindow($handle)
    Start-Sleep -Seconds 5

    return [pscustomobject]@{
        Proc   = $proc
        Handle = $handle
        Root   = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        Dpi    = [Native]::GetDpiForWindow($handle)
    }
}

function Stop-Session {
    param($Session)
    # exdir 关窗口只是隐藏到托盘（隐藏时已统一落盘），收尾直接 Kill
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
}

function Find-Elements {
    param($From, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByType {
    param($From, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-Rows {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem))
}

function Get-VisibleRows {
    param($Session)
    return @(Get-Rows -Session $Session | Where-Object {
        -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0
    })
}

function Get-Row {
    param($Session, [string]$Name)
    return Get-Rows -Session $Session | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1
}

# 某一行有没有真实图标（Image 的尺寸 > 0；拿不到图标时它是 Collapsed，UIA 里根本找不到）
function Test-RowIcon {
    param($Row)
    foreach ($image in (Find-Elements -From $Row -Name $script:iconName)) {
        if ($image.Current.BoundingRectangle.Width -gt 0) { return $true }
    }
    return $false
}

function Click-Row {
    param($Session, $Row)
    $r = $Row.Current.BoundingRectangle
    # 点在名称列的空白区（行首 40 DIP 是展开箭头 + 图标，点那儿会落在图标上）
    $x = [int]($r.X + 300)
    $y = [int]($r.Y + ($r.Height / 2))

    [void][Native]::SetForegroundWindow($Session.Handle)
    [void][Native]::SetCursorPos([int]$r.X + 600, [int]$r.Y + 400)
    Start-Sleep -Milliseconds 150
    [Native]::mouse_event([Native]::MOVE -bor [Native]::ABSOLUTE,
        [uint32]($x * 65535 / ($script:screenW - 1)), [uint32]($y * 65535 / ($script:screenH - 1)), 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    [Native]::mouse_event([Native]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Native]::mouse_event([Native]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}

function Send-Keys {
    param($Session, [string[]]$Keys)
    [void][Native]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 400
    foreach ($key in $Keys) {
        [System.Windows.Forms.SendKeys]::SendWait($key)
        Start-Sleep -Milliseconds 800
    }
    Start-Sleep -Seconds 2
}

function Save-Shot {
    param($Session, [string]$Name)
    New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
    $shot = Join-Path ([System.IO.Path]::GetFullPath($ShotDir)) "shell-icons-$Name.png"

    $rect = New-Object Native+RECT
    [void][Native]::GetWindowRect($Session.Handle, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $gfx.Dispose()
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "已保存 $shot"
}

# 日志里的图标记录：外壳图标：<文件名> <宽>×<高> #<8 位十六进制哈希>
function Get-IconLog {
    if (-not (Test-Path $logPath)) { return @() }

    $map = @{}
    foreach ($line in (Get-Content $logPath)) {
        if ($line -match '外壳图标：(.+?) (\d+)×(\d+) #([0-9A-Fa-f]{8})$') {
            $map[$Matches[1]] = $Matches[4].ToUpperInvariant()
        }
    }
    return $map
}

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$screenW = $screen.Width
$screenH = $screen.Height

# ================================================================== 开跑

$session = Start-Session
$scale = $session.Dpi / 96.0
Write-Host ("窗口 DPI {0}（缩放 {1:N2}），临时目录 {2}" -f $session.Dpi, $scale, $workDir)

Write-Host '--- 用例 1：每一行都有真实图标 ---'
$rows = Get-VisibleRows -Session $session
Write-Host ("  可见行 {0} 个：{1}" -f $rows.Count, (($rows | ForEach-Object { $_.Current.Name }) -join ', '))
Assert ($rows.Count -ge 5) '列表里能看到临时目录的条目'

$withoutIcon = @($rows | Where-Object { -not (Test-RowIcon -Row $_) })
Assert ($withoutIcon.Count -eq 0) ("可见行全部带真实图标（缺图标的行：{0}）" -f (($withoutIcon | ForEach-Object { $_.Current.Name }) -join ', '))
Save-Shot -Session $session -Name 'list'

Write-Host '--- 用例 2/3：日志里的图标哈希 ---'
$icons = Get-IconLog
Write-Host ("  日志记录 {0} 个图标：" -f $icons.Count)
$icons.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("    {0,-16} #{1}" -f $_.Key, $_.Value) }

$exeNames = @($programs.Keys | Where-Object { $_ -ne 'zz-last.exe' })
$missing = @($exeNames | Where-Object { -not $icons.ContainsKey($_) })
Assert ($missing.Count -eq 0) ("几个程序都提取到了图标（缺：{0}）" -f ($missing -join ', '))

$exeHashes = @($exeNames | Where-Object { $icons.ContainsKey($_) } | ForEach-Object { $icons[$_] })
Assert (($exeHashes | Select-Object -Unique).Count -eq $exeHashes.Count) '不同程序用的是各自的图标（哈希互不相同，不是同一个默认图标）'

Assert ($icons.ContainsKey('notepad.lnk')) '.lnk 也提取到了图标'
Assert ($icons.ContainsKey('notepad.lnk') -and $icons['notepad.lnk'] -ne $icons['notepad.exe']) '.lnk 的图标与它指向的程序不同（多了一层快捷方式小箭头）'

$txtLines = @($icons.Keys | Where-Object { $_ -like '*.txt' })
Assert ($txtLines.Count -eq 1) ("同扩展名的文件只提取一次图标（3 个 .txt 只留了 {0} 行日志）" -f $txtLines.Count)

Assert ($icons.ContainsKey('subdir')) '文件夹也提取到了图标'

Write-Host '--- 用例 4：滚动到列表末尾后的行同样有图标 ---'
$anchor = Get-Row -Session $session -Name 'notepad.exe'
if ($null -eq $anchor) { $anchor = $rows[0] }
Click-Row -Session $session -Row $anchor
Send-Keys -Session $session -Keys @('{END}')

$last = Get-Row -Session $session -Name 'zz-last.exe'
Assert ($null -ne $last) '滚动后能看到列表最后的 zz-last.exe'
Assert (($null -ne $last) -and (Test-RowIcon -Row $last)) '滚动后新出现的行也有真实图标（容器回收/重建路径）'

$afterRows = Get-VisibleRows -Session $session
$afterMissing = @($afterRows | Where-Object { -not (Test-RowIcon -Row $_) })
Assert ($afterMissing.Count -eq 0) ("滚动后可见行仍然全部带真实图标（缺图标的行：{0}）" -f (($afterMissing | ForEach-Object { $_.Current.Name }) -join ', '))
Save-Shot -Session $session -Name 'scrolled'

$errors = @()
if (Test-Path $logPath) { $errors = @(Get-Content $logPath | Select-String '异常') }
Assert ($errors.Count -eq 0) ("日志里没有异常（{0}）" -f ($errors -join ' | '))

Stop-Session -Session $session

# ------------------------------------------------------------------ 还原

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 config.json'
}

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
