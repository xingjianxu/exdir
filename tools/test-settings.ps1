# 设置窗口（「配置 → 设置…」）的 UIA 回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-settings.ps1
#   pwsh -NoProfile -File tools\test-settings.ps1 -Exe dist\win-x64\exdir.exe
#
# 九个用例：
#   1. 窗口结构：左侧 5 个分类（文件列表 / 外观 / 布局 / 侧边栏 / 右键菜单）、默认停在「文件列表」，
#      右侧只有当前分类的项（切分类真的换页），初始值与 settings.json 一致；
#   2. 即时生效：拨一下开关，settings.json 立刻变（没有「保存 / 取消」按钮）；
#   3. 生效到界面：关掉「显示文件扩展名」，文件列表行名里的 ".xxx" 立刻消失；
#   4. 跨分类 + 关窗重开：在「布局」页打开双窗格 → 主窗口真变双窗格；重新打开设置窗口仍是新值；
#   5. 「右键菜单」页：列出系统右键菜单项（默认全开），菜单风格开关默认开（内置），
#      关掉「属性」后立即落盘 verb:properties，重新打开窗口时它仍是关的，再拨回来就清空；
#   6. 「文件列表」页的「行高」滑块：初值与 settings.json 一致、切到别的分类就读不到，
#      拖动后立即落盘，并且文件列表的数据行**真的**变高（UIA 量 ListItem 的高度，取中位数）；
#   7. 「侧边栏」页的分组开关：关掉「云存储」分组后侧边栏树里真的读不到它（其它分组不受影响），
#      再拨回来又回来；「主目录」里默认只显示「桌面」与「下载」，
#      打开「显示「文档」」后文档真的出现在树里，关掉「显示「桌面」」后桌面真的消失（用完还原默认）；
#   8. 「外观」页的「标签页使用直角」（默认开）：拨一下就立即落盘并当场应用
#      （exdir.log 里记下「标签页=圆角 / 直角」），关窗重开仍是新值；
#   9. 「外观」页的「主题」下拉框（跟随系统 / 浅色 / 深色）：初值一致；选「深色」/「浅色」
#      立即落盘并当场应用（exdir.log：主题已应用：…）；标题栏那个太阳 / 月亮开关改的是同一个设置
#      （拨一下就固定成显式的浅 / 深，设置窗口里的下拉框跟着同步）。
#
# 说明：开关类配置项是社区工具包 SettingsCard 里的 ToggleSwitch（Windows 11 设置的那种卡片行），
#       UIA 里的类型是 Button（不是 CheckBox），所以要靠 TogglePattern 认它；
#       「行高」是 Slider，靠 RangeValuePattern 读写；「主题」是 ComboBox，靠
#       ExpandCollapse + SelectionItem 选、Selection 读（它没有 TogglePattern，不干扰上面那套计数）。
#       非当前分类的页是 Collapsed 的，UIA 树里根本没有 ——
#       “某分类下能读到哪几个项”本身就是“切分类有效”的验证。
#
# 全程用 UIA 模式（Invoke / Toggle / SelectionItem / RangeValue / Window.Close）驱动，
# 不模拟鼠标：设置窗口是普通窗口，不需要前台焦点，脚本在任何会话里都能跑。
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
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint WM_CLOSE = 0x0010;
}
'@

[void][Native]::SetProcessDpiAwarenessContext([IntPtr][Native]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$repoDir = [System.IO.Path]::GetFullPath("$PSScriptRoot\..")
$settingsPath = Join-Path $env:LOCALAPPDATA 'exdir\settings.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

# 配置项：UIA 名字（= SettingsCard 的 Header，也是里面 ToggleSwitch 的 AutomationProperties.Name）
# → settings.json 里的字段名。名字必须和 Views/SettingsView.xaml 里的 Header 一模一样。
$KeyMap = [ordered]@{
    '显示隐藏文件'           = 'hidden'
    '显示文件扩展名'         = 'extension'
    '文件夹排在文件前面'     = 'foldersFirst'
    '标签页使用直角'         = 'squareTabCorners'
    '过渡动画'               = 'animations'
    '列宽自动适应窗格宽度'   = 'columnAutoFit'
    '显示工具条'             = 'toolbar'
    '显示侧边栏'             = 'sidebar'
    '双窗格模式'             = 'dualPane'
    '显示「主目录」分组'     = 'sidebarHome'
    '显示「收藏夹」分组'     = 'sidebarFavorites'
    '显示「云存储」分组'     = 'sidebarCloud'
    '显示「此电脑」分组'     = 'sidebarComputer'
    '显示「桌面」'           = 'sidebarHomeDesktop'
    '显示「文档」'           = 'sidebarHomeDocuments'
    '显示「下载」'           = 'sidebarHomeDownloads'
    '显示「图片」'           = 'sidebarHomePictures'
    '显示「音乐」'           = 'sidebarHomeMusic'
    '显示「视频」'           = 'sidebarHomeVideos'
    '使用内置的轻量右键菜单' = 'builtInContextMenu'
}

# 左侧分类 → 该分类下应有的开关（顺序即导航顺序）。必须和 SettingsViewModel 里的一致。
# 「右键菜单」页是动态清单（系统里装了什么就有什么），所以这里给 $null，单独在用例 5 里断言。
# 注意：这里只列 ToggleSwitch（开关）类的项；「文件列表」页里的「行高」是滑块，
#       它不是开关、也不参与“这一页有几个开关”的计数，单独在用例 6 里断言。
$CategoryMap = [ordered]@{
    '文件列表' = @('hidden', 'extension', 'foldersFirst')
    '外观'     = @('squareTabCorners', 'animations')
    '布局'     = @('columnAutoFit', 'toolbar', 'sidebar', 'dualPane')
    '侧边栏'   = @('sidebarHome', 'sidebarFavorites', 'sidebarCloud', 'sidebarComputer',
                    'sidebarHomeDesktop', 'sidebarHomeDocuments', 'sidebarHomeDownloads',
                    'sidebarHomePictures', 'sidebarHomeMusic', 'sidebarHomeVideos')
    '右键菜单' = $null
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

    # 老版本的 settings.json 里可能还没有这个字段（例如刚加的 ShellMenuDisabledItems），
    # 直接 $json.$Name = $Value 会报“找不到属性”，所以用 Add-Member -Force
    $json | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content $script:settingsPath -Encoding utf8
}

# ------------------------------------------------------------------ 会话与设置窗口

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
        Scale   = [Native]::GetDpiForWindow($handle) / 96.0
    }
}

function Stop-Session {
    param($Session)
    # exdir 关窗口只是隐藏到托盘（隐藏时已统一落盘），收尾直接 Kill
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

# 弹层（菜单/窗口）关掉之后它的元素可能还留在 UIA 树里（只是 IsOffscreen），
# 所以找“真的在、且有大小”的那个，否则第二次打开会拿到上一次的旧元素。
function Find-VisibleFirst {
    param($From, [string]$Name, $ControlType, [int]$ProcessId = 0)
    foreach ($el in (Find-Elements -From $From -Name $Name)) {
        if ($null -ne $ControlType -and $el.Current.ControlType -ne $ControlType) { continue }
        if ($ProcessId -ne 0 -and $el.Current.ProcessId -ne $ProcessId) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        return $el
    }
    return $null
}

function Get-ToggleState {
    param($Element)
    return $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
}

function Toggle-Element {
    param($Element)
    if ($null -eq $Element) { throw '要拨的开关不在当前页上' }
    $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 800
}

# ------------------------------------------------------------------ 行高（滑块）

# 滑块在 UIA 里是 Slider 类型，支持 RangeValuePattern（读写数值）；
# 左边的标题 TextBlock 的 UIA 名字也是“行高”，所以必须按 ControlType 过滤。
function Find-Slider {
    param($Settings, [string]$Name)
    return Find-VisibleFirst -From $Settings.Window -Name $Name -ControlType ([System.Windows.Automation.ControlType]::Slider)
}

function Get-SliderValue {
    param($Element)
    return $Element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
}

function Set-SliderValue {
    param($Element, [double]$Value)
    $Element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($Value)
    Start-Sleep -Milliseconds 900
}

# ------------------------------------------------------------------ 主题（下拉框）

# 「外观」页的「主题」下拉框（UIA 类型 ComboBox）。左边的标题 TextBlock 也叫“主题”，
# 所以要按 ControlType 过滤。
function Find-ThemeCombo {
    param($Settings)
    return Find-VisibleFirst -From $Settings.Window -Name '主题' -ControlType ([System.Windows.Automation.ControlType]::ComboBox)
}

# 下拉框当前选中的那一项的文字（跟随系统 / 浅色 / 深色）
function Get-ThemeComboText {
    param($Combo)
    if ($null -eq $Combo) { return 'MISSING' }
    $selection = $Combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -eq 0) { return '' }
    return $selection[0].Current.Name
}

# 选下拉框里的某一项：先展开，再从桌面（弹出层不在主窗口的 UIA 子树里，见 AGENTS.md 第 6 节第 24 条）
# 按进程号找那个 ListItem，最后用 SelectionItemPattern.Select()。
function Select-ThemeComboItem {
    param($Settings, [string]$Name)

    $combo = Find-ThemeCombo -Settings $Settings
    if ($null -eq $combo) { Assert $false '「外观」页里找不到「主题」下拉框'; return }

    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $item = $null
    for ($i = 0; $i -lt 24 -and $null -eq $item; $i++) {
        Start-Sleep -Milliseconds 250
        $item = Find-VisibleFirst -From $Settings.Session.Desktop -Name $Name `
            -ControlType ([System.Windows.Automation.ControlType]::ListItem) -ProcessId $Settings.Session.Proc.Id
    }
    if ($null -eq $item) { Assert $false "主题下拉框里找不到「$Name」"; return }

    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
}

# 文件列表数据行的实际高度（物理像素）。只取名字里带 "." 的行：
# 侧边栏树节点在 UIA 里也是 ListItem，但它们没有扩展名，这样能排除掉。
function Get-RowHeights {
    param($Session)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $heights = @()
    foreach ($el in $Session.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($el.Current.IsOffscreen) { continue }
        if ($el.Current.Name -notmatch '\.') { continue }
        $r = $el.Current.BoundingRectangle
        if ($r.Height -gt 0) { $heights += $r.Height }
    }
    return $heights
}

# 一行到底多高：取**中位数**，不要取平均值。
# 视口底部那一行通常只露出一截，UIA 报的是被裁过的矩形（实测 40 DIP 行高时它只剩 48 物理像素），
# 平均值会被它拉低几个像素，恰好落在容差边上就会偶发 FAIL；中位数对“少数被裁的行”免疫。
function Get-TypicalRowHeight {
    param($Session)
    $heights = @(Get-RowHeights -Session $Session)
    if ($heights.Count -eq 0) { return 0 }

    $sorted = @($heights | Sort-Object)
    return $sorted[[int][Math]::Floor($sorted.Count / 2)]
}

# 设置窗口里当前可见的开关：名字 → 元素。
# ToggleSwitch 在 UIA 里是 Button 类型（不是 CheckBox），认它的依据是支持 TogglePattern；
# 外面的 SettingsCard 也是 ButtonBase，但它没有 TogglePattern，会被排除掉。
function Get-VisibleToggles {
    param($Settings)
    $toggles = @{}
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($el in $Settings.Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $pattern = $null
        if (-not $el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { continue }
        $toggles[$el.Current.Name] = $el
    }
    return $toggles
}

# 左侧导航点某个分类（NavigationViewItem 在 UIA 里是 ListItem，支持 SelectionItemPattern）
function Select-Category {
    param($Settings, [string]$Name)
    $item = Find-VisibleFirst -From $Settings.Window -Name $Name -ControlType ([System.Windows.Automation.ControlType]::ListItem)
    if ($null -eq $item) { throw "左侧导航里找不到分类「$Name」" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800
}

function Get-NavNames {
    param($Settings)
    $names = @()
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($el in $Settings.Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $names += $el.Current.Name
    }
    return $names
}

function Get-CurrentCategoryKey {
    param($Settings)
    foreach ($name in $CategoryMap.Keys) {
        if ($name -eq '右键菜单') { continue }
        $item = Find-VisibleFirst -From $Settings.Window -Name $name -ControlType ([System.Windows.Automation.ControlType]::ListItem)
        if ($null -eq $item) { continue }
        $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($pattern.Current.IsSelected) { return $name }
    }
    return $null
}

function Find-Toggle {
    param($Settings, [string]$Key)
    return (Get-VisibleToggles -Settings $Settings)[$NameOfKey[$Key]]
}

# 切到某个分类，读该分类下某个开关的状态
function Get-ToggleStateByKey {
    param($Settings, [string]$Category, [string]$Key)
    Select-Category -Settings $Settings -Name $Category
    $el = Find-Toggle -Settings $Settings -Key $Key
    if ($null -eq $el) { return 'MISSING' }
    return Get-ToggleState -Element $el
}

# 切到某个分类，拨一下该分类下的某个开关
function Invoke-ToggleByKey {
    param($Settings, [string]$Category, [string]$Key)
    Select-Category -Settings $Settings -Name $Category
    $el = Find-Toggle -Settings $Settings -Key $Key
    if ($null -eq $el) { Assert $false "分类「$Category」下找不到开关 $($NameOfKey[$Key])"; return }
    Toggle-Element -Element $el
}

# 「右键菜单」页的清单要现枚举系统菜单，慢的话要等一会儿；等不到就先返回已经有的
function Wait-ShellToggles {
    param($Settings)
    $toggles = @{}
    for ($i = 0; $i -lt 30; $i++) {
        $toggles = Get-VisibleToggles -Settings $Settings
        if ($toggles.Count -ge 10) { return $toggles }
        Start-Sleep -Milliseconds 500
    }
    return $toggles
}

# 打开「配置 → 设置…」。
# 菜单不在主窗口的 UIA 子树里（见 AGENTS.md 第 6 节第 24 条），要从 AutomationElement.RootElement 往下找；
# 菜单有入场动画（开始那几帧坐标还在动），所以用 UIA 模式：MenuBarItem 的 ExpandCollapsePattern
# 打开菜单、菜单项的 InvokePattern 打开窗口。设置窗口是顶层 Window，标题「设置」。
function Find-SettingsWindow {
    param($Session)
    foreach ($el in (Find-Elements -From $Session.Desktop -Name '设置')) {
        if ($el.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
        # 必须限定在当前进程：桌面上别的程序（例如 ApplicationFrameHost 里的 UWP 设置页）
        # 也可能有一个叫“设置”的顶层窗口，不过滤就会把它的坐标/按钮当成我们的
        if ($el.Current.ProcessId -ne $Session.Proc.Id) { continue }
        if ($el.Current.IsOffscreen) { continue }
        return $el
    }
    return $null
}

# 点「配置 → 设置…」菜单项（菜单有入场动画，用 UIA 模式找，见 AGENTS.md 第 6 节第 26 条）
function Invoke-SettingsMenuItem {
    param($Session)

    $menuBarItem = Find-VisibleFirst -From $Session.Root -Name '配置' -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    if ($null -eq $menuBarItem) { throw '主菜单里找不到「配置」' }

    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $settingsItem = $null
    for ($i = 0; $i -lt 24 -and $null -eq $settingsItem; $i++) {
        Start-Sleep -Milliseconds 250
        $settingsItem = Find-VisibleFirst -From $Session.Desktop -Name '设置' -ControlType ([System.Windows.Automation.ControlType]::MenuItem) -ProcessId $Session.Proc.Id
    }
    if ($null -eq $settingsItem) { throw '「配置」菜单里找不到「设置…」' }

    $settingsItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Open-Settings {
    param($Session)

    Invoke-SettingsMenuItem -Session $Session

    $window = $null
    for ($i = 0; $i -lt 30 -and $null -eq $window; $i++) {
        Start-Sleep -Milliseconds 250
        $window = Find-SettingsWindow -Session $Session
    }
    if ($null -eq $window) { throw '设置窗口没有出现' }

    Start-Sleep -Seconds 1

    return [pscustomobject]@{ Window = $window; Session = $Session }
}

# 本进程当前开着几个设置窗口（用来验证“同一时刻只开一个”）
function Get-SettingsWindowCount {
    param($Session)
    $count = 0
    foreach ($el in (Find-Elements -From $Session.Desktop -Name '设置')) {
        if ($el.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
        if ($el.Current.ProcessId -ne $Session.Proc.Id) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $count++
    }
    return $count
}

# 关设置窗口：先试 UIA 的 WindowPattern.Close，不行再用 WM_CLOSE 兜底，
# 最后一定要等到它在 UIA 树里消失（元素不是马上就没的，直接断言会偶发 FAIL）。
function Close-Settings {
    param($Settings)
    $session = $Settings.Session

    try { $Settings.Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        if ($null -eq (Find-SettingsWindow -Session $session)) { return }
    }

    try { [void][Native]::PostMessage([IntPtr]$Settings.Window.Current.NativeWindowHandle, [Native]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) } catch { }
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        if ($null -eq (Find-SettingsWindow -Session $session)) { return }
    }
}

function Test-SettingsClosed {
    param($Session)
    return $null -eq (Find-SettingsWindow -Session $Session)
}

# 取所有行的名字（用来验证「显示文件扩展名」真的作用到了列表）
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

# 侧边栏树里某个分组节点是否真的显示（侧边栏节点的 UIA 类型是 TreeItem）。
# 只认“在、且有大小的”元素：树控件把滚出视口的行也留在 UIA 树里（IsOffscreen=true），
# 被隐藏的分组则完全不在树里。
function Test-SidebarGroupVisible {
    param($Session, [string]$Name)
    foreach ($el in (Find-Elements -From $Session.Root -Name $Name)) {
        if ($el.Current.ControlType -ne [System.Windows.Automation.ControlType]::TreeItem) { continue }
        if ($el.Current.IsOffscreen) { continue }
        $r = $el.Current.BoundingRectangle
        if ($r.Width -gt 0 -and $r.Height -gt 0) { return $true }
    }
    return $false
}

# exdir.log 末尾几行里有没有出现过某个片段。
# 界面上的“标签页直角 / 圆角”没有 UIA 属性可读（四角不暴露在自动化树里），
# 所以“拨一下真的应用了”只能靠 ApplySettings 写下的那行日志来断言
# （视觉上真的变直角 / 圆角由 tools/capture.ps1 的截图人工确认，见 AGENTS.md）。
function Test-LogContains {
    param([string]$Pattern, [int]$Tail = 80)
    if (-not (Test-Path $script:logPath)) { return $false }
    return @((Get-Content $script:logPath -Tail $Tail) | Where-Object { $_ -like "*$Pattern*" }).Count -gt 0
}

# ================================================================== 用例 1：结构 + 初始状态

Write-Host '--- 用例 1：设置窗口结构（左导航 / 右卡片）与初始状态 ---'
# 除了两个用来验证“初始值一致”的项，双窗格也要先归零：
# 用例 4 会按“现在单窗格 → 拨开 → 断言双窗格”推，而 settings.json 里上次退出时的值是不确定的。
Set-Setting 'EnableListAnimations' $true
Set-Setting 'ShowExtensions' $true
Set-Setting 'IsDualPane' $false
# 行高也要先归位：用例 6 要断言“滑块初值 = settings.json”，历史值（或被手改过）会让它不可控
Set-Setting 'RowHeight' 28
# 标签页默认是直角：用例 8 要断言“拨一下就变圆角”，历史值同样会让它不可控
Set-Setting 'SquareTabCorners' $true
# 主题默认是「跟随系统」：用例 9 要断言“初值 = 跟随系统”，历史值同样会让它不可控
Set-Setting 'Theme' 0
# 侧边栏四个分组默认全部显示：不先归位的话，用例 1 的“初始值一致”与用例 7 都会被历史值干扰
Set-Setting 'SidebarShowHome' $true
Set-Setting 'SidebarShowFavorites' $true
Set-Setting 'SidebarShowCloud' $true
Set-Setting 'SidebarShowComputer' $true
# 「主目录」里默认只显示桌面与下载：不归位的话用例 7 的“默认只开两个”会被历史值干扰
Set-Setting 'SidebarHomeDesktop' $true
Set-Setting 'SidebarHomeDocuments' $false
Set-Setting 'SidebarHomeDownloads' $true
Set-Setting 'SidebarHomePictures' $false
Set-Setting 'SidebarHomeMusic' $false
Set-Setting 'SidebarHomeVideos' $false
# 右键菜单默认全部开启，且默认用内置菜单：不先归位的话，用例 5 的断言会被历史值干扰
Set-Setting 'ShellMenuDisabledItems' ([string[]]@())
Set-Setting 'UseBuiltInContextMenu' $true

$session = Start-Session
$settings = Open-Settings -Session $session

$navNames = Get-NavNames -Settings $settings
Write-Host ("  左侧导航: {0}" -f ($navNames -join ' / '))
Assert ($navNames.Count -eq $CategoryMap.Count) "左侧有 $($CategoryMap.Count) 个分类（实际 $($navNames.Count)）"
Assert (($navNames -join '/') -eq (($CategoryMap.Keys) -join '/')) '左侧分类就是「文件列表 / 外观 / 布局 / 侧边栏 / 右键菜单」且顺序一致'
Assert ((Get-CurrentCategoryKey -Settings $settings) -eq '文件列表') '默认选中的是第一个分类「文件列表」'
Assert ($null -eq (Find-First -From $settings.Window -Name '保存' -ControlType ([System.Windows.Automation.ControlType]::Button))) '设置窗口里没有「保存」按钮（改了就生效）'
Assert ($null -eq (Find-First -From $settings.Window -Name '取消' -ControlType ([System.Windows.Automation.ControlType]::Button))) '设置窗口里没有「取消」按钮'

# 同一时刻只开一个：再点一次菜单项应该只是把已有窗口提到前台
Invoke-SettingsMenuItem -Session $session
Start-Sleep -Seconds 2
Assert ((Get-SettingsWindowCount -Session $session) -eq 1) '再点一次「配置 → 设置…」不会开出第二个设置窗口'

# 每个分类下只应能看到该分类自己的开关（其余分类是 Collapsed，UIA 里读不到）
foreach ($category in $CategoryMap.Keys) {
    $expected = $CategoryMap[$category]
    if ($null -eq $expected) { continue }   # 「右键菜单」是动态清单，见用例 5

    Select-Category -Settings $settings -Name $category
    $toggles = Get-VisibleToggles -Settings $settings
    $foundKeys = @($toggles.Keys | ForEach-Object { $KeyMap[$_] } | Where-Object { $_ })
    Assert ($toggles.Count -eq $expected.Count) "「$category」页显示 $($expected.Count) 个开关（实际 $($toggles.Count)）"
    Assert (((($foundKeys | Sort-Object) -join ',') -eq (($expected | Sort-Object) -join ','))) "「$category」页的开关正是: $($expected -join ' / ')"
}

# 初始值应与 settings.json 一致
$states = [ordered]@{}
foreach ($pair in $CategoryMap.GetEnumerator()) {
    $category = $pair.Key
    if ($null -eq $pair.Value) { continue }   # 「右键菜单」的动态清单不在这一步断言
    foreach ($key in $pair.Value) {
        $states[$key] = Get-ToggleStateByKey -Settings $settings -Category $category -Key $key
    }
}
foreach ($key in $KeyMap.Values) { Write-Host ("  {0,-14} = {1}" -f $key, $states[$key]) }
Assert ($states['animations'] -eq 'On') '设置窗口读到的「过渡动画」与 settings.json（true）一致'
Assert ($states['extension'] -eq 'On') '设置窗口读到的「显示文件扩展名」与 settings.json（true）一致'

# 非开关类的项单独认：『文件列表』页里的「行高」滑块
$rowHeightSetting = [double](Get-Setting 'RowHeight')
Select-Category -Settings $settings -Name '文件列表'
$slider = Find-Slider -Settings $settings -Name '行高'
Assert ($null -ne $slider) '「文件列表」页里有「行高」滑块'
if ($null -ne $slider) {
    $sliderValue = Get-SliderValue -Element $slider
    Write-Host ("  行高滑块 = {0}（settings.json = {1}）" -f $sliderValue, $rowHeightSetting)
    Assert ($sliderValue -eq $rowHeightSetting) '行高滑块的初值与 settings.json 一致'
}

# ================================================================== 用例 2：即时生效（没有保存按钮）

Write-Host '--- 用例 2：拨一下开关就立刻落盘（没有「保存 / 取消」） ---'
Invoke-ToggleByKey -Settings $settings -Category '外观' -Key 'animations'
Assert ((Get-Setting 'EnableListAnimations') -eq $false) '拨一下「过渡动画」后 settings.json 立刻变成 false'
Assert ((Get-ToggleStateByKey -Settings $settings -Category '外观' -Key 'animations') -eq 'Off') '窗口里的开关状态也变了'
Invoke-ToggleByKey -Settings $settings -Category '外观' -Key 'animations'
Assert ((Get-Setting 'EnableListAnimations') -eq $true) '再拨回来又立刻落盘（true）'

Close-Settings -Settings $settings
Assert (Test-SettingsClosed -Session $session) '关掉设置窗口'
Assert ((Get-Setting 'EnableListAnimations') -eq $true) '关窗不需要“保存”，改动已经落盘了'
Stop-Session -Session $session

# ================================================================== 用例 3：即时作用到文件列表

Write-Host '--- 用例 3：改设置立刻作用到文件列表 ---'
# 换到一个有真实文件的目录，才能看出「显示文件扩展名」的效果
Set-Setting 'PrimaryTabs' ([string[]]@($repoDir))
Set-Setting 'EnableListAnimations' $true
Set-Setting 'ShowExtensions' $true

$session = Start-Session
$rowsBefore = Get-Rows -Session $session
Assert (@($rowsBefore | Where-Object { $_ -match 'AGENTS' }).Count -gt 0) '会话已打开仓库目录（能看到 AGENTS 开头的行）'
$dotsBefore = Get-DotRowCount -Session $session

$settings = Open-Settings -Session $session
Invoke-ToggleByKey -Settings $settings -Category '文件列表' -Key 'extension'
Assert ((Get-Setting 'ShowExtensions') -eq $false) '拨一下「显示文件扩展名」就立即落盘（ShowExtensions=false）'

Start-Sleep -Seconds 2
$dotsAfter = Get-DotRowCount -Session $session
Write-Host ("  行名里带扩展名的行数: {0} -> {1}" -f $dotsBefore, $dotsAfter)
Assert ($dotsAfter -lt $dotsBefore) '关掉「显示文件扩展名」后文件列表里的扩展名真的消失了'

Invoke-ToggleByKey -Settings $settings -Category '文件列表' -Key 'extension'
Start-Sleep -Seconds 2
Assert ((Get-Setting 'ShowExtensions') -eq $true) '再拨回来立即落盘'
Assert ((Get-DotRowCount -Session $session) -eq $dotsBefore) '重新打开扩展名后行名恢复原样'

# ================================================================== 用例 4：跨分类改「布局」、关窗重开读回

Write-Host '--- 用例 4：在「布局」页改设置、关窗重开读回 ---'
$singlePaneRows = (Get-Rows -Session $session).Count
Invoke-ToggleByKey -Settings $settings -Category '布局' -Key 'dualPane'
Start-Sleep -Seconds 3
Assert ((Get-Setting 'IsDualPane') -eq $true) '打开「双窗格模式」后立即落盘 IsDualPane=true'
Assert ((Get-Rows -Session $session).Count -ge ($singlePaneRows * 2)) '双窗格打开后列表行数明显变多（第二个窗格也开了同一个目录）'

Close-Settings -Settings $settings
$settings = Open-Settings -Session $session
Assert ((Get-CurrentCategoryKey -Settings $settings) -eq '文件列表') '重新打开设置窗口时仍默认停在「文件列表」'
Assert ((Get-ToggleStateByKey -Settings $settings -Category '布局' -Key 'dualPane') -eq 'On') '切到「布局」页读到的就是刚落盘的值'
Invoke-ToggleByKey -Settings $settings -Category '布局' -Key 'dualPane'
Start-Sleep -Seconds 3
Assert ((Get-Setting 'IsDualPane') -eq $false) '再关掉「双窗格模式」也立即落盘'
Assert ((Get-Rows -Session $session).Count -eq $singlePaneRows) '关掉双窗格后回到单个窗格'

# ================================================================== 用例 5：「右键菜单」页

Write-Host '--- 用例 5：「右键菜单」页 —— 系统菜单项清单 + 关掉某项 ---'
Select-Category -Settings $settings -Name '右键菜单'
$shellToggles = Wait-ShellToggles -Settings $settings
Write-Host ("  系统菜单项 {0} 个，前几个：{1}" -f $shellToggles.Count, (($shellToggles.Keys | Select-Object -First 8) -join ' / '))
Assert ($shellToggles.Count -ge 10) "「右键菜单」页列出了系统菜单项（实际 $($shellToggles.Count) 个）"
Assert ($shellToggles.ContainsKey('使用内置的轻量右键菜单')) '「右键菜单」页有菜单风格开关（内置 / 系统）'
if ($shellToggles.ContainsKey('使用内置的轻量右键菜单')) {
    Assert ((Get-ToggleState -Element $shellToggles['使用内置的轻量右键菜单']) -eq 'On') '默认使用内置的轻量右键菜单（On）'
    Toggle-Element -Element $shellToggles['使用内置的轻量右键菜单']
    Assert ((Get-Setting 'UseBuiltInContextMenu') -eq $false) '关掉风格开关后立即落盘（UseBuiltInContextMenu=false，回到系统菜单）'
    Toggle-Element -Element $shellToggles['使用内置的轻量右键菜单']
    Assert ((Get-Setting 'UseBuiltInContextMenu') -eq $true) '再拨回来又立即落盘（UseBuiltInContextMenu=true）'
}
Assert ($shellToggles.ContainsKey('打开')) '清单里有「打开」'
Assert ($shellToggles.ContainsKey('属性')) '清单里有「属性」'
Assert ((Get-ToggleState -Element $shellToggles['打开']) -eq 'On') '系统菜单项默认全部开启（「打开」= On）'

Toggle-Element -Element $shellToggles['属性']
Start-Sleep -Seconds 1

Assert ((@(Get-Setting 'ShellMenuDisabledItems') -contains 'verb:properties')) '关掉「属性」后立即落盘（ShellMenuDisabledItems 里有 verb:properties）'
$known = @(Get-Setting 'ShellMenuKnownItems')
Write-Host ("  ShellMenuKnownItems 落了 {0} 项" -f $known.Count)
Assert ($known.Count -ge 10) '菜单项清单也落盘了（ShellMenuKnownItems）'

# 重新打开：被关掉的项应该还是关着的（关掉的状态真的读回来了）
# 注意：改设置前必须先关掉旧窗口（它手里那份编辑模型会把 settings.json 覆盖回去）
Close-Settings -Settings $settings
$settings = Open-Settings -Session $session
Select-Category -Settings $settings -Name '右键菜单'
$shellToggles = Wait-ShellToggles -Settings $settings
Assert ((Get-ToggleState -Element $shellToggles['属性']) -eq 'Off') '重新打开设置窗口时「属性」仍是关着的'

# 拨回来（顺便验证清空被关列表）
Toggle-Element -Element $shellToggles['属性']
Start-Sleep -Seconds 1
Assert (@(Get-Setting 'ShellMenuDisabledItems').Count -eq 0) '再拨回来后被关掉的项清空了（回到默认全开）'

# ================================================================== 用例 6：行高滑块

Write-Host '--- 用例 6：「文件列表」页的「行高」滑块 ---'
$defaultRowHeight = [double](Get-Setting 'RowHeight')
$rowPxBefore = Get-TypicalRowHeight -Session $session
$rowRawBefore = @(Get-RowHeights -Session $session)
Write-Host ("  设置里的行高 {0} DIP，实测数据行高 {1:N1} 物理像素（{2:N1} DIP，缩放 {3:N2}）；逐行 = [{4}]" -f `
    $defaultRowHeight, $rowPxBefore, ($rowPxBefore / $session.Scale), $session.Scale, ($rowRawBefore -join ', '))
Assert ($rowPxBefore -gt 0) '能量到文件列表的数据行'
Assert ([Math]::Abs($rowPxBefore - ($defaultRowHeight * $session.Scale)) -le 2) `
    "默认行高真的按设置画出来了（$defaultRowHeight DIP；图标与名称仍是行内垂直居中，见 measure-row-align.ps1）"

Select-Category -Settings $settings -Name '文件列表'
$slider = Find-Slider -Settings $settings -Name '行高'
Assert ($null -ne $slider) '重新切回「文件列表」页时「行高」滑块还在'
if ($null -eq $slider) { Close-Settings -Settings $settings; Stop-Session -Session $session; Write-Host ("SUMMARY failures={0}" -f $failures); exit 1 }

# 切到别的分类后它应该读不到（右侧一次只显示一页）
Select-Category -Settings $settings -Name '外观'
Assert ($null -eq (Find-Slider -Settings $settings -Name '行高')) '切到「外观」页后读不到「行高」滑块（只显示当前分类的项）'

$newRowHeight = if ($defaultRowHeight -ge 40) { 24 } else { 40 }
Select-Category -Settings $settings -Name '文件列表'
Set-SliderValue -Element (Find-Slider -Settings $settings -Name '行高') -Value $newRowHeight

Assert ((Get-Setting 'RowHeight') -eq $newRowHeight) "拖滑块就立即落盘（RowHeight=$newRowHeight）"

Start-Sleep -Seconds 1
$rowPxAfter = Get-TypicalRowHeight -Session $session
$rowRawAfter = @(Get-RowHeights -Session $session)
Write-Host ("  改后实测数据行高 {0:N1} 物理像素（{1:N1} DIP）；逐行 = [{2}]" -f $rowPxAfter, ($rowPxAfter / $session.Scale), ($rowRawAfter -join ', '))
Assert ([Math]::Abs($rowPxAfter - ($newRowHeight * $session.Scale)) -le 2) "文件列表的数据行真的变成 $newRowHeight DIP 高"

# 改回默认值，后面的脚本 / 人手看界面都回到原样
Set-SliderValue -Element (Find-Slider -Settings $settings -Name '行高') -Value $defaultRowHeight
Assert ((Get-Setting 'RowHeight') -eq $defaultRowHeight) '把行高改回默认值也立即落盘'

# ================================================================== 用例 7：侧边栏分组开关

Write-Host '--- 用例 7：「侧边栏」页的分组开关真的作用于侧边栏 ---'
Assert (Test-SidebarGroupVisible -Session $session -Name '云存储') '「云存储」分组默认显示在侧边栏树里'
Assert (Test-SidebarGroupVisible -Session $session -Name '此电脑') '「此电脑」分组默认显示在侧边栏树里'

Invoke-ToggleByKey -Settings $settings -Category '侧边栏' -Key 'sidebarCloud'
Start-Sleep -Seconds 1
Assert ((Get-Setting 'SidebarShowCloud') -eq $false) '关掉「显示「云存储」分组」就立即落盘（SidebarShowCloud=false）'
Assert (-not (Test-SidebarGroupVisible -Session $session -Name '云存储')) '关掉后侧边栏树里真的读不到「云存储」分组了'
Assert (Test-SidebarGroupVisible -Session $session -Name '此电脑') '其它分组不受影响（「此电脑」还在）'

Invoke-ToggleByKey -Settings $settings -Category '侧边栏' -Key 'sidebarCloud'
Start-Sleep -Seconds 1
Assert ((Get-Setting 'SidebarShowCloud') -eq $true) '再拨回来立即落盘（SidebarShowCloud=true）'
Assert (Test-SidebarGroupVisible -Session $session -Name '云存储') '「云存储」分组又回到侧边栏树里了'

# --- 「主目录」分组里显示哪些标准文件夹（默认只开桌面与下载）---
Assert (Test-SidebarGroupVisible -Session $session -Name '桌面') '「主目录」里默认显示「桌面」'
Assert (Test-SidebarGroupVisible -Session $session -Name '下载') '「主目录」里默认显示「下载」'
Assert (-not (Test-SidebarGroupVisible -Session $session -Name '文档')) '「主目录」里默认不显示「文档」'

Invoke-ToggleByKey -Settings $settings -Category '侧边栏' -Key 'sidebarHomeDocuments'
Start-Sleep -Seconds 1
Assert ((Get-Setting 'SidebarHomeDocuments') -eq $true) '打开「显示「文档」」后立即落盘（SidebarHomeDocuments=true）'
Assert (Test-SidebarGroupVisible -Session $session -Name '文档') '打开后「文档」真的出现在「主目录」分组里'

Invoke-ToggleByKey -Settings $settings -Category '侧边栏' -Key 'sidebarHomeDesktop'
Start-Sleep -Seconds 1
Assert ((Get-Setting 'SidebarHomeDesktop') -eq $false) '关掉「显示「桌面」」后立即落盘（SidebarHomeDesktop=false）'
Assert (-not (Test-SidebarGroupVisible -Session $session -Name '桌面')) '关掉后「桌面」真的从「主目录」分组里消失'
Assert (Test-SidebarGroupVisible -Session $session -Name '下载') '关掉「桌面」不影响「下载」'

# 用完还原成默认（桌面 + 下载开，其余关）
Invoke-ToggleByKey -Settings $settings -Category '侧边栏' -Key 'sidebarHomeDesktop'
Invoke-ToggleByKey -Settings $settings -Category '侧边栏' -Key 'sidebarHomeDocuments'
Start-Sleep -Milliseconds 800
Assert ((Get-Setting 'SidebarHomeDesktop') -eq $true) '还原「桌面」开关'
Assert ((Get-Setting 'SidebarHomeDocuments') -eq $false) '还原「文档」开关'

Close-Settings -Settings $settings
Stop-Session -Session $session

# ================================================================== 用例 8：标签页直角

Write-Host '--- 用例 8：「外观」页的「标签页使用直角」（默认开） ---'
Set-Setting 'SquareTabCorners' $true

$session = Start-Session
$settings = Open-Settings -Session $session
Assert ((Get-ToggleStateByKey -Settings $settings -Category '外观' -Key 'squareTabCorners') -eq 'On') '默认用直角（settings.json 里为 true）'

Invoke-ToggleByKey -Settings $settings -Category '外观' -Key 'squareTabCorners'
Assert ((Get-Setting 'SquareTabCorners') -eq $false) '拨一下「标签页使用直角」就立即落盘（SquareTabCorners=false）'
Assert ((Get-ToggleStateByKey -Settings $settings -Category '外观' -Key 'squareTabCorners') -eq 'Off') '窗口里的开关状态也变了'
Assert (Test-LogContains -Pattern '标签页=圆角') '改动当场应用到了所有标签页（exdir.log：标签页=圆角）'

# 关窗重开：值应该还在（读回来仍是圆角）
Close-Settings -Settings $settings
$settings = Open-Settings -Session $session
Assert ((Get-ToggleStateByKey -Settings $settings -Category '外观' -Key 'squareTabCorners') -eq 'Off') '关窗重开设置窗口时仍是圆角'

Invoke-ToggleByKey -Settings $settings -Category '外观' -Key 'squareTabCorners'
Assert ((Get-Setting 'SquareTabCorners') -eq $true) '再拨回来立即落盘（SquareTabCorners=true）'
Assert (Test-LogContains -Pattern '标签页=直角') '再拨回来也当场应用到了所有标签页（exdir.log：标签页=直角）'

Close-Settings -Settings $settings
Stop-Session -Session $session

# ================================================================== 用例 9：主题

Write-Host '--- 用例 9：「外观」页的「主题」与标题栏的太阳 / 月亮开关 ---'
Set-Setting 'Theme' 0

$session = Start-Session
$settings = Open-Settings -Session $session
Select-Category -Settings $settings -Name '外观'

$combo = Find-ThemeCombo -Settings $settings
Assert ($null -ne $combo) '「外观」页里有「主题」下拉框'
Assert ((Get-ThemeComboText -Combo $combo) -eq '跟随系统') '下拉框的初值是「跟随系统」（settings.json 里 Theme=0）'

Select-ThemeComboItem -Settings $settings -Name '深色'
Assert ((Get-Setting 'Theme') -eq 2) '选「深色」就立即落盘（Theme=2）'
Assert ((Get-ThemeComboText -Combo (Find-ThemeCombo -Settings $settings)) -eq '深色') '下拉框显示「深色」'
Assert (Test-LogContains -Pattern '主题已应用：深色' -Tail 200) '当场应用到了主窗口（exdir.log：主题已应用：深色）'

# 标题栏那个太阳 / 月亮开关改的是同一个设置（UIA 里是支持 TogglePattern 的 Button）
$themeToggle = Find-VisibleFirst -From $session.Root -Name '深色模式' -ControlType ([System.Windows.Automation.ControlType]::Button)
Assert ($null -ne $themeToggle) '标题栏上（「文件」菜单左边）有主题开关'
if ($null -ne $themeToggle) {
    Assert ((Get-ToggleState -Element $themeToggle) -eq 'On') '深色主题下这个开关是打开状态（显示月亮）'
    Toggle-Element -Element $themeToggle
    Assert ((Get-Setting 'Theme') -eq 1) '拨一下就落盘成显式的浅色（Theme=1）'
    Assert ((Get-ToggleState -Element $themeToggle) -eq 'Off') '开关状态跟着变成关闭（显示太阳）'
    Assert ((Get-ThemeComboText -Combo (Find-ThemeCombo -Settings $settings)) -eq '浅色') '设置窗口里的下拉框也同步成了「浅色」'
    Assert (Test-LogContains -Pattern '主题已应用：浅色' -Tail 200) '当场应用（exdir.log：主题已应用：浅色）'
}

# 选回「跟随系统」：系统主题每台机器不一样，所以只断言落盘与“当场应用”，不断言画出来是深还是浅
Select-ThemeComboItem -Settings $settings -Name '跟随系统'
Assert ((Get-Setting 'Theme') -eq 0) '再选回「跟随系统」立即落盘（Theme=0）'
Assert (Test-LogContains -Pattern '主题已应用：跟随系统' -Tail 200) '当场应用（exdir.log：主题已应用：跟随系统）'

Close-Settings -Settings $settings
Stop-Session -Session $session

# ------------------------------------------------------------------ 还原设置文件

if ($null -ne $originalSettings) {
    Set-Content $settingsPath $originalSettings -Encoding utf8
    Write-Host '已还原 settings.json'
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
