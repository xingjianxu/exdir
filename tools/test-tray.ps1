# 托盘驻留（单窗口模式）回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-tray.ps1
#   pwsh -NoProfile -File tools\test-tray.ps1 -Exe dist\win-x64\exdir.exe
#
# 四个用例（每次都真的启动 exdir、真鼠标操作）：
#   1. 真鼠标点窗口右上角的系统「关闭」按钮 → 窗口隐藏（IsWindowVisible=false）、进程还在、
#      托盘图标真的出现在通知区域（Shell_TrayWnd 或其“隐藏的图标”溢出面板里那个叫 exdir 的按钮）；
#   2. 隐藏状态下再启动一次 exdir.exe → 第二个进程很快自己退出（不会多出第二个托盘图标 /
#      第二份会话），原进程 PID 与窗口句柄都没变、窗口重新可见；
#   3. 「文件 → 隐藏到托盘」再藏一次，然后点通知区域里的托盘图标 → 窗口唤回；
#   4. 「文件 → 退出」→ 进程真的结束（这是唯一的退出方式，关窗口只是隐藏）。
#
# 脚本要求有交互桌面（真鼠标点关闭按钮 / 点托盘图标）；跑完会还原 settings.json 的原始内容。
#
# 为什么不禁托盘右键菜单（显示主窗口 / 退出 exdir）：它是 H.NotifyIcon 用 `TrackPopupMenu` 弹出的
# **Win32 弹出菜单**，UIA 里读不到菜单项（同 AGENTS.md 第 6 節第 42 条的坑），
# 而菜单里两个 Command 与已验证过的路径完全重合（左键单击唤回 / 菜单「文件 → 退出」），所以不重复试。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TrayNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
'@

[void][TrayNative]::SetProcessDpiAwarenessContext([IntPtr][TrayNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$settingsPath = Join-Path $env:LOCALAPPDATA 'exdir\settings.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

function Get-LogText {
    if (-not (Test-Path $script:logPath)) { return '' }
    return (Get-Content $script:logPath -Raw)
}

# 日志是追加写的、不清理：断言“刚刚才发生的那件事”时只能看这一段，
# 否则上一次运行留下的同一行会让断言假通过。
function Reset-LogMark { $script:logMark = (Get-LogText).Length }
function Get-NewLogText {
    $text = Get-LogText
    if ($text.Length -lt $script:logMark) { $script:logMark = 0 }
    return $text.Substring($script:logMark)
}
$script:logMark = 0

# ------------------------------------------------------------------ 会话

function Start-Session {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 600

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

    Start-Sleep -Seconds 5

    return [pscustomobject]@{
        Proc    = $proc
        Handle  = $handle
        Root    = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        Desktop = [System.Windows.Automation.AutomationElement]::RootElement
    }
}

# exdir 现在关窗口只是隐藏到托盘，CloseMainWindow 收不了尾，直接 Kill
function Stop-Session {
    param($Session)
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 500
}

# ------------------------------------------------------------------ UIA / 鼠标

function Find-Elements {
    param($From, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return @($From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

function Find-VisibleFirst {
    param($From, [string[]]$Names, $ControlType)
    foreach ($name in $Names) {
        foreach ($el in (Find-Elements -From $From -Name $name)) {
            if ($null -ne $ControlType -and $el.Current.ControlType -ne $ControlType) { continue }
            if ($el.Current.IsOffscreen) { continue }
            $r = $el.Current.BoundingRectangle
            if ($r.Width -le 0 -or $r.Height -le 0) { continue }
            return $el
        }
    }
    return $null
}

function Click-AtPoint {
    param([double]$X, [double]$Y)
    # 先挪开再挪回来：光标本来就在目标点上时，SetCursorPos 不会产生“移动”消息
    [void][TrayNative]::SetCursorPos([int]($X - 24), [int]($Y - 24))
    Start-Sleep -Milliseconds 200
    [void][TrayNative]::SetCursorPos([int]$X, [int]$Y)
    Start-Sleep -Milliseconds 250
    [TrayNative]::mouse_event([TrayNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [TrayNative]::mouse_event([TrayNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
}

function Click-Element {
    param($Element)
    $r = $Element.Current.BoundingRectangle
    Click-AtPoint -X ($r.X + $r.Width / 2) -Y ($r.Y + $r.Height / 2)
}

# 真鼠标点窗口右上角的系统「关闭」按钮。
# 中文系统上 UIA 仍报 "Close"（WinUI 给标题栏按钮的是英文名），两个名字都试一遍。
function Click-CloseButton {
    param($Session)
    $button = Find-VisibleFirst -From $Session.Root -Names @('关闭', 'Close') -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $button) { return $false }

    # 前台切换不是瞬时的；没抢到前台时点击会落到压在它上面的别的窗口上，整条用例会假 FAIL
    for ($i = 0; $i -lt 10; $i++) {
        [void][TrayNative]::SetForegroundWindow($Session.Handle)
        Start-Sleep -Milliseconds 200
        if ([TrayNative]::GetForegroundWindow() -eq $Session.Handle) { break }
    }

    Click-Element -Element $button
    return $true
}

# 用 UIA 模式触发菜单项：菜单栏项 ExpandCollapsePattern 展开、菜单项 InvokePattern 执行。
# 不用真鼠标点是因为弹层有入场动画，脚本“先取坐标再点”很容易打空（见 AGENTS.md 第 6 节第 26 条）。
function Invoke-MenuItem {
    param($Session, [string]$MenuName, [string]$ItemName)
    $menuBarItem = Find-VisibleFirst -From $Session.Root -Names @($MenuName) -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    if ($null -eq $menuBarItem) { throw "主菜单里找不到「$MenuName」" }

    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $item = $null
    for ($i = 0; $i -lt 24 -and $null -eq $item; $i++) {
        Start-Sleep -Milliseconds 250
        $item = Find-VisibleFirst -From $Session.Desktop -Names @($ItemName) -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    }
    if ($null -eq $item) { throw "「$MenuName」菜单里找不到「$ItemName」" }

    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Wait-WindowVisible {
    param($Session, [bool]$Visible, [int]$TimeoutSeconds = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ([TrayNative]::IsWindowVisible($Session.Handle) -eq $Visible) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# ------------------------------------------------------------------ 通知区域里的托盘图标

# 在通知区域里找那个叫 exdir 的按钮（托盘图标的 UIA 名字就是 ToolTipText）。
# Win11 默认把新图标放进“隐藏的图标”溢出面板，所以先在任务栏里找，没有再点开溢出面板找。
# 返回 @{ Found; Skipped; Element }；用完会把溢出面板收回去。
function Find-TrayIcon {
    $trayHwnd = [TrayNative]::FindWindow('Shell_TrayWnd', $null)
    if ($trayHwnd -eq [IntPtr]::Zero) { return @{ Found = $false; Skipped = $true } }
    $trayRoot = [System.Windows.Automation.AutomationElement]::FromHandle($trayHwnd)

    $hits = @(Find-Elements -From $trayRoot -Name 'exdir')
    if ($hits.Count -gt 0) { return @{ Found = $true; Skipped = $false; Element = $hits[0] } }

    # “显示隐藏的图标”按钮：中文系统是“显示隐藏的图标”，英文是 Show hidden icons
    $chevron = $null
    $buttons = $trayRoot.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    foreach ($el in $buttons) {
        if ($el.Current.Name -match '显示隐藏的图标|Show hidden icons') { $chevron = $el; break }
    }
    if ($null -eq $chevron) { return @{ Found = $false; Skipped = $true } }

    # 面板首次弹出要花时间（尤其是刚注册图标那一会儿），而且点早了可能根本没弹出来，
    # 所以“点一下 → 轮流在两个根下找”重复几次，而不是点一次就下结论（否则会假 FAIL）。
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $deadline = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400

            $roots = @()
            $overflow = [TrayNative]::FindWindow('NotifyIconOverflowWindow', $null)
            if ($overflow -ne [IntPtr]::Zero) { $roots += [System.Windows.Automation.AutomationElement]::FromHandle($overflow) }
            $roots += [System.Windows.Automation.AutomationElement]::RootElement

            foreach ($root in $roots) {
                $hits = @(Find-Elements -From $root -Name 'exdir')
                if ($hits.Count -gt 0) { return @{ Found = $true; Skipped = $false; Element = $hits[0] } }
            }
        }
    }

    return @{ Found = $false; Skipped = $false }
}

function Close-OverflowPanel {
    try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') } catch { }
    Start-Sleep -Milliseconds 600
}

# ================================================================== 用例

Write-Host '=== 用例 1：点系统「关闭」按钮 → 窗口隐藏、进程驻留、托盘图标进通知区域 ==='
$session = Start-Session
$pid1 = $session.Proc.Id
$handle1 = $session.Handle
$startsBefore = ([regex]::Matches((Get-LogText), '应用已启动')).Count
Reset-LogMark

if (-not (Click-CloseButton -Session $session)) { throw '文件管理器窗口的 UIA 树里找不到系统关闭按钮' }

# 合成鼠标输入偶尔会在窗口刚出现的那一刻打空（点击落到别的窗口上），窗口就还看得见；
# 真实用户会再点一次。重试 3 次，断言仍然是“最后必须藏起来”。
$hidden = Wait-WindowVisible -Session $session -Visible $false -TimeoutSeconds 3
for ($attempt = 2; -not $hidden -and $attempt -le 3; $attempt++) {
    if (-not (Click-CloseButton -Session $session)) { throw '文件管理器窗口的 UIA 树里找不到系统关闭按钮' }
    $hidden = Wait-WindowVisible -Session $session -Visible $false -TimeoutSeconds 3
}

$session.Proc.Refresh()
Assert (-not $session.Proc.HasExited) '关闭按钮之后进程还在运行（驻留托盘）'
Assert ($hidden -and -not [TrayNative]::IsWindowVisible($handle1)) '窗口已经隐藏（IsWindowVisible=false）'
$log = Get-NewLogText
Assert ($log -match '窗口已隐藏到托盘（进程继续驻留，托盘图标=True）') '日志确认托盘图标已创建、进程继续驻留'
Assert ((Get-LogText) -match '单实例闸门已就位') '启动时单实例闸门就位'

$trayIcon = Find-TrayIcon
if ($trayIcon.Skipped) {
    Write-Host 'SKIP 任务栏里找不到“显示隐藏的图标”按钮，没法直接确认托盘图标（日志那条已确认 Shell_NotifyIcon 成功）'
} else {
    Assert $trayIcon.Found '托盘图标真的出现在通知区域（Shell_TrayWnd 或它的溢出面板里有叫 exdir 的按钮）'
    if ($trayIcon.Found) { Close-OverflowPanel }
}

Write-Host ''
Write-Host '=== 用例 2：隐藏状态下再启动一次 exe → 第二个进程退出、已有窗口被唤回 ==='
Reset-LogMark
$second = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -PassThru
$secondExited = $second.WaitForExit(15000)
if (-not $secondExited) { try { $second.Kill() } catch { } }
Assert $secondExited '第二个进程没有一直跑（否则会出现第二个托盘图标）'
if ($secondExited) { Assert ($second.ExitCode -eq 0) "第二个进程正常退出（退出码 $($second.ExitCode)）" }

Start-Sleep -Seconds 1
$alive = @(Get-Process -Name 'exdir' -ErrorAction SilentlyContinue)
Assert ($alive.Count -eq 1) "只剩一个 exdir 进程（实际 $($alive.Count) 个）"
Assert ($alive.Count -ge 1 -and $alive[0].Id -eq $pid1) '还活着的就是原来那个进程（PID 没变）'

Assert (Wait-WindowVisible -Session $session -Visible $true) '原窗口被唤回并重新可见'
$session.Proc.Refresh()
Assert ($session.Proc.MainWindowHandle -eq $handle1) '还是原来那只窗口（句柄没变，资源没重建）'
$log = Get-NewLogText
Assert ($log -match '收到其它实例的请求：唤回主窗口') '日志确认收到了第二个实例的唤回请求'
Assert ($log -match '窗口已从托盘唤回') '日志确认窗口已从托盘唤回'
Assert (([regex]::Matches((Get-LogText), '应用已启动')).Count -eq $startsBefore) '整个过程没有再启动过一次应用（没有重建任何窗格）'

Write-Host ''
Write-Host '=== 用例 3：菜单「隐藏到托盘」+ 点托盘图标唤回 ==='
Reset-LogMark
Invoke-MenuItem -Session $session -MenuName '文件' -ItemName '隐藏到托盘'
Assert (Wait-WindowVisible -Session $session -Visible $false) '菜单里的「隐藏到托盘」把窗口藏了起来'
Assert (-not $session.Proc.HasExited) '隐藏之后进程仍然驻留'

$trayIcon = Find-TrayIcon
if (-not $trayIcon.Found) {
    Write-Host 'SKIP 通知区域里找不到托盘图标（任务栏布局/提升状态差异），跳过“点图标唤回”这一步'
} else {
    $icon = $trayIcon.Element
    if ($null -eq $icon) {
        $trayRoot = [System.Windows.Automation.AutomationElement]::FromHandle([TrayNative]::FindWindow('Shell_TrayWnd', $null))
        $hits = @(Find-Elements -From $trayRoot -Name 'exdir')
        if ($hits.Count -gt 0) { $icon = $hits[0] }
    }

    if ($null -eq $icon) {
        Write-Host 'SKIP 拿不到托盘图标元素，跳过“点图标唤回”这一步'
    } else {
        Click-Element -Element $icon
        Assert (Wait-WindowVisible -Session $session -Visible $true) '左键点托盘图标把窗口唤了回来'
        Assert ((Get-NewLogText) -match '窗口已从托盘唤回') '日志确认这次唤回也是走 ShowWindow'
    }
}

Close-OverflowPanel

Write-Host ''
Write-Host '=== 用例 4：文件 → 退出 → 进程真的结束 ==='
Reset-LogMark
Invoke-MenuItem -Session $session -MenuName '文件' -ItemName '退出'
$exited = $session.Proc.WaitForExit(10000)
if (-not $exited) { try { $session.Proc.Kill() } catch { } }
Assert $exited '菜单里的「退出」真的结束了进程'
Assert ((Get-NewLogText) -match '退出：关闭窗口并结束进程') '日志确认走的是真正退出（不是又藏进托盘）'
Assert (@(Get-Process -Name 'exdir' -ErrorAction SilentlyContinue).Count -eq 0) '退出后没有残留的 exdir 进程'

Stop-Session -Session $session

# ------------------------------------------------------------------ 收尾

if ($null -ne $originalSettings) { Set-Content -Path $settingsPath -Value $originalSettings -Encoding utf8 }

Write-Host ''
if ($failures -eq 0) {
    Write-Host '全部通过'
    exit 0
}

Write-Host "$failures 条断言失败"
exit 1
