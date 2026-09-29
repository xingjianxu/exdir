# 打开「配置 → 设置…」对话框，给左侧每个配置大类各截一张图，用于肉眼验证设置对话框的布局。
#
# 用法:
#   pwsh -NoProfile -File tools\shot-settings.ps1
#   pwsh -NoProfile -File tools\shot-settings.ps1 -Exe dist\win-x64\exdir.exe -Width 900 -Height 620
#
# 输出: .artifacts\settings-<分类名>.png （对话框自身区域，不是整个窗口）
#
# 默认把窗口设成 1440x900 物理像素（本机 200% 缩放 = 720x450 DIP），与 tools\capture.ps1 一致，
# 免得两次截图尺寸对不上。
#
# 说明：ContentDialog 是独立的弹出岛窗口（见 AGENTS.md 第 6 节第 27 条），
#       在 UIA 里是 RootElement 下的顶层 Window，所以截图区域取它的 BoundingRectangle。
#       需要交互桌面；无桌面时改用 tools\inspect-ui.ps1。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$OutDir = "$PSScriptRoot\..\.artifacts",
    [int]$Width = 1440,
    [int]$Height = 900,
    [string[]]$Categories = @('文件列表', '外观', '布局')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeShot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}
'@

[void][NativeShot]::SetProcessDpiAwarenessContext([IntPtr][NativeShot]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$outDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

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

# 固定窗口尺寸：设置对话框是嵌在窗口里的，窗口太小对话框会被压扁
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
[void][NativeShot]::ShowWindow($handle, 9)
[void][NativeShot]::MoveWindow($handle, $work.X, $work.Y, [Math]::Min($Width, $work.Width), [Math]::Min($Height, $work.Height), $true)
[void][NativeShot]::SetForegroundWindow($handle)
Start-Sleep -Seconds 5

$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$desktop = [System.Windows.Automation.AutomationElement]::RootElement

function Find-Elements {
    param($From, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Visible {
    param($From, [string]$Name, $Type)
    foreach ($el in (Find-Elements -From $From -Name $Name)) {
        if ($null -ne $Type -and $el.Current.ControlType -ne $Type) { continue }
        if ($el.Current.IsOffscreen) { continue }
        if ($el.Current.BoundingRectangle.Width -le 0) { continue }
        return $el
    }
    return $null
}

function Find-DialogWindow {
    foreach ($el in (Find-Elements -From $desktop -Name '设置')) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and -not $el.Current.IsOffscreen) { return $el }
    }
    return $null
}

function Save-Shot {
    param([string]$Name)

    $rect = (Find-DialogWindow).Current.BoundingRectangle
    $x = [int]$rect.X; $y = [int]$rect.Y; $w = [int]$rect.Width; $h = [int]$rect.Height
    if ($w -le 0 -or $h -le 0) { throw '对话框没有有效区域' }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $gfx.Dispose()
    $file = Join-Path $outDir ("settings-{0}.png" -f ($Name -replace '[^\w\u4e00-\u9fa5]', '_'))
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("已保存 {0}  ({1}x{2} @ {3},{4})" -f $file, $w, $h, $x, $y)
}

try {
    # 用 UIA 模式打开对话框：菜单有入场动画，真鼠标连点两次菜单会打空（见 AGENTS.md 第 6 节第 26 条）
    $menuBarItem = Find-Visible -From $root -Name '配置' -Type ([System.Windows.Automation.ControlType]::MenuItem)
    if ($null -eq $menuBarItem) { throw '主菜单里找不到「配置」' }
    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $settingsItem = $null
    for ($i = 0; $i -lt 24 -and $null -eq $settingsItem; $i++) {
        Start-Sleep -Milliseconds 250
        $settingsItem = Find-Visible -From $desktop -Name '设置' -Type ([System.Windows.Automation.ControlType]::MenuItem)
    }
    if ($null -eq $settingsItem) { throw '「配置」菜单里找不到「设置…」' }
    $settingsItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $dialog = $null
    for ($i = 0; $i -lt 24 -and $null -eq $dialog; $i++) {
        Start-Sleep -Milliseconds 250
        $dialog = Find-DialogWindow
    }
    if ($null -eq $dialog) { throw '设置对话框没有出现' }
    Start-Sleep -Milliseconds 800

    foreach ($name in $Categories) {
        $item = Find-Visible -From $dialog -Name $name -Type ([System.Windows.Automation.ControlType]::ListItem)
        if ($null -eq $item) { Write-Warning "左侧导航里找不到「$name」，跳过"; continue }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700
        Save-Shot -Name $name
    }
} finally {
    try { $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(3000) | Out-Null } catch { }
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
}
