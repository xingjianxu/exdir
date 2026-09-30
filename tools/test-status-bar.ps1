# 状态栏（文件列表区底部一行）的 UIA 回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-status-bar.ps1
#   pwsh -NoProfile -File tools\test-status-bar.ps1 -Exe dist\win-x64\exdir.exe
#
# 四个用例（每次都真的启动 exdir，把窗口置顶后截图到 .artifacts）：
#   1. 状态栏只有一条（不是每个标签页一份）、恰好一行高（ExRowHeight=24 DIP）、贴窗口底边；
#   2. 临时目录里 2 个文件 → 左侧显示「2 项」，右侧显示当前卷的「可用 / 共」；
#   3. 选中 1 个文件 → 「选中 1 项（1.00 KB）」；再选中 1 个 → 「选中 2 项（合计 3.00 KB）」；
#      取消全部选中后选中摘要消失；
#   4. 双窗格 + F6 切换活动窗格后，状态栏跟着换成另一个窗格的目录。
#
# 需要交互桌面（真鼠标点击 + 截图）。跑完会还原 settings.json 的原始内容。
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
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const int HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
}
'@

[void][Native]::SetProcessDpiAwarenessContext([IntPtr][Native]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$settingsPath = Join-Path $env:LOCALAPPDATA 'exdir\settings.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

# 状态栏高度 = 一行（Themes/ExdirTheme.xaml 的 ExRowHeight），单位 DIP
$statusBarDip = 24

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ 临时目录（已知内容，便于断言“合计大小”）

$workDir = Join-Path $env:TEMP 'exdir-status-bar'
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $workDir 'a.txt'), [byte[]]::new(1024))
[System.IO.File]::WriteAllBytes((Join-Path $workDir 'b.txt'), [byte[]]::new(2048))

# 让会话确定地打开这个目录、且不显示文件扩展名（行名就是 a / b）
$json = Get-Content $settingsPath -Raw | ConvertFrom-Json
$json.PrimaryTabs = @($workDir)
$json.PrimaryActiveTab = 0
$json.ShowExtensions = $false
$json.ShowHiddenFiles = $false
$json.IsSidebarVisible = $true
$json.SidebarWidth = 232
$json.IsDualPane = $false
$json.WindowMaximized = $false
$json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

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

    # 置顶：终端窗口铺满屏幕时，不置顶截图/点击都会落到终端上
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

function Get-StatusBar {
    param($Session)
    $bar = $null
    foreach ($el in (Find-Elements -From $Session.Root -Name '状态栏')) {
        if ($el.Current.IsOffscreen) { continue }
        if ($el.Current.BoundingRectangle.Width -gt 0) { $bar = $el; break }
    }
    return $bar
}

function Get-StatusBarCount {
    param($Session)
    return @(Find-Elements -From $Session.Root -Name '状态栏').Count
}

# 状态栏里三个 TextBlock 的文本（按从左到右的顺序）
function Get-StatusTexts {
    param($Bar)
    $items = @()
    foreach ($el in (Find-ByType -From $Bar -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        $r = $el.Current.BoundingRectangle
        if (-not [string]::IsNullOrEmpty($el.Current.Name)) {
            $items += [pscustomobject]@{ X = $r.X; Text = $el.Current.Name }
        }
    }
    return @($items | Sort-Object X | ForEach-Object { $_.Text })
}

function Get-Rows {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem))
}

function Select-Row {
    param($Row)
    $Row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}

function Add-RowToSelection {
    param($Row)
    $Row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    Start-Sleep -Milliseconds 700
}

function Unselect-Row {
    param($Row)
    $Row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).RemoveFromSelection()
    Start-Sleep -Milliseconds 700
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
    $shot = Join-Path ([System.IO.Path]::GetFullPath($ShotDir)) "status-bar-$Name.png"

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

Write-Host '--- 用例 1：只有一条、一行高、贴底 ---'
$bar = Get-StatusBar -Session $session
Assert ($null -ne $bar) '状态栏存在'
Assert ((Get-StatusBarCount -Session $session) -eq 1) '整个窗口只有一条状态栏（不是每个标签页一份）'

if ($null -ne $bar) {
    $r = $bar.Current.BoundingRectangle
    $expectedHeight = $statusBarDip * $scale

    # 贴底要比“客户区底边”而不是 GetWindowRect 底边：窗口矩形里含不可见的 DWM 缩放边框
    $clientRect = New-Object Native+RECT
    [void][Native]::GetClientRect($session.Handle, [ref]$clientRect)
    $clientBottomLeft = New-Object Native+POINT
    $clientBottomLeft.X = $clientRect.Left
    $clientBottomLeft.Y = $clientRect.Bottom
    [void][Native]::ClientToScreen($session.Handle, [ref]$clientBottomLeft)
    $clientTopLeft = New-Object Native+POINT
    [void][Native]::ClientToScreen($session.Handle, [ref]$clientTopLeft)
    $clientBottom = $clientBottomLeft.Y
    $clientLeft = $clientTopLeft.X

    Write-Host ("  状态栏 [{0:N0},{1:N0} {2:N0}x{3:N0}]，客户区 [{4:N0},{5:N0} {6:N0}x{7:N0}]，期望高度 {8:N0}" -f `
        $r.X, $r.Y, $r.Width, $r.Height, $clientLeft, $clientTopLeft.Y, $clientRect.Right, $clientRect.Bottom, $expectedHeight)

    Assert ([Math]::Abs($r.Height - $expectedHeight) -le 2) "状态栏高度 = 一行（$statusBarDip DIP，实际 $([Math]::Round($r.Height / $scale, 1)) DIP）"
    Assert ([Math]::Abs($r.Bottom - $clientBottom) -le 2) '状态栏贴着窗口底边'
    Assert (($r.X - $clientLeft) -ge (232 * $scale - 4)) '状态栏在侧边栏右侧（只占文件列表区，不跨侧边栏）'
    Assert ([Math]::Abs($r.Width - ($clientRect.Right - 232 * $scale - 6 * $scale)) -le 4) '状态栏横跨整个文件列表区（侧边栏 + 分隔条以外的宽度）'
}

Write-Host '--- 用例 2：项数 / 磁盘可用空间 ---'
Start-Sleep -Seconds 2
$texts = Get-StatusTexts -Bar $bar
Write-Host ("  状态栏文本: {0}" -f ($texts -join ' | '))
Assert ($texts -contains '2 项') '左侧显示当前目录的条目数（2 项）'
Assert (@($texts | Where-Object { $_ -match '^[A-Za-z]: 可用 .+ / 共 .+$' }).Count -eq 1) '右侧显示当前卷的可用空间 / 总容量'

Write-Host '--- 用例 3：选中摘要 + 合计大小 ---'
$rows = Get-Rows -Session $session
$rowA = $rows | Where-Object { $_.Current.Name -eq 'a' } | Select-Object -First 1
$rowB = $rows | Where-Object { $_.Current.Name -eq 'b' } | Select-Object -First 1
Assert (($null -ne $rowA) -and ($null -ne $rowB)) '临时目录里能看到 a / b 两行'

Select-Row -Row $rowA
$texts = Get-StatusTexts -Bar (Get-StatusBar -Session $session)
Write-Host ("  选中 1 项: {0}" -f ($texts -join ' | '))
Assert (@($texts | Where-Object { $_ -eq '选中 1 项（1.00 KB）' }).Count -eq 1) '选中 1 个文件 → 显示「选中 1 项（1.00 KB）」'
Save-Shot -Session $session -Name 'one-selected'

Add-RowToSelection -Row $rowB
$texts = Get-StatusTexts -Bar (Get-StatusBar -Session $session)
Write-Host ("  选中 2 项: {0}" -f ($texts -join ' | '))
Assert (@($texts | Where-Object { $_ -eq '选中 2 项（合计 3.00 KB）' }).Count -eq 1) '选中 2 个文件 → 显示「选中 2 项（合计 3.00 KB）」'
Save-Shot -Session $session -Name 'two-selected'

Unselect-Row -Row $rowA
Unselect-Row -Row $rowB
$texts = Get-StatusTexts -Bar (Get-StatusBar -Session $session)
Write-Host ("  取消选中: {0}" -f ($texts -join ' | '))
Assert (@($texts | Where-Object { $_ -like '选中*' }).Count -eq 0) '取消选中后选中摘要消失'

Write-Host '--- 用例 4：切换活动窗格后状态栏跟着换 ---'
$tempCount = @(Get-ChildItem $env:TEMP).Count

Send-Keys -Session $session -Keys @('{F10}')     # 双窗格：第二个窗格也打开临时目录（2 项）
Start-Sleep -Seconds 5
$texts = Get-StatusTexts -Bar (Get-StatusBar -Session $session)
Write-Host ("  双窗格: {0}" -f ($texts -join ' | '))
Assert ($texts -contains '2 项') '打开双窗格后状态栏仍显示活动窗格（临时目录，2 项）'

# 活动窗格里新建一个标签页，切到 %TEMP% 根目录（条目数肯定不是 2）
Send-Keys -Session $session -Keys @('^t')
Send-Keys -Session $session -Keys @('^l')
Send-Keys -Session $session -Keys @($env:TEMP + '{ENTER}')
Start-Sleep -Seconds 3
$texts = Get-StatusTexts -Bar (Get-StatusBar -Session $session)
Write-Host ("  新标签页导航到 %TEMP%: {0}" -f ($texts -join ' | '))
Assert (@($texts | Where-Object { $_ -eq "$tempCount 项" }).Count -eq 1) "新标签页导航后状态栏跟着换成它自己的条目数（$tempCount 项）"

Send-Keys -Session $session -Keys @('{F6}')      # 活动窗格切到另一边（另一个窗格还在临时目录）
Start-Sleep -Seconds 3
$texts = Get-StatusTexts -Bar (Get-StatusBar -Session $session)
Write-Host ("  切到另一个窗格: {0}" -f ($texts -join ' | '))
Assert (@($texts | Where-Object { $_ -eq '2 项' }).Count -eq 1) '切换窗格后状态栏显示另一个窗格的条目数（2 项）'
Save-Shot -Session $session -Name 'pane-switch'

Send-Keys -Session $session -Keys @('{F6}')      # 切回去
Start-Sleep -Seconds 3
$texts = Get-StatusTexts -Bar (Get-StatusBar -Session $session)
Assert (@($texts | Where-Object { $_ -eq "$tempCount 项" }).Count -eq 1) '再切回来状态栏又回到新标签页的条目数'

Stop-Session -Session $session

# ------------------------------------------------------------------ 还原

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 settings.json'
}

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
