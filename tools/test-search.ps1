# 「基于 Everything 的快速搜索」回归脚本（全程 UIA，不需要交互桌面 / 真鼠标）。
#
# 用法:
#   pwsh -NoProfile -File tools\test-search.ps1
#   pwsh -NoProfile -File tools\test-search.ps1 -Exe dist\win-x64\exdir.exe
#
# 十一个用例：
#   1. 导航条上有搜索框（可用、初值空、还没有结果计数）；
#   2. 输入关键字 → 结果替换当前列表（状态栏「N 项」= 命中数、行真的出现、
#      日志里的查询串恰好是 path:"<当前目录>\" <关键字>）；
#   3. 结果行的名称右边标出它在搜索根下的哪个子目录（直接子项不标）；
#   4. 「显示隐藏文件」关着时隐藏项不出现在结果里，打开后立刻重搜并出现在结果里；
#   5. 「整机」开关打开 → 范围变成整个索引（兄弟目录 / 外部目录的文件也进来，日志里没有 path:）；
#   6. 点搜索框上的「×」→ 清空关键字并回到原目录（状态栏变回该目录的项数）；
#   7. 搜索状态下导航到别处（「上一级」）→ 自动退出搜索模式；
#   8. 选中一个位于子目录的结果 → 文件菜单「打开」→ 跳到它所在目录并选中它；
#   9. 打开压缩包（exdir <zip>）→ 那个标签页的搜索框是禁用的（包内搜不了）；
#  10. 用 EXDIR_EVERYTHING_DLL 指向一个不存在的路径启动 → 搜索报「未检测到 Everything」（降级路径）；
#  11. 当前目录不在 Everything 索引里（Everything 只启用了「文件夹索引」时就是这样）→ 查询串仍是当前目录，
#      但提示条会说清“索引里没有这个目录”并给出装 Everything 服务的办法；索引覆盖到的目录不会误报。
#      （夹具是一个**隐藏目录** + Everything 的「排除隐藏文件与文件夹」—— 用例 11 临时改一下它的配置，跑完还原。）
#
# 行是**按里面的文本**找的（不依赖 ListItem 自己的 UIA 名字）：
# 搜索结果的行里除了文件名还有一条“相对目录”的小字，ListItem 的 Name 可能是两者拼起来的。
#
# 前提：本机装了 Everything。目录已经被 Everything 索引时（装了 Everything 服务、按卷索引）本脚本不碰它的配置；
# 没被索引时才**临时**把测试目录加进「文件夹索引」，跑完还原 Everything.ini 并重启它（没装 Everything 时整个脚本 SKIP）。
# 跑完还会还原 config.json 的原始内容并删掉测试目录。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SearchNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}
'@

[void][SearchNative]::SetProcessDpiAwarenessContext([IntPtr][SearchNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }
$exeDir = Split-Path $exePath

# 随包分发的 Everything SDK 客户端必须在 exe 旁边，否则测的就不是真东西
if (-not (Test-Path (Join-Path $exeDir 'Everything64.dll'))) {
    throw "exe 旁边没有 Everything64.dll：$exeDir（先 dotnet build / tools\publish.ps1）"
}

# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

function Skip {
    param([string]$Message)
    Write-Host "SKIP $Message"
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

# ------------------------------------------------------------------ Everything：找个 exe 出来

function Find-EverythingExe {
    $candidates = @()

    # 1) 注册表里的安装位置
    foreach ($key in @('HKLM:\SOFTWARE\Everything', 'HKCU:\Software\Everything', 'HKLM:\SOFTWARE\WOW6432Node\Everything')) {
        if (Test-Path $key) {
            $install = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
            if ($install) { $candidates += (Join-Path $install 'Everything.exe') }
        }
    }

    # 2) 常见安装目录
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $base) { continue }
        $candidates += (Join-Path $base 'Everything\Everything.exe')
        $candidates += (Join-Path $base 'Everything 1.5a\Everything.exe')
    }

    # 3) scoop 的 apps 目录 —— 不能用 shims 里那个转发器：
    #    它的目录里没有 Everything.ini，Everything 读的是**版本目录**里的那份配置
    $scoopApps = Join-Path $env:USERPROFILE 'scoop\apps\everything'
    if (Test-Path $scoopApps) {
        $current = Join-Path $scoopApps 'current\Everything.exe'
        if (Test-Path $current) { $candidates += $current }
        foreach ($exe in (Get-ChildItem $scoopApps -Filter 'Everything.exe' -Recurse -ErrorAction SilentlyContinue)) {
            $candidates += $exe.FullName
        }
    }

    # 4) PATH（winget 装的通常在这里；shims 目录跳过）
    foreach ($dir in ($env:PATH -split ';')) {
        if (-not $dir) { continue }
        $candidates += (Join-Path ($dir.Trim('"')) 'Everything.exe')
    }

    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path $candidate)) { continue }
        if ($candidate -match '\\shims\\') { continue }
        return (Get-Item $candidate).FullName
    }

    return $null
}

function Stop-Everything {
    Get-Process -Name 'Everything' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Seconds 2
}

function Start-Everything {
    param([string]$EverythingExe)
    Start-Process -FilePath $EverythingExe -ArgumentList '-startup', '-minimized'
}

$everythingExe = Find-EverythingExe
if (-not $everythingExe) {
    Skip '本机找不到 Everything.exe（先装一份 Everything：https://www.voidtools.com/）'
    Skip '没有 Everything 时无法验证搜索链路，整个脚本跳过'
    exit 0
}

if ($everythingExe -match '\\shims\\') {
    throw "解析出来的 Everything.exe 是转发器而不是本体：$everythingExe"
}

# Everything 的配置就放在 exe 旁边；安装目录不可写时会落到 %APPDATA%\Everything
$everythingIni = Join-Path (Split-Path $everythingExe) 'Everything.ini'
if (-not (Test-Path $everythingIni)) {
    $roamingIni = Join-Path $env:APPDATA 'Everything\Everything.ini'
    if (Test-Path $roamingIni) { $everythingIni = $roamingIni }
}

Write-Host "Everything：$everythingExe"
Write-Host "Everything.ini：$everythingIni"

# ------------------------------------------------------------------ 测试目录与夹具

$rootDir = Join-Path $env:TEMP 'exdir-search'
$scopeDir = Join-Path $rootDir 'scope'
$siblingDir = Join-Path $rootDir 'scope2'
$outsideDir = Join-Path $rootDir 'outside'
$needle = 'nid' + [Guid]::NewGuid().ToString('N').Substring(0, 8)

$rootFile = Join-Path $scopeDir "$needle-root.txt"
$subFile = Join-Path $scopeDir "sub\$needle-sub.txt"
$deepFile = Join-Path $scopeDir "sub\deep\$needle-deep.bin"
$hiddenFile = Join-Path $scopeDir "$needle-hidden.txt"
$siblingFile = Join-Path $siblingDir "$needle-scope2.txt"
$outsideFile = Join-Path $outsideDir "$needle-outside.txt"
$zipPath = Join-Path $outsideDir 'sample.zip'

if (Test-Path $rootDir) { Remove-Item $rootDir -Recurse -Force }
foreach ($dir in @($scopeDir, (Join-Path $scopeDir 'sub\deep'), $siblingDir, $outsideDir)) {
    New-Item -ItemType Directory -Path $dir | Out-Null
}

[System.IO.File]::WriteAllText($rootFile, 'root')
[System.IO.File]::WriteAllText($subFile, 'sub')
[System.IO.File]::WriteAllBytes($deepFile, [byte[]]::new(1234))
[System.IO.File]::WriteAllText($hiddenFile, 'hidden')
[System.IO.File]::WriteAllText($siblingFile, 'sibling')
[System.IO.File]::WriteAllText($outsideFile, 'outside')
(Get-Item $hiddenFile).Attributes = [System.IO.FileAttributes]::Hidden

# 压缩包（用例 9）：让 exdir 以“进包”的方式打开它 —— 那个标签页的搜索框必须是禁用的
$zipStage = Join-Path $env:TEMP ("exdir-search-zip-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $zipStage | Out-Null
[System.IO.File]::WriteAllText((Join-Path $zipStage 'inside.txt'), 'inside')
Compress-Archive -Path (Join-Path $zipStage '*.txt') -DestinationPath $zipPath -Force
Remove-Item $zipStage -Recurse -Force

# ------------------------------------------------------------------ Everything 的索引：这个目录被索引了就行
#
# 两种来源都算数：
#   * 已经按卷索引了（装了 Everything 服务 + 在选项里勾了磁盘）—— 这时**什么都不用动**；
#   * 没有 —— 才临时把测试目录加进「文件夹索引」并重启 Everything（跑完还原 ini）。
#
# ⚠ 先问一句再改是有必要的：Everything 的文件夹索引与卷索引**不去重**，给一个已经在卷索引里的
#   目录再加一条文件夹索引会让同一批文件在结果里出现两次（count 断言全崩）；而且按卷索引的机器上
#   重启 Everything 会让刚起来的这一会儿查不到新文件（扫描还在排队）。

$script:everythingExe = $everythingExe
$script:everythingIni = $everythingIni
$script:originalIni = if (Test-Path $everythingIni) { Get-Content $everythingIni -Raw } else { $null }
$script:everythingWasRunning = @(Get-Process -Name 'Everything' -ErrorAction SilentlyContinue).Count -gt 0
$script:everythingRestored = $false
$script:iniTouched = $false

function Restore-Everything {
    if ($script:everythingRestored) { return }
    $script:everythingRestored = $true

    # 没动过它的配置就别去杀它：Everything 可能正在忙着索引整块磁盘
    if (-not $script:iniTouched) { return }

    Stop-Everything
    if ($null -ne $script:originalIni) {
        [System.IO.File]::WriteAllText($script:everythingIni, $script:originalIni)
    }
    if ($script:everythingWasRunning) { Start-Everything -EverythingExe $script:everythingExe }
}

# 用冒烟工程当预检：它自己会轮询“Everything 索引到了没”，并断言整条服务链路
function Invoke-Smoke {
    param([string]$Root)

    Push-Location (Join-Path $PSScriptRoot '..')
    try {
        & dotnet run -c Debug --project tools\everything-smoke -- --root $Root 2>&1 |
            ForEach-Object { Write-Host "  $_" }
        return $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

$session = $null
try {
    Write-Host ''
    Write-Host '=== 预检：跑 tools\everything-smoke（先看这个目录是不是已经被 Everything 索引了）==='
    $smokeCode = Invoke-Smoke -Root $rootDir

    if ($smokeCode -ne 0) {
        Write-Host ''
        Write-Host '=== 准备：还没被索引 → 临时加进 Everything 的「文件夹索引」并重启 Everything ==='
        $script:iniTouched = $true
        Stop-Everything
        if ($null -ne $script:originalIni) {
            $lines = Get-Content $everythingIni | ForEach-Object {
                if ($_ -match '^folders=') { "folders=$rootDir" }
                elseif ($_ -match '^folder_monitor_changes=') { 'folder_monitor_changes=1' }
                elseif ($_ -match '^folder_update_types=') { 'folder_update_types=0' }
                elseif ($_ -match '^folder_update_interval_types=') { 'folder_update_interval_types=0' }
                else { $_ }
            }
            Set-Content -Path $everythingIni -Value $lines -Encoding ascii
        }
        else {
            # 本机 Everything 还没写过 ini：给一份最小配置，只开文件夹索引
            [System.IO.File]::WriteAllText($everythingIni, "folders=$rootDir`r`n")
        }

        Start-Everything -EverythingExe $everythingExe

        Write-Host ''
        Write-Host '=== 预检（改完 Everything 的配置之后再跑一次）==='
        $smokeCode = Invoke-Smoke -Root $rootDir
    }
    else {
        Write-Host '  → 这个目录已经被 Everything 索引了（按卷索引或本来就在文件夹索引里），不动它的配置'
    }

    if ($smokeCode -ne 0) {
        throw "服务级预检没通过（退出码 $smokeCode）：先看 tools\everything-smoke 的输出（目录没被索引时重跑一次，等 Everything 把盘扫完）"
    }

    # ---------------------------------------------------------------- exdir 配置

    $overrides = [ordered]@{
        PrimaryTabs      = @($scopeDir)
        PrimaryActiveTab = 0
        IsDualPane       = $false
        IsSidebarVisible = $false
        ShowHiddenFiles  = $false
        ShowExtensions   = $true
        WindowMaximized  = $false
    }

    $json = if ($null -ne $originalSettings) { $originalSettings | ConvertFrom-Json } else { New-Object psobject }
    foreach ($key in $overrides.Keys) {
        if ($json.PSObject.Properties.Name -contains $key) { $json.$key = $overrides[$key] }
        else { $json | Add-Member -NotePropertyName $key -NotePropertyValue $overrides[$key] }
    }
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

    # ---------------------------------------------------------------- UIA 小工具

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

    # 可见的行（UIA 只暴露已生成的容器）
    function Get-Rows {
        param($Session)
        return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
            Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
    }

    # 行里的 TextBlock 文本（名称 + 搜索结果右侧那条相对目录）
    function Get-RowTexts {
        param($Row)
        if ($null -eq $Row) { return @() }
        return @(Find-ByType -From $Row -ControlType ([System.Windows.Automation.ControlType]::Text) |
            ForEach-Object { $_.Current.Name })
    }

    # 按“行里有这段文本”找行 —— 不依赖 ListItem 自己的 UIA 名字
    #（搜索结果的行里除了文件名还有相对目录的小字，Name 可能是拼起来的）
    function Find-Row {
        param($Session, [string]$Text)
        foreach ($row in (Get-Rows -Session $Session)) {
            if ((Get-RowTexts -Row $row) -contains $Text) { return $row }
        }
        return $null
    }

    function Wait-Row {
        param($Session, [string]$Text, [int]$TimeoutSeconds = 25)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            $row = Find-Row -Session $Session -Text $Text
            if ($null -ne $row) { return $row }
            Start-Sleep -Milliseconds 250
        }

        $all = @(Get-Rows -Session $Session | ForEach-Object { (Get-RowTexts -Row $_) -join '/' })
        Write-Host ("  实际行: {0}" -f ($all -join ' | '))
        return $null
    }

    function Assert-NoRow {
        param($Session, [string]$Text, [string]$Message)
        Start-Sleep -Milliseconds 700
        Assert ($null -eq (Find-Row -Session $Session -Text $Text)) $Message
    }

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

    # 搜索框旁边那个计数（容器给名字、叶子 TextBlock 不给 → 叶子自己的名字就是文本）
    function Get-SearchStatus {
        param($Session, [int]$TimeoutSeconds = 10)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            foreach ($el in (Find-Elements -From $Session.Root -Name '搜索状态')) {
                if ($el.Current.IsOffscreen -or $el.Current.BoundingRectangle.Width -le 0) { continue }
                foreach ($txt in (Find-ByType -From $el -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                    if (-not [string]::IsNullOrEmpty($txt.Current.Name)) { return $txt.Current.Name }
                }
            }

            # 计数条消失时要立刻返回空串，所以超时循环只在“期望非空”时才有意义
            if ($TimeoutSeconds -le 1) { return '' }
            Start-Sleep -Milliseconds 250
        }

        return ''
    }

    function Wait-SearchStatus {
        param($Session, [string]$Expected, [int]$TimeoutSeconds = 25)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if ((Get-SearchStatus -Session $Session -TimeoutSeconds 1) -eq $Expected) { return $true }
            Start-Sleep -Milliseconds 250
        }

        Write-Host ("  实际计数: 「{0}」（期望「{1}」）" -f (Get-SearchStatus -Session $Session -TimeoutSeconds 1), $Expected)
        return $false
    }

    function Wait-StatusText {
        param($Session, [string]$Text, [int]$TimeoutSeconds = 25)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if ((Get-StatusTexts -Session $Session) -contains $Text) { return $true }
            Start-Sleep -Milliseconds 300
        }

        Write-Host ("  实际状态栏: {0}" -f ((Get-StatusTexts -Session $Session) -join ' | '))
        return $false
    }

    function Wait-StatusMatch {
        param($Session, [string]$Pattern, [int]$TimeoutSeconds = 25)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if (((Get-StatusTexts -Session $Session) -join '|') -match $Pattern) { return $true }
            Start-Sleep -Milliseconds 300
        }

        Write-Host ("  实际状态栏: {0}" -f ((Get-StatusTexts -Session $Session) -join ' | '))
        return $false
    }

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

    function Wait-TabKey {
        param($Session, [string]$Pattern, [int]$TimeoutSeconds = 25)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            $actual = Get-TabKey -Session $Session
            if ($actual -match $Pattern) { return $true }
            Start-Sleep -Milliseconds 300
        }

        Write-Host ("  实际标签: {0}（期望匹配 {1}）" -f (Get-TabKey -Session $Session), $Pattern)
        return $false
    }

    function Get-SearchBox {
        param($Session)
        return Find-VisibleFirst -From $Session.Root -Names @('搜索') -ControlType ([System.Windows.Automation.ControlType]::Edit)
    }

    # 往搜索框里写字（等价于用户一个字符一个字符敲；VM 那边去抖 180ms 后自动搜）
    function Set-SearchText {
        param($Session, [string]$Text)
        $box = Get-SearchBox -Session $Session
        if ($null -eq $box) { throw '导航条上找不到搜索框（AutomationProperties.Name="搜索"）' }
        $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    }

    function Get-SearchText {
        param($Session)
        $box = Get-SearchBox -Session $Session
        if ($null -eq $box) { return '' }
        return $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    }

    function Wait-SearchText {
        param($Session, [string]$Expected, [int]$TimeoutSeconds = 15)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if ((Get-SearchText -Session $Session) -eq $Expected) { return $true }
            Start-Sleep -Milliseconds 250
        }

        Write-Host ("  实际搜索框: 「{0}」（期望「{1}」）" -f (Get-SearchText -Session $Session), $Expected)
        return $false
    }

    function Invoke-Button {
        param($Session, [string]$Name)
        $el = Find-VisibleFirst -From $Session.Root -Names @($Name) -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($null -eq $el) { throw "找不到按钮「$Name」" }
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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

        # 上一拍刚做过导航 / 切标签时菜单栏会有一瞬间不在（UIA 里 IsOffscreen）—— 重试几次，别直接断言失败
        $menuBarItem = $null
        for ($i = 0; $i -lt 20 -and $null -eq $menuBarItem; $i++) {
            $menuBarItem = Find-VisibleFirst -From $Session.Root -Names @($MenuName) -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
            if ($null -eq $menuBarItem) { Start-Sleep -Milliseconds 250 }
        }
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

    # ---------------------------------------------------------------- 会话

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

    # ================================================================== 用例

    $session = Start-Session

    Write-Host ''
    Write-Host '=== 用例 1：导航条上有可用的搜索框 ==='
    $box = Get-SearchBox -Session $session
    Assert ($null -ne $box) '找到搜索框（AutomationProperties.Name="搜索"）'
    Assert ($box.Current.IsEnabled) '搜索框可用（当前在真实目录里）'
    Assert ((Get-SearchText -Session $session) -eq '') '搜索框初值为空'
    Assert ((Get-SearchStatus -Session $session -TimeoutSeconds 1) -eq '') '还没搜索时不显示结果计数'
    Assert ($null -ne (Find-VisibleFirst -From $session.Root -Names @('搜索整机') -ControlType ([System.Windows.Automation.ControlType]::Button))) '找得到「整机」范围开关'
    Assert (Wait-StatusText -Session $session -Text '2 项') '起始目录里 2 项（needle-root.txt + sub；隐藏文件关着）'

    Write-Host ''
    Write-Host '=== 用例 2：输入关键字 → 结果替换当前列表 ==='
    Reset-LogMark
    Set-SearchText -Session $session -Text $needle
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-root.txt")) '结果里出现 scope 下的 needle-root.txt'
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-sub.txt")) '结果里出现子目录里的 needle-sub.txt（递归生效）'
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-deep.bin")) '结果里出现 sub\deep 里的 needle-deep.bin'    Assert (Wait-StatusText -Session $session -Text '3 项') '状态栏「3 项」= 命中数（不是目录里的 2 项）'
    Assert (Wait-SearchStatus -Session $session -Expected '3 项') '搜索框旁边显示「3 项」'

    # 日志里的查询串就是“范围 = 当前目录”的全部实现，直接用真值断言
    # （注意：PowerShell 双引号串里不能写 \" —— 转义符是反引号，这里全用单引号拼接）
    $expectedQuery = 'path:"' + $scopeDir + '\" ' + $needle
    Assert (Wait-NewLog -Pattern ([regex]::Escape($expectedQuery))) ('日志里的查询串是 path:"<当前目录>\" + 关键字（' + $expectedQuery + '）')

    Write-Host ''
    Write-Host '=== 用例 3：结果行的名称右边标出它在搜索根下的哪个子目录 ==='
    $rootRow = Wait-Row -Session $session -Text "$needle-root.txt"
    $subRow = Wait-Row -Session $session -Text "$needle-sub.txt"
    $deepRow = Wait-Row -Session $session -Text "$needle-deep.bin"
    $rootRowTexts = Get-RowTexts -Row $rootRow
    $subRowTexts = Get-RowTexts -Row $subRow
    $deepRowTexts = Get-RowTexts -Row $deepRow
    Write-Host ("  needle-root.txt: {0}" -f ($rootRowTexts -join ' / '))
    Write-Host ("  needle-sub.txt: {0}" -f ($subRowTexts -join ' / '))
    Write-Host ("  needle-deep.bin: {0}" -f ($deepRowTexts -join ' / '))
    Assert ($rootRowTexts -notcontains 'sub') '直接位于搜索根里的结果不标目录'
    Assert ($subRowTexts -contains 'sub') 'sub 里的结果标出「sub」'
    Assert ($deepRowTexts -contains 'sub\deep') 'sub\deep 里的结果标出「sub\deep」'

    Write-Host ''
    Write-Host '=== 用例 4：「显示隐藏文件」当场作用到搜索结果 ==='
    Assert-NoRow -Session $session -Text "$needle-hidden.txt" '「显示隐藏文件」关着时隐藏项不出现在结果里'
    Reset-LogMark
    Invoke-MenuItem -Session $session -MenuName '查看' -ItemName '显示隐藏文件'
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-hidden.txt")) '打开「显示隐藏文件」后隐藏项立刻出现在结果里'
    Assert (Wait-StatusText -Session $session -Text '4 项') '状态栏跟着变成「4 项」'
    Assert (Wait-NewLog -Pattern ([regex]::Escape('Everything 搜索：' + $expectedQuery))) '开关变化触发了重搜（日志里有新的查询）'
    Invoke-MenuItem -Session $session -MenuName '查看' -ItemName '显示隐藏文件'
    Assert-NoRow -Session $session -Text "$needle-hidden.txt" '再关掉「显示隐藏文件」，隐藏项又消失'

    Write-Host ''
    Write-Host '=== 用例 5：「整机」开关把范围放到整个索引 ==='
    $chip = Find-VisibleFirst -From $session.Root -Names @('搜索整机') -ControlType ([System.Windows.Automation.ControlType]::Button)
    Reset-LogMark
    $chip.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-scope2.txt")) '前辍相同的兄弟目录 scope2 里的文件被搜到（整机范围）'
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-outside.txt")) 'scope 之外（outside 目录）的文件被搜到'
    Assert (Wait-StatusText -Session $session -Text '5 项') '整机范围 5 项（scope 3 + scope2 1 + outside 1）'
    Assert (Wait-NewLog -Pattern ('Everything 搜索：' + [regex]::Escape($needle) + ' →')) '日志里的查询串变成没有 path: 限定（整机）'
    Assert ($chip.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) '「整机」开关处于勾选状态'

    Write-Host ''
    Write-Host '=== 用例 6：点「×」清空 → 回到原目录 ==='
    $chip.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 1200
    Invoke-Button -Session $session -Name '清除搜索'
    Assert (Wait-SearchText -Session $session -Expected '') '搜索框被清空'
    Assert (Wait-StatusText -Session $session -Text '2 项') '回到原目录（scope 的 2 项）'
    Assert ($null -ne (Wait-Row -Session $session -Text 'sub')) '列表里又看得见目录 sub（搜索结果被换掉了）'
    Assert ((Get-SearchStatus -Session $session -TimeoutSeconds 1) -eq '') '结果计数消失'

    Write-Host ''
    Write-Host '=== 用例 7：搜索状态下导航到别处 → 自动退出搜索模式 ==='
    Set-SearchText -Session $session -Text $needle
    Assert (Wait-SearchStatus -Session $session -Expected '3 项') '（前提）又搜出 3 项'
    Invoke-Button -Session $session -Name '上一级'
    Assert (Wait-SearchText -Session $session -Expected '') '导航后搜索框自动清空'
    Assert ($null -ne (Wait-Row -Session $session -Text 'outside')) '列表变回上一级目录的内容（scope / scope2 / outside）'
    Assert (Wait-StatusText -Session $session -Text '3 项') '上一级目录里 3 项'
    Assert ((Get-SearchStatus -Session $session -TimeoutSeconds 1) -eq '') '搜索计数消失（已经退出搜索模式）'

    Write-Host ''
    Write-Host '=== 用例 8：选中结果 → 文件菜单「打开」→ 跳到它所在目录并选中它 ==='
    # 用命令行再开一个标签页回到 scope（不依赖键盘 / 真鼠标）
    $second = Start-Process -FilePath $exePath -WorkingDirectory $exeDir -ArgumentList @($scopeDir) -PassThru
    [void]$second.WaitForExit(20000)
    Assert (Wait-TabKey -Session $session -Pattern '\[scope\]') '命令行在活动窗格里新开了一个 scope 标签页'
    Assert (Wait-StatusText -Session $session -Text '2 项') '（前提）活动标签页是 scope'

    Set-SearchText -Session $session -Text $needle
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-deep.bin")) '搜出 needle-deep.bin'
    $deepRow = Wait-Row -Session $session -Text "$needle-deep.bin"
    $deepRow.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-MenuItem -Session $session -MenuName '文件' -ItemName '打开'
    Assert (Wait-StatusMatch -Session $session -Pattern '选中 1 项') '跳到所在目录并选中了它（状态栏「选中 1 项」）'
    Assert (Wait-SearchText -Session $session -Expected '') '跳转后退出搜索模式（搜索框清空）'
    Assert (Wait-TabKey -Session $session -Pattern '\[deep\]') '标签页标题变成 deep（= needle-deep.bin 所在的目录）'
    Assert ($null -ne (Wait-Row -Session $session -Text "$needle-deep.bin")) '目标目录里那一行就在眼前'

    Write-Host ''
    Write-Host '=== 用例 9：打开压缩包 → 那个标签页的搜索框是禁用的 ==='
    Stop-Session -Session $session
    $session = Start-Session -ArgumentList @($zipPath)
    Assert (Wait-TabKey -Session $session -Pattern '\[sample\.zip\]') '新标签页打开了压缩包'
    $box = Get-SearchBox -Session $session
    Assert ($null -ne $box) '压缩包标签页里也有搜索框（只是禁用）'
    Assert (-not $box.Current.IsEnabled) '压缩包内的搜索框是禁用的（包内不在 Everything 索引里）'
    Assert ((Get-SearchStatus -Session $session -TimeoutSeconds 1) -eq '') '压缩包内不显示搜索结果计数'

    Write-Host ''
    Write-Host '=== 用例 10：EXDIR_EVERYTHING_DLL 指向不存在的路径 → 「未检测到 Everything」 ==='
    Stop-Session -Session $session
    $session = $null
    $env:EXDIR_EVERYTHING_DLL = Join-Path $env:TEMP 'no-such-everything64.dll'
    $session = Start-Session
    Set-SearchText -Session $session -Text $needle
    $message = ''
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and $message -eq '') {
        $message = Find-WindowText -Session $session -Like '未检测到 Everything*'
        Start-Sleep -Milliseconds 300
    }
    Write-Host "  提示: $message"
    Assert ($message -ne '') '搜索时提示「未检测到 Everything」（找不到 DLL 的降级路径）'
    Assert (Wait-StatusText -Session $session -Text '2 项') '降级时列表保持当前目录的内容（没有被清空）'
    Assert ((Get-SearchStatus -Session $session -TimeoutSeconds 1) -eq '') '降级时不显示结果计数'

    Write-Host ''
    Write-Host '=== 用例 11：当前目录不在 Everything 索引里 → 提示条说明原因（而不是只显示「没有匹配项」） ==='
    Stop-Session -Session $session
    $session = $null

    # 用例 10 把 EXDIR_EVERYTHING_DLL 指向了不存在的 DLL（环境变量会被子进程继承），别让它漏到这一轮
    $env:EXDIR_EVERYTHING_DLL = $null

    # --- 11a) 反面先做：Everything 还是机器上原样的配置，scope 这个目录它索引得到 ——
    #           关键字没匹配上时**不该**弹“索引没覆盖”那句话（不然这个提示就成噪声了）。
    $json = if ($null -ne $originalSettings) { $originalSettings | ConvertFrom-Json } else { New-Object psobject }
    foreach ($key in $overrides.Keys) {
        if ($json.PSObject.Properties.Name -contains $key) { $json.$key = $overrides[$key] }
        else { $json | Add-Member -NotePropertyName $key -NotePropertyValue $overrides[$key] }
    }
    $json.PrimaryTabs = @($scopeDir)
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

    Reset-LogMark
    $session = Start-Session
    Assert (Wait-TabKey -Session $session -Pattern '\[scope\]') '（前提）标签页开在 scope 上（Everything 索引覆盖得到它）'
    Assert (Wait-StatusText -Session $session -Text '2 项') '（前提）scope 里 2 项'

    Set-SearchText -Session $session -Text "definitelynotpresent$needle"
    Assert (Wait-SearchStatus -Session $session -Expected '0 项') 'scope 里也没有匹配（但索引覆盖到了它）'
    Assert (Wait-NewLog -Pattern 'Everything 索引覆盖检查') '搜索没命中时会去查一下“索引到底覆没覆盖这个目录”'
    Start-Sleep -Seconds 3
    Assert ((Get-NewLogText) -notmatch 'Everything 的索引没有覆盖当前目录') '索引覆盖到的目录不误报「索引没覆盖」'
    Assert ((Find-WindowText -Session $session -Like 'Everything 的索引里没有*') -eq '') '索引覆盖到的目录不弹那条提示'

    Stop-Session -Session $session
    $session = $null

    # --- 11b) 正面：把 Everything 配成“排除隐藏文件与文件夹”，
    #           再用一个**隐藏目录**当“索引覆盖不到的目录”：给 Everything 打开「排除隐藏文件与文件夹」之后，
    # 它不索引隐藏目录里的东西（而 exdir 这边把「显示隐藏文件」打开，列表里照样看得到它）——
    # 于是「当前目录」范围的查询必然 0 项，正是用户遇到的那种情况（Everything 只装了/只启用了
    # 「文件夹索引」时，磁盘上大部分目录都是这个样子）。
    #
    # 为什么不用 subst 造一个“索引之外的盘符”：实测 Everything 会把 subst 盘符解析回真实卷，
    # 用 X:\ 查照样能命中（path:"X:\" → 1 项）。
    $unindexedDir = Join-Path $env:TEMP 'exdir-search-unindexed'
    if (Test-Path $unindexedDir) { Remove-Item $unindexedDir -Recurse -Force }
    New-Item -ItemType Directory -Path $unindexedDir | Out-Null
    (Get-Item $unindexedDir).Attributes = [System.IO.FileAttributes]::Hidden
    [System.IO.File]::WriteAllText((Join-Path $unindexedDir "$needle-unindexed.txt"), 'unindexed')

    Write-Host '  （改一下 Everything 的配置：排除隐藏文件与文件夹）'
    $script:iniTouched = $true
    Stop-Everything
    $lines = Get-Content $everythingIni | ForEach-Object {
        if ($_ -match '^exclude_list_enabled=') { 'exclude_list_enabled=1' }
        elseif ($_ -match '^exclude_hidden_files_and_folders=') { 'exclude_hidden_files_and_folders=1' }
        else { $_ }
    }
    Set-Content -Path $everythingIni -Value $lines -Encoding ascii
    Start-Everything -EverythingExe $everythingExe

    $json = if ($null -ne $originalSettings) { $originalSettings | ConvertFrom-Json } else { New-Object psobject }
    foreach ($key in $overrides.Keys) {
        if ($json.PSObject.Properties.Name -contains $key) { $json.$key = $overrides[$key] }
        else { $json | Add-Member -NotePropertyName $key -NotePropertyValue $overrides[$key] }
    }
    $json.PrimaryTabs = @($unindexedDir)
    $json.ShowHiddenFiles = $true        # 列表里要看得到那个隐藏目录里的文件（索引里才没有）
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

    Reset-LogMark
    $session = Start-Session
    Assert (Wait-TabKey -Session $session -Pattern '\[exdir-search-unindexed\]') '（前提）标签页开在那个隐藏目录里'
    Assert (Wait-StatusText -Session $session -Text '1 项') '（前提）这个目录里有 1 个文件（磁盘上有内容，只是索引里没有）'

    Set-SearchText -Session $session -Text $needle
    Assert (Wait-SearchStatus -Session $session -Expected '0 项') '当前目录范围 0 项（Everything 的索引里没有这个目录）'
    Assert ((Find-WindowText -Session $session -Like '没有匹配项*') -ne '') '列表显示「没有匹配项」'

    # 查询串本身仍是“当前目录”：范围没跑偏，是索引没覆盖它
    $unindexedQuery = 'path:"' + $unindexedDir + '\" ' + $needle
    Assert (Wait-NewLog -Pattern ([regex]::Escape($unindexedQuery))) ('日志里的查询串仍是当前目录（' + $unindexedQuery + '）')
    Assert (Wait-NewLog -Pattern 'Everything 的索引没有覆盖当前目录') '日志里记下了「索引没覆盖当前目录」'

    $hint = ''
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and $hint -eq '') {
        $hint = Find-WindowText -Session $session -Like 'Everything 的索引里没有*'
        Start-Sleep -Milliseconds 300
    }
    Write-Host "  提示: $hint"
    Assert ($hint -ne '') '提示条说明原因（Everything 的索引里没有这个目录）'
    Assert ($hint -like '*install-service*') '提示里给出了可操作的办法（以管理员身份装一次 Everything 服务）'
}
finally {
    $env:EXDIR_EVERYTHING_DLL = $null
    if ($null -ne $session) { Stop-Session -Session $session }
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    Restore-Everything

    if ($null -ne $originalSettings) { Set-Content $settingsPath $originalSettings -Encoding utf8 }
    if (Test-Path $rootDir) { Remove-Item $rootDir -Recurse -Force -ErrorAction SilentlyContinue }
    $unindexedLeftover = Join-Path $env:TEMP 'exdir-search-unindexed'
    if (Test-Path $unindexedLeftover) { Remove-Item $unindexedLeftover -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host '全部通过'
    exit 0
}

Write-Host "$failures 条断言失败"
exit 1
