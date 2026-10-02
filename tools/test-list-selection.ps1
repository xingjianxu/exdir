# 文件列表“选择”行为的 UIA + 真鼠标回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-list-selection.ps1
#   pwsh -NoProfile -File tools\test-list-selection.ps1 -Exe dist\win-x64\exdir.exe
#
# 四个用例（真的启动 exdir、把窗口置顶后用真鼠标点击 + SendKeys）：
#   1. 单击某一行 → 恰好那一行被选中；
#   2. Ctrl+A → 列表里所有行都被选中（3 行的临时目录），状态栏也显示「选中 3 项」；
#   3. 单击列表空白处（最后一行下方）→ 选中被清空；
#   4. 再单击某一行 → 只选中该行（行上的点击不算“空白处”，不会被清空成 0）；
#   5. 焦点在地址栏时 Ctrl+A 仍然是文本框全选，不会跑去全选文件列表。
#
# 需要交互桌面（真鼠标点击）。跑完会还原 config.json 的原始内容。
#
# 注意：本脚本会把 exdir 窗口设成 TOPMOST —— 终端窗口常常铺满屏幕，
# 不置顶的话点击与截图都会落到终端上。

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

# ------------------------------------------------------------------ 临时目录（3 个文件，全选的数量是确定的）

$workDir = Join-Path $env:TEMP 'exdir-list-selection'
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir | Out-Null
foreach ($name in 'a.txt', 'b.txt', 'c.txt') {
    [System.IO.File]::WriteAllText((Join-Path $workDir $name), 'x')
}

# 让会话确定地打开这个目录、不显示扩展名（行名就是 a / b / c）、单窗格
$json = Get-Content $settingsPath -Raw | ConvertFrom-Json
$json.PrimaryTabs = @($workDir)
$json.PrimaryActiveTab = 0
$json.ShowExtensions = $false
$json.ShowHiddenFiles = $false
$json.IsSidebarVisible = $true
$json.SidebarWidth = 232
$json.IsDualPane = $false
$json.WindowMaximized = $false
$json.WindowX = 40
$json.WindowY = 40
$json.WindowWidth = 1100
$json.WindowHeight = 700
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

# 可见的行（UIA 只暴露真正生成出来的行，这里 3 行的目录正好全在里面）
function Get-Rows {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
}

function Get-SelectedRowNames {
    param($Session)
    $names = @()
    foreach ($row in (Get-Rows -Session $Session)) {
        try {
            $pattern = $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($pattern.Current.IsSelected) { $names += $row.Current.Name }
        } catch { }
    }
    return @($names | Sort-Object)
}

function Get-RowCenter {
    param($Row)
    $r = $Row.Current.BoundingRectangle
    return [pscustomobject]@{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2) }
}

function Invoke-Click {
    param($Session, [int]$X, [int]$Y)
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    [void][Native]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 120
    [Native]::mouse_event([Native]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [Native]::mouse_event([Native]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 1
}

# 把 exdir 弄到前台且“点得到”：桌面上可能残留弹层（开始菜单 / 搜索），本 agent 的终端窗口
# 也常常盖在 exdir 上面 —— 真鼠标点击的第一接收者是光标下面那个窗口，弄不干净就会出现
# “点行没反应、选中一直是 0”的假结论。这里循环把 exdir 重新置顶并确认该点确实属于它
# （WindowFromPoint 可能返回子窗口，所以用 GetAncestor(GA_ROOT) 比到顶层窗口）。
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

function Click-Row {
    param($Session, $Row)
    $p = Get-RowCenter -Row $Row
    Write-Host ("  点击行 '{0}' 于 ({1},{2})" -f $Row.Current.Name, $p.X, $p.Y)
    Invoke-Click -Session $Session -X $p.X -Y $p.Y
}

function Send-Keys {
    param($Session, [string[]]$Keys)
    [void][Native]::SetWindowPos($Session.Handle, [IntPtr][Native]::HWND_TOPMOST, 0, 0, 0, 0,
        [Native]::SWP_NOMOVE -bor [Native]::SWP_NOSIZE -bor [Native]::SWP_SHOWWINDOW)
    [void][Native]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 500
    foreach ($key in $Keys) {
        [System.Windows.Forms.SendKeys]::SendWait($key)
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 1
}

function Save-Shot {
    param($Session, [string]$Name)
    New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
    $shot = Join-Path ([System.IO.Path]::GetFullPath($ShotDir)) "list-selection-$Name.png"

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

$rows = Get-Rows -Session $session
$rowNames = @($rows | ForEach-Object { $_.Current.Name } | Sort-Object)
Write-Host ("  列表行: {0}" -f ($rowNames -join ', '))
Assert ($rowNames.Count -eq 3) '临时目录里能看到 3 行'
Assert (($rowNames -join ',') -eq 'a,b,c') '行名与临时目录一致（a / b / c）'
Assert ((Get-SelectedRowNames -Session $session).Count -eq 0) '刚打开目录时没有选中项'

Write-Host '--- 用例 1：单击一行 → 只选中这一行 ---'
$rowB = $rows | Where-Object { $_.Current.Name -eq 'b' } | Select-Object -First 1
$bCenter = Get-RowCenter -Row $rowB
# 先把 exdir 弄到前台（并确认这个点真的归它）：桌面上开着开始菜单 / 别的窗口盖在 exdir 上面时，
# 真鼠标点击根本到不了它。点击之后再查就不准了：鼠标停在行上会冒出提示框（tooltip 是另一个窗口）。
# 这里只打印提示、不断言：置顶是异步生效的，紧接着探测偶尔还会读到压在 exdir 上面的终端窗口
# （SetForegroundWindow 在脚本进程不是前台进程时也会被系统拦下），真正的证据是下面的选中结果。
[void](Focus-Session -Session $session -X $bCenter.X -Y $bCenter.Y)
Write-Host ("  提示：点击点 ({0},{1}) 的顶层窗口 {2}（exdir = {3}，置顶 / 前台偶尔滞后）" -f $bCenter.X, $bCenter.Y, `
    [Native]::GetAncestor([Native]::WindowFromPoint($bCenter.X, $bCenter.Y), [Native]::GA_ROOT), $session.Handle)
Click-Row -Session $session -Row $rowB
$selected = Get-SelectedRowNames -Session $session
Write-Host ("  选中: {0}" -f ($selected -join ', '))
Assert (($selected.Count -eq 1) -and ($selected[0] -eq 'b')) '单击 b 行后恰好选中 b'
Save-Shot -Session $session -Name 'one-row'

Write-Host '--- 用例 2：Ctrl+A → 全选 ---'
Send-Keys -Session $session -Keys @('^a')
$selected = Get-SelectedRowNames -Session $session
Write-Host ("  选中: {0}" -f ($selected -join ', '))
Assert ($selected.Count -eq 3) 'Ctrl+A 后 3 行全部选中'
Assert (($selected -join ',') -eq 'a,b,c') '选中的正是 a / b / c'
$statusTexts = @(Find-ByType -From (Find-Elements -From $session.Root -Name '状态栏' | Select-Object -First 1) `
    -ControlType ([System.Windows.Automation.ControlType]::Text) | ForEach-Object { $_.Current.Name })
Write-Host ("  状态栏: {0}" -f ($statusTexts -join ' | '))
Assert (@($statusTexts | Where-Object { $_ -like '选中 3 项*' }).Count -eq 1) '状态栏也显示「选中 3 项…」（选择真的同步到了 ViewModel）'
Save-Shot -Session $session -Name 'select-all'

Write-Host '--- 用例 3：单击列表空白处 → 清空选择 ---'
# 空白点：最后一行下方 40 物理像素、同一列方向；必须还在列表里（状态栏之上）
$lastRowRect = $rows[-1].Current.BoundingRectangle
$statusBar = Find-Elements -From $session.Root -Name '状态栏' | Select-Object -First 1
$blankX = [int]($lastRowRect.X + $lastRowRect.Width / 2)
$blankY = [int]($lastRowRect.Y + $lastRowRect.Height + 40)
if ($null -ne $statusBar) {
    $statusTop = [int]$statusBar.Current.BoundingRectangle.Y
    Assert ($blankY -lt ($statusTop - 4)) "空白点（y=$blankY）在状态栏（y=$statusTop）之上"
}
Invoke-Click -Session $session -X $blankX -Y $blankY
$selected = Get-SelectedRowNames -Session $session
Write-Host ("  选中: {0}" -f ($selected -join ', '))
Assert ($selected.Count -eq 0) '单击空白处后选中被清空'
Save-Shot -Session $session -Name 'blank-click'

Write-Host '--- 用例 4：行上的点击不算空白处 ---'
$rowA = $rows | Where-Object { $_.Current.Name -eq 'a' } | Select-Object -First 1
Click-Row -Session $session -Row $rowA
$selected = Get-SelectedRowNames -Session $session
Write-Host ("  选中: {0}" -f ($selected -join ', '))
Assert (($selected.Count -eq 1) -and ($selected[0] -eq 'a')) '单击 a 行后只选中 a（没被当成空白处清空）'

Write-Host '--- 用例 5：地址栏的 Ctrl+A 仍是文本框全选（加速器只作用于文件列表） ---'
Send-Keys -Session $session -Keys @('^l')   # 进入地址栏编辑态
Send-Keys -Session $session -Keys @('^a')   # 应该是文本框全选，而不是全选文件列表
$selected = Get-SelectedRowNames -Session $session
Write-Host ("  选中: {0}" -f ($selected -join ', '))
Assert (($selected.Count -eq 1) -and ($selected[0] -eq 'a')) '地址栏里按 Ctrl+A 不会把文件列表全选'
$pathBox = Find-Elements -From $session.Root -Name '路径' | Select-Object -First 1
if ($null -ne $pathBox) {
    try {
        $textPattern = $pathBox.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $ranges = $textPattern.GetSelection()
        $selectedText = if ($ranges.Count -gt 0) { $ranges[0].GetText(-1) } else { '' }
        Write-Host ("  地址栏选中: '{0}'" -f $selectedText)
        Assert ($selectedText.Length -gt 3) '地址栏里的 Ctrl+A 选中的是路径文本'
    } catch {
        Write-Host ("  地址栏读不到 TextPattern：{0}" -f $_.Exception.Message)
    }
}
Save-Shot -Session $session -Name 'address-bar-ctrl-a'

Stop-Session -Session $session

# ------------------------------------------------------------------ 还原

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 config.json'
}

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
