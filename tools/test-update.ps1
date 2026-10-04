# 在线更新（GitHub Release）的界面回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-update.ps1
#   pwsh -NoProfile -File tools\test-update.ps1 -Source dist\win-x64
#
# 全程 UIA（不需要交互桌面、不需要真鼠标）：**不会**真的访问 GitHub —— 本机的 mock feed
# （tools\update-test-server，纯 node）冒充「最新 Release」接口；而“安装目录”是
# dist 的一份临时副本（%TEMP%\exdir-update-ui\app），所以**动不到开发机上真正的那份 exdir**。
#
# 用例:
#   1. 启动时的后台检查（设置「启动 → 启动时自动检查更新」）发现新版本 →
#      主窗口顶部弹出「发现新版本」提示条，里面的文字带远端版本号；
#   2. 提示条上的「立即更新」→ 打开更新窗口，窗口里显示当前 / 最新版本与发行说明；
#   3. 窗口里的「立即更新」→ 下载（本机 mock，几十 MB 也很快）→「重启并完成更新」按钮出现；
#   4. 点它 → exdir 退出 → 替换脚本 robocopy 覆盖安装目录 → 自动重启：
#      断言安装目录里真的多出更新包里的标记文件、apply.log 里有 robocopy 与重启记录；
#   5. 反面：feed 的版本 = 当前版本 → 后台检查什么都不做（日志里“不比当前新”、界面上没有提示条）；
#   6. 反面：feed 给的 SHA256 与实际不符 → 下载后校验失败，窗口里显示「校验失败」、不给重启按钮；
#   7. 设置里关掉「启动时自动检查更新」→ 启动时根本不联网；「帮助 → 检查更新…」仍能手动查到并开窗；
#   8. 手动检查但没有新版本 → 弹一个「检查更新」回执对话框（里面写着已是最新版本）。
#
# 收尾：杀掉 exdir 与 mock server、删掉临时目录、还原环境变量（XDG_* / EXDIR_UPDATE_FEED）。

param(
    [string]$Source = "$PSScriptRoot\..\dist\win-x64"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceDir = [System.IO.Path]::GetFullPath($Source)

if (-not (Test-Path (Join-Path $sourceDir 'exdir.exe'))) {
    throw "找不到发布产物：$sourceDir\exdir.exe"
}
if (-not (Test-Path (Join-Path $sourceDir 'build-info.txt'))) {
    # 自动替换要求“当前目录看起来是发布版安装目录”（Services/UpdateService.ProbeSelfUpdate）
    throw "这份产物没有 build-info.txt，不能测自动替换：先跑 tools\publish.ps1，或用 -Source 指向 dist\win-x64"
}

$serverScript = Join-Path $repo 'tools\update-test-server\server.js'
if (-not (Test-Path $serverScript)) { throw "找不到 mock feed：$serverScript" }

# ------------------------------------------------------------------ 临时工作区

$workRoot = Join-Path $env:TEMP 'exdir-update-ui'
if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }

$appDir = Join-Path $workRoot 'app'          # “安装目录”（被测的那一份）
$payloadDir = Join-Path $workRoot 'payload'  # 新版本的内容（多一个标记文件，用来证明真的被换过）
$zipPath = Join-Path $workRoot 'update.zip'
$configRoot = Join-Path $workRoot 'cfg'
$notesPath = Join-Path $workRoot 'notes.md'
Write-Host '=== 准备：安装目录 / 更新包 / 配置文件 ==='
New-Item -ItemType Directory -Force -Path $appDir, $payloadDir, (Join-Path $configRoot 'exdir') | Out-Null
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $appDir -Recurse -Force
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $payloadDir -Recurse -Force

# 这个标记文件只存在于“新版本”里：安装目录里出现它就说明替换真的发生了
[System.IO.File]::WriteAllText((Join-Path $payloadDir 'update-applied.txt'), "replaced by test-update`n")

Set-Content -Path $notesPath -Value "### 测试用发行说明（mock feed）`n`n- 这条说明来自 tools/update-test-server`n" -Encoding utf8
Compress-Archive -Path (Join-Path $payloadDir '*') -DestinationPath $zipPath -Force
$zipSizeMB = (Get-Item $zipPath).Length / 1MB
Write-Host ("  安装目录={0}`n  更新包={1}（{2:N1} MB）" -f $appDir, $zipPath, $zipSizeMB)

$currentVersion = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Join-Path $appDir 'exdir.exe')).ProductVersion -split '\+')[0]
$remoteTag = 'v99.0.20991231'

$settingsPath = Join-Path $configRoot 'exdir\config.json'
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$applyLog = Join-Path $env:LOCALAPPDATA 'exdir\update\apply.log'

$originalXdgConfig = $env:XDG_CONFIG_HOME
$originalXdgData = $env:XDG_DATA_HOME
$originalFeed = $env:EXDIR_UPDATE_FEED
$env:XDG_CONFIG_HOME = $configRoot
$env:XDG_DATA_HOME = $configRoot

function Set-Config {
    # CheckOnStartup = 是否开「启动 → 启动时自动检查更新」：用例 7 / 8 要把它关掉，
    # 那样才能证明确实是「帮助 → 检查更新…」那条手动路径在干活
    param([bool]$CheckOnStartup = $true)
    $cfg = [ordered]@{
        SchemaVersion         = 12
        CheckUpdatesOnStartup = $CheckOnStartup
        IsSidebarVisible      = $true
        IsDualPane            = $false
        ShowHiddenFiles       = $false
        ShowExtensions        = $true
        FoldersFirst          = $true
        RowHeight             = 28
        ColumnWidths          = @()
        ColumnAutoFit         = $true
        PinnedFoldersInitialized = $true
        PinnedFolders         = @()
        PrimaryTabs           = @($appDir)
        PrimaryActiveTab      = 0
        SecondaryTabs         = @()
    }
    $cfg | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding utf8
}

Set-Config

# ------------------------------------------------------------------ 断言 / 日志

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" } else { Write-Host "FAIL $Message"; $script:failures++ }
}

function Get-LogText {
    if (-not (Test-Path $script:logPath)) { return '' }
    return (Get-Content $script:logPath -Raw)
}

$script:logMark = (Get-LogText).Length
function Reset-LogMark { $script:logMark = (Get-LogText).Length }
function Get-NewLogText {
    $text = Get-LogText
    if ($text.Length -lt $script:logMark) { $script:logMark = 0 }
    return $text.Substring($script:logMark)
}

function Wait-NewLog {
    param([string]$Pattern, [int]$TimeoutSeconds = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-NewLogText) -match $Pattern) { return $true }
        Start-Sleep -Milliseconds 400
    }
    Write-Host ("  实际新增日志: {0}" -f ((Get-NewLogText) -replace "`r?`n", ' / '))
    return $false
}

# ------------------------------------------------------------------ UIA 小工具

function Get-Names {
    param($From)
    if ($null -eq $From) { return @() }
    return @($From.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
}

function Test-NameLike {
    param($From, [string]$Pattern)
    return @(Get-Names -From $From | Where-Object { $_ -match $Pattern }).Count -gt 0
}

function Wait-NameLike {
    param($From, [string]$Pattern, [int]$TimeoutSeconds = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-NameLike -From $From -Pattern $Pattern) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

# 主窗口的子树很大（文件列表几千行），所以先按**精确名字**找到小容器（服务端条件搜索很快），
# 再在它里面用正则看文字
function Wait-Element {
    param($From, [string]$Name, [int]$TimeoutSeconds = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $element = @(Get-Elements -From $From -Name $Name) | Select-Object -First 1
        if ($null -ne $element -and -not $element.Current.IsOffscreen) { return $element }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Get-Elements {
    param($From, [string]$Name, $ControlType = $null)
    $conditions = @(New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name))
    if ($null -ne $ControlType) {
        $conditions += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
    }
    $condition = if ($conditions.Count -eq 1) {
        $conditions[0]
    } else {
        New-Object System.Windows.Automation.AndCondition($conditions)
    }
    return @($From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Get-ButtonNames {
    param($From)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    if ($null -eq $From) { return @() }
    return @($From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
        Where-Object { -not $_.Current.IsOffscreen } | ForEach-Object { $_.Current.Name })
}

# 某个按钮在不在（可见且可用）。注意不能拿名字去匹配整棵树的文字：
# 状态行里也会出现「重启并完成更新」这几个字，那样会假阳性。
function Wait-Button {
    param($From, [string]$Name, [int]$TimeoutSeconds = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $button = @(Get-Elements -From $From -Name $Name -ControlType ([System.Windows.Automation.ControlType]::Button)) |
            Select-Object -First 1
        if ($null -ne $button -and -not $button.Current.IsOffscreen -and $button.Current.IsEnabled) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

# 按名字点一个按钮（按钮一定是 Invoke）；名字找不到 / 还没启用就等着
function Invoke-Button {
    param($From, [string]$Name, [int]$TimeoutSeconds = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $button = @(Get-Elements -From $From -Name $Name -ControlType ([System.Windows.Automation.ControlType]::Button)) | Select-Object -First 1
        if ($null -ne $button -and $button.Current.IsEnabled -and -not $button.Current.IsOffscreen) {
            $pattern = $null
            if ($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
                $pattern.Invoke()
                Start-Sleep -Milliseconds 600
                return $true
            }
        }
        Start-Sleep -Milliseconds 400
    }

    Write-Host ("  实际可见按钮: {0}" -f ((Get-ButtonNames -From $From) -join ', '))
    return $false
}

function Find-VisibleFirst {
    param($From, [string]$Name, $ControlType = $null, [int]$ProcessId = 0)
    foreach ($element in @(Get-Elements -From $From -Name $Name -ControlType $ControlType)) {
        if ($ProcessId -ne 0 -and $element.Current.ProcessId -ne $ProcessId) { continue }
        if ($element.Current.IsOffscreen) { continue }
        return $element
    }
    return $null
}

# 打开主菜单里的某一项（菜单不在主窗口的 UIA 子树里，要从 RootElement 往下找，
# 而且带入场动画，所以走 UIA 模式而不是鼠标，见 AGENTS.md 第 6 节第 24 / 26 条）。
# 注意：菜单项的 UIA 名字是它的 AutomationProperties.Name（不带省略号），不是 Text。
function Invoke-MenuItem {
    param($Session, [string]$MenuBarTitle, [string]$ItemName)

    $menuBarItem = Find-VisibleFirst -From $Session.Root -Name $MenuBarTitle `
        -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    if ($null -eq $menuBarItem) { Write-Host "  主菜单里找不到「$MenuBarTitle」"; return $false }

    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $item = $null
    for ($i = 0; $i -lt 24 -and $null -eq $item; $i++) {
        Start-Sleep -Milliseconds 250
        $item = Find-VisibleFirst -From ([System.Windows.Automation.AutomationElement]::RootElement) `
            -Name $ItemName -ControlType ([System.Windows.Automation.ControlType]::MenuItem) -ProcessId $Session.Proc.Id
    }
    if ($null -eq $item) { Write-Host "  「$MenuBarTitle」菜单里找不到「$ItemName」"; return $false }

    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    return $true
}

# 顶层窗口。注意**不能只找 RootElement 的直接子元素**：ContentDialog 的弹出岛窗口挂在
# 主窗口的一个“弹出窗口”窗口下面（UIA 树里是嵌套的），只有 Descendants 才找得到；
# 所以这里按 Descendants 搜、再按 RuntimeId 去重（同一个岛窗口可能同时出现在两处）。
function Get-TopWindows {
    param([int]$TargetProcessId)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetProcessId)
    $windows = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $cond) |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window })

    $seen = New-Object System.Collections.Generic.HashSet[string]
    return @($windows | Where-Object { $seen.Add((($_.GetRuntimeId()) -join ',')) })
}

function Wait-TopWindow {
    param([int]$TargetProcessId, [string]$NameLike, [int]$TimeoutSeconds = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $window = @(Get-TopWindows -TargetProcessId $TargetProcessId | Where-Object { $_.Current.Name -match $NameLike }) |
            Select-Object -First 1
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 400
    }

    Write-Host ("  实际顶层窗口: {0}" -f (((Get-TopWindows -TargetProcessId $TargetProcessId) | ForEach-Object { $_.Current.Name }) -join ', '))
    return $null
}

# ------------------------------------------------------------------ 会话 / mock feed

function Stop-AllExdir {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 800
}

$script:servers = @()
$script:serverIndex = 0
function Start-MockFeed {
    param([string]$Tag, [switch]$BadDigest)

    # 每次一个新的输出文件：上一次的服务器还没杀掉时，重定向到同一个文件会撞车
    $script:serverIndex++
    $outFile = Join-Path $workRoot "server-out-$($script:serverIndex).txt"
    $errFile = Join-Path $workRoot "server-err-$($script:serverIndex).txt"

    $arguments = @($serverScript, '--zip', $zipPath, '--tag', $Tag, '--notes', $notesPath, '--port', '0')
    if ($BadDigest) { $arguments += '--bad-digest' }

    $process = Start-Process -FilePath 'node' -PassThru -NoNewWindow `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile -ArgumentList $arguments
    $script:servers += $process

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 300
        $ready = @(Get-Content $outFile -ErrorAction SilentlyContinue | Where-Object { $_ -like 'READY*' }) |
            Select-Object -First 1
        if ($ready) {
            return ([regex]::Match($ready, '^READY\s+(.*)$').Groups[1].Value | ConvertFrom-Json)
        }
        if ($process.HasExited) { break }
    }

    Write-Host '  --- mock feed stdout ---'
    Get-Content $outFile -ErrorAction SilentlyContinue
    Write-Host '  --- mock feed stderr ---'
    Get-Content $errFile -ErrorAction SilentlyContinue
    throw 'mock feed 没起来'
}

function Start-App {
    param([int]$FeedPort, [bool]$CheckOnStartup = $true)
    Stop-AllExdir
    Set-Config -CheckOnStartup $CheckOnStartup

    $env:EXDIR_UPDATE_FEED = "http://127.0.0.1:$FeedPort/latest"

    $process = Start-Process -FilePath (Join-Path $appDir 'exdir.exe') -WorkingDirectory $appDir -PassThru
    $handle = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ($process.HasExited) { throw "exdir 启动后立刻退出（退出码 $($process.ExitCode)）" }
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $process.MainWindowHandle; break }
    }
    if ($handle -eq [IntPtr]::Zero) { throw '没等到主窗口' }

    Start-Sleep -Seconds 3

    return [pscustomobject]@{
        Proc = $process
        Root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    }
}

function Stop-Session {
    param($Session)
    if ($null -eq $Session) { return }
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 600
}

# ------------------------------------------------------------------ 用例

Write-Host ''
Write-Host "=== 当前版本 $currentVersion（远端 mock 会给 $remoteTag） ==="

$session = $null
try {
    # -------------------------------------------------- 用例 1～4：发现新版本并自动替换
    Write-Host ''
    Write-Host '=== 用例 1：启动时后台检查发现新版本 → 顶部提示条 ==='
    $feed = Start-MockFeed -Tag $remoteTag
    Reset-LogMark
    $session = Start-App -FeedPort $feed.port

    $bar = Wait-Element -From $session.Root -Name '更新提示条' -TimeoutSeconds 60
    Assert ($null -ne $bar) '主窗口顶部出现「更新提示条」'
    Assert (($null -ne $bar) -and (Test-NameLike -From $bar -Pattern ([regex]::Escape($remoteTag)))) '提示条里带远端版本号'
    Assert (Wait-NewLog -Pattern ('检查更新：发现新版本 ' + [regex]::Escape($remoteTag)) -TimeoutSeconds 20) '日志里记下了发现新版本'

    Write-Host ''
    Write-Host '=== 用例 2：提示条上的「立即更新」→ 更新窗口 ==='
    Assert (Invoke-Button -From $session.Root -Name '立即更新') '点到了提示条上的「立即更新」'

    $updateWindow = Wait-TopWindow -TargetProcessId $session.Proc.Id -NameLike '^更新 exdir$' -TimeoutSeconds 30
    Assert ($null -ne $updateWindow) '打开了更新窗口'
    if ($null -ne $updateWindow) {
        Assert (Wait-NameLike -From $updateWindow -Pattern ('当前版本：' + [regex]::Escape($currentVersion)) -TimeoutSeconds 10) '窗口里显示当前版本'
        Assert (Wait-NameLike -From $updateWindow -Pattern ('最新版本：' + [regex]::Escape($remoteTag)) -TimeoutSeconds 10) '窗口里显示最新版本'
        Assert (Wait-NameLike -From $updateWindow -Pattern '测试用发行说明' -TimeoutSeconds 10) '窗口里显示发行说明'

        Write-Host ''
        Write-Host '=== 用例 3：下载更新 ==='
        Assert (Invoke-Button -From $updateWindow -Name '下载更新') '点到了「立即更新」（下载）'
        Assert (Wait-NameLike -From $updateWindow -Pattern '下载完成' -TimeoutSeconds 120) '下载完成'
        Assert (Wait-Button -From $updateWindow -Name '重启并完成更新' -TimeoutSeconds 30) '出现「重启并完成更新」按钮'

        Write-Host ''
        Write-Host '=== 用例 4：重启并替换安装目录 ==='
        $oldPid = $session.Proc.Id
        Assert (Invoke-Button -From $updateWindow -Name '重启并完成更新') '点到了「重启并完成更新」'

        $exited = $false
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            if ($session.Proc.HasExited) { $exited = $true; break }
            Start-Sleep -Milliseconds 500
        }
        Assert $exited '旧的 exdir 进程退出了（替换脚本在等它）'

        # 替换脚本 robocopy 完会重新启动 exdir
        $restarted = $null
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            $restarted = @(Get-Process -Name 'exdir' -ErrorAction SilentlyContinue |
                Where-Object { $_.Id -ne $oldPid -and $_.Path -eq (Join-Path $appDir 'exdir.exe') }) | Select-Object -First 1
            if ($null -ne $restarted) { break }
            Start-Sleep -Milliseconds 700
        }
        Assert ($null -ne $restarted) '替换后 exdir 自动重启（而且是从被测的安装目录起的）'
        Assert (Test-Path (Join-Path $appDir 'update-applied.txt')) '安装目录里出现了新版本才有的标记文件'

        if (Test-Path $applyLog) {
            $applyText = Get-Content $applyLog -Raw
            $exitCode = [regex]::Match($applyText, 'robocopy exit=(\d+)').Groups[1].Value
            Assert (($exitCode -ne '') -and ([int]$exitCode -lt 8)) 'apply.log 里 robocopy 成功（退出码 < 8）'
            Assert ($applyText -match 'restarted exdir') 'apply.log 里记下了重新启动 exdir'
        } else {
            Assert $false "找不到替换脚本的日志：$applyLog"
        }

        if ($null -ne $restarted) { try { $restarted.Kill() } catch { } }
    } else {
        Assert $false '更新窗口没打开，后面的用例跳过'
    }

    Stop-Session -Session $session
    $session = $null

    # -------------------------------------------------- 用例 5：没有新版本
    Write-Host ''
    Write-Host '=== 用例 5：feed 的版本 = 当前版本 → 什么都不发生 ==='
    $feed = Start-MockFeed -Tag ("v" + $currentVersion)
    Reset-LogMark
    $session = Start-App -FeedPort $feed.port
    Start-Sleep -Seconds 6

    Assert (Wait-NewLog -Pattern ([regex]::Escape("不比当前 $currentVersion 新")) -TimeoutSeconds 30) '日志里说明远端不比当前新'
    Assert ($null -eq (Wait-Element -From $session.Root -Name '更新提示条' -TimeoutSeconds 3)) '界面上没有出现提示条'
    Stop-Session -Session $session
    $session = $null

    # -------------------------------------------------- 用例 6：SHA256 校验失败
    Write-Host ''
    Write-Host '=== 用例 6：摘要不符 → 校验失败、不安装 ==='
    # 用例 4 的标记文件还留在安装目录里，先删掉，这样“没被改过”才有意义
    Remove-Item (Join-Path $appDir 'update-applied.txt') -Force -ErrorAction SilentlyContinue
    $feed = Start-MockFeed -Tag $remoteTag -BadDigest
    Reset-LogMark
    $session = Start-App -FeedPort $feed.port

    $bar = Wait-Element -From $session.Root -Name '更新提示条' -TimeoutSeconds 60
    Assert ($null -ne $bar) '提示条出现（用例 6 前提）'
    Assert (Invoke-Button -From $session.Root -Name '立即更新') '点到了提示条上的「立即更新」'

    $updateWindow = Wait-TopWindow -TargetProcessId $session.Proc.Id -NameLike '^更新 exdir$' -TimeoutSeconds 30
    Assert ($null -ne $updateWindow) '打开了更新窗口'
    if ($null -ne $updateWindow) {
        Assert (Invoke-Button -From $updateWindow -Name '下载更新') '点到了「立即更新」（下载）'
        Assert (Wait-NameLike -From $updateWindow -Pattern '校验失败' -TimeoutSeconds 120) '窗口里报告校验失败'
        Assert (-not (Wait-Button -From $updateWindow -Name '重启并完成更新' -TimeoutSeconds 3)) '失败后没有给出「重启并完成更新」'
        Assert (-not (Test-Path (Join-Path $appDir 'update-applied.txt'))) '安装目录没有被改过'
    }

    Stop-Session -Session $session
    $session = $null

    # -------------------------------------------------- 用例 7：关掉开关 → 只能手动检查
    Write-Host ''
    Write-Host '=== 用例 7：关掉「启动时自动检查更新」→ 手动检查 ==='
    $feed = Start-MockFeed -Tag $remoteTag
    Reset-LogMark
    $session = Start-App -FeedPort $feed.port -CheckOnStartup $false
    Start-Sleep -Seconds 7

    Assert ($null -eq (Wait-Element -From $session.Root -Name '更新提示条' -TimeoutSeconds 2)) '关掉开关后启动时不弹提示条'
    Assert (-not ((Get-NewLogText) -match '检查更新')) '关掉开关后启动时根本没联网（日志里没有“检查更新”）'

    Assert (Invoke-MenuItem -Session $session -MenuBarTitle '帮助' -ItemName '检查更新') '点到了「帮助 → 检查更新…」'
    $updateWindow = Wait-TopWindow -TargetProcessId $session.Proc.Id -NameLike '^更新 exdir$' -TimeoutSeconds 40
    Assert ($null -ne $updateWindow) '手动检查也能打开更新窗口'
    if ($null -ne $updateWindow) {
        Assert (Wait-NameLike -From $updateWindow -Pattern '可以更新到新版本' -TimeoutSeconds 10) '窗口状态行说明有新版本'
    }

    Stop-Session -Session $session
    $session = $null

    # -------------------------------------------------- 用例 8：手动检查但没有新版本 → 回执对话框
    Write-Host ''
    Write-Host '=== 用例 8：手动检查（没有新版本）→ 回执对话框 ==='
    $feed = Start-MockFeed -Tag ("v" + $currentVersion)
    $session = Start-App -FeedPort $feed.port -CheckOnStartup $false

    Assert (Invoke-MenuItem -Session $session -MenuBarTitle '帮助' -ItemName '检查更新') '点到了「帮助 → 检查更新…」'
    $dialog = Wait-TopWindow -TargetProcessId $session.Proc.Id -NameLike '^检查更新$' -TimeoutSeconds 40
    Assert ($null -ne $dialog) '弹出「检查更新」回执对话框（内容对话框是独立的顶层窗口）'
    if ($null -ne $dialog) {
        Assert (Wait-NameLike -From $dialog -Pattern ('当前已是最新版本（' + [regex]::Escape($currentVersion)) -TimeoutSeconds 10) '对话框里说当前已是最新版本'
        $null = Invoke-Button -From $dialog -Name '确定' -TimeoutSeconds 10
    }
}
finally {
    Stop-Session -Session $session
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    foreach ($server in $script:servers) { try { if (-not $server.HasExited) { $server.Kill() } } catch { } }

    $env:XDG_CONFIG_HOME = $originalXdgConfig
    $env:XDG_DATA_HOME = $originalXdgData
    $env:EXDIR_UPDATE_FEED = $originalFeed

    if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host '临时目录与 mock feed 已清理'
}

Write-Host ''
Write-Host "结果: $(if ($failures -eq 0) { '全部通过' } else { "$failures 条断言失败" })" -ForegroundColor $(if ($failures -eq 0) { 'Green' } else { 'Red' })

if ($failures -gt 0) { exit 1 }
