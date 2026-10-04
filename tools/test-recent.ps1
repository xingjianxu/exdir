# 侧边栏「最新访问」的界面回归脚本（2026-10 改版：侧边栏只有一个入口，点它开一个专用标签页）。
#
# 用法:
#   pwsh -NoProfile -File tools\test-recent.ps1
#   pwsh -NoProfile -File tools\test-recent.ps1 -Exe dist\win-x64\exdir.exe
#
# 用例（全程 UIA：不敲键盘、不点真鼠标，所以**不需要交互桌面**；用例 8 的真鼠标右键会自动 SKIP）：
#   1. 侧边栏里「最新访问」只有一个节点：排在最上面、下面紧挨着就是下一个分组（没有平铺的子项）、
#      自己不带展开箭头；点它 → 在活动窗格**新开**一个「最新访问」标签页（原标签页还在）；
#      再点一次 → 不重复开，切到已打开的那个；
#   2. 列表里是最近访问过的目录与文件，**按访问时间倒序**（记录里的顺序），
#      即使“文件夹排在文件前面”开着也不会被重排；已经不存在的路径不显示（但记录还留着）；
#      目录行有展开箭头、文件行没有；
#   3. 导航一次 → 记到最前（recents.json 的第一条，IsDirectory=true）；
#   4. 旧格式（只有 Folders 的 recents.json）自动迁移成新格式（Entries + IsDirectory），
#      原来的顺序不变；
#   5. 上限 50 条，超出挤掉最旧的；
#   6. SidebarShowRecent=false 时侧边栏里没有这个入口；
#   7. 打开文件（用默认程序）也会记进「最新访问」，IsDirectory=false —— 需要交互桌面
#      （要真的起默认程序），没有就 SKIP；
#   8. 右键节点 →「清空最新访问」（真鼠标，没有交互桌面时 SKIP）。
#
# 脚本用独立的 XDG_CONFIG_HOME / XDG_DATA_HOME（都在 %TEMP% 下），
# 完全不动用户的 config.json 与 recents.json；跑完删掉整个临时目录。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RecentTestNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint MOVE = 0x0001;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;
    public const uint ABSOLUTE = 0x8000;
}
'@

[void][RecentTestNative]::SetProcessDpiAwarenessContext([IntPtr][RecentTestNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }
$exeDir = Split-Path $exePath

# ------------------------------------------------------------------ 测试用的目录与配置

$workRoot = Join-Path $env:TEMP 'exdir-recent-ui'
if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }

$configRoot = Join-Path $workRoot 'cfg'
$dataRoot = Join-Path $workRoot 'data'
New-Item -ItemType Directory -Force -Path (Join-Path $configRoot 'exdir'), (Join-Path $dataRoot 'exdir') | Out-Null

$settingsPath = Join-Path $configRoot 'exdir\config.json'
$recentsPath = Join-Path $dataRoot 'exdir\recents.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'

$dirs = @{}
foreach ($name in @('recA', 'recB', 'recC') + (0..54 | ForEach-Object { "rec$_" })) {
    $dirs[$name] = Join-Path $workRoot $name
    New-Item -ItemType Directory -Force -Path $dirs[$name] | Out-Null
}

$fileInA = Join-Path $dirs['recA'] 'file1.txt'
$fileInB = Join-Path $dirs['recB'] 'file2.txt'
$openedFile = Join-Path $dirs['recC'] 'probe.txt'
[System.IO.File]::WriteAllText($fileInA, 'file1')
[System.IO.File]::WriteAllText($fileInB, 'file2')
[System.IO.File]::WriteAllText($openedFile, 'probe')
$goneDir = Join-Path $workRoot 'recGone'   # 故意不创建：记录里有、磁盘上没有

$recentName = '最新访问'
$allGroupNames = @($recentName, '收藏夹', '主目录', '云存储', '远程', '此电脑')

$originalXdgConfig = $env:XDG_CONFIG_HOME
$originalXdgData = $env:XDG_DATA_HOME
$env:XDG_CONFIG_HOME = $configRoot
$env:XDG_DATA_HOME = $dataRoot

function Set-Config {
    param([string[]]$Tabs, [bool]$ShowRecent = $true)
    $cfg = [ordered]@{
        SchemaVersion            = 11
        IsSidebarVisible         = $true
        IsDualPane               = $false
        SidebarShowRecent        = $ShowRecent
        SidebarShowHome          = $true
        PinnedFoldersInitialized = $true
        PinnedFolders            = @()
        ShowHiddenFiles          = $false
        ShowExtensions           = $true
        FoldersFirst             = $true
        RowHeight                = 28
        ColumnWidths             = @()
        ColumnAutoFit            = $true
        PrimaryTabs              = @($Tabs)
        PrimaryActiveTab         = 0
        SecondaryTabs            = @()
    }
    $cfg | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding utf8
}

function Set-Recents {
    param([object[]]$Entries)
    @{ Entries = @($Entries) } | ConvertTo-Json -Depth 5 | Set-Content -Path $recentsPath -Encoding utf8
}

function Set-RecentsLegacy {
    param([string[]]$Paths)
    @{ Folders = @($Paths) } | ConvertTo-Json -Depth 4 | Set-Content -Path $recentsPath -Encoding utf8
}

# 读回来的条目（新格式给 Entries，旧格式退化成“都是目录”）
function Get-RecentEntries {
    if (-not (Test-Path $recentsPath)) { return @() }
    try {
        $json = Get-Content $recentsPath -Raw | ConvertFrom-Json
        if ($null -ne $json.Entries) { return @($json.Entries) }
        if ($null -ne $json.Folders) {
            return @($json.Folders | ForEach-Object { [pscustomobject]@{ Path = $_; IsDirectory = $true } })
        }
        return @()
    } catch { return @() }
}

function Get-RecentPaths {
    return @((Get-RecentEntries) | ForEach-Object { $_.Path })
}

# ------------------------------------------------------------------ 断言 / UIA 工具

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

function Find-ByType {
    param($From, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    return @($From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

function Find-Elements {
    param($From, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return @($From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

function Get-VisibleByType {
    param($Session, $ControlType)
    return @(Find-ByType -From $Session.Root -ControlType $ControlType |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
}

# 侧边栏树节点（按屏幕上的上下顺序，也就是树里的顺序）
function Get-TreeNodes {
    param($Session)
    return @(Get-VisibleByType -Session $Session -ControlType ([System.Windows.Automation.ControlType]::TreeItem) |
        Sort-Object { $_.Current.BoundingRectangle.Y })
}

function Get-TreeNames {
    param($Session)
    return @((Get-TreeNodes -Session $Session) | ForEach-Object { $_.Current.Name })
}

function Wait-TreeItem {
    param($Session, [string]$Name, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $found = @(Get-TreeNodes -Session $Session | Where-Object { $_.Current.Name -eq $Name })
        if ($found.Count -gt 0) { return $found[0] }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# 侧边栏节点的点击：ItemInvoked 走的是 Invoke（SelectionItem 只改选中，不会导航）
function Invoke-TreeItem {
    param($Session, [string]$Name)
    $item = Wait-TreeItem -Session $Session -Name $Name
    if ($null -eq $item) {
        Write-Host ("  实际树节点: {0}" -f ((Get-TreeNames -Session $Session) -join ', '))
        throw "侧边栏里找不到「$Name」"
    }

    $pattern = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    }
    elseif ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
    }
    else {
        throw "「$Name」既不支持 Invoke 也不支持 Select"
    }

    Start-Sleep -Milliseconds 1500
}

# 标签条（UIA 返回顺序就是逻辑顺序，不要按 X 排序，见 AGENTS.md 第 6 节第 73 条）
function Get-TabItems {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::TabItem) |
        Where-Object { $_.Current.BoundingRectangle.Width -gt 0 })
}

function Get-ActiveTabName {
    param($Session)
    foreach ($tab in (Get-TabItems -Session $Session)) {
        $p = $null
        if ($tab.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p) -and $p.Current.IsSelected) {
            return $tab.Current.Name
        }
    }
    return ''
}

function Wait-Tab {
    param($Session, [string]$Name, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-TabItems -Session $Session | Where-Object { $_.Current.Name -eq $Name }).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 250
    }
    Write-Host ("  实际标签: {0}" -f ((Get-TabItems -Session $Session | ForEach-Object { $_.Current.Name }) -join ', '))
    return $false
}

# 状态栏的文本：容器有 AutomationProperties.Name="状态栏"，里面的 TextBlock 不给名字
# （UIA 名字就是它的文本），所以能直接断言「N 项」。列表是虚拟化的，UIA 只能读到已生成
# 容器的那些行，要数总数得看这里（见 AGENTS.md 第 6 节第 108 条）。
function Get-StatusTexts {
    param($Session)
    $bar = @(Find-Elements -From $Session.Root -Name '状态栏' |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 } |
        Select-Object -First 1)[0]
    if ($null -eq $bar) { return @() }

    return @(Find-ByType -From $bar -ControlType ([System.Windows.Automation.ControlType]::Text) |
        ForEach-Object { $_.Current.Name } | Where-Object { -not [string]::IsNullOrEmpty($_) })
}

function Wait-StatusItemCount {
    param($Session, [int]$Count, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-StatusTexts -Session $Session) -contains "$Count 项") { return $true }
        Start-Sleep -Milliseconds 250
    }

    Write-Host ("  实际状态栏: {0}" -f ((Get-StatusTexts -Session $Session) -join ' | '))
    return $false
}

# 文件列表的行（ListItems）；非活动标签页的内容是 offscreen，会被过滤掉
function Get-Rows {
    param($Session)
    return @(Get-VisibleByType -Session $Session -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
        Sort-Object { $_.Current.BoundingRectangle.Y })
}

function Get-RowNames {
    param($Session)
    return @((Get-Rows -Session $Session) | ForEach-Object { $_.Current.Name })
}

function Get-RowByName {
    param($Session, [string]$Name)
    return @(Get-Rows -Session $Session | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1)[0]
}

function Wait-Rows {
    param($Session, [int]$Count, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Rows -Session $Session).Count -eq $Count) { return $true }
        Start-Sleep -Milliseconds 250
    }
    Write-Host ("  实际行: {0}" -f ((Get-RowNames -Session $Session) -join ', '))
    return $false
}

# 行内那个 18px 展开箭头（UIA 名字是「展开或折叠」）：按“落在这行的矩形里”筛出来
function Get-RowExpander {
    param($Session, $Row)
    if ($null -eq $Row) { return $null }
    $rowRect = $Row.Current.BoundingRectangle
    return @(Find-Elements -From $Session.Root -Name '展开或折叠' | Where-Object {
            $b = $_.Current.BoundingRectangle
            (-not $_.Current.IsOffscreen) -and $b.Width -gt 0 -and $b.Height -gt 0 -and
            $b.Top -ge $rowRect.Top -and $b.Bottom -le $rowRect.Bottom -and
            $b.Left -ge $rowRect.Left -and $b.Right -le $rowRect.Right
        } | Select-Object -First 1)[0]
}

# ------------------------------------------------------------------ 进程

function Start-Session {
    param([string[]]$ArgumentList = @())

    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 600

    $startArgs = @{ FilePath = $script:exePath; WorkingDirectory = $script:exeDir; PassThru = $true }
    if ($ArgumentList.Count -gt 0) { $startArgs.ArgumentList = $ArgumentList }

    $proc = Start-Process @startArgs
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
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 500
}

# ------------------------------------------------------------------ 真鼠标 / 弹出菜单（只给用例 8 用）

function Move-Mouse {
    param([int]$X, [int]$Y)
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $nx = [uint32][math]::Round($X * 65535 / ($screen.Width - 1))
    $ny = [uint32][math]::Round($Y * 65535 / ($screen.Height - 1))
    [RecentTestNative]::mouse_event([RecentTestNative]::MOVE -bor [RecentTestNative]::ABSOLUTE, $nx, $ny, 0, [IntPtr]::Zero)
}

function Invoke-RightClick {
    param($Session, [int]$X, [int]$Y)

    for ($i = 0; $i -lt 10; $i++) {
        [void][RecentTestNative]::SetForegroundWindow($Session.Handle)
        Start-Sleep -Milliseconds 300
        if ([RecentTestNative]::GetForegroundWindow() -eq $Session.Handle) { break }
    }

    # 先移到屏幕角落再移回来：同坐标的悬停不产生 PointerMoved（见 AGENTS.md 第 6 节第 13 条）
    Move-Mouse 1 1
    Start-Sleep -Milliseconds 200
    Move-Mouse $X $Y
    Start-Sleep -Milliseconds 300
    [RecentTestNative]::mouse_event([RecentTestNative]::RIGHTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [RecentTestNative]::mouse_event([RecentTestNative]::RIGHTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1500
}

# 弹出菜单不在主窗口的 UIA 子树里（是另一个 XAML 岛），要从桌面根往下找（见 AGENTS.md 第 6 节第 24 条）
function Invoke-FlyoutItem {
    param($Session, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)

    for ($i = 0; $i -lt 20; $i++) {
        $item = @($Session.Desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
            Where-Object {
                $_.Current.ProcessId -eq $Session.Proc.Id -and $_.Current.Name -eq $Name -and
                -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0
            } | Select-Object -First 1)[0]

        if ($null -ne $item) {
            $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1200
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

# 用 UIA 模式展开主菜单并执行菜单项
function Invoke-MenuItem {
    param($Session, [string]$MenuName, [string]$ItemName)
    $menuBarItem = @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::MenuItem) |
        Where-Object { $_.Current.Name -eq $MenuName -and -not $_.Current.IsOffscreen } | Select-Object -First 1)[0]
    if ($null -eq $menuBarItem) { throw "主菜单里找不到「$MenuName」" }

    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)

    $item = $null
    for ($i = 0; $i -lt 24 -and $null -eq $item; $i++) {
        Start-Sleep -Milliseconds 250
        $item = @($Session.Desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
            Where-Object {
                $_.Current.ProcessId -eq $Session.Proc.Id -and $_.Current.Name -eq $ItemName -and
                -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0
            } | Select-Object -First 1)[0]
    }

    if ($null -eq $item) { throw "「$MenuName」菜单里找不到「$ItemName」" }

    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800
}

# ================================================================== 用例

$userProfile = $env:USERPROFILE
$session = $null

try {

Write-Host '=== 用例 1：侧边栏只有一个入口 → 点它开专用标签页 ==='
Set-Recents @(
    @{ Path = $dirs['recB']; IsDirectory = $true },
    @{ Path = $fileInB;      IsDirectory = $false },
    @{ Path = $dirs['recA']; IsDirectory = $true },
    @{ Path = $fileInA;      IsDirectory = $false },
    @{ Path = $goneDir;      IsDirectory = $true }
)
Set-Config -Tabs @($dirs['recC'])
$session = Start-Session

$nodes = Get-TreeNames -Session $session
Assert ($nodes.Count -gt 0 -and $nodes[0] -eq $recentName) "「最新访问」排在侧边栏最前面（实际第一个是 $(if ($nodes.Count) { $nodes[0] } else { '<空>' })）"

$recentNode = Wait-TreeItem -Session $session -Name $recentName
Assert ($null -ne $recentNode) '侧边栏里有「最新访问」入口'
if ($null -ne $recentNode) {
    $rect = $recentNode.Current.BoundingRectangle
    Assert ($null -eq (Get-RowExpander -Session $session -Row $recentNode)) '「最新访问」节点不带展开箭头（它没有平铺的子项）'

    $below = @(Get-TreeNodes -Session $session | Where-Object { $_.Current.BoundingRectangle.Y -gt $rect.Y })
    $nextName = if ($below.Count -gt 0) { $below[0].Current.Name } else { '' }
    Assert ($nextName -eq '收藏夹') "「最新访问」下面紧挨着就是下一个分组、没有子项（实际: $nextName）"
}

Assert ((Get-TabItems -Session $session).Count -eq 1) '（前提）起始只有一个标签页'
Invoke-TreeItem -Session $session -Name $recentName
Assert (Wait-Tab -Session $session -Name $recentName) '点侧边栏入口后出现「最新访问」标签页'
Assert ((Get-TabItems -Session $session).Count -eq 2) '是**新开**一个标签页（原来的那个还在）'
Assert ((Get-ActiveTabName -Session $session) -eq $recentName) '新开的标签页成为活动标签页'
Assert ((Get-TabItems -Session $session | ForEach-Object { $_.Current.Name }) -contains 'recC') '原来那个 recC 标签页还在'

Write-Host ''
Write-Host '=== 用例 2：列表内容 = 最近访问过的目录与文件，按访问时间倒序 ==='
Assert (Wait-Rows -Session $session -Count 4) '列表里就是那 4 条（已不存在的 recGone 不显示）'
Assert ((Get-RowNames -Session $session) -join '|' -eq 'recB|file2.txt|recA|file1.txt') `
    "顺序就是记录里的顺序、没有被「文件夹排在文件前面」重排（实际: $((Get-RowNames -Session $session) -join ', ')）"
Assert ((Get-RecentPaths) -contains $goneDir) '已经不存在的路径只是不显示，记录本身还留着（不做清理）'

$dirRow = Get-RowByName -Session $session -Name 'recB'
$fileRow = Get-RowByName -Session $session -Name 'file2.txt'
$dirArrow = Get-RowExpander -Session $session -Row $dirRow
$fileArrow = Get-RowExpander -Session $session -Row $fileRow
Assert ($null -ne $dirArrow) '目录行有行首展开箭头（可以就地展开）'

# 行首那个箭头元素每行都有（不可展开时只是个空字形占位），所以不能拿“元素在不在”
# 当“能不能展开”；直接点一下看会不会多出行才是真的判据。
if ($null -ne $fileArrow) {
    try { $fileArrow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
}
Start-Sleep -Milliseconds 900
Assert ((Get-Rows -Session $session).Count -eq 4) '在文件行的箭头上点一下不会展开出任何东西（它不是目录）'

if ($null -ne $dirArrow) {
    try { $dirArrow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
}
Start-Sleep -Milliseconds 1200
Assert ((Get-Rows -Session $session).Count -eq 5) '在目录行的箭头上点一下会就地展开出子项（recB 里的 file2.txt）'

Invoke-TreeItem -Session $session -Name $recentName
Assert ((Get-TabItems -Session $session).Count -eq 2) '再点一次侧边栏入口不会重复开标签页（切到已打开的那个）'

Write-Host ''
Write-Host '=== 用例 3：导航一次 → 记到最前（IsDirectory=true） ==='
Invoke-TreeItem -Session $session -Name '主目录'
$entries = Get-RecentEntries
Assert ($entries.Count -eq 6) "新访问的一条被记下来了（实际 $($entries.Count) 条）"
Assert (($entries[0].Path).TrimEnd('\') -eq $userProfile.TrimEnd('\')) "主目录排在第一条（$($entries[0].Path)）"
Assert ($entries[0].IsDirectory -eq $true) '目录记录带 IsDirectory=true'

Write-Host ''
Write-Host '=== 用例 4：旧格式（只有 Folders）自动迁移 ==='
Set-RecentsLegacy @($dirs['recA'], $dirs['recB'])
Set-Config -Tabs @($dirs['recC'])
$session = Start-Session
Invoke-TreeItem -Session $session -Name $recentName
Assert (Wait-Rows -Session $session -Count 2) '旧格式里的两条目录都列出来了'
Assert ((Get-RowNames -Session $session) -join '|' -eq 'recA|recB') '旧格式的顺序原样保留（最新的在最前）'

Invoke-TreeItem -Session $session -Name '主目录'
$json = Get-Content $recentsPath -Raw | ConvertFrom-Json
Assert ($null -ne $json.Entries) '落盘时已经换成新格式（Entries）'
Assert ($null -eq $json.Folders -or $json.Folders.Count -eq 0) '旧字段 Folders 不再写出来'
$entries = Get-RecentEntries
Assert ($entries.Count -eq 3) "迁移 + 新访问共 3 条（实际 $($entries.Count)）"
Assert (@($entries | Where-Object { -not $_.IsDirectory }).Count -eq 0) '迁移过来的两条都被当成目录（IsDirectory=true）'
Assert (($entries[0].Path).TrimEnd('\') -eq $userProfile.TrimEnd('\')) '新访问的目录排在最前'

Write-Host ''
Write-Host '=== 用例 5：上限 50 条，超过的不会进列表、新增时挤掉最旧的 ==='
$many = @(0..54 | ForEach-Object { @{ Path = $dirs["rec$_"]; IsDirectory = $true } })
Set-Recents $many
Set-Config -Tabs @($dirs['recA'])
$session = Start-Session
Assert ((Get-RecentEntries).Count -eq 55) '（前提）文件里预置了 55 条（超过上限）'

Invoke-TreeItem -Session $session -Name $recentName
# 列表是虚拟化的，UIA 只能读到已经生成容器的那些行 —— 总数看状态栏的「N 项」（第 108 条）
Assert (Wait-StatusItemCount -Session $session -Count 50) '装载时就截到 50 条（文件里多出来的不会进列表）'

Invoke-TreeItem -Session $session -Name '主目录'
$entries = Get-RecentEntries
Assert ($entries.Count -eq 50) "新增一条后仍然是 50 条（实际 $($entries.Count)）"
Assert (($entries[0].Path).TrimEnd('\') -eq $userProfile.TrimEnd('\')) '新访问的目录在最前'
Assert ((Get-RecentPaths) -notcontains $dirs['rec49']) '新增时挤掉的是当时最旧的一条（rec49）'
Assert ((Get-RecentPaths) -contains $dirs['rec48']) 'rec49 前面那条还在（只挤掉一条）'
Stop-Session -Session $session
$session = $null

Write-Host ''
Write-Host '=== 用例 6：SidebarShowRecent=false 时侧边栏里没有这个入口 ==='
Set-Recents @(@{ Path = $dirs['recB']; IsDirectory = $true })
Set-Config -Tabs @($dirs['recC']) -ShowRecent $false
$session = Start-Session
$found = Wait-TreeItem -Session $session -Name $recentName -TimeoutSeconds 5
Assert ($null -eq $found) 'SidebarShowRecent=false 时侧边栏里没有「最新访问」'

Write-Host ''
Write-Host '=== 用例 7：用默认程序打开文件也会记进「最新访问」（需要交互桌面） ==='
if ([RecentTestNative]::GetForegroundWindow() -eq [IntPtr]::Zero) {
    Write-Host '  SKIP 没有交互桌面，不真的去启动默认程序（见 AGENTS.md 第 6 节第 18 条）' -ForegroundColor Yellow
}
else {
    Set-Recents @(@{ Path = $dirs['recC']; IsDirectory = $true })
    Set-Config -Tabs @($dirs['recC'])
    Stop-Session -Session $session
    $session = Start-Session

    $before = @(Get-Process -Name 'Notepad' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

    # 当前目录是 recC，「文件 → 打开」作用于选中项：先把 probe.txt 选中
    $deadline = (Get-Date).AddSeconds(10)
    $probeRow = $null
    while ((Get-Date) -lt $deadline -and $null -eq $probeRow) {
        $probeRow = @(Get-Rows -Session $session | Where-Object { $_.Current.Name -eq 'probe.txt' } | Select-Object -First 1)[0]
        if ($null -eq $probeRow) { Start-Sleep -Milliseconds 300 }
    }

    if ($null -eq $probeRow) {
        Write-Host ("  实际行: {0}" -f ((Get-RowNames -Session $session) -join ', '))
        Assert $false '列表里找不到 probe.txt'
    }
    else {
        $probeRow.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 400
        Invoke-MenuItem -Session $session -MenuName '文件' -ItemName '打开'

        $deadline = (Get-Date).AddSeconds(20)
        $recorded = $false
        while ((Get-Date) -lt $deadline -and -not $recorded) {
            $recorded = (Get-RecentPaths) -contains $openedFile
            if (-not $recorded) { Start-Sleep -Milliseconds 400 }
        }

        Assert $recorded '打开文件后 probe.txt 被记进 recents.json'
        $entry = @(Get-RecentEntries | Where-Object { $_.Path -eq $openedFile } | Select-Object -First 1)[0]
        Assert ($null -ne $entry -and $entry.IsDirectory -eq $false) '文件记录带 IsDirectory=false'
    }

    # 收尾：把这次打开默认程序新起的 Notepad 关掉（用户自己开着的那个不动）
    @(Get-Process -Name 'Notepad' -ErrorAction SilentlyContinue |
        Where-Object { $before -notcontains $_.Id }) | ForEach-Object { try { $_.Kill() } catch { } }
}

Write-Host ''
Write-Host '=== 用例 8：右键「清空最新访问」（真鼠标） ==='
if ([RecentTestNative]::GetForegroundWindow() -eq [IntPtr]::Zero) {
    Write-Host '  SKIP 没有交互桌面，无法模拟右键（见 AGENTS.md 第 6 节第 18 条）' -ForegroundColor Yellow
}
else {
    Set-Recents @(@{ Path = $dirs['recB']; IsDirectory = $true })
    Set-Config -Tabs @($dirs['recC'])
    Stop-Session -Session $session
    $session = Start-Session

    $node = Wait-TreeItem -Session $session -Name $recentName
    if ($null -eq $node) {
        Assert $false '找不到「最新访问」入口'
    }
    else {
        $r = $node.Current.BoundingRectangle
        $opened = $false
        for ($attempt = 0; $attempt -lt 2 -and -not $opened; $attempt++) {
            Invoke-RightClick -Session $session -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))
            $opened = Invoke-FlyoutItem -Session $session -Name '清空最新访问'
        }

        if (-not $opened) {
            Assert $false '右键「最新访问」没有得到「清空最新访问」菜单项'
        }
        else {
            Assert ((Get-RecentEntries).Count -eq 0) '清空后 recents.json 里没有记录'
        }
    }
}

Stop-Session -Session $session
$session = $null

Write-Host ''
Write-Host "结论: $(if ($failures -eq 0) { '全部通过' } else { "$failures 个断言失败" })" -ForegroundColor $(if ($failures -eq 0) { 'Green' } else { 'Red' })
}
finally {
    if ($null -ne $session) { Stop-Session -Session $session }
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    $env:XDG_CONFIG_HOME = $originalXdgConfig
    $env:XDG_DATA_HOME = $originalXdgData

    if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host '临时目录与进程已清理'
}

if ($failures -gt 0) { exit 1 }
