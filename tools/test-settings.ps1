# 设置对话框（「配置 → 设置…」）的 UIA 回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-settings.ps1
#   pwsh -NoProfile -File tools\test-settings.ps1 -Exe dist\win-x64\exdir.exe
#
# 四个用例（每次都真的启动 exdir、用真鼠标点菜单，再点对话框按钮）：
#   1. 对话框结构：左侧 3 个分类（文件列表 / 外观 / 布局）、默认停在「文件列表」，
#      右侧只有当前分类的开关（切分类真的换页），初始值与 settings.json 一致；
#   2. 改一项后点「取消」→ settings.json 不变；
#   3. 改一项后点「保存」→ 立即落盘，并且真的作用到文件列表（关掉扩展名后行名里的 ".xxx" 消失）；
#   4. 跨分类读取：在「布局」页改双窗格，重新打开对话框读到的是刚落盘的值。
#
# 说明：配置项现在是 SettingsToggleRow 里的 ToggleSwitch，UIA 里的类型是 Button（不是 CheckBox），
#       所以要靠 TogglePattern 认它；而且非当前分类的开关是 Visibility=Collapsed 的，
#       UIA 树里根本没有 —— 断言“某分类下能读到哪几个开关”本身就是“切分类有效”的验证。
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

# 配置项：UIA 名字（= SettingsToggleRow 的 Title）→ settings.json 里的字段名。
# 名字必须和 Views/SettingsDialog.xaml 里的 Title 一模一样。
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

# 左侧分类 → 该分类下应有的配置项（顺序即导航顺序）。必须和 SettingsViewModel 里的一致。
$CategoryMap = [ordered]@{
    '文件列表' = @('hidden', 'extension', 'foldersFirst')
    '外观'     = @('animations')
    '布局'     = @('columnAutoFit', 'toolbar', 'sidebar', 'dualPane')
}

$NameOfKey = @{}
foreach ($pair in $KeyMap.GetEnumerator()) { $NameOfKey[$pair.Value] = $pair.Key }

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

# 对话框里当前可见的开关：名字 → 元素。
# ToggleSwitch 在 UIA 里是 Button 类型（不是 CheckBox），认它的依据是支持 TogglePattern。
function Get-VisibleToggles {
    param($Dialog)
    $toggles = @{}
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($el in $Dialog.Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $pattern = $null
        if (-not $el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { continue }
        $toggles[$el.Current.Name] = $el
    }
    return $toggles
}

# 左侧导航点某个分类（用 SelectionItemPattern，不受弹层入场动画影响）
function Select-Category {
    param($Dialog, [string]$Name)
    $item = Find-VisibleFirst -From $Dialog.Window -Name $Name -ControlType ([System.Windows.Automation.ControlType]::ListItem)
    if ($null -eq $item) { throw "左侧导航里找不到分类「$Name」" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}

function Get-NavNames {
    param($Dialog)
    $names = @()
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($el in $Dialog.Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $names += $el.Current.Name
    }
    return $names
}

function Get-CurrentCategoryKey {
    param($Dialog)
    foreach ($name in $CategoryMap.Keys) {
        $item = Find-VisibleFirst -From $Dialog.Window -Name $name -ControlType ([System.Windows.Automation.ControlType]::ListItem)
        if ($null -eq $item) { continue }
        $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($pattern.Current.IsSelected) { return $name }
    }
    return $null
}

function Find-Toggle {
    param($Dialog, [string]$Key)
    return (Get-VisibleToggles -Dialog $Dialog)[$NameOfKey[$Key]]
}

# 切到某个分类，读该分类下某个开关的状态
function Get-ToggleStateByKey {
    param($Dialog, [string]$Category, [string]$Key)
    Select-Category -Dialog $Dialog -Name $Category
    $el = Find-Toggle -Dialog $Dialog -Key $Key
    if ($null -eq $el) { return 'MISSING' }
    return Get-ToggleState -Element $el
}

# 切到某个分类，拨一下该分类下的某个开关
function Invoke-ToggleByKey {
    param($Dialog, [string]$Category, [string]$Key)
    Select-Category -Dialog $Dialog -Name $Category
    $el = Find-Toggle -Dialog $Dialog -Key $Key
    if ($null -eq $el) { Assert $false "分类「$Category」下找不到开关 $($NameOfKey[$Key])"; return }
    Toggle-Element -Element $el
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

    return [pscustomobject]@{
        Window = $dialogWindow
        Save   = Find-First -From $dialogWindow -Name '保存' -ControlType ([System.Windows.Automation.ControlType]::Button)
        Cancel = Find-First -From $dialogWindow -Name '取消' -ControlType ([System.Windows.Automation.ControlType]::Button)
    }
}

function Test-DialogClosed {
    param($Session)
    return $null -eq (Find-DialogWindow -Session $Session)
}

# 取行名里带扩展名的行数（用来验证「显示文件扩展名」真的作用到了列表）
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

# ================================================================== 用例 1：结构 + 初始状态

Write-Host '--- 用例 1：对话框结构（左导航 / 右正文）与初始状态 ---'
# 除了两个用来验证“初始值一致”的项，双窗格也要先归零：
# 用例 4 会按“现在单窗格 → 拨开 → 断言双窗格”推，而 settings.json 里上次退出时的值是不确定的。
Set-Setting 'EnableListAnimations' $true
Set-Setting 'ShowExtensions' $true
Set-Setting 'IsDualPane' $false

$session = Start-Session
$dialog = Open-SettingsDialog -Session $session

$navNames = Get-NavNames -Dialog $dialog
Write-Host ("  左侧导航: {0}" -f ($navNames -join ' / '))
Assert ($navNames.Count -eq $CategoryMap.Count) "左侧有 $($CategoryMap.Count) 个分类（实际 $($navNames.Count)）"
Assert (($navNames -join '/') -eq (($CategoryMap.Keys) -join '/')) '左侧分类就是「文件列表 / 外观 / 布局」且顺序一致'
Assert ((Get-CurrentCategoryKey -Dialog $dialog) -eq '文件列表') '默认选中的是第一个分类「文件列表」'
Assert ($null -ne $dialog.Save) '底部有「保存」按钮'
Assert ($null -ne $dialog.Cancel) '底部有「取消」按钮'

# 每个分类下只应能看到该分类自己的开关（其余分类是 Collapsed，UIA 里读不到）
foreach ($category in $CategoryMap.Keys) {
    Select-Category -Dialog $dialog -Name $category
    $toggles = Get-VisibleToggles -Dialog $dialog
    $expected = $CategoryMap[$category]
    $foundKeys = @($toggles.Keys | ForEach-Object { $KeyMap[$_] } | Where-Object { $_ })
    Assert ($toggles.Count -eq $expected.Count) "「$category」页显示 $($expected.Count) 个开关（实际 $($toggles.Count)）"
    Assert (((($foundKeys | Sort-Object) -join ',') -eq (($expected | Sort-Object) -join ','))) "「$category」页的开关正是: $($expected -join ' / ')"
}

# 初始值应与 settings.json 一致
$states = [ordered]@{}
foreach ($pair in $CategoryMap.GetEnumerator()) {
    $category = $pair.Key
    foreach ($key in $pair.Value) {
        $states[$key] = Get-ToggleStateByKey -Dialog $dialog -Category $category -Key $key
    }
}
foreach ($key in $KeyMap.Values) { Write-Host ("  {0,-14} = {1}" -f $key, $states[$key]) }
Assert ($states['animations'] -eq 'On') '对话框读到的「过渡动画」与 settings.json（true）一致'
Assert ($states['extension'] -eq 'On') '对话框读到的「显示文件扩展名」与 settings.json（true）一致'

# ================================================================== 用例 2：取消不落盘

Write-Host '--- 用例 2：取消不落盘 ---'
Invoke-ToggleByKey -Dialog $dialog -Category '外观' -Key 'animations'
Assert ((Get-ToggleStateByKey -Dialog $dialog -Category '外观' -Key 'animations') -eq 'Off') '勾选状态可以切换（改的是快照，不是设置本身）'

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
Invoke-ToggleByKey -Dialog $dialog -Category '文件列表' -Key 'extension'
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
Invoke-ToggleByKey -Dialog $dialog -Category '文件列表' -Key 'extension'
Click-Element -Session $session -Element $dialog.Save
Start-Sleep -Seconds 2
Assert ((Get-Setting 'ShowExtensions') -eq $true) '再点一次「保存」把设置改回来'
Assert ((Get-DotRowCount -Session $session) -eq $dotsBefore) '重新打开扩展名后行名恢复原样'

# ================================================================== 用例 4：跨分类改「布局」并读回

Write-Host '--- 用例 4：在「布局」页改设置、保存、重新打开读回 ---'
$singlePaneRows = (Get-Rows -Session $session).Count
$dialog = Open-SettingsDialog -Session $session
Invoke-ToggleByKey -Dialog $dialog -Category '布局' -Key 'dualPane'
Click-Element -Session $session -Element $dialog.Save
Start-Sleep -Seconds 3
Assert ((Get-Setting 'IsDualPane') -eq $true) '打开「双窗格模式」后落盘 IsDualPane=true'
Assert ((Get-Rows -Session $session).Count -ge ($singlePaneRows * 2)) '双窗格打开后列表行数明显变多（第二个窗格也开了同一个目录）'
$dialog = Open-SettingsDialog -Session $session
Assert ((Get-CurrentCategoryKey -Dialog $dialog) -eq '文件列表') '重新打开对话框时仍默认停在「文件列表」'
Assert ((Get-ToggleStateByKey -Dialog $dialog -Category '布局' -Key 'dualPane') -eq 'On') '切到「布局」页读到的就是刚落盘的值'
Invoke-ToggleByKey -Dialog $dialog -Category '布局' -Key 'dualPane'
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
