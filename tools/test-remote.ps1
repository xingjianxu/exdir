# 远程位置（SFTP / FTP）的界面回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-remote.ps1
#   pwsh -NoProfile -File tools\test-remote.ps1 -Exe dist\win-x64\exdir.exe
#
# 全程 UIA 模式（不用真鼠标、不敲键盘，所以不需要交互桌面）：侧边栏出现「远程」分组、
# 点一下能列出服务器上的目录、行内箭头能在列表里就地展开远程子目录、只读守卫拦得住写操作、
# 「复制」把文件下到本地中转目录并放进系统剪贴板、「打开」把文件下到本地缓存。
# 真鼠标/拖拽那条路（双击下载打开、拖到资源管理器）需要交互桌面，见 AGENTS.md「远程位置」。
#
# 协议那一头是本机的 tools\remote-test-server（node，SFTP 用 ssh2、FTP 用 ftp-srv）；
# 服务级（不开界面）的协议覆盖在 tools\remote-smoke。
#
# 用例：
#   1. 侧边栏「远程」分组里有配置的三个位置（FTP / SFTP / SFTP 私钥）；
#   2. 点 FTP 位置 → 文件列表列出服务器上的条目（状态栏「项」跟着变）；
#   3. 点 SFTP 位置（密码登录）→ 一样列得出来（换一种协议，同一条链路）；
#   4. 行内箭头就在列表里展开远程子目录（不导航进包/进目录，行数只多两行）；
#   5. 只读：选中远程文件后执行「编辑 → 删除」，什么都不发生（行还在、磁盘无变化）并弹出只读提示；
#   6. 「编辑 → 复制」把 hello.txt 下到 remote-cache\copy 并放进系统剪贴板（CF_HDROP）；
#   7. 「文件 → 打开」把 hello.txt 下到 remote-cache\open 并交给默认程序；
#   8. 标签页标题 / 面包屑认识远程路径（不是把整条 sftp:// 当目录名）、远程文件行不当压缩包。
#
# 真鼠标与拖拽（双击下载打开、拖到资源管理器、右键菜单）需要交互桌面，见 AGENTS.md「远程位置」。
#
#   9. 设置窗口「远程」页：列出已配置的位置、新增一个匿名 FTP（真的写进 config.json）、
#      侧边栏立即多出那个位置，再删掉它（确认框 + 落盘）。
#
# 跑完会还原 config.json / 恢复环境变量并删掉测试目录。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RemoteTestNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}
'@

[void][RemoteTestNative]::SetProcessDpiAwarenessContext([IntPtr][RemoteTestNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }
$exeDir = Split-Path $exePath

$repo = Split-Path $PSScriptRoot -Parent
$serverScript = Join-Path $repo 'tools\remote-test-server\server.js'
if (-not (Test-Path $serverScript)) { throw "找不到测试服务器: $serverScript" }

$ftpPort = 2321
$sftpPort = 2323

# ------------------------------------------------------------------ 测试数据（服务器的根目录）

$workRoot = Join-Path $env:TEMP 'exdir-remote-ui'
$srvRoot = Join-Path $workRoot 'srv'
if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$srvRoot\sub\deep", "$srvRoot\empty" | Out-Null
[System.IO.File]::WriteAllText("$srvRoot\hello.txt", 'hello remote')
[System.IO.File]::WriteAllText("$srvRoot\sub\inner.txt", 'inner')
[System.IO.File]::WriteAllText("$srvRoot\sub\deep\deep.txt", 'deep')
$clientKey = Join-Path $workRoot 'client-key'

# ------------------------------------------------------------------ 启动测试服务器

$serverOut = Join-Path $workRoot 'server-out.txt'
$serverErr = Join-Path $workRoot 'server-err.txt'

# npm 依赖装到 tools\remote-test-server\node_modules（npm install 在那里执行；node_modules 已被 .gitignore 掉）
if (-not (Test-Path (Join-Path $repo 'tools\remote-test-server\node_modules\ssh2'))) {
    Write-Host '首次运行：安装测试服务器的 npm 依赖（ssh2 / ftp-srv）…'
    Push-Location (Join-Path $repo 'tools\remote-test-server')
    try { npm install --silent --no-fund --no-audit | Out-Null } finally { Pop-Location }
}

$server = Start-Process -FilePath 'node' -PassThru -NoNewWindow `
    -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr `
    -ArgumentList @(
        $serverScript,
        '--root', $srvRoot,
        '--ftp-port', $ftpPort,
        '--sftp-port', $sftpPort,
        '--user', 'testuser',
        '--password', 'testpass',
        '--key-out', $clientKey
    )

$serverReady = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    if ((Get-Content $serverOut -ErrorAction SilentlyContinue | Where-Object { $_ -like 'READY*' }).Count -gt 0) { $serverReady = $true; break }
    if ($server.HasExited) { break }
}

if (-not $serverReady) {
    Write-Host '  --- server stdout ---'
    Get-Content $serverOut -ErrorAction SilentlyContinue
    Write-Host '  --- server stderr ---'
    Get-Content $serverErr -ErrorAction SilentlyContinue
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    throw '本机测试服务器没起来'
}

# ------------------------------------------------------------------ 凭据（DPAPI 要现场生成）

$smokeProject = Join-Path $repo 'tools\remote-smoke'
$protectedSecret = $null
Push-Location $repo
try {
    $protectedSecret = (dotnet run -c Debug --project $smokeProject -- --protect testpass | Select-Object -Last 1).Trim()
} finally {
    Pop-Location
}

if ([string]::IsNullOrWhiteSpace($protectedSecret)) {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    throw '拿不到加密后的测试密码（remote-smoke --protect 没输出）'
}

# ------------------------------------------------------------------ 配置：用自己的配置目录，不动用户的

$configRoot = Join-Path $workRoot 'cfg'
New-Item -ItemType Directory -Force -Path (Join-Path $configRoot 'exdir') | Out-Null
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$originalXdg = $env:XDG_CONFIG_HOME
$originalXdgData = $env:XDG_DATA_HOME
$env:XDG_CONFIG_HOME = $configRoot
# 「最新访问」写在 XDG_DATA_HOME 下，而这个脚本会点远程位置导航：
# 也指到临时工作目录里，免得把 sftp:// / ftp:// 写进用户的 recents.json
$env:XDG_DATA_HOME = $configRoot
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$remoteCache = Join-Path $env:LOCALAPPDATA 'exdir\remote-cache'

$settings = [ordered]@{
    SchemaVersion     = 10
    IsDualPane        = $false
    IsSidebarVisible  = $true
    ShowHiddenFiles   = $false
    ShowExtensions    = $true
    FoldersFirst      = $true
    RowHeight         = 28
    ColumnWidths      = @()
    ColumnAutoFit     = $true
    SidebarShowRemote = $true
    PrimaryTabs       = @("$env:USERPROFILE")
    PrimaryActiveTab  = 0
    SecondaryTabs     = @()
    RemoteLocations   = @(
        [ordered]@{
            Id                      = 'ftptest'
            Name                    = '本机 FTP'
            Protocol                = 1
            Host                    = '127.0.0.1'
            Port                    = $ftpPort
            UserName                = 'testuser'
            Auth                    = 0
            ProtectedPassword       = $protectedSecret
            PrivateKeyPath          = ''
            ProtectedPassphrase     = ''
            StartPath               = '/'
            UsePassive              = $true
            AllowInvalidCertificate = $false
        },
        [ordered]@{
            Id                      = 'sftptest'
            Name                    = '本机 SFTP'
            Protocol                = 0
            Host                    = '127.0.0.1'
            Port                    = $sftpPort
            UserName                = 'testuser'
            Auth                    = 0
            ProtectedPassword       = $protectedSecret
            PrivateKeyPath          = ''
            ProtectedPassphrase     = ''
            StartPath               = '/'
            UsePassive              = $true
            AllowInvalidCertificate = $false
        },
        [ordered]@{
            Id                      = 'sftpkey'
            Name                    = '本机 SFTP（私钥）'
            Protocol                = 0
            Host                    = '127.0.0.1'
            Port                    = $sftpPort
            UserName                = 'testuser'
            Auth                    = 1
            ProtectedPassword       = ''
            PrivateKeyPath          = $clientKey
            ProtectedPassphrase     = ''
            StartPath               = '/sub'
            UsePassive              = $true
            AllowInvalidCertificate = $false
        }
    )
}

$settings | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding utf8

# ------------------------------------------------------------------ 断言 / 工具

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

function Reset-LogMark { $script:logMark = (Get-LogText).Length }
function Get-NewLogText {
    $text = Get-LogText
    if ($text.Length -lt $script:logMark) { $script:logMark = 0 }
    return $text.Substring($script:logMark)
}
$script:logMark = 0

function Wait-NewLog {
    param([string]$Pattern, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-NewLogText) -match $Pattern) { return $true }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ("  实际新增日志: {0}" -f ((Get-NewLogText) -replace "`r?`n", ' / '))
    return $false
}

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

function Get-TreeItems {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::TreeItem))
}

function Wait-TreeItem {
    param($Session, [string]$Name, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $found = @(Get-TreeItems -Session $Session | Where-Object { $_.Current.Name -eq $Name })
        if ($found.Count -gt 0) { return $found[0] }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ("  实际树节点: {0}" -f ((Get-TreeItems -Session $Session | ForEach-Object { $_.Current.Name }) -join ', '))
    return $null
}

# 侧边栏树节点的点击：ItemInvoked 走的是 Invoke（SelectionItem 只改选中，不会导航）
function Invoke-TreeItem {
    param($Session, [string]$Name)
    $item = Wait-TreeItem -Session $Session -Name $Name
    if ($null -eq $item) { throw "侧边栏里找不到「$Name」" }

    $pattern = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    } elseif ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
    } else {
        throw "「$Name」既不支持 Invoke 也不支持 Select"
    }

    Start-Sleep -Milliseconds 1500
}

function Get-Rows {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
}

function Get-RowNames {
    param($Session)
    return @((Get-Rows -Session $Session) | ForEach-Object { $_.Current.Name } | Sort-Object)
}

function Get-RowByName {
    param($Session, [string]$Name)
    return @(Get-Rows -Session $Session | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1)[0]
}

function Wait-RowName {
    param($Session, [string]$Name, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($null -ne (Get-RowByName -Session $Session -Name $Name)) { return $true }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ("  实际行: {0}" -f ((Get-RowNames -Session $Session) -join ', '))
    return $false
}

function Select-Row {
    param($Session, [string]$Name)
    $row = Get-RowByName -Session $Session -Name $Name
    if ($null -eq $row) { throw "列表里找不到「$Name」" }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 500
}

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

function Invoke-Expander {
    param($Session, $Row)
    $expander = Get-RowExpander -Session $Session -Row $Row
    if ($null -eq $expander) { return $false }
    $expander.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1500
    return $true
}

function Invoke-MenuItem {
    param($Session, [string]$MenuName, [string]$ItemName)
    $menuBarItem = $null
    foreach ($el in (Find-Elements -From $Session.Root -Name $MenuName)) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -and -not $el.Current.IsOffscreen) {
            $menuBarItem = $el
            break
        }
    }

    if ($null -eq $menuBarItem) { throw "主菜单里找不到「$MenuName」" }

    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)

    $item = $null
    for ($attempt = 0; $attempt -lt 2 -and $null -eq $item; $attempt++) {
        if ($attempt -gt 0) {
            Start-Sleep -Milliseconds 800
            try { $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
            Start-Sleep -Milliseconds 300
            $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        }

        for ($i = 0; $i -lt 24 -and $null -eq $item; $i++) {
            Start-Sleep -Milliseconds 250
            $item = @($Session.Desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
                Where-Object {
                    $_.Current.ProcessId -eq $Session.Proc.Id -and $_.Current.Name -eq $ItemName -and
                    -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0
                } | Select-Object -First 1)[0]
        }
    }

    if ($null -eq $item) { throw "「$MenuName」菜单里找不到「$ItemName」" }

    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
}

function Get-StatusBarText {
    param($Session)
    $statusBar = Find-Elements -From $Session.Root -Name '状态栏' | Select-Object -First 1
    if ($null -eq $statusBar) { return '' }

    $texts = @(Find-ByType -From $statusBar -ControlType ([System.Windows.Automation.ControlType]::Text))
    return (@($texts | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ')
}

function Get-InfoBarTexts {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::Text) |
        ForEach-Object { $_.Current.Name })
}

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

function Get-ClipboardFiles {
    try {
        $data = [System.Windows.Forms.Clipboard]::GetDataObject()
        if ($null -eq $data -or -not $data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) { return @() }
        return @($data.GetData([System.Windows.Forms.DataFormats]::FileDrop))
    } catch {
        return @()
    }
}

function Wait-ClipboardFile {
    param([string]$FileName, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $match = @(Get-ClipboardFiles | Where-Object { (Split-Path $_ -Leaf) -eq $FileName } | Select-Object -First 1)
        if ($match.Count -gt 0) { return $match[0] }
        Start-Sleep -Milliseconds 400
    }

    Write-Host ("  实际剪贴板: {0}" -f ((Get-ClipboardFiles) -join ', '))
    return $null
}

function Wait-File {
    param([string]$Path, [int]$TimeoutSeconds = 40)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { return $true }
        Start-Sleep -Milliseconds 400
    }

    return $false
}

# ------------------------------------------------------------------ 设置窗口「远程」页

function Find-SettingsWindow {
    param($Session)
    foreach ($el in (Find-Elements -From $Session.Desktop -Name '设置')) {
        if ($el.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
        if ($el.Current.ProcessId -ne $Session.Proc.Id) { continue }
        if ($el.Current.IsOffscreen) { continue }
        return $el
    }
    return $null
}

function Open-Settings {
    param($Session)
    Invoke-MenuItem -Session $Session -MenuName '配置' -ItemName '设置'

    $window = $null
    for ($i = 0; $i -lt 30 -and $null -eq $window; $i++) {
        Start-Sleep -Milliseconds 250
        $window = Find-SettingsWindow -Session $Session
    }
    if ($null -eq $window) { throw '设置窗口没有出现' }
    Start-Sleep -Seconds 1
    return $window
}

function Select-Category {
    param($Window, [string]$Name)
    $item = Find-VisibleFirst -From $Window -Name $Name -ControlType ([System.Windows.Automation.ControlType]::ListItem)
    if ($null -eq $item) { throw "左侧导航里找不到分类「$Name」" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
}

function Set-TextValue {
    param($From, [string]$Name, [string]$Value)
    $box = Find-VisibleFirst -From $From -Name $Name -ControlType ([System.Windows.Automation.ControlType]::Edit)
    if ($null -eq $box) { throw "找不到输入框「$Name」" }
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
    Start-Sleep -Milliseconds 200
}

# 下拉框：展开 → 在弹出层里选那一项（下拉项在 UIA 里是 ListItem）
function Select-ComboItem {
    param($Session, $From, [string]$ComboName, [string]$ItemName)
    $combo = Find-VisibleFirst -From $From -Name $ComboName -ControlType ([System.Windows.Automation.ControlType]::ComboBox)
    if ($null -eq $combo) { throw "找不到下拉框「$ComboName」" }
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $item = $null
    for ($i = 0; $i -lt 16 -and $null -eq $item; $i++) {
        Start-Sleep -Milliseconds 250
        $item = Find-VisibleFirst -From $Session.Desktop -Name $ItemName `
            -ControlType ([System.Windows.Automation.ControlType]::ListItem) -ProcessId $Session.Proc.Id
    }
    if ($null -eq $item) { throw "下拉框「$ComboName」里找不到「$ItemName」" }

    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}

# 弹出来的对话框（ContentDialog 是独立的弹出岛窗口，不在设置窗口的子树里，见 AGENTS.md 第 27 条）
function Wait-Dialog {
    param($Session, [string]$Title, [int]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $found = Find-VisibleFirst -From $Session.Desktop -Name $Title `
            -ControlType ([System.Windows.Automation.ControlType]::Window) -ProcessId $Session.Proc.Id
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-ButtonIn {
    param($From, [string]$Name)
    $button = Find-VisibleFirst -From $From -Name $Name -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $button) { throw "找不到按钮「$Name」" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
}

function Start-Session {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 600

    $proc = Start-Process -FilePath $exePath -WorkingDirectory $exeDir -PassThru
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

# ------------------------------------------------------------------ 跑用例

$session = $null
try {
    # 中转目录先清干净，免得看到上一次运行留下的东西
    if (Test-Path $remoteCache) { Remove-Item $remoteCache -Recurse -Force -ErrorAction SilentlyContinue }

    $session = Start-Session
    Reset-LogMark

    Write-Host ''
    Write-Host '用例 1：侧边栏「远程」分组'
    $group = Wait-TreeItem -Session $session -Name '远程'
    Assert ($null -ne $group) '侧边栏出现「远程」分组'
    Assert ($null -ne (Wait-TreeItem -Session $session -Name '本机 FTP')) '分组里有「本机 FTP」'
    Assert ($null -ne (Wait-TreeItem -Session $session -Name '本机 SFTP')) '分组里有「本机 SFTP」'
    Assert ($null -ne (Wait-TreeItem -Session $session -Name '本机 SFTP（私钥）')) '分组里有「本机 SFTP（私钥）」'

    Write-Host ''
    Write-Host '用例 2：点 FTP 位置 → 列出服务器上的目录'
    Invoke-TreeItem -Session $session -Name '本机 FTP'
    Assert (Wait-RowName -Session $session -Name 'hello.txt') 'FTP：列表里有 hello.txt'
    Assert (Wait-RowName -Session $session -Name 'sub') 'FTP：列表里有子目录 sub'
    Assert (Wait-RowName -Session $session -Name 'empty') 'FTP：列表里有空目录 empty'
    $status = Get-StatusBarText -Session $session
    Assert ($status -match '项') "FTP：状态栏显示项数（$status）"

    Write-Host ''
    Write-Host '用例 3：点 SFTP 位置（密码登录）→ 一样列得出来'
    Invoke-TreeItem -Session $session -Name '本机 SFTP'
    Assert (Wait-RowName -Session $session -Name 'hello.txt') 'SFTP：列表里有 hello.txt'
    Assert (Wait-RowName -Session $session -Name 'sub') 'SFTP：列表里有子目录 sub'

    Write-Host ''
    Write-Host '用例 4：行内箭头就地展开远程子目录'
    $subRow = Get-RowByName -Session $session -Name 'sub'
    Assert ($null -ne $subRow) '找得到 sub 行'
    $before = (Get-Rows -Session $session).Count
    Assert (Invoke-Expander -Session $session -Row $subRow) 'sub 行上有展开箭头'
    Assert (Wait-RowName -Session $session -Name 'inner.txt') '展开后出现子项 inner.txt（没有导航进目录）'
    $after = (Get-Rows -Session $session).Count
    Assert ($after -gt $before) "展开后行数变多（$before → $after）"
    Assert ((Get-RowByName -Session $session -Name 'sub') -ne $null) 'sub 行自己还在（就地展开而不是进目录）'

    Write-Host ''
    Write-Host '用例 5：只读守卫（选中远程文件 → 编辑菜单「删除」）'
    Select-Row -Session $session -Name 'hello.txt'
    Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '删除'
    Start-Sleep -Seconds 1
    Assert ((Get-RowByName -Session $session -Name 'hello.txt') -ne $null) '删除被拦住：行还在'
    Assert (Test-Path (Join-Path $srvRoot 'hello.txt')) '删除被拦住：服务器上的文件也还在'
    $readOnlyShown = @(Get-InfoBarTexts -Session $session | Where-Object { $_ -like '*远程位置不支持该操作*' }).Count -gt 0
    Assert $readOnlyShown '弹出「远程位置不支持该操作」提示'

    Write-Host ''
    Write-Host '用例 6：编辑菜单「复制」→ 下到本地中转目录 + 系统剪贴板'
    Reset-LogMark
    Select-Row -Session $session -Name 'hello.txt'
    Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '复制'
    $copied = Wait-ClipboardFile -FileName 'hello.txt'
    Assert ($null -ne $copied) '剪贴板里出现了 hello.txt（CF_HDROP）'
    if ($null -ne $copied) {
        Assert (Test-Path -LiteralPath $copied) '剪贴板里的路径真的存在'
        Assert ((Get-Content -LiteralPath $copied -Raw) -match 'hello remote') '剪贴板里的文件内容正确'
        Assert ($copied.StartsWith($remoteCache, [StringComparison]::OrdinalIgnoreCase)) "文件落在 remote-cache 里（$copied）"
    }
    Assert (Wait-NewLog -Pattern '远程复制到剪贴板') '日志里有「远程复制到剪贴板」'

    Write-Host ''
    Write-Host '用例 7：文件菜单「打开」→ 下到本地缓存并交给默认程序'
    Reset-LogMark
    Select-Row -Session $session -Name 'hello.txt'
    Invoke-MenuItem -Session $session -MenuName '文件' -ItemName '打开'
    Assert (Wait-NewLog -Pattern '打开远程文件') '日志里有「打开远程文件」'
    $opened = Get-ChildItem -Path (Join-Path $remoteCache 'open') -Recurse -Filter 'hello.txt' -ErrorAction SilentlyContinue | Select-Object -First 1
    Assert ($null -ne $opened) '本地缓存里出现了 hello.txt'
    if ($null -ne $opened) {
        Assert ($opened.Length -gt 0) '缓存副本非空'
    }

    Write-Host ''
    Write-Host '用例 8：标签页标题 / 面包屑 / 行内箭头都认识远程路径'
    $tabText = @(Find-ByType -From $session.Root -ControlType ([System.Windows.Automation.ControlType]::TabItem) |
        Where-Object { -not $_.Current.IsOffscreen } | ForEach-Object { $_.Current.Name })
    Assert (@($tabText | Where-Object { $_ -like '*testuser@127.0.0.1*' }).Count -gt 0) "标签页标题显示登录身份（$($tabText -join ', ')）"

    $crumb = @(Find-Elements -From $session.Root -Name "testuser@127.0.0.1:$sftpPort" |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and -not $_.Current.IsOffscreen })
    Assert ($crumb.Count -gt 0) '面包屑第一段是登录身份（不是整条原始路径）'

    $fileRow = Get-RowByName -Session $session -Name 'hello.txt'
    Assert ($null -ne $fileRow) '找得到 hello.txt 行'

    # 行模板里每行都有那个 18px 箭头（不可展开的只是没字形 / 不可点），所以不能拿
    # “有没有箭头元素”当断言 —— 要看“点下去会不会就地展开”：远程文件不是压缩包，不应该多出行
    $beforeNames = (Get-RowNames -Session $session) -join ','
    $invoked = Invoke-Expander -Session $session -Row $fileRow
    $afterNames = (Get-RowNames -Session $session) -join ','
    Assert ($beforeNames -eq $afterNames) "点远程文件行的箭头不会就地展开（行集合不变：$afterNames）"
    Assert $invoked '远程文件行上找得到并点了那个箭头（只是不产生任何效果）'

    $dirRow = Get-RowByName -Session $session -Name 'sub'
    Assert ($null -ne (Get-RowExpander -Session $session -Row $dirRow)) '远程目录行有展开箭头'

    Write-Host ''
    Write-Host '用例 9：设置窗口「远程」页（列出已有位置 / 添加 / 删除，真的写 config.json）'
    $settingsWindow = Open-Settings -Session $session
    Select-Category -Window $settingsWindow -Name '远程'

    $rowTitles = @(Find-Elements -From $settingsWindow -Name '本机 SFTP' | ForEach-Object { $_.Current.Name })
    Assert ($rowTitles.Count -gt 0) '「远程」页列出了已配置的位置'
    Assert ($null -ne (Find-VisibleFirst -From $settingsWindow -Name '添加位置' -ControlType ([System.Windows.Automation.ControlType]::Button))) '有「添加位置…」按钮'

    Invoke-ButtonIn -From $settingsWindow -Name '添加位置'
    $dialog = Wait-Dialog -Session $session -Title '添加远程位置'
    Assert ($null -ne $dialog) '弹出了「添加远程位置」对话框'

    if ($null -ne $dialog) {
        Set-TextValue -From $dialog -Name '远程位置名称' -Value '本机 FTP（匿名）'
        Set-TextValue -From $dialog -Name '远程主机' -Value '127.0.0.1'
        Set-TextValue -From $dialog -Name '远程端口' -Value "$ftpPort"
        Select-ComboItem -Session $session -From $dialog -ComboName '远程协议' -ItemName 'FTP'
        Select-ComboItem -Session $session -From $dialog -ComboName '远程登录方式' -ItemName '匿名（仅 FTP）'

        Invoke-ButtonIn -From $dialog -Name '确定'
        Start-Sleep -Seconds 1
    }

    $added = $false
    $namesNow = @()
    for ($i = 0; $i -lt 20 -and -not $added; $i++) {
        $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $namesNow = @($json.RemoteLocations | ForEach-Object { $_.Name })
        $added = @($namesNow | Where-Object { $_ -eq '本机 FTP（匿名）' }).Count -gt 0
        if (-not $added) { Start-Sleep -Milliseconds 300 }
    }
    Assert $added "新位置写进了 config.json（现有：$($namesNow -join ' / ')）"

    if ($added) {
        $new = @($json.RemoteLocations | Where-Object { $_.Name -eq '本机 FTP（匿名）' })[0]
        Assert ([int]$new.Protocol -eq 1) '存下来的协议是 FTP'
        Assert ([int]$new.Auth -eq 2) '存下来的登录方式是匿名'
        Assert ([int]$new.Port -eq $ftpPort) '存下来的端口正确'
        Assert ([string]::IsNullOrEmpty($new.ProtectedPassword)) '匿名登录不存密码'
    }

    Assert ($null -ne (Wait-TreeItem -Session $session -Name '本机 FTP（匿名）')) '主窗口侧边栏立即多出了这个位置'

    Write-Host '  （删掉刚加的位置）'
    Invoke-ButtonIn -From $settingsWindow -Name '删除 本机 FTP（匿名）'
    $confirm = Wait-Dialog -Session $session -Title '删除远程位置'
    Assert ($null -ne $confirm) '弹出删除确认对话框'

    if ($null -ne $confirm) {
        Invoke-ButtonIn -From $confirm -Name '删除'
        Start-Sleep -Seconds 1
    }

    $removed = $false
    for ($i = 0; $i -lt 20 -and -not $removed; $i++) {
        $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $removed = @($json.RemoteLocations).Count -eq 3
        if (-not $removed) { Start-Sleep -Milliseconds 300 }
    }
    Assert $removed '删除后 config.json 里又只剩下三个位置'
}
finally {
    if ($null -ne $session) { Stop-Session -Session $session }

    if ($null -ne $server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Get-Process -Name exdir -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    $env:XDG_CONFIG_HOME = $originalXdg
    $env:XDG_DATA_HOME = $originalXdgData
    if (-not $env:EXDIR_KEEP_TEST_DIR) { Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "保留工作目录: $workRoot" }
    if (Test-Path $remoteCache) { Remove-Item $remoteCache -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($failures -eq 0) { Write-Host '全部通过' } else { Write-Host "失败 $failures 项" }
exit $failures
