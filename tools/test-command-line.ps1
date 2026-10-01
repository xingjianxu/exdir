# 命令行调用（exdir [path]）回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-command-line.ps1
#   pwsh -NoProfile -File tools\test-command-line.ps1 -Exe dist\win-x64\exdir.exe
#
# 十个用例（全程 UIA，不需要交互桌面、不需要真鼠标）：
#   1. 启动时就带路径（exdir <目录>）→ 会话照旧恢复，请求的目录在新标签页里打开并切过去；
#   2. 已在运行 + exdir <另一个目录> → 活动窗格里新开标签页（原标签页不动）；
#   3. 已在运行 + exdir <同一个目录> → 不新开标签页（标签数不变）；
#   4. 已在运行 + exdir <文件> → 打开文件所在目录（新标签页）并选中该文件（状态栏「选中 1 项」）；
#   5. 再执行一次同一个文件 → 标签数不变，文件仍然被选中；
#   6. 窗口藏在托盘里 + exdir <目录> → 窗口被唤回、请求照样处理；
#   7. 无参数 + 工作目录是 exe 所在目录（双击 exe / 点任务栏图标就是这种）→ 只唤回窗口，不开新标签页；
#   8. 无参数 + 工作目录是用户在终端里的目录 → 打开该目录；
#   9. 路径不存在 → 不新开标签页，当前标签页显示「无法打开…」；
#  10. 带空格的路径（命令行里是带引号的）→ 一样能打开。
#
# 每个用例都顺带断言：第二个进程自己很快退出（不会多出第二个实例 / 第二个托盘图标），
# 原进程 PID 与窗口句柄都不变。
#
# 跑完会还原 settings.json 的原始内容并删掉测试目录。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CliNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}
'@

[void][CliNative]::SetProcessDpiAwarenessContext([IntPtr][CliNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }
$exeDir = Split-Path $exePath

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

# 日志是追加写的、不清理：断言“刚刚才发生的那件事”时只能看这一段
function Reset-LogMark { $script:logMark = (Get-LogText).Length }
function Get-NewLogText {
    $text = Get-LogText
    if ($text.Length -lt $script:logMark) { $script:logMark = 0 }
    return $text.Substring($script:logMark)
}
$script:logMark = 0

# ------------------------------------------------------------------ 测试目录

$workRoot = Join-Path $env:TEMP 'exdir-cli'
$alpha = Join-Path $workRoot 'alpha'
$beta = Join-Path $workRoot 'beta'
$gamma = Join-Path $workRoot 'gamma'
$targetFile = Join-Path $gamma 'target.txt'
$missingPath = Join-Path $workRoot 'nope-does-not-exist'

if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }
foreach ($dir in @($alpha, $beta, $gamma)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($targetFile, [byte[]]::new(1024))   # 1024 B → 状态栏显示 1.00 KB

# 会话固定成「打开 $workRoot」，这样带路径启动的用例能看到“会话 + 请求的目录”两个标签页
if ($null -ne $originalSettings) { $json = $originalSettings | ConvertFrom-Json } else { $json = New-Object psobject }
$overrides = [ordered]@{
    PrimaryTabs      = @($workRoot)
    PrimaryActiveTab = 0
    IsDualPane       = $false
    IsSidebarVisible = $true
    ShowHiddenFiles  = $false
    ShowExtensions   = $false
    WindowMaximized  = $false
}
foreach ($key in $overrides.Keys) {
    if ($json.PSObject.Properties.Name -contains $key) { $json.$key = $overrides[$key] }
    else { $json | Add-Member -NotePropertyName $key -NotePropertyValue $overrides[$key] }
}
$json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

# ------------------------------------------------------------------ UIA 小工具

function Find-Elements {
    param($From, [string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return @($From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

function Find-ByType {
    param($From, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
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

# 标签页清单：TabItem 的 UIA 名字就是标签标题（= 目录名）；选中的那个用 [ ] 标出来。
# 不用 BoundingRectangle 排序：标签多到装不下时（标签条会横向滚动）溢出的标签
# 报出来的 X 是 NaN / 负数这类无效值，按它排会得到乱的顺序；
# FindAll 给的顺序就是标签条的**逻辑顺序**（与界面上一致，已实测）。
function Get-TabKey {
    param($Session)
    $items = Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::TabItem)

    return (@($items | ForEach-Object {
                $selected = $false
                try {
                    $selected = $_.GetCurrentPattern(
                        [System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
                } catch { }

                if ($selected) { "[$($_.Current.Name)]" } else { $_.Current.Name }
            }) -join ',')
}

# 等标签页状态变成期望的样子（导航是异步的，得给它时间）
function Wait-TabKey {
    param($Session, [string]$Expected, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $actual = Get-TabKey -Session $Session
        if ($actual -eq $Expected) { return $true }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ("  实际标签: {0}（期望 {1}）" -f (Get-TabKey -Session $Session), $Expected)
    return $false
}

# 状态栏里三个 TextBlock 的文本（从左到右）
function Get-StatusTexts {
    param($Session)
    $bar = $null
    foreach ($el in (Find-Elements -From $Session.Root -Name '状态栏')) {
        if ($el.Current.BoundingRectangle.Width -gt 0) { $bar = $el; break }
    }

    if ($null -eq $bar) { return @() }

    $items = @()
    foreach ($el in (Find-ByType -From $bar -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        if (-not [string]::IsNullOrEmpty($el.Current.Name)) {
            $items += [pscustomobject]@{ X = $el.Current.BoundingRectangle.X; Text = $el.Current.Name }
        }
    }

    return @($items | Sort-Object X | ForEach-Object { $_.Text })
}

function Wait-StatusText {
    param($Session, [string]$Text, [int]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-StatusTexts -Session $Session) -contains $Text) { return $true }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ("  实际状态栏: {0}" -f ((Get-StatusTexts -Session $Session) -join ' | '))
    return $false
}

function Wait-NewLog {
    param([string]$Pattern, [int]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-NewLogText) -match $Pattern) { return $true }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ("  实际新增日志: {0}" -f ((Get-NewLogText) -replace "`r?`n", ' / '))
    return $false
}

function Find-WindowText {
    param($Session, [string]$Like)
    foreach ($el in (Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        if ($el.Current.Name -like $Like) { return $el.Current.Name }
    }

    return ''
}

# 用 UIA 模式展开菜单并执行菜单项（弹层有入场动画，真鼠标点容易打空）
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

# ------------------------------------------------------------------ 会话与第二个实例

function Start-Session {
    param([string[]]$ArgumentList = @(), [string]$WorkingDirectory = '')

    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 600

    $startArgs = @{ FilePath = $script:exePath; WorkingDirectory = $script:exeDir; PassThru = $true }
    if ($ArgumentList.Count -gt 0) { $startArgs.ArgumentList = $ArgumentList }
    if ($WorkingDirectory) { $startArgs.WorkingDirectory = $WorkingDirectory }

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

# 再敲一次 exdir（第二个实例）：它应该很快自己退出，绝不能留下来变成第二个进程
function Invoke-Cli {
    param($Session, [string[]]$ArgumentList = @(), [string]$WorkingDirectory = '')

    $startArgs = @{ FilePath = $script:exePath; WorkingDirectory = $script:exeDir; PassThru = $true }
    if ($ArgumentList.Count -gt 0) { $startArgs.ArgumentList = $ArgumentList }
    if ($WorkingDirectory) { $startArgs.WorkingDirectory = $WorkingDirectory }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $second = Start-Process @startArgs
    $exited = $second.WaitForExit(20000)
    $watch.Stop()

    $code = if ($exited) { $second.ExitCode } else { try { $second.Kill() } catch { }; -1 }

    $session.Proc.Refresh()
    $alive = @(Get-Process -Name 'exdir' -ErrorAction SilentlyContinue)

    Assert $exited "第二个进程很快自己退出（$([Math]::Round($watch.Elapsed.TotalMilliseconds)) ms）"
    Assert ($exited -and $code -eq 0) "第二个进程正常退出（退出码 $code）"
    Assert ($alive.Count -eq 1) "只剩一个 exdir 进程（实际 $($alive.Count) 个）"
    Assert ($alive.Count -ge 1 -and $alive[0].Id -eq $Session.Proc.Id) '还活着的就是原来那个进程（PID 没变）'

    # 藏进托盘时 MainWindowHandle 是 0，所以只在窗口可见时断言句柄（用例 6 会在唤回后再补一次）
    if ([CliNative]::IsWindowVisible($Session.Handle)) {
        Assert (-not $Session.Proc.HasExited -and $Session.Proc.MainWindowHandle -eq $Session.Handle) '主窗口还是原来那只（句柄没变，没有重建）'
    }
}

function Wait-WindowVisible {
    param($Session, [bool]$Visible, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ([CliNative]::IsWindowVisible($Session.Handle) -eq $Visible) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# ================================================================== 用例

Write-Host '=== 用例 1：启动时就带路径 → 会话照旧恢复 + 请求的目录在新标签页打开 ==='
$session = Start-Session -ArgumentList @($alpha)
Assert (Wait-TabKey -Session $session -Expected 'exdir-cli,[alpha]') '活动窗格里是「会话恢复的 exdir-cli + 新标签页 alpha」，且 alpha 是活动标签页'
Assert ([CliNative]::IsWindowVisible($session.Handle)) '窗口可见'
Assert ((Get-LogText) -match '命令行：路径参数') '日志确认解析出了路径参数'

Write-Host ''
Write-Host '=== 用例 2：已在运行 + exdir <另一个目录> → 新开标签页 ==='
Reset-LogMark
Invoke-Cli -Session $session -ArgumentList @($beta)
Assert (Wait-TabKey -Session $session -Expected 'exdir-cli,alpha,[beta]') '在新标签页里打开 beta，原标签页仍在'
Assert (Wait-NewLog -Pattern '已把请求（打开 ') '日志确认第二个实例把请求转交给了已有实例'
Assert (Wait-NewLog -Pattern '命令行：在新标签页打开 .*\\beta') '日志确认按“新标签页打开”处理'

Write-Host ''
Write-Host '=== 用例 3：已在运行 + exdir <同一个目录> → 不新开标签页 ==='
Reset-LogMark
Invoke-Cli -Session $session -ArgumentList @($beta)
Assert (Wait-NewLog -Pattern '命令行：已经在 .*\\beta，不新开标签页') '日志确认走的是“已经在同一目录”这条路'
Assert ((Get-TabKey -Session $session) -eq 'exdir-cli,alpha,[beta]') '标签页数量与顺序都没变（没有重复开同一个目录）'

Write-Host ''
Write-Host '=== 用例 4：已在运行 + exdir <文件> → 打开所在目录并选中它 ==='
Reset-LogMark
Invoke-Cli -Session $session -ArgumentList @($targetFile)
Assert (Wait-TabKey -Session $session -Expected 'exdir-cli,alpha,beta,[gamma]') '在文件所在目录（gamma）新开标签页'
Assert (Wait-StatusText -Session $session -Text '选中 1 项（1.00 KB）') '状态栏显示「选中 1 项（1.00 KB）」（文件被选中）'
Assert (Wait-NewLog -Pattern '并选中 .*target\.txt') '日志确认导航后选中了那个文件'

Write-Host ''
Write-Host '=== 用例 5：再执行一次同一个文件 → 标签页不变，仍然选中 ==='
Reset-LogMark
Invoke-Cli -Session $session -ArgumentList @($targetFile)
Assert (Wait-NewLog -Pattern '命令行：已在 .*\\gamma，选中 .*target\.txt') '日志确认是“已在同一目录 → 重新选中文件”'
Assert ((Get-TabKey -Session $session) -eq 'exdir-cli,alpha,beta,[gamma]') '没有新开标签页（已经在同一个目录）'
Assert ((Get-StatusTexts -Session $session) -contains '选中 1 项（1.00 KB）') '文件仍然处于选中状态'

Write-Host ''
Write-Host '=== 用例 6：窗口藏在托盘里 + exdir <目录> → 唤回窗口并处理请求 ==='
Assert ([CliNative]::IsWindowVisible($session.Handle)) '（前提）窗口此刻是可见的'
Invoke-MenuItem -Session $session -MenuName '文件' -ItemName '隐藏到托盘'
Assert (Wait-WindowVisible -Session $session -Visible $false) '窗口已隐藏到托盘'
Reset-LogMark
Invoke-Cli -Session $session -ArgumentList @($alpha)
Assert (Wait-WindowVisible -Session $session -Visible $true) '窗口被唤回并重新可见'
Assert ($session.Proc.MainWindowHandle -eq $session.Handle) '还是原来那只窗口（句柄没变）'
Assert (Wait-NewLog -Pattern '窗口已从托盘唤回') '日志确认走了 ShowWindow 唤回'Assert (Wait-TabKey -Session $session -Expected 'exdir-cli,alpha,beta,gamma,[alpha]') '窗口藏着的时候请求照样处理（新标签页打开 alpha）'

Write-Host ''
Write-Host '=== 用例 7：无参数 + 工作目录是 exe 所在目录（双击 exe / 点任务栏图标）→ 只唤回窗口 ==='
Reset-LogMark
Invoke-Cli -Session $session
Assert (Wait-NewLog -Pattern '没有路径参数，工作目录 .* 是程序/系统目录') '日志确认识别出了“启动器给的工作目录”'
Assert ((Get-TabKey -Session $session) -eq 'exdir-cli,alpha,beta,gamma,[alpha]') '没有新开标签页、浏览位置也没变'
Assert ([CliNative]::IsWindowVisible($session.Handle)) '窗口仍然可见'

Write-Host ''
Write-Host '=== 用例 8：无参数 + 工作目录是终端里的目录 → 打开该目录 ==='
Reset-LogMark
Invoke-Cli -Session $session -WorkingDirectory $workRoot
Assert (Wait-NewLog -Pattern '没有路径参数，打开工作目录') '日志确认按“打开当前工作目录”处理'
Assert (Wait-TabKey -Session $session -Expected 'exdir-cli,alpha,beta,gamma,alpha,[exdir-cli]') '在活动窗格里新开标签页打开工作目录'

Write-Host ''
Write-Host '=== 用例 9：路径不存在 → 不新开标签页，当前标签页显示「无法打开」 ==='
Reset-LogMark
Invoke-Cli -Session $session -ArgumentList @($missingPath)
Assert (Wait-NewLog -Pattern '命令行：无法打开') '日志确认走的是“路径不存在”这条路'
Start-Sleep -Seconds 2
Assert ((Get-TabKey -Session $session) -eq 'exdir-cli,alpha,beta,gamma,alpha,[exdir-cli]') '没有为不存在的路径新开标签页'
$errorText = Find-WindowText -Session $session -Like '无法打开：*'
Write-Host "  错误提示: $errorText"
Assert ($errorText -ne '') '当前标签页显示了「无法打开…」错误'
Assert ($errorText -like "*$([System.IO.Path]::GetFileName($missingPath))*") '错误里带着那个路径'

Write-Host ''
Write-Host '=== 用例 10：带空格的路径（shell 会带上引号）→ 一样能打开 ==='
Reset-LogMark
$spaceDir = Join-Path $workRoot 'with space'
New-Item -ItemType Directory -Path $spaceDir | Out-Null
Invoke-Cli -Session $session -ArgumentList "`"$spaceDir`""
Assert (Wait-NewLog -Pattern '并在新标签页打开 .*with space|命令行：在新标签页打开 .*with space') '带引号的路径被当成一个参数、解析成正确的路径'
Assert (Wait-TabKey -Session $session -Expected 'exdir-cli,alpha,beta,gamma,alpha,exdir-cli,[with space]') '新标签页打开的就是那个带空格的目录'

Stop-Session -Session $session

# ------------------------------------------------------------------ 还原

if ($null -ne $originalSettings) { Set-Content $settingsPath $originalSettings -Encoding utf8 }
if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }

Write-Host ''
if ($failures -eq 0) {
    Write-Host '全部通过'
    exit 0
}

Write-Host "$failures 条断言失败"
exit 1
