# 压缩包只读浏览（双击压缩包 = 以目录形式进入）的 UIA + 真鼠标回归脚本。
#
# 用法:
#   pwsh -NoProfile -File tools\test-archive.ps1
#   pwsh -NoProfile -File tools\test-archive.ps1 -Exe dist\win-x64\exdir.exe
#
# 覆盖（见 AGENTS.md 第 4 节“压缩包只读浏览”）：
#   1. 双击 .zip → 当前标签页进入压缩包，列表是包内条目、面包屑出现压缩包名、导航条上有「只读」徽标
#   2. 压缩包根「上一级」回到真实目录；包内子目录「上一级」回到包根
#   3. 点面包屑里压缩包那一段 → 回包根
#   4. 包内目录行点行首箭头 → 就地展开（列表里多出子项）
#   5. 包内仍然能排序；行首有图标
#   6. 只读守卫：包内右键菜单里没有剪切/复制/粘贴/删除/属性/“在资源管理器中显示”，
#      空白处菜单里没有粘贴/新建文件夹/在此处打开终端；Ctrl+V 只弹只读提示、磁盘无变化；
#      包内拖拽根本不启动（日志里没有「拖拽开始」）
#   7. 双击包内文件 → 解到临时目录（日志有「打开压缩包内文件：」，临时文件真的在）
#   8. .tar.gz → 直接看到 tar 里的内容（透明解开中间那层）
#   9. 会话能恢复到包内目录
#  10. 命令行 exdir <压缩包> → 新标签页进入压缩包
#  11. .7z（装了 7-Zip 时；没装就 SKIP）
#
# 需要交互桌面（真鼠标双击 / 右键）。跑完还原 config.json 与临时目录。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$ShotDir = "$PSScriptRoot\..\.artifacts"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ArchiveTestNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const int HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;
    public const uint MOVE_ABS = 0x0001;
    public const uint MOVE_ABSOLUTE = 0x8000;
    public const uint GA_ROOT = 2;
}
'@

[void][ArchiveTestNative]::SetProcessDpiAwarenessContext([IntPtr][ArchiveTestNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$originalSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'

$failures = 0
function Assert {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "PASS $Message" }
    else { Write-Host "FAIL $Message"; $script:failures++ }
}

function Get-LogTail {
    param([int]$Lines = 200)
    if (-not (Test-Path $logPath)) { return @() }
    return @(Get-Content $logPath -Tail $Lines)
}

# ------------------------------------------------------------------ 测试数据

$workDir = Join-Path $env:TEMP 'exdir-archive-test'
if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
$staging = Join-Path $workDir 'staging'
New-Item -ItemType Directory -Force -Path "$staging\sub\deep", "$staging\空目录" | Out-Null
[System.IO.File]::WriteAllText("$staging\hello.txt", 'hello 世界')
[System.IO.File]::WriteAllText("$staging\sub\inner.txt", 'inner')
[System.IO.File]::WriteAllText("$staging\sub\deep\deep.txt", 'deep')

$zipPath = Join-Path $workDir 'sample.zip'
$tarGzPath = Join-Path $workDir 'sample.tar.gz'
$sevenZipPath = Join-Path $workDir 'sample.7z'

Compress-Archive -Path "$staging\*" -DestinationPath $zipPath -Force
& tar.exe -czf $tarGzPath -C $staging .
if ($LASTEXITCODE -ne 0) { throw 'tar.exe 造不出 tar.gz' }

$sevenZipExe = (Get-Command 7z.exe -ErrorAction SilentlyContinue)?.Source
if (-not $sevenZipExe -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZipExe = "$env:ProgramFiles\7-Zip\7z.exe" }
$hasSevenZip = $false
if ($sevenZipExe) {
    & $sevenZipExe a -t7z $sevenZipPath "$staging\*" | Out-Null
    $hasSevenZip = ($LASTEXITCODE -eq 0) -and (Test-Path $sevenZipPath)
}

Remove-Item $staging -Recurse -Force

# 让会话确定地打开这个目录：单窗格、不显示扩展名、行宽/行高默认、列宽自动填满、内置右键菜单
$json = Get-Content $settingsPath -Raw | ConvertFrom-Json
$json.PrimaryTabs = @($workDir)
$json.PrimaryActiveTab = 0
$json.SecondaryTabs = @()
$json.IsDualPane = $false
$json.ShowExtensions = $true
$json.ShowHiddenFiles = $false
$json.FoldersFirst = $true
$json.UseBuiltInContextMenu = $true
$json.IsSidebarVisible = $true
$json.SidebarWidth = 232
$json.ColumnWidths = @()
$json.ColumnAutoFit = $true
$json.ColumnAutoFillName = $true
$json.RowHeight = 28
$json.WindowMaximized = $false
$json.WindowX = 40
$json.WindowY = 40
$json.WindowWidth = 1100
$json.WindowHeight = 700
$json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8

# 预期行集合（ShowExtensions=true，所以名称就是完整文件名）
$folderRows = @('sample.tar.gz', 'sample.zip')
if ($hasSevenZip) { $folderRows += 'sample.7z' }
$zipRootRows = @('hello.txt', 'sub', '中文名称.txt')
$zipSubRows = @('deep', 'inner.txt')
$tarGzRows = @('hello.txt', 'sub', '空目录', '中文名称.txt')

function Sort-Rows { param($Rows) return @($Rows | Sort-Object) }

# ------------------------------------------------------------------ 会话

function Start-Session {
    param([string]$Argument = '')

    Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); $_.WaitForExit(3000) } catch { } }

    $proc = if ($Argument -eq '') {
        Start-Process -FilePath $script:exePath -WorkingDirectory (Split-Path $script:exePath) -PassThru
    } else {
        Start-Process -FilePath $script:exePath -ArgumentList $Argument -WorkingDirectory (Split-Path $script:exePath) -PassThru
    }

    $handle = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $proc.MainWindowHandle; break }
    }
    if ($handle -eq [IntPtr]::Zero) { throw '未出现主窗口' }

    [void][ArchiveTestNative]::SetWindowPos($handle, [IntPtr][ArchiveTestNative]::HWND_TOPMOST, 0, 0, 0, 0,
        [ArchiveTestNative]::SWP_NOMOVE -bor [ArchiveTestNative]::SWP_NOSIZE -bor [ArchiveTestNative]::SWP_SHOWWINDOW)
    [void][ArchiveTestNative]::SetForegroundWindow($handle)
    Start-Sleep -Seconds 5

    return [pscustomobject]@{
        Proc   = $proc
        Handle = $handle
        Root   = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        Dpi    = [ArchiveTestNative]::GetDpiForWindow($handle)
    }
}

function Stop-Session {
    param($Session)
    try { if (-not $Session.Proc.HasExited) { $Session.Proc.Kill() } } catch { }
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

function Get-Rows {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::ListItem) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
}

function Get-RowNames {
    param($Session)
    return Sort-Rows (@((Get-Rows -Session $Session) | ForEach-Object { $_.Current.Name }))
}

function Wait-RowNames {
    param($Session, [string[]]$Expected, [int]$TimeoutSeconds = 12)
    $expectedSorted = Sort-Rows $Expected
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $names = Get-RowNames -Session $Session
    while ((Get-Date) -lt $deadline) {
        if (($names -join '|') -eq ($expectedSorted -join '|')) { return $names }
        Start-Sleep -Milliseconds 250
        $names = Get-RowNames -Session $Session
    }
    return $names
}

function Get-RowByName {
    param($Session, [string]$Name)
    return @(Get-Rows -Session $Session | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1)[0]
}

function Get-CrumbNames {
    param($Session)
    return @(Find-ByType -From $Session.Root -ControlType ([System.Windows.Automation.ControlType]::Button) |
        ForEach-Object { $_.Current.Name })
}

function Get-ReadOnlyBadgeCount {
    param($Session)
    return (Find-Elements -From $Session.Root -Name '只读压缩包' |
        Where-Object { -not $_.Current.IsOffscreen }).Count
}

# ------------------------------------------------------------------ 真鼠标

function Focus-Session {
    param($Session, [int]$X, [int]$Y)
    for ($i = 0; $i -lt 8; $i++) {
        $owner = [ArchiveTestNative]::GetAncestor([ArchiveTestNative]::WindowFromPoint($X, $Y), [ArchiveTestNative]::GA_ROOT)
        if (($owner -eq $Session.Handle) -and ([ArchiveTestNative]::GetForegroundWindow() -eq $Session.Handle)) { return $true }
        try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') } catch { }
        Start-Sleep -Milliseconds 250
        [void][ArchiveTestNative]::SetWindowPos($Session.Handle, [IntPtr][ArchiveTestNative]::HWND_TOPMOST, 0, 0, 0, 0,
            [ArchiveTestNative]::SWP_NOMOVE -bor [ArchiveTestNative]::SWP_NOSIZE -bor [ArchiveTestNative]::SWP_SHOWWINDOW)
        [void][ArchiveTestNative]::SetForegroundWindow($Session.Handle)
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Park-Cursor {
    param($Session)
    $rect = New-Object ArchiveTestNative+RECT
    [void][ArchiveTestNative]::GetWindowRect($Session.Handle, [ref]$rect)
    [void][ArchiveTestNative]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), $rect.Top + 18)
    Start-Sleep -Milliseconds 200
}

function Invoke-DoubleClick {
    param($Session, [int]$X, [int]$Y)
    Park-Cursor -Session $Session
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    [void][ArchiveTestNative]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt 2; $i++) {
        [ArchiveTestNative]::mouse_event([ArchiveTestNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 40
        [ArchiveTestNative]::mouse_event([ArchiveTestNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 1200
}

function Invoke-Click {
    param($Session, [int]$X, [int]$Y)
    Park-Cursor -Session $Session
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    [void][ArchiveTestNative]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 120
    [ArchiveTestNative]::mouse_event([ArchiveTestNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [ArchiveTestNative]::mouse_event([ArchiveTestNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

function Invoke-RightClick {
    param($Session, [int]$X, [int]$Y)
    Park-Cursor -Session $Session
    [void](Focus-Session -Session $Session -X $X -Y $Y)
    [void][ArchiveTestNative]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 200
    [ArchiveTestNative]::mouse_event([ArchiveTestNative]::RIGHTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [ArchiveTestNative]::mouse_event([ArchiveTestNative]::RIGHTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
}

# 想真的拖动：按下 → 逐步移动（每步都要发 MOUSEEVENTF_MOVE，SetCursorPos 不会产生 PointerMoved）
function Invoke-DragAttempt {
    param($Session, [int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY)
    Park-Cursor -Session $Session
    [void](Focus-Session -Session $Session -X $FromX -Y $FromY)
    [void][ArchiveTestNative]::SetCursorPos($FromX, $FromY)
    Start-Sleep -Milliseconds 200
    [ArchiveTestNative]::mouse_event([ArchiveTestNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200

    for ($i = 1; $i -le 8; $i++) {
        $x = [int]($FromX + ($ToX - $FromX) * $i / 8)
        $y = [int]($FromY + ($ToY - $FromY) * $i / 8)
        [ArchiveTestNative]::SetCursorPos($x, $y)
        [ArchiveTestNative]::mouse_event([ArchiveTestNative]::MOVE_ABS, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }

    [ArchiveTestNative]::mouse_event([ArchiveTestNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 800
}

function Send-Keys {
    param($Session, [string[]]$Keys)
    [void][ArchiveTestNative]::SetWindowPos($Session.Handle, [IntPtr][ArchiveTestNative]::HWND_TOPMOST, 0, 0, 0, 0,
        [ArchiveTestNative]::SWP_NOMOVE -bor [ArchiveTestNative]::SWP_NOSIZE -bor [ArchiveTestNative]::SWP_SHOWWINDOW)
    [void][ArchiveTestNative]::SetForegroundWindow($Session.Handle)
    Start-Sleep -Milliseconds 400
    foreach ($key in $Keys) {
        [System.Windows.Forms.SendKeys]::SendWait($key)
        Start-Sleep -Milliseconds 500
    }
}

function Dismiss-Menu {
    param($Session)
    try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') } catch { }
    Start-Sleep -Milliseconds 400
}

function Find-MenuItems {
    param($Session)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)
    $items = @()
    for ($i = 0; $i -lt 15; $i++) {
        $items = @($desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
            Where-Object {
                $_.Current.ProcessId -eq $Session.Proc.Id -and -not $_.Current.IsOffscreen -and
                $_.Current.BoundingRectangle.Width -gt 0
            })
        if ($items.Count -gt 0) { break }
        Start-Sleep -Milliseconds 200
    }
    return @($items)
}

function Get-MenuItemNames {
    param($Session)
    return @(Find-MenuItems -Session $Session | ForEach-Object { $_.Current.Name })
}

function Save-Shot {
    param($Session, [string]$Name)
    # 截图需要交互桌面（没有的话 CopyFromScreen 会报“句柄无效”，见 AGENTS.md 第 6 节第 18 条）：
    # 那只是拿不到证据附件，不影响断言，所以这里只警告不抛
    try {
        New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
        $shot = Join-Path ([System.IO.Path]::GetFullPath($ShotDir)) "archive-$Name.png"
        $rect = New-Object ArchiveTestNative+RECT
        [void][ArchiveTestNative]::GetWindowRect($Session.Handle, [ref]$rect)
        $w = $rect.Right - $rect.Left
        $h = $rect.Bottom - $rect.Top
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $gfx = [System.Drawing.Graphics]::FromImage($bmp)
        $gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
        $gfx.Dispose()
        $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host "已保存 $shot"
    } catch {
        Write-Host "  (截图跳过：$($_.Exception.Message))"
    }
}

# ================================================================== 开跑

try {

$session = Start-Session
$scale = $session.Dpi / 96.0
Write-Host ("  缩放: {0:P0}" -f $scale)

$names = Wait-RowNames -Session $session -Expected $folderRows
Write-Host ("  测试目录: {0}" -f ($names -join ', '))
Assert (($names -join ',') -eq ((Sort-Rows $folderRows) -join ',')) '测试目录里能看到压缩包（真实目录浏览未受影响）'

# ------------------------------------------------------------------ 用例 1：双击 zip 进包

Write-Host '--- 用例 1：双击 sample.zip → 进入压缩包 ---'
$zipRow = Get-RowByName -Session $session -Name 'sample.zip'
Assert ($null -ne $zipRow) '找到 sample.zip 行'
$zipRect = $zipRow.Current.BoundingRectangle
Invoke-DoubleClick -Session $session -X ([int]($zipRect.X + $zipRect.Width / 2)) -Y ([int]($zipRect.Y + $zipRect.Height / 2))

$names = Wait-RowNames -Session $session -Expected $zipRootRows
Write-Host ("  进包后的行: {0}" -f ($names -join ', '))
Assert (($names -join ',') -eq ((Sort-Rows $zipRootRows) -join ',')) '.zip 双击后列表是包内条目'
$crumbs = Get-CrumbNames -Session $session
Assert ($crumbs -contains 'sample.zip') '面包屑里出现压缩包名'
Assert ((Get-ReadOnlyBadgeCount -Session $session) -eq 1) '导航条上出现「只读」徽标'
Assert (@(Get-LogTail | Where-Object { $_ -match '压缩包：sample\.zip 共 \d+ 项' }).Count -ge 1) '日志里记下了打开压缩包与条目数'
Save-Shot -Session $session -Name 'zip-root'

# ------------------------------------------------------------------ 用例 2：上一级

Write-Host '--- 用例 2：压缩包根「上一级」回到真实目录 ---'
Send-Keys -Session $session -Keys @('%{UP}')
$names = Wait-RowNames -Session $session -Expected $folderRows
Assert (($names -join ',') -eq ((Sort-Rows $folderRows) -join ',')) '包根按上一级回到压缩包所在目录'

# ------------------------------------------------------------------ 用例 3/4：进子目录、面包屑回包根、行内展开

Write-Host '--- 用例 3：进包内子目录 → 点面包屑里的压缩包名回包根 ---'
$zipRow = Get-RowByName -Session $session -Name 'sample.zip'
$zipRect = $zipRow.Current.BoundingRectangle
Invoke-DoubleClick -Session $session -X ([int]($zipRect.X + $zipRect.Width / 2)) -Y ([int]($zipRect.Y + $zipRect.Height / 2))
[void](Wait-RowNames -Session $session -Expected $zipRootRows)

$subRow = Get-RowByName -Session $session -Name 'sub'
$subRect = $subRow.Current.BoundingRectangle
Invoke-DoubleClick -Session $session -X ([int]($subRect.X + $subRect.Width / 2)) -Y ([int]($subRect.Y + $subRect.Height / 2))
$names = Wait-RowNames -Session $session -Expected $zipSubRows
Write-Host ("  包内子目录的行: {0}" -f ($names -join ', '))
Assert (($names -join ',') -eq ((Sort-Rows $zipSubRows) -join ',')) '双击包内目录行能进去'

$crumbs = Get-CrumbNames -Session $session
Assert ($crumbs -contains 'sample.zip') '包内子目录的面包屑里仍然有压缩包那一段'

$zipCrumb = @(Find-ByType -From $session.Root -ControlType ([System.Windows.Automation.ControlType]::Button) |
    Where-Object { $_.Current.Name -eq 'sample.zip' })[0]
Assert ($null -ne $zipCrumb) '找到面包屑里的 sample.zip 段'
if ($zipCrumb) {
    $r = $zipCrumb.Current.BoundingRectangle
    Invoke-Click -Session $session -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))
    $names = Wait-RowNames -Session $session -Expected $zipRootRows
    Assert (($names -join ',') -eq ((Sort-Rows $zipRootRows) -join ',')) '点面包屑里的压缩包段回到包根'
}

Write-Host '--- 用例 4：包内目录行点行首箭头 → 就地展开 ---'
$subRow = Get-RowByName -Session $session -Name 'sub'
$subRect = $subRow.Current.BoundingRectangle
$arrows = @(Find-Elements -From $session.Root -Name '展开或折叠' | Where-Object {
        $b = $_.Current.BoundingRectangle
        (-not $_.Current.IsOffscreen) -and $b.Height -gt 0 -and
        $b.Top -ge $subRect.Top -and $b.Bottom -le $subRect.Bottom -and
        $b.Left -ge $subRect.Left -and $b.Right -le $subRect.Right
    })
Assert ($arrows.Count -ge 1) '包内目录行里有展开箭头'
if ($arrows.Count -ge 1) {
    $a = $arrows[0].Current.BoundingRectangle
    Invoke-Click -Session $session -X ([int]($a.Left + $a.Width / 2)) -Y ([int]($a.Top + $a.Height / 2))
    $names = Wait-RowNames -Session $session -Expected ($zipRootRows + $zipSubRows)
    Write-Host ("  展开后的行: {0}" -f ($names -join ', '))
    Assert (($names -join ',') -eq ((Sort-Rows ($zipRootRows + $zipSubRows)) -join ',')) '包内目录能就地展开（列表多出 deep / inner）'
    Assert ((Get-CrumbNames -Session $session) -contains 'sample.zip') '就地展开不会导航走（还在包根）'
    Save-Shot -Session $session -Name 'zip-expanded'
}

# ------------------------------------------------------------------ 用例 5：排序与图标

Write-Host '--- 用例 5：包内仍能排序、行首有图标 ---'
$before = Get-RowNames -Session $session
$sortHeader = @(Find-ByType -From $session.Root -ControlType ([System.Windows.Automation.ControlType]::Button) |
    Where-Object { $_.Current.Name -eq '按名称排序' })[0]
Assert ($null -ne $sortHeader) '包内仍有「按名称排序」列头'
if ($sortHeader) {
    $r = $sortHeader.Current.BoundingRectangle
    Invoke-Click -Session $session -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 600
    $after = Get-RowNames -Session $session
    Assert (($after -join ',') -ne ($before -join ',')) '点列头后包内行顺序变了（排序仍然可用）'
    Invoke-Click -Session $session -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 600
}
Assert ((Find-Elements -From $session.Root -Name '程序图标' |
    Where-Object { -not $_.Current.IsOffscreen }).Count -ge 1) '包内的行首有真实图标（虚拟目录用通用文件夹图标）'

# ------------------------------------------------------------------ 用例 6：只读守卫

Write-Host '--- 用例 6：包内只读守卫 ---'
$fileRow = Get-RowByName -Session $session -Name 'hello.txt'
$fileRect = $fileRow.Current.BoundingRectangle
Invoke-RightClick -Session $session -X ([int]($fileRect.X + $fileRect.Width / 3)) -Y ([int]($fileRect.Y + $fileRect.Height / 2))

$items = Get-MenuItemNames -Session $session
Write-Host ("  包内文件菜单: {0}" -f ($items -join ' | '))
Assert ($items -contains '打开') '包内文件菜单里有「打开」'
Assert ($items -contains '复制路径') '包内文件菜单里有「复制路径」'
foreach ($absent in '剪切', '复制', '粘贴', '删除', '属性', '在资源管理器中显示') {
    Assert ($items -notcontains $absent) ("包内文件菜单里没有「{0}」" -f $absent)
}
Save-Shot -Session $session -Name 'context-menu-file'
Dismiss-Menu -Session $session

$rows = Get-Rows -Session $session
$lastRow = ($rows | Sort-Object { $_.Current.BoundingRectangle.Top } | Select-Object -Last 1).Current.BoundingRectangle
$blankX = [int]($lastRow.Left + $lastRow.Width / 2)
$blankY = [int]($lastRow.Bottom + 40)
Invoke-RightClick -Session $session -X $blankX -Y $blankY
$items = Get-MenuItemNames -Session $session
Write-Host ("  包内空白处菜单: {0}" -f ($items -join ' | '))
Assert ($items -contains '刷新') '包内空白处菜单里有「刷新」'
Assert ($items -contains '全选') '包内空白处菜单里有「全选」'
foreach ($absent in '粘贴', '新建文件夹', '在此处打开终端') {
    Assert ($items -notcontains $absent) ("包内空白处菜单里没有「{0}」" -f $absent)
}
Save-Shot -Session $session -Name 'context-menu-background'
Dismiss-Menu -Session $session

# Ctrl+V：只弹只读提示，磁盘上什么都不该多出来
$beforeFiles = (Get-ChildItem $workDir -Force | Measure-Object).Count
Send-Keys -Session $session -Keys @('^v')
Start-Sleep -Seconds 1
$infoBar = @(Find-Elements -From $session.Root -Name '压缩包内不支持该操作（只读浏览）')
$infoBar += @(Find-ByType -From $session.Root -ControlType ([System.Windows.Automation.ControlType]::Text) |
    Where-Object { $_.Current.Name -like '压缩包内不支持该操作*' })
Assert ($infoBar.Count -ge 1) '包内 Ctrl+V 弹出「压缩包内不支持该操作（只读浏览）」'
$afterFiles = (Get-ChildItem $workDir -Force | Measure-Object).Count
Assert ($beforeFiles -eq $afterFiles) '包内 Ctrl+V 没有在磁盘上建出任何文件'

# 拖拽：包内条目根本没有真实路径，手势不该启动
$logBefore = (Get-LogTail -Lines 400 | Where-Object { $_ -match '拖拽开始（文件列表）' }).Count
Invoke-DragAttempt -Session $session -FromX ([int]($fileRect.X + $fileRect.Width / 3)) -FromY ([int]($fileRect.Y + $fileRect.Height / 2)) `
    -ToX ([int]($fileRect.X + $fileRect.Width / 2)) -ToY ([int]($fileRect.Y + $fileRect.Height * 2.5))
$logAfter = (Get-LogTail -Lines 400 | Where-Object { $_ -match '拖拽开始（文件列表）' }).Count
Assert ($logAfter -eq $logBefore) '包内拖拽不产生「拖拽开始」日志（手势被取消）'

# ------------------------------------------------------------------ 用例 7：双击包内文件 → 临时目录

Write-Host '--- 用例 7：双击包内文件 → 解到临时目录打开 ---'
$fileRow = Get-RowByName -Session $session -Name 'hello.txt'
$fileRect = $fileRow.Current.BoundingRectangle
Invoke-DoubleClick -Session $session -X ([int]($fileRect.X + $fileRect.Width / 2)) -Y ([int]($fileRect.Y + $fileRect.Height / 2))
Start-Sleep -Seconds 2

$openLine = @(Get-LogTail -Lines 200 | Where-Object { $_ -match '打开压缩包内文件：(.*) :: (.*) → (.*)$' } | Select-Object -Last 1)
Assert ($openLine.Count -ge 1) '日志里有「打开压缩包内文件：… → 临时文件」'
if ($openLine.Count -ge 1) {
    $null = $openLine[0] -match '打开压缩包内文件：(.*) :: (.*) → (.*)$'
    $tempFile = $Matches[3].Trim()
    Assert (Test-Path $tempFile) ("临时文件真的存在：{0}" -f $tempFile)
    Assert ((Get-Content $tempFile -Raw) -match 'hello') '临时文件内容与包内文件一致'
}
Assert ((Get-RowNames -Session $session) -join ',' -eq ((Sort-Rows ($zipRootRows + $zipSubRows)) -join ',')) '双击包内文件不会导航走'

# ------------------------------------------------------------------ 用例 8：.tar.gz 透明解开

Write-Host '--- 用例 8：tar.gz 直接看到 tar 内容 ---'
Send-Keys -Session $session -Keys @('%{UP}')
[void](Wait-RowNames -Session $session -Expected $folderRows)

$tarRow = Get-RowByName -Session $session -Name 'sample.tar.gz'
$tarRect = $tarRow.Current.BoundingRectangle
Invoke-DoubleClick -Session $session -X ([int]($tarRect.X + $tarRect.Width / 2)) -Y ([int]($tarRect.Y + $tarRect.Height / 2))
$names = Wait-RowNames -Session $session -Expected $tarGzRows
Write-Host ("  tar.gz 的行: {0}" -f ($names -join ', '))
Assert (($names -join ',') -eq ((Sort-Rows $tarGzRows) -join ',')) '.tar.gz 双击直接看到 tar 里的内容（不是只有一行 sample.tar）'
Assert (@(Get-LogTail | Where-Object { $_ -match 'sample\.tar\.gz 的内层 tar 已解开' }).Count -ge 1) '日志里记下了内层 tar 已解开'
Save-Shot -Session $session -Name 'targz-root'

# ------------------------------------------------------------------ 用例 10：命令行进包

Write-Host '--- 用例 10：命令行 exdir <压缩包> → 新标签页进包 ---'
Send-Keys -Session $session -Keys @('%{UP}')
[void](Wait-RowNames -Session $session -Expected $folderRows)
Stop-Session -Session $session

$session = Start-Session -Argument $zipPath
$names = Wait-RowNames -Session $session -Expected $zipRootRows
Assert (($names -join ',') -eq ((Sort-Rows $zipRootRows) -join ',')) '命令行给压缩包路径也能进包'
$tabs = @(Find-ByType -From $session.Root -ControlType ([System.Windows.Automation.ControlType]::TabItem))
Assert ($tabs.Count -eq 2) '命令行请求是「新开一个标签页」（原有标签页 + 压缩包标签页）'

# ------------------------------------------------------------------ 用例 9：会话恢复到包内

Write-Host '--- 用例 9：会话能恢复到包内目录 ---'
$subRow = Get-RowByName -Session $session -Name 'sub'
$subRect = $subRow.Current.BoundingRectangle
Invoke-DoubleClick -Session $session -X ([int]($subRect.X + $subRect.Width / 2)) -Y ([int]($subRect.Y + $subRect.Height / 2))
[void](Wait-RowNames -Session $session -Expected $zipSubRows)

# Alt+F4 = 关闭窗口 → 隐藏到托盘并落盘会话（见 AGENTS.md 第 4 节“托盘驻留”）
Send-Keys -Session $session -Keys @('%{F4}')
Start-Sleep -Seconds 2
Stop-Session -Session $session

$saved = @((Get-Content $settingsPath -Raw | ConvertFrom-Json).PrimaryTabs)
Write-Host ("  落盘的标签页: {0}" -f ($saved -join ' | '))
Assert (@($saved | Where-Object { $_ -match 'sample\.zip\\sub$' }).Count -ge 1) 'config.json 里记下了包内目录'

$session = Start-Session
$names = Wait-RowNames -Session $session -Expected $zipSubRows
Write-Host ("  恢复后的行: {0}" -f ($names -join ', '))
Assert (($names -join ',') -eq ((Sort-Rows $zipSubRows) -join ',')) '重启后会话恢复到包内子目录'

# ------------------------------------------------------------------ 用例 11：.7z

Write-Host '--- 用例 11：.7z ---'
if ($hasSevenZip) {
    Send-Keys -Session $session -Keys @('%{UP}', '%{UP}')
    [void](Wait-RowNames -Session $session -Expected $folderRows)
    $row = Get-RowByName -Session $session -Name 'sample.7z'
    Assert ($null -ne $row) '找到 sample.7z 行'
    if ($row) {
        $r = $row.Current.BoundingRectangle
        Invoke-DoubleClick -Session $session -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))
        $names = Wait-RowNames -Session $session -Expected $zipRootRows
        Assert (($names -join ',') -eq ((Sort-Rows $zipRootRows) -join ',')) '双击 .7z 也以目录形式进入'
    }
} else {
    Write-Host 'SKIP 没找到 7z.exe（装个 7-Zip 再跑这一条）'
}

Stop-Session -Session $session
}
catch {
    Write-Host ("FAIL 运行中抛出异常：{0}" -f $_)
    $failures++
}
finally {
    # 还原一定要走到：中途 throw（例如 UIA 查不到元素）不能把用户的 config.json 留在测试状态
    Get-Process exdir -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    if ($null -ne $originalSettings) {
        Set-Content $settingsPath $originalSettings -Encoding utf8
        Write-Host '已还原 config.json'
    }

    if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force }
    Write-Host '已删除测试目录'
}

Write-Host ("SUMMARY failures={0}" -f $failures)
if ($failures -gt 0) { exit 1 }
