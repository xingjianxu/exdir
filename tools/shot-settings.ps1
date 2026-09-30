# 打开「配置 → 设置…」，给左侧每个配置大类各截一张图，用于肉眼验证设置窗口的布局。
#
# 用法:
#   pwsh -NoProfile -File tools\shot-settings.ps1
#   pwsh -NoProfile -File tools\shot-settings.ps1 -Exe dist\win-x64\exdir.exe
#
# 输出: .artifacts\settings-<分类名>.png （设置窗口自身区域，不是整个窗口）
#
# 说明：设置窗口是独立顶层窗口（标题「设置」，见 Views/SettingsWindow），
#       在 UIA 里是 RootElement 下的顶层 Window，所以截图区域取它的 BoundingRectangle；
#       桌面上别的程序（例如 ApplicationFrameHost 里的 UWP 设置页）也可能有叫“设置”的窗口，
#       所以必须按进程号过滤。
#       需要交互桌面（要真把窗口提到前台才截得到内容）；无桌面时改用 tools\inspect-ui.ps1。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$OutDir = "$PSScriptRoot\..\.artifacts",
    [string[]]$Categories = @('文件列表', '外观', '布局', '侧边栏', '右键菜单')
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
Start-Sleep -Milliseconds 500

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

$dpi = [NativeShot]::GetDpiForWindow($handle)
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

function Find-SettingsWindow {
    for ($i = 0; $i -lt 40; $i++) {
        foreach ($el in (Find-Elements -From $desktop -Name '设置')) {
            if ($el.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
            if ($el.Current.ProcessId -ne $proc.Id) { continue }
            if ($el.Current.IsOffscreen) { continue }
            return $el
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Save-Shot {
    param([string]$Name)

    $rect = (Find-SettingsWindow).Current.BoundingRectangle
    $x = [int]$rect.X; $y = [int]$rect.Y; $w = [int]$rect.Width; $h = [int]$rect.Height
    if ($w -le 0 -or $h -le 0) { throw '设置窗口没有有效区域' }

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
    # 用 UIA 模式打开：菜单有入场动画，真鼠标连点两次菜单会打空（见 AGENTS.md 第 6 节第 26 条）
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

    $window = Find-SettingsWindow
    if ($null -eq $window) { throw '设置窗口没有出现' }

    # 截屏前把设置窗口提到前台，否则截到的是盖在它上面的窗口
    [void][NativeShot]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle)
    Start-Sleep -Seconds 3

    foreach ($name in $Categories) {
        $item = Find-Visible -From $window -Name $name -Type ([System.Windows.Automation.ControlType]::ListItem)
        if ($null -eq $item) { Write-Warning "左侧导航里找不到「$name」，跳过"; continue }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

        # 「右键菜单」页的清单是切过去之后现枚举系统菜单填出来的，多等一会儿再截
        if ($name -eq '右键菜单') { Start-Sleep -Seconds 3 } else { Start-Sleep -Milliseconds 800 }
        Save-Shot -Name $name
    }
} finally {
    # exdir 关窗口只是隐藏到托盘（隐藏时已统一落盘），收尾直接 Kill
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
}
