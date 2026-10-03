# 压缩包内「复制 → 粘贴到外部目录」的回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-archive-copy.ps1
#   pwsh -NoProfile -File tools\test-archive-copy.ps1 -Exe dist\win-x64\exdir.exe
#
# 用例（全程 UIA 模式：不点真鼠标、不敲键盘，所以**不需要交互桌面**；
# 真鼠标那条路（右键菜单里的「复制」）在 tools\test-archive.ps1 的用例 12 里）：
#   1. exdir <压缩包> → 新标签页进包（包内行 + 导航条上的「只读」徽标）；
#   2. 选中包内文件 → 编辑菜单「复制」：只记在内存里（日志「复制压缩包内条目：1 项」），
#      磁盘上什么都没多出来，而且系统剪贴板被清空（包内条目与系统剪贴板互斥，见
#      Services/IArchiveClipboardService.cs）；
#   3. 「上一级」回真实目录 → 编辑菜单「粘贴」：hello.txt 真的落到磁盘上、内容一致，
#      日志里有「压缩包复制：sample.zip 解出 1 项」与「粘贴压缩包内条目：1 项」，
#      中转的临时副本用完就删（archive-cache\copy 里不留东西）；
#   4. 再敲一次 exdir <压缩包> → 新标签页进包 → 多选（文件 + 目录）→ 复制 → 上一级 → 粘贴：
#      文件与目录的整棵子树（sub\inner.txt、sub\deep\deep.txt）都到位，日志记的是 2 项；
#   5. 系统剪贴板优先：包内复制之后再让“别的程序”往剪贴板放一个文件 → 粘贴粘的是那个文件，
#      不是内存里的包内条目（否则资源管理器里复制的东西会被包内条目顶掉）；
#   6. .iso（光盘映像，7z.dll 的 Iso / Udf 处理器）一样当目录进、一样能复制出来
#      （测试用的 ISO 用 Windows 自带的 IMAPI2FS 现造，造不出就 SKIP 这一条）；
#   7. 真实目录里点压缩包文件行的行首箭头 → **就地展开**（不进包）：列表多出包内条目、
#      行数只多两行、导航条上没有「只读」徽标；
#   8. 就地展开出来的包内行仍然是只读的：「复制」照样只记内存（并清空系统剪贴板），
#      「删除」被只读守卫拦住（日志里没有真的删除、弹出只读提示、磁盘无变化）；
#   9. 再点一次箭头折叠回去，行集合与展开前完全一致。
#
# 跑完会还原 config.json 的原始内容并删掉测试目录。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ArchiveCopyNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}
'@

Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// IMAPI2FS 造出来的 ISO 是一个 COM IStream，PowerShell 不能直接把它读成文件，
// 所以在 C# 里做一次 QI + 分块拷到磁盘（与 .artifacts 里的探针同一套做法）。
public static class IsoStreamHelper
{
    public static void CopyToFile(object streamObject, string path)
    {
        var stream = (IStream)streamObject;
        using var file = File.Create(path);
        var buffer = new byte[64 * 1024];
        var read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);
                var count = Marshal.ReadInt32(read);
                if (count <= 0) { break; }
                file.Write(buffer, 0, count);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }
    }
}
'@

[void][ArchiveCopyNative]::SetProcessDpiAwarenessContext([IntPtr][ArchiveCopyNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }
$exeDir = Split-Path $exePath

# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$archiveCache = Join-Path $env:LOCALAPPDATA 'exdir\archive-cache'

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

# ------------------------------------------------------------------ 剪贴板（模拟“别的程序”，与 tools\test-file-ops.ps1 同一套）

function Get-ClipboardFiles {
    try {
        $data = [System.Windows.Forms.Clipboard]::GetDataObject()
        if ($null -eq $data -or -not $data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) { return @() }
        return @($data.GetData([System.Windows.Forms.DataFormats]::FileDrop))
    } catch {
        return @()
    }
}

function Set-ClipboardFiles {
    param([string[]]$Paths, [int]$DropEffect = 1)
    $collection = New-Object System.Collections.Specialized.StringCollection
    foreach ($path in $Paths) { [void]$collection.Add($path) }

    $data = New-Object System.Windows.Forms.DataObject
    $data.SetFileDropList($collection)
    $data.SetData('Preferred DropEffect', (New-Object System.IO.MemoryStream (, [BitConverter]::GetBytes($DropEffect))))
    [System.Windows.Forms.Clipboard]::SetDataObject($data, $true)
}

# 用 Windows 自带的 IMAPI2FS 现造一个测试用 ISO（7-Zip 自己不会写 ISO）。造不出来返回 $false。
# 必须在“测试数据”之前定义：那一刻就要用它造 sample.iso。
function New-TestIso {
    param([string]$SourceDirectory, [string]$Destination)
    try {
        $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
        $fsi.FileSystemsToCreate = 3   # ISO9660 | Joliet（Joliet 才有小写名）
        $fsi.VolumeName = 'EXDIRTEST'
        $fsi.Root.AddTree($SourceDirectory, $false)
        $result = $fsi.CreateResultImage()
        [void][IsoStreamHelper]::CopyToFile($result.ImageStream, $Destination)
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($result)
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($fsi)
        return (Test-Path -LiteralPath $Destination)
    } catch {
        Write-Host ("  (造 ISO 失败：{0})" -f $_.Exception.Message)
        return $false
    }
}

# ------------------------------------------------------------------ 测试数据

$workRoot = Join-Path $env:TEMP 'exdir-archive-copy'
$staging = Join-Path $workRoot 'staging'
$outDir = Join-Path $workRoot 'outdir'
$elsewhere = Join-Path $workRoot 'elsewhere'
$zipPath = Join-Path $workRoot 'sample.zip'
$zetaFile = Join-Path $elsewhere 'zeta.txt'
$isoSource = Join-Path $workRoot 'iso-src'
$isoPath = Join-Path $workRoot 'sample.iso'
$isoOutDir = Join-Path $workRoot 'iso-out'

if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir, $elsewhere, $isoOutDir, "$staging\sub\deep", "$isoSource\sub" | Out-Null
[System.IO.File]::WriteAllText("$staging\hello.txt", 'hello 世界')
[System.IO.File]::WriteAllText("$staging\sub\inner.txt", 'inner')
[System.IO.File]::WriteAllText("$staging\sub\deep\deep.txt", 'deep')
[System.IO.File]::WriteAllText($zetaFile, 'zeta')
[System.IO.File]::WriteAllText("$isoSource\hello.txt", 'hello 世界')
[System.IO.File]::WriteAllText("$isoSource\sub\inner.txt", 'inner')
Compress-Archive -Path "$staging\*" -DestinationPath $zipPath -Force
Remove-Item $staging -Recurse -Force
$hasIso = New-TestIso -SourceDirectory $isoSource -Destination $isoPath

# 会话固定成「打开测试目录」，行名/行距稳定；扩展名必须显示（否则行名是 hello 不是 hello.txt）
if ($null -ne $originalSettings) { $json = $originalSettings | ConvertFrom-Json } else { $json = New-Object psobject }
$overrides = [ordered]@{
    PrimaryTabs      = @($workRoot)
    PrimaryActiveTab = 0
    SecondaryTabs    = @()
    IsDualPane       = $false
    IsSidebarVisible = $true
    ShowHiddenFiles  = $false
    ShowExtensions   = $true
    FoldersFirst     = $true
    RowHeight        = 28
    ColumnWidths     = @()
    ColumnAutoFit    = $true
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

# 文件列表的行就是 ListItem（侧边栏是 TreeItem、标签条是 TabItem，混不进来）
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

function Wait-RowCount {
    param($Session, [int]$Count, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Rows -Session $Session).Count -eq $Count) { return $true }
        Start-Sleep -Milliseconds 250
    }

    Write-Host ("  实际行: {0}" -f ((Get-RowNames -Session $Session) -join ', '))
    return $false
}

function Wait-RowName {
    param($Session, [string]$Name, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($null -ne (Get-RowByName -Session $Session -Name $Name)) { return $true }
        Start-Sleep -Milliseconds 250
    }

    Write-Host ("  实际行: {0}" -f ((Get-RowNames -Session $Session) -join ', '))
    return $false
}

function Select-Row {
    param($Session, [string]$Name)
    $row = Get-RowByName -Session $Session -Name $Name
    if ($null -eq $row) { throw "列表里找不到「$Name」" }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}

function Add-ToSelection {
    param($Session, [string]$Name)
    $row = Get-RowByName -Session $Session -Name $Name
    if ($null -eq $row) { throw "列表里找不到「$Name」" }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    Start-Sleep -Milliseconds 300
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

function Invoke-Expander {
    param($Session, $Row)
    $expander = Get-RowExpander -Session $Session -Row $Row
    if ($null -eq $expander) { return $false }
    $expander.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
    return $true
}

function Invoke-Button {
    param($Session, [string]$Name)
    $button = Find-VisibleFirst -From $Session.Root -Names @($Name) -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $button) { throw "找不到按钮「$Name」" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
}

function Wait-File {
    param([string]$Path, [int]$TimeoutSeconds = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { return $true }
        Start-Sleep -Milliseconds 400
    }

    return $false
}

# 用 UIA 模式展开菜单并执行菜单项（弹层有入场动画，真鼠标点容易打空，见 AGENTS.md 第 6 节第 26 条）。
# 菜单项要从**本进程**的窗口里找：别的程序也可能有一个叫「复制」的可见菜单项（见第 6 节第 53 条）。
function Invoke-MenuItem {
    param($Session, [string]$MenuName, [string]$ItemName)
    $menuBarItem = Find-VisibleFirst -From $Session.Root -Names @($MenuName) -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
    if ($null -eq $menuBarItem) { throw "主菜单里找不到「$MenuName」" }

    $menuBarItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)

    # 展开后偶尔一个菜单项都读不到（菜单项可用状态要在弹出时现查剪贴板，UIA 树也偶发迟一拍）
    # → 收起来再展开一次，别把这个当成产品问题（实测每十来次会碰上一次）。
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
    Start-Sleep -Milliseconds 800
}

# ------------------------------------------------------------------ 会话

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

# 再敲一次 exdir（第二个实例）：它应该很快自己退出，请求由已有实例处理（见 tools\test-command-line.ps1）
function Invoke-Cli {
    param($Session, [string[]]$ArgumentList = @())
    $second = Start-Process -FilePath $script:exePath -ArgumentList $ArgumentList -WorkingDirectory $script:exeDir -PassThru
    $exited = $second.WaitForExit(20000)
    if (-not $exited) { try { $second.Kill() } catch { } }
    Assert $exited '第二个进程很快自己退出（单实例闸门）'
    $session.Proc.Refresh()
}

function Get-CopyCacheLeftovers {
    if (-not (Test-Path $archiveCache)) { return 0 }
    $copyDir = Join-Path $archiveCache 'copy'
    if (-not (Test-Path $copyDir)) { return 0 }
    return @(Get-ChildItem $copyDir -Force).Count
}


# ================================================================== 用例

try {

Write-Host '=== 用例 1：exdir <压缩包> → 新标签页进包 ==='
$session = Start-Session -ArgumentList @($zipPath)
Assert (Wait-RowName -Session $session -Name 'hello.txt') '包内条目列出来了（hello.txt）'
Assert ((Get-RowNames -Session $session) -join ',' -eq 'hello.txt,sub') '包内就是那两项（hello.txt / sub）'
Assert ((Find-Elements -From $session.Root -Name '只读压缩包' | Where-Object { -not $_.Current.IsOffscreen }).Count -ge 1) '导航条上有「只读」徽标'
Assert ([ArchiveCopyNative]::IsWindowVisible($session.Handle)) '窗口可见'

Write-Host ''
Write-Host '=== 用例 2：包内选中文件 → 编辑菜单「复制」只记在内存里 ==='
Reset-LogMark
Select-Row -Session $session -Name 'hello.txt'
Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '复制'
Assert (Wait-NewLog -Pattern '复制压缩包内条目：1 项') '日志记下「复制压缩包内条目：1 项」'
Assert (-not (Test-Path -LiteralPath (Join-Path $workRoot 'hello.txt'))) '“复制”不在磁盘上产生任何东西（包内条目没有真实路径）'
Assert (@(Get-ClipboardFiles).Count -eq 0) '包内复制会清空系统剪贴板（两份内容互斥）'
Assert ((Get-RowNames -Session $session).Count -eq 2) '复制不会导航走（还在包内）'

Write-Host ''
Write-Host '=== 用例 3：上一级回真实目录 → 粘贴 → 文件真的解出来 ==='
Reset-LogMark
Invoke-Button -Session $session -Name '上一级'
Assert (Wait-RowName -Session $session -Name 'sample.zip') '「上一级」回到了压缩包所在的真实目录'
Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '粘贴'
$pasted = Join-Path $workRoot 'hello.txt'
Assert (Wait-File -Path $pasted) '粘贴后磁盘上真的出现了 hello.txt'
if (Test-Path -LiteralPath $pasted) {
    Assert ((Get-Content -LiteralPath $pasted -Raw) -match 'hello') '粘出来的内容与包内文件一致'
}
Assert (Wait-NewLog -Pattern '压缩包复制：sample\.zip 解出 1 项') '日志有「压缩包复制：sample.zip 解出 1 项」'
Assert (Wait-NewLog -Pattern '粘贴压缩包内条目：1 项') '日志有「粘贴压缩包内条目：1 项」'
Assert (@(Get-NewLogText | Select-String -Pattern '文件操作：复制 1 项').Count -ge 1) '真正搬文件的是外壳（日志「文件操作：复制 1 项」）'
Assert ((Get-CopyCacheLeftovers) -eq 0) '中转副本用完就删（archive-cache\copy 里没有残留）'

Write-Host ''
Write-Host '=== 用例 4：多选（文件 + 目录）一次复制 → 整棵子树都到位 ==='
Remove-Item -LiteralPath $pasted -Force -ErrorAction SilentlyContinue
Invoke-Cli -Session $session -ArgumentList @($zipPath)
Assert (Wait-RowCount -Session $session -Count 2) '新标签页也进了压缩包（包内 2 项）'
Reset-LogMark
Select-Row -Session $session -Name 'hello.txt'
Add-ToSelection -Session $session -Name 'sub'
Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '复制'
Assert (Wait-NewLog -Pattern '复制压缩包内条目：2 项') '多选时日志记的是「复制压缩包内条目：2 项」'
Invoke-Button -Session $session -Name '上一级'
Assert (Wait-RowName -Session $session -Name 'sample.zip') '「上一级」回到真实目录'
Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '粘贴'
Assert (Wait-File -Path (Join-Path $workRoot 'sub\inner.txt')) '多选粘贴：目录整棵子树到位（sub\inner.txt）'
Assert (Test-Path -LiteralPath (Join-Path $workRoot 'hello.txt')) '多选粘贴：文件也到位'
Assert (Test-Path -LiteralPath (Join-Path $workRoot 'sub\deep\deep.txt')) '子树里的深层文件也在'
if (Test-Path -LiteralPath (Join-Path $workRoot 'sub\inner.txt')) {
    Assert ((Get-Content -LiteralPath (Join-Path $workRoot 'sub\inner.txt') -Raw) -match 'inner') '子树里的内容正确'
}
Assert (Wait-NewLog -Pattern '粘贴压缩包内条目：2 项') '多选粘贴的日志也记下了'
Assert ((Get-CopyCacheLeftovers) -eq 0) '多选中转副本也没有残留'

Write-Host ''
Write-Host '=== 用例 5：系统剪贴板优先（别处复制来的东西不会被包内条目顶掉） ==='
Invoke-Cli -Session $session -ArgumentList @($zipPath)
Assert (Wait-RowCount -Session $session -Count 2) '再次进包（包内 2 项）'
Reset-LogMark
Select-Row -Session $session -Name 'hello.txt'
Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '复制'
Assert (Wait-NewLog -Pattern '复制压缩包内条目：1 项') '包内条目又记进了内存剪贴板'

# 模拟“另一个程序（资源管理器）把一批文件放进剪贴板”
Set-ClipboardFiles -Paths @($zetaFile) -DropEffect 1
Assert (@(Get-ClipboardFiles).Count -eq 1) '（前提）系统剪贴板上此刻是别的程序放的那个文件'

Reset-LogMark
Invoke-Cli -Session $session -ArgumentList @($outDir)
Assert (Wait-RowCount -Session $session -Count 0) '新标签页打开的是空目录 outdir'
Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '粘贴'
Assert (Wait-File -Path (Join-Path $outDir 'zeta.txt')) '粘进来的是系统剪贴板上的 zeta.txt'
Assert (-not (Test-Path -LiteralPath (Join-Path $outDir 'hello.txt'))) '包内那个 hello.txt 没有被粘进来（系统剪贴板优先）'
Assert ((Get-NewLogText) -notmatch '粘贴压缩包内条目') '这次粘贴没有走“解包内条目”那条路'

Write-Host ''
Write-Host '=== 用例 6：.iso 也像压缩包一样被浏览、被复制出来 ==='
if (-not $hasIso) {
    Write-Host 'SKIP 造不出测试 ISO（系统里没有 IMAPI2FS？）'
} else {
    Assert (Test-Path -LiteralPath $isoPath) '（前提）测试用的 sample.iso 造好了'

    Invoke-Cli -Session $session -ArgumentList @($isoPath)
    Assert (Wait-RowCount -Session $session -Count 2) 'exdir <sample.iso> 以目录形式进了光盘映像（hello.txt / sub）'
    Assert ((Get-RowNames -Session $session) -join ',' -eq 'hello.txt,sub') 'iso 根列出的就是那两项'

    Reset-LogMark
    Select-Row -Session $session -Name 'hello.txt'
    Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '复制'
    Assert (Wait-NewLog -Pattern '复制压缩包内条目：1 项') 'iso 里的条目也能「复制」（日志里同样算包内条目）'

    Invoke-Cli -Session $session -ArgumentList @($isoOutDir)
    Assert (Wait-RowCount -Session $session -Count 0) '新标签页打开的是空的 iso-out 目录'
    Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '粘贴'
    Assert (Wait-File -Path (Join-Path $isoOutDir 'hello.txt')) 'iso 里的文件被粘到了真实目录'
    Assert ((Get-Content -LiteralPath (Join-Path $isoOutDir 'hello.txt') -Raw) -match 'hello') '粘出来的内容与 iso 里的文件一致'
    Assert (Wait-NewLog -Pattern '压缩包复制：sample\.iso 解出 1 项') '日志有「压缩包复制：sample.iso 解出 1 项」'
    Assert (Wait-NewLog -Pattern '粘贴压缩包内条目：1 项') '日志有「粘贴压缩包内条目：1 项」'
    Assert ((Get-CopyCacheLeftovers) -eq 0) 'iso 的中转副本也没有残留'
}

Write-Host ''
Write-Host '=== 用例 7：真实目录里就地展开压缩包（不进包） ==='
# 先把前面几条造出来的东西清掉，行集合才是确定的（sample.zip / sample.iso + 几个目录）
Remove-Item -LiteralPath (Join-Path $workRoot 'hello.txt') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $workRoot 'sub') -Recurse -Force -ErrorAction SilentlyContinue

Invoke-Cli -Session $session -ArgumentList @($workRoot)
Assert (Wait-RowName -Session $session -Name 'sample.zip') '回到真实目录（能看到 sample.zip）'
Assert ((Find-Elements -From $session.Root -Name '只读压缩包' | Where-Object { -not $_.Current.IsOffscreen }).Count -eq 0) '（前提）真实目录里没有「只读」徽标'

$baseline = Get-RowNames -Session $session
$zipRow = Get-RowByName -Session $session -Name 'sample.zip'
$expander = Get-RowExpander -Session $session -Row $zipRow
Assert ($null -ne $expander) '压缩包文件行有行首展开箭头（与目录行同款）'

if ($null -ne $expander) {
    [void](Invoke-Expander -Session $session -Row $zipRow)
    Assert (Wait-RowName -Session $session -Name 'hello.txt') '就地展开后列表里多出包内条目（hello.txt）'
    $expanded = Get-RowNames -Session $session
    Assert (($expanded -contains 'sub') -and ($expanded -contains 'sample.zip')) '包内子目录与压缩包行本身都还在'
    Assert ($expanded.Count -eq ($baseline.Count + 2)) '只是在压缩包行下面插入了包内两行（没有整表重建）'
    Assert ((Find-Elements -From $session.Root -Name '只读压缩包' | Where-Object { -not $_.Current.IsOffscreen }).Count -eq 0) '就地展开不会进包（导航条上没有「只读」徽标）'

    Write-Host ''
    Write-Host '=== 用例 8：就地展开出来的包内行仍然是只读的 ==='
    Reset-LogMark
    Select-Row -Session $session -Name 'hello.txt'

    # 「复制」仍然走内存剪贴板（包内条目没有真实路径）
    Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '复制'
    Assert (Wait-NewLog -Pattern '复制压缩包内条目：1 项') '就地展开的行「复制」也记在内存里（日志「复制压缩包内条目：1 项」）'
    Assert (@(Get-ClipboardFiles).Count -eq 0) '就地展开的行复制时也清空系统剪贴板'

    # 「删除」被只读守卫拦住：日志里不应出现真的删除，磁盘也不变
    $beforeCount = @(Get-ChildItem $workRoot -Force).Count
    Reset-LogMark
    Invoke-MenuItem -Session $session -MenuName '编辑' -ItemName '删除'
    Start-Sleep -Milliseconds 1200
    Assert ((Get-NewLogText) -notmatch '删除：') '就地展开的行「删除」没有真的发起删除（只读守卫）'
    Assert (@(Find-Elements -From $session.Root -Name '压缩包内不支持该操作（只读浏览）').Count -ge 1) '就地展开的行「删除」弹出只读提示'
    Assert (@(Get-ChildItem $workRoot -Force).Count -eq $beforeCount) '就地展开的行「删除」没有在磁盘上改动任何东西'

    Write-Host ''
    Write-Host '=== 用例 9：再点一次箭头折叠回去 ==='
    $zipRow = Get-RowByName -Session $session -Name 'sample.zip'
    Assert (Invoke-Expander -Session $session -Row $zipRow) '折叠前还能找到压缩包行的箭头'
    Assert (Wait-RowCount -Session $session -Count $baseline.Count) '再点一次箭头把包内行全部折回去'
    Assert ((Get-RowNames -Session $session) -join ',' -eq ($baseline -join ',')) '折叠后的行集合与展开前完全一致'
}

Stop-Session -Session $session
}
finally {
    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    if ($null -ne $originalSettings) {
        Set-Content $settingsPath $originalSettings -Encoding utf8
        Write-Host '已还原 config.json'
    }

    if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }
    Write-Host '已删除测试目录'
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host '全部通过'
    exit 0
}

Write-Host ("SUMMARY failures={0}" -f $failures)
exit 1
