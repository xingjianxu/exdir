# 列宽拖动（列头右边界）回归脚本：真鼠标拖拽 + UIA 量列头位置。
#
# 用法:
#   pwsh -NoProfile -File tools\test-column-resize.ps1
#   pwsh -NoProfile -File tools\test-column-resize.ps1 -Exe dist\win-x64\exdir.exe
#
# 用例（真的启动 exdir、把窗口置顶后用真鼠标拖）：
#   1. 基线：4 个可见列头（名称/修改日期/类型/大小）都在、列头与数据行对齐；
#   2. 拖「名称|修改日期」边界向左 → 只有名称列变窄（后面的列整体左移），数据行仍与列头对齐；
#   3. 拖「修改日期|类型」边界向右 → 只有修改日期列变宽；
#   4. 拖最右边（大小列右边界）向左 → 只有大小列变窄（最右一列也拖得动）；
#   5. 双击名称列边界 → 该列恢复默认宽度并回到自适应；
#   6. 复位后再拖一次（向左）→ 自适应关掉后名称列不再吃富余宽度；
#   7. 列头排序按钮仍可用（点列头 → 行顺序倒序 / 再点回升序）；
#   8. 关窗口（隐藏到托盘前会落盘）后 config.json 里落盘的正是这几列拖出来的宽度，且 ColumnAutoFit=false。
#
# 为什么要有这个脚本：把手一旦落在**零宽度的 Grid 单元格**里（例如隐藏时的“状态”列），
# WinUI 就收不到它的指针事件 —— 列头看上去一切正常、鼠标却拖不动任何列宽，
# 见 AGENTS.md“踩过的坑”。用例 2/4/5 在修复前全部没有反应（列宽纹丝不动）。
#
# 本机没有交互桌面时鼠标事件送不到窗口，脚本会先探测前台窗口。
# 注意：本脚本会把 exdir 窗口设成 TOPMOST（终端常常铺满屏幕），跑完还原 config.json。

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
public static class ColResizeNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const int HWND_TOPMOST = -1;
    public const uint GA_ROOT = 2;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint MOVE = 0x0001;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
    public const uint ABSOLUTE = 0x8000;
}
'@

[void][ColResizeNative]::SetProcessDpiAwarenessContext([IntPtr][ColResizeNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }
if (-not $originalSettings) { throw 'config.json 不存在，请先正常运行一次 exdir' }

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ 临时目录（3 个文件，行内容固定）

$workDir = Join-Path $env:TEMP 'exdir-column-resize'
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir | Out-Null
foreach ($name in 'alpha.txt', 'beta.txt', 'gamma.txt') {
    [System.IO.File]::WriteAllText((Join-Path $workDir $name), 'x')
}

# 让会话确定地打开这个目录：单窗格、无隐藏文件 / 扩展名、列宽回到默认；
# 关掉“名称列吃掉富余宽度”（AutoFillName）与打开“自适应”（AutoFit），
# 这样 4 个列边界都在窗格里、拖某一列时其它列的宽度不会被自动重算影响（断言才确定）。
$json = $originalSettings | ConvertFrom-Json
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
$json.WindowWidth = 900
$json.WindowHeight = 500
$json.ColumnWidths = @(56, 320, 136, 104, 86)
$json.ColumnAutoFillName = $false
$json.ColumnAutoFit = $true
$json | ConvertTo-Json -Depth 12 | Set-Content $settingsPath -Encoding utf8

# ------------------------------------------------------------------ 会话与鼠标

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

    [void][ColResizeNative]::SetWindowPos($handle, [IntPtr][ColResizeNative]::HWND_TOPMOST, 0, 0, 0, 0,
        [ColResizeNative]::SWP_NOMOVE -bor [ColResizeNative]::SWP_NOSIZE -bor [ColResizeNative]::SWP_SHOWWINDOW)
    [void][ColResizeNative]::SetForegroundWindow($handle)
    Start-Sleep -Seconds 5

    return [pscustomobject]@{
        Proc   = $proc
        Handle = $handle
        Root   = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        Dpi    = [ColResizeNative]::GetDpiForWindow($handle)
    }
}

function Stop-Session {
    param($Session)
    # exdir 关窗口只是隐藏到托盘（列宽变化平时只写内存，隐藏时已经统一落盘），收尾直接 Kill
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 800
}

# 用例 8 要读 config.json 里落盘的列宽：关窗口（= 隐藏到托盘）会先
# SaveWindowPlacement + SaveSession + _settings.Save()，也就是真的走一遍“落盘”，
# 所以这一处不能直接 Kill（那会把内存里的新列宽一起丢掉）。
function Close-Session {
    param($Session)
    try { $Session.Proc.CloseMainWindow() | Out-Null } catch { }
    Start-Sleep -Seconds 2
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

function Get-HeaderRect {
    param($Session, [string]$Name)
    $el = Find-Elements -From $Session.Root -Name $Name |
        Where-Object { $_.Current.BoundingRectangle.Width -gt 0 } | Select-Object -First 1
    if ($null -eq $el) { return $null }
    return $el.Current.BoundingRectangle
}

<#
    读出 4 列的宽度与 4 个列边界（都是物理像素）：
      边界 = 列头按钮的右边界；“大小”列做了一次右外扩（ExtendHeader(SizeHeader, 0, 6 DIP)，
      让列头高亮铺到窗格边缘），所以它的右边界要减掉 6 DIP；
      名称列在隐藏“状态”列时做了一次左外扩（6 DIP），所以列区左原点要加回 6 DIP。
#>
function Get-Metrics {
    param($Session)
    $scale = $Session.Dpi / 96.0
    $name = Get-HeaderRect -Session $Session -Name '按名称排序'
    $date = Get-HeaderRect -Session $Session -Name '按修改日期排序'
    $type = Get-HeaderRect -Session $Session -Name '按类型排序'
    $size = Get-HeaderRect -Session $Session -Name '按大小排序'
    if ($null -eq $name -or $null -eq $date -or $null -eq $type -or $null -eq $size) { return $null }

    $bName = [int]($name.X + $name.Width)
    $bDate = [int]($date.X + $date.Width)
    $bType = [int]($type.X + $type.Width)
    $bSize = [int]($size.X + $size.Width - 6 * $scale)
    $left = [int]($name.X + 6 * $scale)

    return [pscustomobject]@{
        Left      = $left
        BName     = $bName
        BDate     = $bDate
        BType     = $bType
        BSize     = $bSize
        NameW     = ($bName - $left) / $scale
        DateW     = ($bDate - $bName) / $scale
        TypeW     = ($bType - $bDate) / $scale
        SizeW     = ($bSize - $bType) / $scale
        HeaderCy  = [int]($name.Y + $name.Height / 2)
    }
}

function Show-Metrics {
    param($Metrics, [string]$Tag)
    Write-Host ("  [{0}] 名称={1:N1} 修改日期={2:N1} 类型={3:N1} 大小={4:N1} DIP（边界 {5}/{6}/{7}/{8}）" -f
        $Tag, $Metrics.NameW, $Metrics.DateW, $Metrics.TypeW, $Metrics.SizeW,
        $Metrics.BName, $Metrics.BDate, $Metrics.BType, $Metrics.BSize)
}

function Get-FirstRowDateX {
    param($Session)
    $row = Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 } | Select-Object -First 1
    if ($null -eq $row) { return -1 }
    $texts = @(Find-ByType -From $row -ControlType ([System.Windows.Automation.ControlType]::Text))
    if ($texts.Count -lt 2) { return -1 }
    return [int]$texts[1].Current.BoundingRectangle.X
}

# 可见行的名字（按显示顺序）；虚拟化下只拿得到真正生成出来的那几行
function Get-RowNames {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 } |
        ForEach-Object { $_.Current.Name })
}

function Get-Settings {
    try { return (Get-Content $script:settingsPath -Raw | ConvertFrom-Json) } catch { return $null }
}

function Move-Mouse {
    param([int]$X, [int]$Y)
    # SetCursorPos 不会让 WinUI 收到 PointerMoved，拖动必须用 MOUSEEVENTF_ABSOLUTE 逐步移动
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $nx = [uint32][math]::Round($X * 65535 / ($screen.Width - 1))
    $ny = [uint32][math]::Round($Y * 65535 / ($screen.Height - 1))
    [ColResizeNative]::mouse_event([ColResizeNative]::MOVE -bor [ColResizeNative]::ABSOLUTE, $nx, $ny, 0, [IntPtr]::Zero)
}

# exdir 可能被终端窗口盖住：真鼠标的第一接收者是光标下面那个窗口，先把它弄到前台
function Focus-Session {
    param($Session, [int]$X, [int]$Y)
    for ($i = 0; $i -lt 8; $i++) {
        $owner = [ColResizeNative]::GetAncestor([ColResizeNative]::WindowFromPoint($X, $Y), [ColResizeNative]::GA_ROOT)
        if (($owner -eq $Session.Handle) -and ([ColResizeNative]::GetForegroundWindow() -eq $Session.Handle)) { return $true }
        try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') } catch { }
        Start-Sleep -Milliseconds 250
        [void][ColResizeNative]::SetWindowPos($Session.Handle, [IntPtr][ColResizeNative]::HWND_TOPMOST, 0, 0, 0, 0,
            [ColResizeNative]::SWP_NOMOVE -bor [ColResizeNative]::SWP_NOSIZE -bor [ColResizeNative]::SWP_SHOWWINDOW)
        [void][ColResizeNative]::SetForegroundWindow($Session.Handle)
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Invoke-Drag {
    param($Session, [int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY)
    [void](Focus-Session -Session $Session -X $FromX -Y $FromY)
    Start-Sleep -Milliseconds 300
    Move-Mouse -X $FromX -Y $FromY
    Start-Sleep -Milliseconds 250
    [ColResizeNative]::mouse_event([ColResizeNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150

    $steps = 20
    for ($i = 1; $i -le $steps; $i++) {
        Move-Mouse -X ([int]($FromX + ($ToX - $FromX) * $i / $steps)) -Y ([int]($FromY + ($ToY - $FromY) * $i / $steps))
        Start-Sleep -Milliseconds 40
    }

    Start-Sleep -Milliseconds 300
    [ColResizeNative]::mouse_event([ColResizeNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

function Invoke-Click {
    param($Session, [int]$X, [int]$Y)
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    Start-Sleep -Milliseconds 300
    Move-Mouse -X $X -Y $Y
    Start-Sleep -Milliseconds 200
    [ColResizeNative]::mouse_event([ColResizeNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [ColResizeNative]::mouse_event([ColResizeNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

function Invoke-DoubleClick {
    param($Session, [int]$X, [int]$Y)
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    Start-Sleep -Milliseconds 300
    Move-Mouse -X $X -Y $Y
    Start-Sleep -Milliseconds 200
    # 两次按下的间隔要小于系统双击时间
    for ($i = 0; $i -lt 2; $i++) {
        [ColResizeNative]::mouse_event([ColResizeNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [ColResizeNative]::mouse_event([ColResizeNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 80
    }
    Start-Sleep -Milliseconds 900
}

function Save-Shot {
    param($Session, [string]$Name)
    New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
    $shot = Join-Path ([System.IO.Path]::GetFullPath($ShotDir)) "column-resize-$Name.png"

    $rect = New-Object ColResizeNative+RECT
    [void][ColResizeNative]::GetWindowRect($Session.Handle, [ref]$rect)
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

if ([ColResizeNative]::GetForegroundWindow() -eq [IntPtr]::Zero) {
    Write-Host '没有交互桌面（GetForegroundWindow 返回 0）：无法模拟拖拽，只能人工验证。' -ForegroundColor Yellow
    Set-Content $settingsPath $originalSettings -Encoding utf8
    exit 0
}

$session = Start-Session
$scale = $session.Dpi / 96.0

try {
    Write-Host '--- 用例 1：基线与对齐 ---'
    $base = Get-Metrics -Session $session
    if ($null -eq $base) { throw '看不到 4 个列头（名称/修改日期/类型/大小），窗口太窄或列头没出来' }
    Show-Metrics -Metrics $base -Tag '起点'
    Assert (($base.NameW -gt 40) -and ($base.DateW -gt 40) -and ($base.TypeW -gt 40) -and ($base.SizeW -gt 40)) `
        '4 列都显示出来了（列头都在窗格里）'
    # 自适应模式下列宽总和 = 可用宽度：窗格装不下默认宽度时各列会被等比压缩，所以三列都比默认值略小
    Assert (([math]::Abs($base.DateW - 136) -le 4) -and ([math]::Abs($base.TypeW - 104) -le 4) -and ([math]::Abs($base.SizeW - 86) -le 4)) `
        '修改日期/类型/大小 三列接近默认宽度'
    $available = $base.NameW + $base.DateW + $base.TypeW + $base.SizeW
    Write-Host ("  可用宽度≈{0:N1} DIP（自适应模式下列宽总和刚好填满窗格）" -f $available)
    Assert ([math]::Abs($base.NameW - ($available - $base.DateW - $base.TypeW - $base.SizeW)) -le 2) '名称列吃掉了富余宽度（自适应）'
    $dateX = Get-FirstRowDateX -Session $session
    Write-Host ("  数据行「修改日期」格子 x={0}（列头左边界 {1}）" -f $dateX, $base.BName)
    Assert ([math]::Abs($dateX - $base.BName) -le 4) '数据行与列头对齐（拖动前）'
    Save-Shot -Session $session -Name '01-before'

    Write-Host '--- 用例 2：拖「名称|修改日期」边界 → 名称列变窄 ---'
    $stepDip = 120
    $stepPx = [int]($stepDip * $scale)
    Invoke-Drag -Session $session -FromX $base.BName -FromY $base.HeaderCy -ToX ($base.BName - $stepPx) -ToY $base.HeaderCy
    $m = Get-Metrics -Session $session
    Show-Metrics -Metrics $m -Tag '拖后'
    Assert ([math]::Abs(($base.NameW - $m.NameW) - $stepDip) -le 3) "名称列宽 -$stepDip DIP（拖动真的生效了）"
    Assert ([math]::Abs(($base.BDate - $m.BDate) - $stepPx) -le 8) '修改日期列整体左移相同的距离'
    # 拖过列宽后自适应关掉，各列回到 requested 宽度（所以比“被压缩过”的起点宽一点点，3~4 DIP）
    Assert (([math]::Abs($m.DateW - $base.DateW) -le 4) -and ([math]::Abs($m.TypeW - $base.TypeW) -le 4) -and ([math]::Abs($m.SizeW - $base.SizeW) -le 4)) `
        '其它三列宽度没怎么变（只改了名称列）'
    $nameAfterCase2 = $m.NameW
    $dateX = Get-FirstRowDateX -Session $session
    Write-Host ("  数据行「修改日期」格子 x={0}（列头左边界 {1}）" -f $dateX, $m.BName)
    Assert ([math]::Abs($dateX - $m.BName) -le 4) '拖动后数据行仍与列头对齐'
    Save-Shot -Session $session -Name '02-name-dragged'

    Write-Host '--- 用例 3：拖「修改日期|类型」边界 → 修改日期列变宽 ---'
    $stepDip = 48
    $stepPx = [int]($stepDip * $scale)
    $m0 = $m
    Invoke-Drag -Session $session -FromX $m0.BDate -FromY $m0.HeaderCy -ToX ($m0.BDate + $stepPx) -ToY $m0.HeaderCy
    $m = Get-Metrics -Session $session
    Show-Metrics -Metrics $m -Tag '拖后'
    Assert ([math]::Abs(($m.DateW - $m0.DateW) - $stepDip) -le 3) "修改日期列宽 +$stepDip DIP"
    Assert (([math]::Abs($m.NameW - $m0.NameW) -le 2) -and ([math]::Abs($m.TypeW - $m0.TypeW) -le 2) -and ([math]::Abs($m.SizeW - $m0.SizeW) -le 2)) `
        '名称/类型/大小 三列宽度没变'
    Assert ([math]::Abs(($m.BType - $m0.BType) - $stepPx) -le 8) '类型列整体右移相同的距离'
    $dateXAfter = Get-FirstRowDateX -Session $session
    Assert ([math]::Abs($dateXAfter - $m.BName) -le 4) '拖动后数据行仍与新的列头边界对齐'
    Save-Shot -Session $session -Name '03-date-dragged'

    Write-Host '--- 用例 4：拖最右的「大小」列右边界 ---'
    $sizeStepDip = 24
    $sizeStepPx = [int]($sizeStepDip * $scale)
    $m0 = $m
    # 最右那条把手的中心就在列边界上（左半边在窗格内），从边界往左 4 物理像素按下更稳
    $fromX = $m0.BSize - 4
    Invoke-Drag -Session $session -FromX $fromX -FromY $m0.HeaderCy -ToX ($fromX - $sizeStepPx) -ToY $m0.HeaderCy
    $m = Get-Metrics -Session $session
    Show-Metrics -Metrics $m -Tag '拖后'
    Assert ([math]::Abs(($m0.SizeW - $m.SizeW) - $sizeStepDip) -le 3) "大小列宽 -$sizeStepDip DIP（最右一列也拖得动）"
    Assert (([math]::Abs($m.NameW - $m0.NameW) -le 2) -and ([math]::Abs($m.DateW - $m0.DateW) -le 2) -and ([math]::Abs($m.TypeW - $m0.TypeW) -le 2)) `
        '其它三列宽度没变'
    Save-Shot -Session $session -Name '04-size-dragged'

    Write-Host '--- 用例 5：双击「修改日期」列边界 → 该列恢复默认宽度并回到自适应 ---'
    # 双击只重置被拖的那一列（日期列回到默认 136）并把 AutoFit 重新打开；AutoFit 打开后名称列又会吃满富余宽度。
    Invoke-DoubleClick -Session $session -X $m.BDate -Y $m.HeaderCy
    $r = Get-Metrics -Session $session
    Show-Metrics -Metrics $r -Tag '复位'
    Assert ([math]::Abs($r.DateW - 136) -le 3) '双击后修改日期列宽回到默认 136 DIP'
    Assert (([math]::Abs($r.TypeW - $m.TypeW) -le 2) -and ([math]::Abs($r.SizeW - $m.SizeW) -le 2)) '双击只影响这一列，类型/大小宽度不变'
    Assert ([math]::Abs($r.NameW - ($available - $r.DateW - $r.TypeW - $r.SizeW)) -le 3) '名称列重新吃满富余宽度（自适应已恢复）'
    Save-Shot -Session $session -Name '05-reset'

    Write-Host '--- 用例 6：复位后再拖一次「修改日期」边界（向左，避免超出窗格）---'
    $m0 = $r
    Invoke-Drag -Session $session -FromX $m0.BDate -FromY $m0.HeaderCy -ToX ($m0.BDate - $stepPx) -ToY $m0.HeaderCy
    $m = Get-Metrics -Session $session
    Show-Metrics -Metrics $m -Tag '拖后'
    Assert ([math]::Abs(($m0.DateW - $m.DateW) - $stepDip) -le 3) "修改日期列宽 -$stepDip DIP（复位后仍能继续拖）"
    Assert (([math]::Abs($m.TypeW - $m0.TypeW) -le 2) -and ([math]::Abs($m.SizeW - $m0.SizeW) -le 2)) '类型/大小 两列宽度没变'
    # 名称列又回到自己 requested 的宽度：拖动会把自适应关掉，名称列不再吃富余宽度
    Assert ([math]::Abs($m.NameW - $nameAfterCase2) -le 3) '名称列回到拖出来的固定宽度（自适应已关闭）'
    $expectedName = [int]([math]::Round($nameAfterCase2))
    $expectedDate = [int]([math]::Round($m.DateW))
    $expectedSize = [int]([math]::Round($m.SizeW))

    Write-Host '--- 用例 7：列头排序按钮仍可用（表头结构改过的连带回归）---'
    $names = Get-RowNames -Session $session
    Write-Host ("  行顺序: {0}" -f ($names -join ', '))
    Assert (($names.Count -ge 3) -and ($names[0] -eq 'alpha')) '初始按名称升序（alpha 在最前）'
    $metrics = Get-Metrics -Session $session
    Invoke-Click -Session $session -X ([int](($metrics.Left + $metrics.BName) / 2)) -Y $metrics.HeaderCy
    $names = Get-RowNames -Session $session
    Write-Host ("  点「名称」列头后: {0}" -f ($names -join ', '))
    Assert (($names.Count -ge 3) -and ($names[0] -eq 'gamma')) '点列头后改成倒序（gamma 在最前）'
    Invoke-Click -Session $session -X ([int](($metrics.Left + $metrics.BName) / 2)) -Y $metrics.HeaderCy
    $names = Get-RowNames -Session $session
    Write-Host ("  再点一次: {0}" -f ($names -join ', '))
    Assert (($names.Count -ge 3) -and ($names[0] -eq 'alpha')) '再点一次回到升序'
    Save-Shot -Session $session -Name '06-header-sort'
}
finally {
    # 这里必须走“关窗口”而不是 Kill：隐藏前会落盘，用例 8 读的就是它写下去的值
    Close-Session -Session $session
}

Write-Host '--- 用例 8：关窗口（隐藏到托盘）后设置落盘 ---'
$settings = Get-Settings
if ($null -eq $settings) {
    Assert $false 'config.json 读不回来'
}
else {
    Write-Host ("  ColumnAutoFit={0} ColumnWidths=[{1}]" -f $settings.ColumnAutoFit, ($settings.ColumnWidths -join ', '))
    Assert ($settings.ColumnAutoFit -eq $false) '拖过列宽后 ColumnAutoFit 落盘为 false'
    Assert ([math]::Abs($settings.ColumnWidths[1] - $expectedName) -le 3) '名称列落盘的宽度就是拖出来的宽度'
    Assert ([math]::Abs($settings.ColumnWidths[2] - $expectedDate) -le 3) '修改日期列落盘为拖出来的宽度'
    Assert ([math]::Abs($settings.ColumnWidths[4] - $expectedSize) -le 3) '大小列落盘为拖出来的宽度'
}

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 config.json'
}

if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
