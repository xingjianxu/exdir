# 文件列表“双击命中范围”的 UIA + 真鼠标回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-row-dblclick.ps1
#   pwsh -NoProfile -File tools\test-row-dblclick.ps1 -Exe dist\win-x64\exdir.exe
#
# 断言的是：**只要鼠标悬停会高亮的那一块**（整行）双击都能触发条目动作，
# 不再只有“双击到名称文字/图标上”才有效。逐个用例把双击落点挪到行内的不同位置：
#   1. 行内左边距（最左边 3 DIP 处）        → 进入子目录
#   2. 名称文字右侧的空白（名称列很宽）      → 进入子目录
#   3. 行内上半留白（文字上方那几像素）      → 进入子目录
#   4. 类型列文字右侧的空白（大小列左侧）    → 进入子目录
#   5. 大小列左侧空白（大小是右对齐的）      → 进入子目录
#   6. 名称文字正中（原本就能用的对照点）    → 进入子目录
#   7. 行首展开箭头                          → 只就地展开，不进目录
#   8. 列表下方空白处                        → 什么都不发生
#
# 需要交互桌面（真鼠标双击）。跑完会还原 config.json 的原始内容。
#
# 注意：本脚本会把 exdir 窗口设成 TOPMOST —— 终端窗口常常铺满屏幕，
# 不置顶的话点击与截图都会落到终端上；每次点击前还会把光标挪到标题栏“停”一下，
# 免得上一行的悬停提示框停在下一个落点上（行有 ToolTip，挡住点击点就点空了）。

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
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const int HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
'@

[void][Native]::SetProcessDpiAwarenessContext([IntPtr][Native]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

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

# ------------------------------------------------------------------ 临时目录
# workDir 里三个子目录（目录排在文件前面，所以顺序是 sub1 / sub2 / tree）；
# sub1 / sub2 里有各自的文件，用来判断“真的进到子目录了”；tree 里有子项，用来测展开箭头。

$sub1 = 'sub1'
$sub2 = 'sub2'
$tree = 'tree'
$workDir = Join-Path $env:TEMP 'exdir-row-dblclick'
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
foreach ($name in $sub1, $sub2, $tree) { New-Item -ItemType Directory -Path (Join-Path $workDir $name) | Out-Null }
[System.IO.File]::WriteAllText((Join-Path $workDir "$sub1\inner1.txt"), 'x')
[System.IO.File]::WriteAllText((Join-Path $workDir "$sub2\inner2.txt"), 'x')
[System.IO.File]::WriteAllText((Join-Path $workDir "$tree\child.txt"), 'x')

# 让会话确定地打开这个目录：单窗格、不显示扩展名、行高默认、列宽自动填满
$json = Get-Content $settingsPath -Raw | ConvertFrom-Json
$json.PrimaryTabs = @($workDir)
$json.PrimaryActiveTab = 0
$json.ShowExtensions = $false
$json.ShowHiddenFiles = $false
$json.FoldersFirst = $true
$json.IsSidebarVisible = $true
$json.SidebarWidth = 232
$json.IsDualPane = $false
$json.ColumnWidths = @()
$json.ColumnAutoFit = $true
$json.ColumnAutoFillName = $true
$json.RowHeight = 28
$json.WindowMaximized = $false
$json.WindowX = 40
$json.WindowY = 40
$json.WindowWidth = 1100
$json.WindowHeight = 700
$json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

# workDir 里的行 / 进入子目录后的行
$rootRows = @($sub1, $sub2, $tree) | Sort-Object

# ------------------------------------------------------------------ 会话

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

# 可见的行。UIA 里行名就是 FileItemViewModel.DisplayName（行的 AutomationProperties.Name）
function Get-Rows {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
}

function Get-RowNames {
    param($Session)
    return @((Get-Rows -Session $Session) | ForEach-Object { $_.Current.Name } | Sort-Object)
}

# 等列表变成预期的行集合（导航是异步的），超时返回实际值
function Wait-RowNames {
    param($Session, [string[]]$Expected, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $names = Get-RowNames -Session $Session
    while ((Get-Date) -lt $deadline) {
        if (($names -join '|') -eq ($Expected -join '|')) { return $names }
        Start-Sleep -Milliseconds 250
        $names = Get-RowNames -Session $Session
    }
    return $names
}

function Get-RowByName {
    param($Session, [string]$Name)
    return (Get-Rows -Session $Session | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1)
}

# ------------------------------------------------------------------ 真鼠标

# 把 exdir 弄到前台且“点得到”：桌面上可能残留弹层，终端窗口也常常盖在 exdir 上面，
# 真鼠标点击的第一接收者是光标下面那个窗口（WindowFromPoint 可能返回子窗口，故比到顶层窗口）。
function Focus-Session {
    param($Session, [int]$X, [int]$Y)
    for ($i = 0; $i -lt 8; $i++) {
        $pointOwner = [Native]::GetAncestor([Native]::WindowFromPoint($X, $Y), [Native]::GA_ROOT)
        if (($pointOwner -eq $Session.Handle) -and ([Native]::GetForegroundWindow() -eq $Session.Handle)) { return $true }

        try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') } catch { }
        Start-Sleep -Milliseconds 250
        [void][Native]::SetWindowPos($Session.Handle, [IntPtr][Native]::HWND_TOPMOST, 0, 0, 0, 0,
            [Native]::SWP_NOMOVE -bor [Native]::SWP_NOSIZE -bor [Native]::SWP_SHOWWINDOW)
        [void][Native]::SetForegroundWindow($Session.Handle)
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# 把光标停到标题栏中间（那里没有 ToolTip、没有按钮），顺手把上一行的悬停提示框赶走
function Park-Cursor {
    param($Session)
    $rect = New-Object Native+RECT
    [void][Native]::GetWindowRect($Session.Handle, [ref]$rect)
    [void][Native]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), $rect.Top + 18)
    Start-Sleep -Milliseconds 200
}

# 真双击：两次按下必须落在同一点、间隔小于系统双击时间（SetCursorPos 只摆位置，
# 所以两次 mouse_event 之间不能再移动光标）
function Invoke-DoubleClick {
    param($Session, [int]$X, [int]$Y)
    Park-Cursor -Session $Session
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    [void][Native]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt 2; $i++) {
        [Native]::mouse_event([Native]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 40
        [Native]::mouse_event([Native]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 800
}

function Invoke-Click {
    param($Session, [int]$X, [int]$Y)
    Park-Cursor -Session $Session
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    [void][Native]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 120
    [Native]::mouse_event([Native]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Native]::mouse_event([Native]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}

function Send-Keys {
    param($Session, [string[]]$Keys)
    [void][Native]::SetWindowPos($Session.Handle, [IntPtr][Native]::HWND_TOPMOST, 0, 0, 0, 0,
        [Native]::SWP_NOMOVE -bor [Native]::SWP_NOSIZE -bor [Native]::SWP_SHOWWINDOW)
    [void][Native]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 400
    foreach ($key in $Keys) {
        [System.Windows.Forms.SendKeys]::SendWait($key)
        Start-Sleep -Milliseconds 400
    }
}

function Save-Shot {
    param($Session, [string]$Name)
    New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
    $shot = Join-Path ([System.IO.Path]::GetFullPath($ShotDir)) "row-dblclick-$Name.png"

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

# ================================================================== 开跑

$session = Start-Session
$scale = $session.Dpi / 96.0
Write-Host ("  缩放: {0:P0}" -f $scale)

$names = Wait-RowNames -Session $session -Expected $rootRows
Write-Host ("  列表行: {0}" -f ($names -join ', '))
Assert (($names -join ',') -eq ($rootRows -join ',')) '临时目录里能看到 sub1 / sub2 / tree 三行'

# 一行里各个落点的算法（全按 DIP 算再乘缩放）：
#   行左边距 → 名称列起点 = 行左 + ExRowPadding(6) + 箭头 18 + 图标 16 + 图标右外边距 6 = 行左 + 46
#   类型列文字右侧 / 大小列左侧 → 从行右边界倒着数：右内边距 6 + 大小列 86 + 类型列 104
$row = Get-RowByName -Session $session -Name $sub1
Assert ($null -ne $row) ("找到 '{0}' 行" -f $sub1)
$r = $row.Current.BoundingRectangle
$rowLeft = [int]$r.Left
$rowRight = [int]$r.Right
$rowTop = [int]$r.Top
$rowCenterY = [int]($r.Top + $r.Height / 2)
$nameStart = $rowLeft + [int](46 * $scale)

$points = @(
    [pscustomobject]@{ Name = '行内左边距';       X = $rowLeft + [int](3 * $scale); Y = $rowCenterY }
    [pscustomobject]@{ Name = '名称文字右侧空白'; X = $nameStart + [int](150 * $scale); Y = $rowCenterY }
    [pscustomobject]@{ Name = '行内上半留白';     X = $nameStart + [int](12 * $scale); Y = $rowTop + [int](3 * $scale) }
    [pscustomobject]@{ Name = '类型列文字右侧';   X = $rowRight - [int](126 * $scale); Y = $rowCenterY }
    [pscustomobject]@{ Name = '大小列左侧空白';   X = $rowRight - [int](82 * $scale); Y = $rowCenterY }
    [pscustomobject]@{ Name = '名称文字正中（对照）'; X = $nameStart + [int](12 * $scale); Y = $rowCenterY }
)

Assert (($points | Where-Object { $_.X -le $rowLeft -or $_.X -ge $rowRight }).Count -eq 0) `
    '所有落点都在行（= 悬停会高亮的那一块）之内'

# 落点是不是真的属于 exdir 窗口，只作为提示打出来：桌面上的终端 / 开始菜单盖在 exdir 上面时
# 这里的探测会失败，但前面的 Focus-Session（循环置顶 + 重试）已经把窗口弄到光标下面了，
# 所以真正的证据是下面每条“双击后真的进去了”的断言。
$firstPoint = $points[0]
$owner = [Native]::GetAncestor([Native]::WindowFromPoint($firstPoint.X, $firstPoint.Y), [Native]::GA_ROOT)
Write-Host ("  提示：首次点击点 ({0},{1}) 的窗口 {2} / exdir {3}" -f $firstPoint.X, $firstPoint.Y, $owner, $session.Handle)

foreach ($point in $points) {
    Write-Host ("--- 双击「{0}」({1},{2}) → 应该进入 {3} ---" -f $point.Name, $point.X, $point.Y, $sub1)
    Invoke-DoubleClick -Session $session -X $point.X -Y $point.Y

    $inside = Wait-RowNames -Session $session -Expected @('inner1')
    Write-Host ("  进入后的行: {0}" -f ($inside -join ', '))
    Assert (($inside -join ',') -eq 'inner1') ("双击{0}能进入 {1}（列表里是 {1} 的内容）" -f $point.Name, $sub1)

    $crumbs = @(Find-ByType -From $session.Root -ControlType ([System.Windows.Automation.ControlType]::Button) |
        ForEach-Object { $_.Current.Name })
    Assert ($crumbs -contains $sub1) ("双击{0}后地址栏面包屑里出现 {1}" -f $point.Name, $sub1)

    if ($point.Name -eq '名称文字右侧空白') { Save-Shot -Session $session -Name 'entered' }

    # 退回 workDir（历史后退），准备下一个落点
    Send-Keys -Session $session -Keys @('%{LEFT}')
    $back = Wait-RowNames -Session $session -Expected $rootRows
    Assert (($back -join ',') -eq ($rootRows -join ',')) ("从 {0} 退回后又是 sub1 / sub2 / tree 三行" -f $sub1)

    $row = Get-RowByName -Session $session -Name $sub1
    if ($null -eq $row) { break }
}

Write-Host '--- 行首展开箭头：单击就展开，双击也不进目录 ---'
$expandedRows = @(($rootRows + 'child') | Sort-Object)
$treeRow = Get-RowByName -Session $session -Name $tree
Assert ($null -ne $treeRow) ("找到 '{0}' 行" -f $tree)
$tr = $treeRow.Current.BoundingRectangle
$arrows = @(Find-Elements -From $session.Root -Name '展开或折叠' | Where-Object {
        $b = $_.Current.BoundingRectangle
        (-not $_.Current.IsOffscreen) -and $b.Height -gt 0 -and $b.Top -ge $tr.Top -and $b.Bottom -le $tr.Bottom -and $b.Left -ge $tr.Left -and $b.Right -le $tr.Right
    })
Assert ($arrows.Count -ge 1) ("在 '{0}' 行里找到展开箭头" -f $tree)
$arrowX = 0
$arrowY = 0
if ($arrows.Count -ge 1) {
    $a = $arrows[0].Current.BoundingRectangle
    $arrowX = [int]($a.Left + $a.Width / 2)
    $arrowY = [int]($a.Top + $a.Height / 2)

    Invoke-Click -Session $session -X $arrowX -Y $arrowY
    $expanded = Wait-RowNames -Session $session -Expected $expandedRows
    Write-Host ("  单击后的行: {0}" -f ($expanded -join ', '))
    Assert (($expanded -join ',') -eq ($expandedRows -join ',')) '单击展开箭头仍然是就地展开（行里多出子项）'

    # 双击 = 两次 Click：展开又被折叠，所以这里只断言“没有进目录”。
    # （进目录的话列表会变成 child 一项，行集合不再是 workDir 的那几个名字）
    Invoke-DoubleClick -Session $session -X $arrowX -Y $arrowY
    Start-Sleep -Seconds 1
    $afterArrow = Get-RowNames -Session $session
    Write-Host ("  双击后的行: {0}" -f ($afterArrow -join ', '))
    Assert ($afterArrow.Count -ge 3) '双击展开箭头不会导航进目录（仍能看到 workDir 的三行）'
    Assert (($afterArrow -contains $sub1) -and ($afterArrow -contains $sub2) -and ($afterArrow -contains $tree)) `
        '双击展开箭头后仍然停在 workDir'
    Save-Shot -Session $session -Name 'expander'
}

Write-Host '--- 双击列表下方空白处 → 什么都不发生 ---'
$before = Get-RowNames -Session $session
$rows = Get-Rows -Session $session
$lastRowRect = ($rows | Sort-Object { $_.Current.BoundingRectangle.Top } | Select-Object -Last 1).Current.BoundingRectangle
$statusBar = Find-Elements -From $session.Root -Name '状态栏' | Select-Object -First 1
$blankX = [int]($lastRowRect.Left + $lastRowRect.Width / 2)
$blankY = [int]($lastRowRect.Bottom + 40)
if ($null -ne $statusBar) {
    $statusTop = [int]$statusBar.Current.BoundingRectangle.Y
    Assert ($blankY -lt ($statusTop - 4)) "空白点（y=$blankY）在状态栏（y=$statusTop）之上"
}
Invoke-DoubleClick -Session $session -X $blankX -Y $blankY
Start-Sleep -Seconds 1
$after = Get-RowNames -Session $session
Write-Host ("  双击后的行: {0}" -f ($after -join ', '))
Assert (($after -join ',') -eq ($before -join ',')) '双击列表下方空白处不会导航（还是原来那几行）'

Stop-Session -Session $session

# ------------------------------------------------------------------ 还原

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 config.json'
}

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
