# 启动 exdir 并截图，用于在没有人工查看界面的情况下验证 UI 渲染结果。
#
# 用法:
#   pwsh -NoProfile -File tools\capture.ps1                       # Debug 构建，默认 1440x900
#   pwsh -NoProfile -File tools\capture.ps1 -Width 2000 -Delay 6
#
# 输出: .artifacts\shot-<n>.png （自动递增，便于对比多次迭代）

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$OutDir = "$PSScriptRoot\..\.artifacts",
    [int]$Width = 1440,
    [int]$Height = 900,
    [int]$Delay = 6,
    [string[]]$Keys,
    [switch]$KeepRunning,
    [switch]$PrintWindow,
    [string]$Name
)

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}
'@

# 必须在创建任何窗口之前调用，否则后续所有坐标都会被 DPI 虚拟化，
# 导致截图区域与实际窗口错位（看不清“缺失”的控件其实只是被截掉了）。
[void][NativeWin]::SetProcessDpiAwarenessContext([IntPtr][NativeWin]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

Add-Type -AssemblyName System.Windows.Forms
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$outDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$existing = @(Get-ChildItem $outDir -Filter 'shot-*.png' -ErrorAction SilentlyContinue).Count
$index = $existing + 1
$fileName = if ($Name) { "shot-$Name.png" } else { "shot-{0:d2}.png" -f $index }
$outFile = Join-Path $outDir $fileName

# 关闭上次残留的实例
Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(3000) } catch { }
}

$proc = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -PassThru

# 等待主窗口句柄
$handle = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    if ($proc.HasExited) { break }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $handle = $proc.MainWindowHandle
        break
    }
}

if ($proc.HasExited) {
    $dir = Split-Path $exePath
    $log = Get-ChildItem $dir -Filter '*.log' -ErrorAction SilentlyContinue | Select-Object -First 3
    Write-Error ("进程已退出，退出码 = {0}。日志: {1}" -f $proc.ExitCode, (($log | ForEach-Object Name) -join ', '))
    exit 2
}

if ($handle -eq [IntPtr]::Zero) {
    try { $proc.Kill() } catch { }
    Write-Error '40 秒内未出现主窗口（可能启动即崩溃）'
    exit 3
}

# 固定尺寸与位置，保证多次截图可对比（坐标已按物理像素处理）
$w = [Math]::Min($Width, $work.Width)
$h = [Math]::Min($Height, $work.Height)

[void][NativeWin]::ShowWindow($handle, 9)                          # SW_RESTORE
[void][NativeWin]::MoveWindow($handle, $work.X, $work.Y, $w, $h, $true)
[void][NativeWin]::SetForegroundWindow($handle)
Start-Sleep -Seconds $Delay

$proc.Refresh()
if ($proc.HasExited) { Write-Error "窗口出现后进程崩溃，退出码 = $($proc.ExitCode)"; exit 4 }

# 发送按键（SendKeys 语法，例如 F10 / ^{t} / %{LEFT}），用于验证快捷键与双窗格等交互
if ($Keys) {
    foreach ($key in $Keys) {
        [void][NativeWin]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait($key)
        Start-Sleep -Milliseconds 800
    }

    Start-Sleep -Seconds 2
}

[void][NativeWin]::SetForegroundWindow($handle)
Start-Sleep -Milliseconds 400

$rect = New-Object NativeWin+RECT
[void][NativeWin]::GetWindowRect($handle, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$dpi = [NativeWin]::GetDpiForWindow($handle)
Write-Host ("窗口 {0}x{1} 物理像素, DPI {2} (缩放 {3:N2}, 等值于 {4:N0}x{5:N0} DIP)" -f $w, $h, $dpi, ($dpi / 96.0), ($w * 96.0 / $dpi), ($h * 96.0 / $dpi))

$bitmap = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bitmap)

# 优先 PrintWindow（不受遮挡影响），失败则退回屏幕拷贝
# 默认用屏幕拷贝：非客户区的系统窗口按钮由 DWM 绘制，PrintWindow 抓不到。
# 需要不受遮挡影响时用 -PrintWindow。
$ok = $false
if ($PrintWindow) {
    $hdc = $g.GetHdc()
    $ok = [NativeWin]::PrintWindow($handle, $hdc, 2)     # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)

    $probe = $bitmap.GetPixel([int]($w / 2), [int]($h / 3))
    if ($probe.R -lt 4 -and $probe.G -lt 4 -and $probe.B -lt 4) { $ok = $false }
}

if (-not $ok) {
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
}

$g.Dispose()
$bitmap.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Host ("已保存 {0}  ({1}x{2})" -f $outFile, $w, $h)

if (-not $KeepRunning) {
    # exdir 关窗口只是隐藏到托盘（隐藏时已统一落盘），收尾直接 Kill
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
}
