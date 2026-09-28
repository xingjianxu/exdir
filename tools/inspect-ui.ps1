# 用 UI Automation 操作/导出 exdir 窗口，用于在无法肉眼看界面时验证布局。
#
# 用法:
#   pwsh -NoProfile -File tools\inspect-ui.ps1                      # 列出控件树（有名称的节点）
#   pwsh -NoProfile -File tools\inspect-ui.ps1 -All                 # 列出全部节点
#   pwsh -NoProfile -File tools\inspect-ui.ps1 -Filter Desktop      # 按名称精确匹配并打印位置
#   pwsh -NoProfile -File tools\inspect-ui.ps1 -Keys "{F10}|^{t}"   # 先发送按键（SendKeys 语法），再导出
#   pwsh -NoProfile -File tools\inspect-ui.ps1 -Click "快捷菜单"      # 真实鼠标点击该元素，并截图到 .artifacts
#
# 说明：脚本必须先声明 Per-Monitor V2 DPI 感知，否则窗口坐标会被 DPI 虚拟化，
#       得到的坐标与实际像素不一致（会导致“看起来有控件缺失”的误判）。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$ShotDir = "$PSScriptRoot\..\.artifacts",
    [switch]$All,
    [string]$Filter,
    [string]$Click,
    [string]$Keys,
    [int]$MaxDepth = 30
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeDpi {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
'@

[void][NativeDpi]::SetProcessDpiAwarenessContext([IntPtr][NativeDpi]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

$proc = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -PassThru

$handle = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $proc.MainWindowHandle; break }
}
if ($handle -eq [IntPtr]::Zero) { try { $proc.Kill() } catch { }; throw '未出现主窗口' }

Start-Sleep -Seconds 4
$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$windowRect = $root.Current.BoundingRectangle
$nativeRect = New-Object NativeDpi+RECT
[void][NativeDpi]::GetWindowRect($handle, [ref]$nativeRect)
$dpi = [NativeDpi]::GetDpiForWindow($handle)
Write-Host ('窗口: {0}  UIA=[{1:N0},{2:N0} {3:N0}x{4:N0}]  Win32=[{5},{6} {7}x{8}]  DPI={9} (缩放 {10:N2})' -f `
    $proc.MainWindowTitle, $windowRect.X, $windowRect.Y, $windowRect.Width, $windowRect.Height, `
    $nativeRect.Left, $nativeRect.Top, ($nativeRect.Right - $nativeRect.Left), ($nativeRect.Bottom - $nativeRect.Top), `
    $dpi, ($dpi / 96.0))

function Show-Tree {
    param($element, [int]$depth, [int]$index)
    if ($depth -gt $MaxDepth) { return }

    $r = $element.Current.BoundingRectangle
    $name = $element.Current.Name
    $type = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''

    if ($All -or $name) {
        Write-Host ('{0}{1,-2} {2,-14} [{3,6:N0},{4,6:N0} {5,5:N0}x{6,5:N0}] {7}' -f
            ('  ' * $depth), $index, $type, $r.X, $r.Y, $r.Width, $r.Height, $name)
    }

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($element)
    $i = 0
    while ($null -ne $child) {
        Show-Tree -element $child -depth ($depth + 1) -index $i
        $child = $walker.GetNextSibling($child)
        $i++
    }
}

function Invoke-ByName {
    param([string]$name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name,
        [System.Windows.Automation.PropertyConditionFlags]::IgnoreCase)
    $found = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($found.Count -eq 0) {
        Write-Error "找不到名称为 '$name' 的元素"
        try { $proc.Kill() } catch { }
        exit 5
    }

    $target = $found[0]
    $r = $target.Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width / 2)
    $cy = [int]($r.Y + $r.Height / 2)
    Write-Host ("点击 '{0}' 于 ({1},{2})" -f $target.Current.Name, $cx, $cy)

    [void][NativeDpi]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400
    [void][NativeDpi]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 250
    [NativeDpi]::mouse_event([NativeDpi]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [NativeDpi]::mouse_event([NativeDpi]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
}

if ($Keys) {
    [void][NativeDpi]::SetForegroundWindow($handle)
    foreach ($key in $Keys.Split('|')) {
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait($key)
    }

    Start-Sleep -Seconds 2
}

if ($Click) {
    Invoke-ByName -name $Click

    $outDir = [System.IO.Path]::GetFullPath($ShotDir)
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $shot = Join-Path $outDir ('click-' + ($Click -replace '[^\w\u4e00-\u9fa5]', '_') + '.png')

    $rect = $root.Current.BoundingRectangle
    $w = [int]$rect.Width
    $h = [int]$rect.Height
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $gfx.Dispose()
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "已保存 $shot"
}

if ($Filter) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Filter,
        [System.Windows.Automation.PropertyConditionFlags]::IgnoreCase)
    $found = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    Write-Host ("匹配 '{0}' 的元素数: {1}" -f $Filter, $found.Count)
    foreach ($el in $found) {
        $r = $el.Current.BoundingRectangle
        Write-Host ('  {0,-14} [{1:N0},{2:N0} {3:N0}x{4:N0}] {5}' -f ($el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''), $r.X, $r.Y, $r.Width, $r.Height, $el.Current.Name)
    }
} else {
    Show-Tree -element $root -depth 0 -index 0
}

try { $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(3000) | Out-Null } catch { }
try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
