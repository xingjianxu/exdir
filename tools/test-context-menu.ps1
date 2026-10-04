# 系统右键菜单的回归脚本（真鼠标右键 + 截图 + exdir.log 断言）。
#
# 用法:
#   pwsh -NoProfile -File tools\test-context-menu.ps1
#   pwsh -NoProfile -File tools\test-context-menu.ps1 -Exe dist\win-x64\exdir.exe
#
# 三个用例（每次都真的启动 exdir、用真鼠标右键）：
#   1. 在文件行上右键 → 弹出系统菜单（Win32 的 #32768 弹出菜单窗口属于 exdir 进程），
#      截图存到 .artifacts\context-menu-file.png；
#   2. 在列表空白处右键 → 弹出目录背景菜单，截图 context-menu-background.png；
#   3. 把某个菜单项在设置里关掉（config.json 的 ShellMenuDisabledItems 写进「属性」的键 verb:properties）
#      → 重启后弹出的菜单里不再有「属性」（exdir.log 里会写“已关闭 属性”），截图 context-menu-filtered.png。
#
# 另外两个用例验证默认的“内置轻量菜单”（config.json 的 UseBuiltInContextMenu=true）：
#   4. 内置菜单是 WinUI MenuFlyout（会进 UIA 树，能直接读菜单项），进程里**没有** #32768；
#      文件行菜单里有「打开 / 在资源管理器中显示 / 复制路径 / 属性」；
#   5. 空白处菜单里有「新建文件夹 / 全选 / 在此处打开终端」，用 UIA 的 InvokePattern 点「新建文件夹」
#      → 磁盘上真的建出了目录（内置菜单的命令是 exdir 自己执行的，不是交回外壳）。
#
# 最后一个用例验的是「内置菜单里合并系统菜单项」（BuiltInMenuIncludeShellItems=true）：
#   6. 打开这个开关后，内置菜单（仍是 WinUI MenuFlyout、进程里没有 #32768）里会出现外壳的项
#      （「发送到」这种 exdir 自己没实现的），而「打开 / 复制 / 属性」不会重复（按规范动词去重）；
#      展开「发送到」子菜单真的多出项来（子菜单是外壳“即将展开时才填”的，渲染前替它代发了
#      WM_INITMENUPOPUP）；背景菜单里也合并了（日志）；启动预热日志里有「系统右键菜单：预热完成」。
#
# 为什么用 exdir.log 断言而不是 UIA：Windows 11 的外壳右键菜单是自绘的，
# Win32 #32768 窗口里没有可供 UIA 读取的 MenuItem（整张菜单在 UIA 里就是一个 Pane），
# 所以“菜单弹出来了”靠 EnumWindows 找 #32768，“有哪些项 / 关掉了哪些项”靠 exdir 自己的日志。
#
# 脚本要求有交互桌面（真实鼠标右键 + 截图）；跑完会还原 config.json 的原始内容。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class MenuNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;

    /// <summary>某个进程当前的 Win32 弹出菜单窗口（#32768）。</summary>
    public static List<IntPtr> FindPopupMenus(uint targetPid) {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid) return true;
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "#32768") found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

[void][MenuNative]::SetProcessDpiAwarenessContext([IntPtr][MenuNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$shotDir = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\.artifacts")
# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

# 自己造一个只有两个文件的目录当测试现场：这样列表下面一定有空白的“背景”可以右键
$testDir = Join-Path $env:TEMP 'exdir-context-menu-test'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
Set-Content (Join-Path $testDir 'alpha.txt') 'a' -Encoding utf8
Set-Content (Join-Path $testDir 'beta.txt') 'b' -Encoding utf8

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# ------------------------------------------------------------------ config.json / 日志

function Get-Setting {
    param([string]$Name)
    return (Get-Content $script:settingsPath -Raw | ConvertFrom-Json).$Name
}

function Set-Setting {
    param([string]$Name, $Value)
    $json = Get-Content $script:settingsPath -Raw | ConvertFrom-Json

    # 老版本的 config.json 里可能还没有这个字段（例如刚加的 ShellMenuDisabledItems），
    # 直接 $json.$Name = $Value 会报“找不到属性”，所以用 Add-Member -Force
    $json | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content $script:settingsPath -Encoding utf8
}

function Get-LogTail {
    param([int]$Lines = 30)
    if (-not (Test-Path $script:logPath)) { return @() }
    return @(Get-Content $script:logPath -Tail $Lines)
}

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

    Start-Sleep -Seconds 5

    return [pscustomobject]@{
        Proc   = $proc
        Handle = $handle
        Root   = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    }
}

function Stop-Session {
    param($Session)
    # exdir 关窗口只是隐藏到托盘（隐藏时已统一落盘），收尾直接 Kill
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 500
}

# ------------------------------------------------------------------ UIA / 鼠标

function Find-Rows {
    param($Session)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($Session.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

function Invoke-RightClick {
    param($Session, [int]$ScreenX, [int]$ScreenY)
    [void][MenuNative]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 400
    [void][MenuNative]::SetCursorPos($ScreenX, $ScreenY)
    Start-Sleep -Milliseconds 300
    [MenuNative]::mouse_event([MenuNative]::RIGHTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [MenuNative]::mouse_event([MenuNative]::RIGHTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 3
}

function Dismiss-Menu {
    param($Session)
    [void][MenuNative]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 2
}

function Get-PopupMenus {
    param($Session)
    return @([MenuNative]::FindPopupMenus([uint32]$Session.Proc.Id))
}

# 内置（自建）右键菜单是 WinUI MenuFlyout，会进 UIA 树（和系统菜单不同）；
# 但它不在主窗口的 UIA 子树里（见 AGENTS.md 第 6 节第 24 条），要从桌面往下找、并按进程过滤。
function Get-VisibleMenuItems {
    param($Session)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)
    $items = @()
    foreach ($el in $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($el.Current.ProcessId -ne $Session.Proc.Id) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $items += $el
    }
    return $items
}

function Find-MenuItems {
    param($Session, [string]$Name)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $items = @()
    foreach ($el in $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($el.Current.ProcessId -ne $Session.Proc.Id) { continue }
        if ($el.Current.ControlType -ne [System.Windows.Automation.ControlType]::MenuItem) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $items += $el
    }
    return $items
}

# 截“窗口 ∪ 弹出菜单”：菜单在窗口外面，只截窗口会漏掉它
function Save-Shot {
    param($Session, [string]$Name)
    New-Item -ItemType Directory -Force -Path $script:shotDir | Out-Null
    $shot = Join-Path $script:shotDir ($Name + '.png')

    $rect = $Session.Root.Current.BoundingRectangle
    $left = [int]$rect.X; $top = [int]$rect.Y
    $right = [int]($rect.X + $rect.Width); $bottom = [int]($rect.Y + $rect.Height)

    foreach ($hwnd in (Get-PopupMenus -Session $Session)) {
        $r = New-Object MenuNative+RECT
        if (-not [MenuNative]::GetWindowRect($hwnd, [ref]$r)) { continue }
        $left = [Math]::Min($left, $r.Left); $top = [Math]::Min($top, $r.Top)
        $right = [Math]::Max($right, $r.Right); $bottom = [Math]::Max($bottom, $r.Bottom)
    }

    # 内置菜单是 XAML 弹层（不是 #32768），把可见菜单项的矩形也并进来
    $menuCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($el in ([System.Windows.Automation.AutomationElement]::RootElement).FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, $menuCond)) {
        if ($el.Current.ProcessId -ne $Session.Proc.Id) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $left = [Math]::Min($left, [int]$r.X); $top = [Math]::Min($top, [int]$r.Y)
        $right = [Math]::Max($right, [int]($r.X + $r.Width)); $bottom = [Math]::Max($bottom, [int]($r.Y + $r.Height))
    }

    $left = [Math]::Max(0, $left); $top = [Math]::Max(0, $top)
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $right = [Math]::Min($screen.Width, $right); $bottom = [Math]::Min($screen.Height, $bottom)

    $w = [Math]::Max(1, $right - $left); $h = [Math]::Max(1, $bottom - $top)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $gfx.Dispose()
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "已保存 $shot"
}

try {

    # ============================================================== 用例 1：文件行上的系统菜单

    Write-Host '--- 用例 1：在文件行上右键弹出系统菜单 ---'
    Set-Setting 'PrimaryTabs' ([string[]]@($testDir))
    Set-Setting 'IsDualPane' $false
    Set-Setting 'ShellMenuDisabledItems' ([string[]]@())
    Set-Setting 'ShellMenuKnownItems' ([string[]]@())
    # 这一部分验的是系统菜单，必须显式关掉内置菜单（默认值是内置）
    Set-Setting 'UseBuiltInContextMenu' $false

    # 窗口尺寸也写死：上次退出时如果窗口是最小化的，位置/尺寸可能是哨兵值，
    # 窗口会小到只剩侧边栏，右键点不到列表（见 MainWindow.SaveWindowPlacement）
    Set-Setting 'WindowWidth' 1280
    Set-Setting 'WindowHeight' 800
    Set-Setting 'WindowX' 0
    Set-Setting 'WindowY' 0
    Set-Setting 'WindowMaximized' $false
    Set-Setting 'SidebarWidth' 232

    $session = Start-Session

    $rows = Find-Rows -Session $session
    $alpha = $rows | Where-Object { $_.Current.Name -like 'alpha*' } | Select-Object -First 1
    Assert ($null -ne $alpha) '测试目录里的 alpha.txt 出现在列表里'

    $rect = $alpha.Current.BoundingRectangle
    Invoke-RightClick -Session $session -ScreenX ([int]($rect.X + $rect.Width / 3)) -ScreenY ([int]($rect.Y + $rect.Height / 2))

    Assert ((Get-PopupMenus -Session $session).Count -ge 1) '右键文件行弹出了系统菜单（进程里出现了 #32768 弹出菜单窗口）'
    $log = Get-LogTail
    Assert (@($log | Where-Object { $_ -match '系统右键菜单：文件 上下文共 (\d+) 项' }).Count -ge 1) '日志记下了“文件”上下文的菜单项数量'
    Save-Shot -Session $session -Name 'context-menu-file'
    Dismiss-Menu -Session $session
    Assert ((Get-PopupMenus -Session $session).Count -eq 0) 'Esc 之后弹出菜单关掉了'

    # ============================================================== 用例 2：列表空白处的背景菜单

    Write-Host '--- 用例 2：在列表空白处右键弹出背景菜单 ---'
    # 空白处要按“文件行的正下方”算：$rows 里也包含侧边栏树的节点，不能用“最底下的那个 ListItem”
    $rowRect = $alpha.Current.BoundingRectangle
    $blankX = [int]($rowRect.X + $rowRect.Width / 3)
    $blankY = [int]($rowRect.Y + $rowRect.Height * 4)
    Write-Host ("  空白处坐标 ({0},{1})" -f $blankX, $blankY)

    Invoke-RightClick -Session $session -ScreenX $blankX -ScreenY $blankY

    Assert ((Get-PopupMenus -Session $session).Count -ge 1) '空白处右键弹出了背景菜单'
    $log = Get-LogTail
    Assert (@($log | Where-Object { $_ -match '系统右键菜单：背景 上下文共 (\d+) 项' }).Count -ge 1) '日志记下了“背景”上下文的菜单项数量'
    Save-Shot -Session $session -Name 'context-menu-background'
    Dismiss-Menu -Session $session

    Stop-Session -Session $session

    # 菜单里见到的项应该被记进清单（退出时落盘）
    $known = @(Get-Setting 'ShellMenuKnownItems')
    $knownTexts = @($known | ForEach-Object { $_.Text })
    Write-Host ("  清单里记下了 {0} 项：{1}" -f $known.Count, (($knownTexts | Select-Object -First 12) -join ' / '))
    Assert ($known.Count -gt 10) '退出后 config.json 的 ShellMenuKnownItems 记下了枚举出来的菜单项'
    Assert (@($known | Where-Object { $_.Key -eq 'verb:properties' }).Count -eq 1) '清单里有「属性」（key = verb:properties）'
    Assert (@($knownTexts | Where-Object { $_ -eq '打开' }).Count -eq 1) '清单里有「打开」（加速键与省略号已去掉）'
    Assert (@($known | Where-Object { $_.Scopes -contains '背景' }).Count -gt 0) '清单里记下了「背景」上下文的项'

    # ============================================================== 用例 3：关掉的项不再出现

    Write-Host '--- 用例 3：在设置里关掉「属性」后菜单里不再有它 ---'
    Set-Setting 'UseBuiltInContextMenu' $false
    Set-Setting 'ShellMenuDisabledItems' ([string[]]@('verb:properties'))

    $session = Start-Session
    $rows = Find-Rows -Session $session
    $alpha = $rows | Where-Object { $_.Current.Name -like 'alpha*' } | Select-Object -First 1
    $rect = $alpha.Current.BoundingRectangle
    Invoke-RightClick -Session $session -ScreenX ([int]($rect.X + $rect.Width / 3)) -ScreenY ([int]($rect.Y + $rect.Height / 2))

    Assert ((Get-PopupMenus -Session $session).Count -ge 1) '关掉「属性」后菜单照样弹得出来'
    $log = Get-LogTail
    Write-Host ("  日志: {0}" -f (@($log | Where-Object { $_ -match '系统右键菜单' }) -join ' | '))
    Assert (@($log | Where-Object { $_ -match '已关闭 属性' }).Count -ge 1) '日志表明「属性」这次被从菜单里删掉了（已关闭 属性）'
    Save-Shot -Session $session -Name 'context-menu-filtered'
    Dismiss-Menu -Session $session
    Stop-Session -Session $session

    # ============================================================== 用例 4：内置菜单（轻量、弹出快）

    Write-Host '--- 用例 4：切到内置菜单后右键弹出的是 exdir 自建菜单 ---'
    Set-Setting 'UseBuiltInContextMenu' $true

    $session = Start-Session
    $rows = Find-Rows -Session $session
    $alpha = $rows | Where-Object { $_.Current.Name -like 'alpha*' } | Select-Object -First 1
    Assert ($null -ne $alpha) '测试目录里的 alpha.txt 出现在列表里（内置菜单）'

    $rect = $alpha.Current.BoundingRectangle
    Invoke-RightClick -Session $session -ScreenX ([int]($rect.X + $rect.Width / 3)) -ScreenY ([int]($rect.Y + $rect.Height / 2))

    Assert ((Get-PopupMenus -Session $session).Count -eq 0) '内置菜单不是 Win32 弹出菜单（进程里没有 #32768）'
    Assert ((Find-MenuItems -Session $session -Name '打开').Count -ge 1) '内置菜单里有「打开」'
    Assert ((Find-MenuItems -Session $session -Name '在资源管理器中显示').Count -ge 1) '内置菜单里有「在资源管理器中显示」'
    Assert ((Find-MenuItems -Session $session -Name '复制路径').Count -ge 1) '内置菜单里有「复制路径」'
    Assert ((Find-MenuItems -Session $session -Name '属性').Count -ge 1) '内置菜单里有「属性」'
    Assert ((Find-MenuItems -Session $session -Name '新建文件夹').Count -eq 0) '文件行的菜单里没有背景命令「新建文件夹」'
    # 默认**不**合并系统菜单项（那一步要把第三方 shell 扩展 Load 进本进程，弹出会变慢）
    Assert ((Find-MenuItems -Session $session -Name '发送到').Count -eq 0) '默认不合并系统菜单项（没有外壳的「发送到」）'
    Assert (@(Get-LogTail | Where-Object { $_ -match '内置右键菜单：文件 上下文 \d+ 项' }).Count -ge 1) '日志记下了内置菜单的上下文与项数'

    Save-Shot -Session $session -Name 'context-menu-builtin-file'
    Dismiss-Menu -Session $session
    Assert ((Find-MenuItems -Session $session -Name '打开').Count -eq 0) 'Esc 之后内置菜单关掉了'

    # ============================================================== 用例 5：内置菜单的背景命令真的能用

    Write-Host '--- 用例 5：内置菜单的「新建文件夹」真的建出目录 ---'
    Get-ChildItem -Path $testDir -Filter '新建文件夹*' -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

    $rowRect = $alpha.Current.BoundingRectangle
    $blankX = [int]($rowRect.X + $rowRect.Width / 3)
    $blankY = [int]($rowRect.Y + $rowRect.Height * 4)
    Invoke-RightClick -Session $session -ScreenX $blankX -ScreenY $blankY

    Assert (@(Get-LogTail | Where-Object { $_ -match '内置右键菜单：背景 上下文 \d+ 项' }).Count -ge 1) '空白处右键弹出了内置背景菜单（日志）'
    Assert ((Find-MenuItems -Session $session -Name '全选').Count -ge 1) '内置背景菜单里有「全选」'
    Assert ((Find-MenuItems -Session $session -Name '在此处打开终端').Count -ge 1) '内置背景菜单里有「在此处打开终端」'
    Assert ((Find-MenuItems -Session $session -Name '打开').Count -eq 0) '背景菜单里没有文件命令「打开」'

    $newFolderItem = Find-MenuItems -Session $session -Name '新建文件夹' | Select-Object -First 1
    Assert ($null -ne $newFolderItem) '内置背景菜单里有「新建文件夹」'
    Save-Shot -Session $session -Name 'context-menu-builtin-background'

    if ($null -ne $newFolderItem) {
        $newFolderItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3
        Assert (Test-Path (Join-Path $testDir '新建文件夹')) '点「新建文件夹」后磁盘上真的建出了目录'
    }

    Stop-Session -Session $session

    # ============================================================== 用例 6：内置菜单里合并系统菜单项

    Write-Host '--- 用例 6：打开「在内置菜单里合并系统菜单项」后，内置菜单里出现外壳的项 ---'
    Set-Setting 'BuiltInMenuIncludeShellItems' $true

    $session = Start-Session

    # 预热：启动时就把常见上下文读一遍并缓存（把第三方扩展 Load 进本进程）；
    # 它在消息循环空闲时才跑，所以等一会儿（它自己在日志里留一行）
    $preheated = $false
    for ($i = 0; $i -lt 20 -and -not $preheated; $i++) {
        $preheated = @(Get-LogTail -Lines 200 | Where-Object { $_ -match '系统右键菜单：预热完成' }).Count -ge 1
        if (-not $preheated) { Start-Sleep -Seconds 1 }
    }
    Assert $preheated '启动预热里读了系统菜单项（日志「系统右键菜单：预热完成」）'

    $rows = Find-Rows -Session $session
    $alpha = $rows | Where-Object { $_.Current.Name -like 'alpha*' } | Select-Object -First 1
    Assert ($null -ne $alpha) '测试目录里的 alpha.txt 出现在列表里（合并系统菜单项）'

    $rect = $alpha.Current.BoundingRectangle
    Invoke-RightClick -Session $session -ScreenX ([int]($rect.X + $rect.Width / 3)) -ScreenY ([int]($rect.Y + $rect.Height / 2))

    Assert ((Get-PopupMenus -Session $session).Count -eq 0) '合并系统菜单项后仍然是 WinUI 菜单（进程里没有 #32768）'
    Assert ((Find-MenuItems -Session $session -Name '发送到').Count -ge 1) '内置菜单里有外壳的「发送到」（exdir 自己没这一项）'
    Assert ((Find-MenuItems -Session $session -Name '打开').Count -eq 1) '「打开」不重复（按规范动词 verb:open 去掉了外壳那一份）'
    Assert ((Find-MenuItems -Session $session -Name '复制').Count -eq 1) '「复制」不重复（verb:copy 去重）'
    Assert ((Find-MenuItems -Session $session -Name '属性').Count -eq 1) '「属性」不重复（verb:properties 去重）'
    Assert (@(Get-LogTail | Where-Object { $_ -match '内置右键菜单：文件 上下文 \d+ 项（含系统菜单项 \d+ 项）' }).Count -ge 1) '日志记下了合并进来的系统菜单项个数'

    # 系统菜单项自带图标（MENUITEMINFO.hbmpItem）的像素副本：服务层抄到几个、有没有抄砸，都写在日志里。
    # 本机 Windows 10 的“打开”一项总是带图标（文件类型图标），所以抄到的个数必然大于 0。
    $iconLog = Get-LogTail -Lines 200 | Where-Object { $_ -match '系统右键菜单：图标抄到 \d+ 个' } | Select-Object -Last 1
    Assert ($null -ne $iconLog) '日志里有“系统右键菜单：图标抄到 N 个”（系统菜单项的图标真的被抄出来了）'
    if ($null -ne $iconLog) {
        $iconCount = [int]([regex]::Match($iconLog, '图标抄到 (\d+) 个').Groups[1].Value)
        Assert ($iconCount -gt 0) "抄到了 $iconCount 个系统菜单项图标（内置菜单里的系统项因此带图标）"
    }

    # 子菜单里的项是“即将展开时才填”的（外壳的 WM_INITMENUPOPUP）：我们渲染前替外壳代发了它，
    # 所以展开「发送到」应该真的多出项来（空的子菜单在服务层就被丢掉了）
    $sendTo = Find-MenuItems -Session $session -Name '发送到' | Select-Object -First 1
    if ($null -ne $sendTo) {
        $before = @(Get-VisibleMenuItems -Session $session).Count
        try { $sendTo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { }
        Start-Sleep -Seconds 2
        $after = @(Get-VisibleMenuItems -Session $session).Count
        Write-Host ("  「发送到」展开前 {0} 个菜单项，展开后 {1} 个" -f $before, $after)
        Assert ($after -gt $before) '「发送到」子菜单展开后真的多出了项（不是空的占位子菜单）'
    }

    Save-Shot -Session $session -Name 'context-menu-builtin-shell-items'
    Dismiss-Menu -Session $session

    # 背景菜单也要合并且不去重掉外壳的「属性」（背景里本来没有 属性，不能拿行菜单那份动词集合来套）
    $rowRect = $alpha.Current.BoundingRectangle
    Invoke-RightClick -Session $session -ScreenX ([int]($rowRect.X + $rowRect.Width / 3)) -ScreenY ([int]($rowRect.Y + $rowRect.Height * 4))
    Assert ((Find-MenuItems -Session $session -Name '全选').Count -ge 1) '合并后的背景菜单里仍有内置的「全选」'
    Assert (@(Get-LogTail | Where-Object { $_ -match '内置右键菜单：背景 上下文 \d+ 项（含系统菜单项 \d+ 项）' }).Count -ge 1) '背景菜单也合并了系统菜单项（日志）'
    Save-Shot -Session $session -Name 'context-menu-builtin-shell-items-background'
    Dismiss-Menu -Session $session

    Stop-Session -Session $session

}
finally {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    # 清掉用例 5 里“新建文件夹”建出来的目录
    Get-ChildItem -Path $testDir -Filter '新建文件夹*' -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

    if ($null -ne $originalSettings) {
        Set-Content $settingsPath $originalSettings -Encoding utf8
        Write-Host '已还原 config.json'
    }
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
