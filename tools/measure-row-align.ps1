# 量“文件列表行里的图标”与“名称文字”是否垂直居中对齐，并断言偏差在容差内。
#
# 用法:
#   pwsh -NoProfile -File tools\measure-row-align.ps1
#   pwsh -NoProfile -File tools\measure-row-align.ps1 -Exe dist\win-x64\exdir.exe
#
# 为什么需要它：图标盒子与文字行盒在布局上都是“行高居中”的，但文字行盒底部还有一段
# 降部（descent）空白，视觉重心比行盒中心低约 1 DIP；图标盒子如果严格居中，看起来就会偏上。
# Views/DetailsView.xaml 里把图标下推了 1 DIP（RenderTransform）去对文字的视觉中心，
# 这个脚本就是那个数值的回归：先截图，再用 UIA 拿到每行“图标 / 名称文字”的矩形，
# 在截图里量两者“墨迹”的上下界，比较中心。
#
# 判定：可见行“文字墨迹中心 − 图标墨迹中心”的中位数 ≤ MaxMedianPx 物理像素（默认 3 = 1.5 DIP），
#       平均值 ≤ MaxMeanPx（默认 3.5 = 1.75 DIP）。
#       容差不是 0：图标自己的画稿在 16×16 盒子里就不一定居中（有的图标下方留白多），
#       带降部的名字（g/p/q/y）墨迹也会被尾巴拉低，所以只看中位数与平均值是否在同侧偏大。
#       去掉 DetailsView 里那 1 DIP 下推时，中位数会从 ~2.5 恶化到 ~4.5、平均值从 ~2.0 到 ~4.0，两条断言都会失败。
#
# 需要交互桌面（截图）。跑完会还原 config.json 的原始内容。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$ShotDir = "$PSScriptRoot\..\.artifacts",
    [double]$MaxMedianPx = 3,
    [double]$MaxMeanPx = 3.5
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AlignNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const int HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
}
'@

[void][AlignNative]::SetProcessDpiAwarenessContext([IntPtr][AlignNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

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
# 名字里故意混了“带降部”（program / gamma / kappa）与“不带降部”（alpha / delta / sigma）两类；
# 目录行还会顺带覆盖“文件夹图标 + 行首展开箭头”。

$workDir = Join-Path $env:TEMP 'exdir-row-align'
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $workDir 'folder-one') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $workDir 'folder-two') | Out-Null

foreach ($name in 'alpha.txt', 'beta.log', 'delta.ini', 'program.exe', 'gamma.cfg', 'sigma.xml', 'kappa.toml') {
    Set-Content (Join-Path $workDir $name) 'x'
}

# 残留实例退出时会把 config.json 写成它自己的会话，会把下面刚写好的会话盖掉
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

# ------------------------------------------------------------------ 启动 + 截图 + 量

$proc = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -PassThru
$handle = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $proc.MainWindowHandle; break }
}
if ($handle -eq [IntPtr]::Zero) { throw '未出现主窗口' }

[void][AlignNative]::SetWindowPos($handle, [IntPtr][AlignNative]::HWND_TOPMOST, 0, 0, 0, 0,
    [AlignNative]::SWP_NOMOVE -bor [AlignNative]::SWP_NOSIZE -bor [AlignNative]::SWP_SHOWWINDOW)
[void][AlignNative]::SetForegroundWindow($handle)
Start-Sleep -Seconds 6

$rect = New-Object AlignNative+RECT
[void][AlignNative]::GetWindowRect($handle, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$dpi = [AlignNative]::GetDpiForWindow($handle)
$scale = $dpi / 96.0

$bmp = New-Object System.Drawing.Bitmap $width, $height
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))
$gfx.Dispose()

New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
$shot = Join-Path ([System.IO.Path]::GetFullPath($ShotDir)) 'row-align.png'
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ("窗口原点 ({0},{1})，DPI {2}（缩放 {3:N2}），截图 {4}" -f $rect.Left, $rect.Top, $dpi, $scale, $shot)

# 截图坐标 = 屏幕坐标 − 窗口原点
$originX = $rect.Left
$originY = $rect.Top

$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

function Find-ByType {
    param($From, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByName {
    param($From, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# 在给定矩形里找“墨迹”的上下界（与背景亮度差 > 30 的行算墨迹）；传的是屏幕坐标
function Get-InkBounds {
    param([int]$X0, [int]$Y0, [int]$X1, [int]$Y1, [double]$Background)

    $min = -1
    $max = -1

    for ($y = $Y0; $y -lt $Y1; $y++) {
        $hit = $false
        for ($x = $X0; $x -lt $X1; $x++) {
            $c = $bmp.GetPixel(($x - $script:originX), ($y - $script:originY))
            $lum = ($c.R * 299 + $c.G * 587 + $c.B * 114) / 1000
            if ([Math]::Abs($lum - $Background) -gt 30) { $hit = $true; break }
        }

        if ($hit) {
            if ($min -lt 0) { $min = $y }
            $max = $y
        }
    }

    return @($min, $max)
}

Write-Host ''
Write-Host ("{0,-16} {1,-21} {2,-21} {3}" -f '行', '图标盒子[上..下] 心', '文字盒子[上..下] 心', '墨迹心差(文字−图标, 物理像素)')

$deltas = @()
$rowList = Find-ByType -From $root -ControlType ([System.Windows.Automation.ControlType]::ListItem)

foreach ($row in $rowList) {
    $name = $row.Current.Name
    if ([string]::IsNullOrEmpty($name) -or $row.Current.IsOffscreen) { continue }

    $image = @(Find-ByName -From $row -Name '程序图标') | Where-Object { $_.Current.BoundingRectangle.Width -gt 0 } | Select-Object -First 1
    $text = @(Find-ByType -From $row -ControlType ([System.Windows.Automation.ControlType]::Text)) |
        Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if ($null -eq $image -or $null -eq $text) { continue }

    $ir = $image.Current.BoundingRectangle
    $tr = $text.Current.BoundingRectangle

    $iconY0 = [Math]::Max([int]$ir.Y, $originY)
    $iconY1 = [Math]::Min([int]($ir.Y + $ir.Height), $originY + $height)
    $textY0 = [Math]::Max([int]$tr.Y, $originY)
    $textY1 = [Math]::Min([int]($tr.Y + $tr.Height), $originY + $height)

    $bgSample = $bmp.GetPixel(([int]$ir.X - 6 - $originX), ([int]($ir.Y + $ir.Height / 2) - $originY))
    $background = ($bgSample.R * 299 + $bgSample.G * 587 + $bgSample.B * 114) / 1000

    $iconInk = Get-InkBounds -X0 ([int]$ir.X) -Y0 $iconY0 -X1 ([int]($ir.X + $ir.Width)) -Y1 $iconY1 -Background $background
    $textInk = Get-InkBounds -X0 ([int]$tr.X) -Y0 $textY0 -X1 ([int]($tr.X + $tr.Width)) -Y1 $textY1 -Background $background

    $iconBoxCenter = $ir.Y + ($ir.Height / 2)
    $textBoxCenter = $tr.Y + ($tr.Height / 2)

    if ($iconInk[0] -lt 0 -or $textInk[0] -lt 0) {
        Write-Host ("{0,-16} 跳过（量不到墨迹）" -f $name)
        continue
    }

    $delta = (($textInk[0] + $textInk[1]) / 2) - (($iconInk[0] + $iconInk[1]) / 2)
    $deltas += $delta

    Write-Host ("{0,-16} [{1,4}..{2,4}] {3,7:N1}   [{4,4}..{5,4}] {6,7:N1}   {7,7:N1}" -f `
        $name, $iconY0, $iconY1, $iconBoxCenter, $textY0, $textY1, $textBoxCenter, $delta)
}

$bmp.Dispose()
# exdir 关窗口只是隐藏到托盘（隐藏时已统一落盘），收尾直接 Kill
try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }

# ------------------------------------------------------------------ 还原

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 config.json'
}
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }

# ------------------------------------------------------------------ 断言

Write-Host ''
Assert ($deltas.Count -ge 5) ("量到足够多的行（{0} 行）" -f $deltas.Count)

if ($deltas.Count -gt 0) {
    $sorted = @($deltas | Sort-Object)
    $median = $sorted[[int][Math]::Floor($sorted.Count / 2)]
    $mean = ($deltas | Measure-Object -Average).Average

    Write-Host ("  偏差：中位数 {0:N1} 物理像素（{1:N1} DIP），平均 {2:N1} 物理像素（{3:N1} DIP）" -f `
        $median, ($median / $scale), $mean, ($mean / $scale))

    Assert ([Math]::Abs($median) -le $MaxMedianPx) ("图标与名称文字垂直居中对齐：偏差中位数 ≤ {0} 物理像素" -f $MaxMedianPx)
    Assert ([Math]::Abs($mean) -le $MaxMeanPx) ("整体偏差平均 ≤ {0} 物理像素" -f $MaxMeanPx)
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
