# 设置对话框（「配置 → 设置…」）的 UIA 回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-settings.ps1
#   pwsh -NoProfile -File tools\test-settings.ps1 -Exe dist\win-x64\exdir.exe
#
# 三个用例（每次都真的启动 exdir、用真鼠标点菜单，再点对话框按钮）：
#   1. 对话框内容齐全（8 个勾选项 + 保存/取消），且读到的值与 settings.json 一致；
#   2. 改一项后点「取消」→ settings.json 不变；
#   3. 改一项后点「保存」→ 立即落盘，并且真的作用到文件列表（关掉扩展名后行名里的 ".xxx" 消失）。
#
# 脚本要求有交互桌面（真实鼠标点击 + 截图）；无桌面时请改用 tools\inspect-ui.ps1。
# 跑完会还原 settings.json 的原始内容。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
'@

[void][Native]::SetProcessDpiAwarenessContext([IntPtr][Native]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$repoDir = [System.IO.Path]::GetFullPath("$PSScriptRoot\..")
$settingsPath = Join-Path $env:LOCALAPPDATA 'exdir\settings.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

$KeyMap = [ordered]@{
    '显示隐藏文件'           = 'hidden'
    '显示文件扩展名'         = 'extension'
    '文件夹排在文件前面'     = 'foldersFirst'
    '过渡动画'               = 'animations'
    '列宽自动适应窗格宽度'   = 'columnAutoFit'
    '显示工具条'             = 'toolbar'
    '显示侧边栏'             = 'sidebar'
    '双窗格模式'             = 'dualPane'
}

# ------------------------------------------------------------------ settings.json 读写

function Get-Setting {
    param([string]$Name)
    return (Get-Content $script:settingsPath -Raw | ConvertFrom-Json).$Name
}

function Set-Setting {
    param([string]$Name, $Value)
    $json = Get-Content $script:settingsPath -Raw | ConvertFrom-Json
    $json.$Name = $Value
    $json | ConvertTo-Json -Depth 10 | Set-Content $script:settingsPath -Encoding utf8
}

# ------------------------------------------------------------------ 会话与对话框

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
        Proc    = $proc
        Handle  = $handle
        Root    = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        Desktop = [System.Windows.Automation.AutomationElement]::RootElement
    }
}

function Stop-Session {
    param($Session)
    try { $Session.Proc.CloseMainWindow() | Out-Null; $Session.Proc.WaitForExit(3000) | Out-Null } catch { }
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
}

function Find-Elements {
    param($From, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-First {
    param($From, [string]$Name, $ControlType)
    foreach ($el in (Find-Elements -From $From -Name $Name)) {
        if ($null -eq $ControlType -or $el.Current.ControlType -eq $ControlType) { return $el }
    }
    return $null
}

# 菜单/对话框这类弹层关掉之后，它的元素可能还留在 UIA 树里（只是 IsOffscreen），
# 所以要找“真的在屏上、且有大小”的那个，否则第二次打开点到的是上一次的旧元素。
function Find-VisibleFirst {
    param($From, [string]$Name, $ControlType)
    foreach ($el in (Find-Elements -From $From -Name $Name)) {
        if ($null -ne $ControlType -and $el.Current.ControlType -ne $ControlType) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        return $el
    }
    return $null
}

function Click-Element {
    param($Session, $Element)
    $r = $Element.Current.BoundingRectangle
    [void][Native]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 300
    [void][Native]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 250
    [Native]::mouse_event([Native]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [Native]::mouse_event([Native]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
}

function Get-ToggleState {
    param($Element)
    return $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
}

function Toggle-Element {
    param($Element)
    $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 600
}

function Get-Rows {
    param($Session)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $names = @()
    foreach ($el in $Session.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $names += $el.Current.Name
    }
    return $names
}

function Get-DotRowCount {
    param($Session)
    return @((Get-Rows -Session $Session) | Where-Object { $_ -match '\.' }).Count
}

# 打开「配置 → 设置…」。
# 弹层不在主窗口的 UIA 子树里（见 AGENTS.md 第 6 节第 24 条），要从 AutomationElement.RootElement 往下找；
# 而且菜单有入场动画（开始那几帧坐标还在动），反复“点菜单栏→点菜单项”命中不稳定，
# 所以这里用 UIA 模式：MenuBarItem 的 ExpandCollapsePattern 打开菜单、菜单项的 InvokePattern 打开对话框。
# 对话框本身是一个独立的弹出窗口（顶层 Window，名字就是标题「设置」）。
function Find-DialogWindow {
    param($Session)
    foreach ($el in (Find-Elements -From $Session.Desktop -Name '设置')) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and -not $el.Current.IsOffscreen) { return $el }
    }
    return $null
}

function Open-SettingsDialog {
    param($Session)

    $menuBarItem = Find-VisibleFirst -From $Session.Root -Name '配置' -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    if ($null -eq $menuBarItem) { throw '主菜单里找不到「配置」' }

    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $settingsItem = $null
    for ($i = 0; $i -lt 24 -and $null -eq $settingsItem; $i++) {
        Start-Sleep -Milliseconds 250
        $settingsItem = Find-VisibleFirst -From $Session.Desktop -Name '设置' -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    }
    if ($null -eq $settingsItem) { throw '「配置」菜单里找不到「设置…」' }

    $settingsItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $dialogWindow = $null
    for ($i = 0; $i -lt 24 -and $null -eq $dialogWindow; $i++) {
        Start-Sleep -Milliseconds 250
        $dialogWindow = Find-DialogWindow -Session $Session
    }
    if ($null -eq $dialogWindow) { throw '设置对话框没有出现' }

    Start-Sleep -Milliseconds 500

    # 对话框内容超出可视区时，屏幕外的勾选项 IsOffscreen=true 但仍在 UIA 树里，
    # 所以这里按“是不是对话框窗口的后代”筛选，而不是按可见性
    $checkBoxes = @{}
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
    foreach ($el in $dialogWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($KeyMap.Contains($el.Current.Name)) { $checkBoxes[$KeyMap[$el.Current.Name]] = $el }
    }

    return [pscustomobject]@{
        Window     = $dialogWindow
        CheckBoxes = $checkBoxes
        Save       = Find-First -From $dialogWindow -Name '保存' -ControlType ([System.Windows.Automation.ControlType]::Button)
        Cancel     = Find-First -From $dialogWindow -Name '取消' -ControlType ([System.Windows.Automation.ControlType]::Button)
    }
}

function Test-DialogClosed {
    param($Session)
    return $null -eq (Find-DialogWindow -Session $Session)
}

function Invoke-CheckBoxToggle {
    param($Dialog, [string]$Key)
    if (-not $Dialog.CheckBoxes.ContainsKey($Key)) {
        Assert $false "对话框里找不到勾选项 $Key"
        return
    }

    Toggle-Element -Element $Dialog.CheckBoxes[$Key]
}

# ================================================================== 用例 1：内容齐全 + 状态与 settings.json 一致

Write-Host '--- 用例 1：对话框内容与初始状态 ---'
Set-Setting 'EnableListAnimations' $true
Set-Setting 'ShowExtensions' $true

$session = Start-Session
$dialog = Open-SettingsDialog -Session $session

Assert ($dialog.CheckBoxes.Count -eq $KeyMap.Count) "对话框里有 $($KeyMap.Count) 个配置项（实际 $($dialog.CheckBoxes.Count)）"
Assert ($null -ne $dialog.Save) '底部有「保存」按钮'
Assert ($null -ne $dialog.Cancel) '底部有「取消」按钮'

$states = @{}
foreach ($key in $KeyMap.Values) {
    $states[$key] = if ($dialog.CheckBoxes.ContainsKey($key)) { Get-ToggleState -Element $dialog.CheckBoxes[$key] } else { 'MISSING' }
    Write-Host ("  {0,-14} = {1}" -f $key, $states[$key])
}
Assert ($states['animations'] -eq 'On') '对话框读到的「过渡动画」与 settings.json（true）一致'
Assert ($states['extension'] -eq 'On') '对话框读到的「显示文件扩展名」与 settings.json（true）一致'

# ================================================================== 用例 2：取消不落盘

Write-Host '--- 用例 2：取消不落盘 ---'
Invoke-CheckBoxToggle -Dialog $dialog -Key 'animations'
Assert ((Get-ToggleState -Element $dialog.CheckBoxes['animations']) -eq 'Off') '勾选状态可以切换（改的是快照，不是设置本身）'

Click-Element -Session $session -Element $dialog.Cancel
Start-Sleep -Seconds 1
Assert (Test-DialogClosed -Session $session) '点「取消」后对话框关闭'
Assert ((Get-Setting 'EnableListAnimations') -eq $true) '点「取消」后 settings.json 没有被修改'
Stop-Session -Session $session

# ================================================================== 用例 3：保存立即落盘并生效

Write-Host '--- 用例 3：保存立即落盘并作用到文件列表 ---'
# 换到一个有真实文件的目录，才能看出「显示文件扩展名」的效果
Set-Setting 'PrimaryTabs' ([string[]]@($repoDir))
Set-Setting 'EnableListAnimations' $true
Set-Setting 'ShowExtensions' $true

$session = Start-Session
$rowsBefore = Get-Rows -Session $session
Assert (@($rowsBefore | Where-Object { $_ -match 'AGENTS' }).Count -gt 0) '会话已打开仓库目录（能看到 AGENTS 开头的行）'
$dotsBefore = Get-DotRowCount -Session $session

$dialog = Open-SettingsDialog -Session $session
Invoke-CheckBoxToggle -Dialog $dialog -Key 'extension'
Click-Element -Session $session -Element $dialog.Save
Start-Sleep -Seconds 1

Assert (Test-DialogClosed -Session $session) '点「保存」后对话框关闭'
Assert ((Get-Setting 'ShowExtensions') -eq $false) '点「保存」后立即落盘（ShowExtensions=false）'

Start-Sleep -Seconds 2
$dotsAfter = Get-DotRowCount -Session $session
Write-Host ("  行名里带扩展名的行数: {0} -> {1}" -f $dotsBefore, $dotsAfter)
Assert ($dotsAfter -lt $dotsBefore) '关掉「显示文件扩展名」后文件列表里的扩展名真的消失了'

# 改回来（这次也顺便验证「保存」能把设置改回去）
$dialog = Open-SettingsDialog -Session $session
Invoke-CheckBoxToggle -Dialog $dialog -Key 'extension'
Click-Element -Session $session -Element $dialog.Save
Start-Sleep -Seconds 2
Assert ((Get-Setting 'ShowExtensions') -eq $true) '再点一次「保存」把设置改回来'
Assert ((Get-DotRowCount -Session $session) -eq $dotsBefore) '重新打开扩展名后行名恢复原样'

# 双窗格：走的是另一条代码路径（要真的把第二个窗格建出来并应用设置）
$singlePaneRows = (Get-Rows -Session $session).Count
$dialog = Open-SettingsDialog -Session $session
Invoke-CheckBoxToggle -Dialog $dialog -Key 'dualPane'
Click-Element -Session $session -Element $dialog.Save
Start-Sleep -Seconds 3
Assert ((Get-Setting 'IsDualPane') -eq $true) '打开「双窗格模式」后落盘 IsDualPane=true'
Assert ((Get-Rows -Session $session).Count -ge ($singlePaneRows * 2)) '双窗格打开后列表行数明显变多（第二个窗格也开了同一个目录）'

$dialog = Open-SettingsDialog -Session $session
Assert ((Get-ToggleState -Element $dialog.CheckBoxes['dualPane']) -eq 'On') '重新打开对话框时读到的就是刚落盘的值'
Invoke-CheckBoxToggle -Dialog $dialog -Key 'dualPane'
Click-Element -Session $session -Element $dialog.Save
Start-Sleep -Seconds 3
Assert ((Get-Setting 'IsDualPane') -eq $false) '再关掉「双窗格模式」也立即落盘'
Assert ((Get-Rows -Session $session).Count -eq $singlePaneRows) '关掉双窗格后回到单个窗格'
Stop-Session -Session $session

# ------------------------------------------------------------------ 还原设置文件

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 settings.json'
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
