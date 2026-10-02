# 验证“把目录从文件列表 / 侧边栏拖到工具条固定目录区”与“工具条上拖拽固定目录排序”两条交互链路，
# 以及“把目录拖到侧边栏「收藏夹」分组”这条平行链路。
#
# 本机没有交互桌面时，鼠标事件送不到窗口（见 AGENTS.md 第 6 节第 18 条），脚本会先探测前台窗口。
# 拖动必须用 MOUSEEVENTF_ABSOLUTE 逐步移动（SetCursorPos 不会让 WinUI 收到 PointerMoved，见第 13 条）。
#
# 用法:
#   pwsh -NoProfile -File tools\test-pin-drag.ps1                 # Debug 版，跑全部用例
#   pwsh -NoProfile -File tools\test-pin-drag.ps1 -Exe dist\win-x64\exdir.exe
#
# 脚本会临时往固定的目录里塞两个条目，结束时从备份还原 config.json（并杀掉 exdir）。

param(
    [string]$Exe = "$PSScriptRoot\..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\exdir.exe",
    [string]$ShotDir = "$PSScriptRoot\..\.artifacts",
    [string]$ListFolder = '.cargo',
    [string]$TreeFolder = '图片',
    # 拖到侧边栏「收藏夹」的源目录（必须不在默认固定目录里，否则得不出“新增”的结论）
    [string]$FavoriteSourceFolder = '音乐',
    # 默认以普通权限启动 exdir（Windows 不允许提权进程参与拖放，详见 AGENTS.md）
    [switch]$AsAdmin
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PinDragNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public const uint MOVE = 0x0001;
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
    public const uint ABSOLUTE = 0x8000;
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP = 0x0010;
}
'@

[void][PinDragNative]::SetProcessDpiAwarenessContext([IntPtr][PinDragNative]::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
$ErrorActionPreference = 'Stop'

$exePath = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $exePath)) { throw "找不到可执行文件: $exePath" }

# 配置文件在 ~/.config/exdir/config.json（设了 XDG_CONFIG_HOME 就用它；见 Services/SettingsService.cs）
$configRoot = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $env:USERPROFILE '.config' }
$settingsPath = Join-Path $configRoot 'exdir\config.json'
$backupPath = "$settingsPath.pintest-backup"
$logPath = Join-Path $env:LOCALAPPDATA 'exdir\exdir.log'
$hadSettings = Test-Path $settingsPath
if ($hadSettings) { Copy-Item $settingsPath $backupPath -Force }

function Show-DragLog {
    if (-not (Test-Path $logPath)) { return }
    $lines = Select-String -Path $logPath -Pattern '拖拽|拖放|固定目录|收藏' | Select-Object -Last 8
    foreach ($line in $lines) { Write-Host "    log> $($line.Line)" -ForegroundColor DarkGray }
}

$shotDirFull = [System.IO.Path]::GetFullPath($ShotDir)
New-Item -ItemType Directory -Force -Path $shotDirFull | Out-Null
$stamp = Get-Date -Format 'HHmmss'

Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
Start-Sleep -Milliseconds 500

$proc = $null
$elevated = (New-Object System.Security.Principal.WindowsPrincipal(
        [System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)

if ($elevated -and -not $AsAdmin) {
    # 当前 shell 是管理员时，Start-Process 会让 exdir 也带着高完整性级别运行，
    # 而 Windows 直接禁止提权进程参与拖放（拖拽会“开始”但永远得不到 DragOver/Drop）。
    # 借 explorer.exe（中完整性级别）启动子进程就能降下来。
    Write-Host '当前 shell 已提权：改用 explorer.exe 以普通权限启动 exdir' -ForegroundColor Yellow
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exePath`""
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $proc = Get-Process -Name 'exdir' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) { break }
    }
}
else {
    $proc = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -PassThru
}

if (-not $proc) { throw '没有启动 exdir 进程' }
$handle = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $proc.MainWindowHandle; break }
}
if ($handle -eq [IntPtr]::Zero) { throw '未出现主窗口' }

Start-Sleep -Seconds 4
$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

function Get-Pins {
    if (-not (Test-Path $settingsPath)) { return @() }
    try { return @((Get-Content $settingsPath -Raw | ConvertFrom-Json).PinnedFolders) } catch { return @() }
}

# 固定目录按钮的 AutomationProperties.Name 是路径最后一段（和 PinnedFolderViewModel 一致）
function Get-PinName {
    param([string]$Path)
    $trimmed = $Path.TrimEnd('\')
    $name = [System.IO.Path]::GetFileName($trimmed)
    if ([string]::IsNullOrEmpty($name)) { return $trimmed }
    return $name
}

# 侧边栏「收藏夹」分组的子行：WinUI TreeView 在 UIA 里是平铺的（子项不是父项的 UIA 后代），
# 所以按 Y 坐标取“收藏夹”与它下面最近的那个分组标题之间的 TreeItem，顺序就是树里的顺序。
function Get-FavoriteRows {
    $fav = Find-Element -Name '收藏夹' -Type 'TreeItem'
    if ($null -eq $fav) { return @() }

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TreeItem)
    $fr = Get-Rect $fav

    # 下边界 = 收藏夹下面最近的那个分组标题（分组顺序可变，不能写死“云存储”）
    $bottom = $root.Current.BoundingRectangle.Bottom
    foreach ($name in @('主目录', '云存储', '此电脑')) {
        $group = Find-Element -Name $name -Type 'TreeItem'
        if ($null -eq $group) { continue }
        $gy = (Get-Rect $group).Y
        if ($gy -gt $fr.Y -and $gy -lt $bottom) { $bottom = $gy }
    }

    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) `
        | Where-Object { $_.Current.Name -and $_.Current.BoundingRectangle.Width -gt 0 } `
        | Where-Object { $_.Current.BoundingRectangle.Y -gt $fr.Y -and $_.Current.BoundingRectangle.Y -lt $bottom } `
        | Sort-Object { $_.Current.BoundingRectangle.Y })
}

function Find-Element {
    param([string]$Name, [int]$Index = 0, [string]$Type, $From)
    if (-not $From) { $From = $root }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name,
        [System.Windows.Automation.PropertyConditionFlags]::IgnoreCase)
    $found = $From.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $hits = if ($Type) { @($found | Where-Object { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$Type" }) } else { @($found) }
    if ($hits.Count -le $Index) { return $null }
    return $hits[$Index]
}

function Get-Rect($element) { return $element.Current.BoundingRectangle }

function Move-Mouse {
    param([int]$X, [int]$Y)
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $nx = [uint32][math]::Round($X * 65535 / ($screen.Width - 1))
    $ny = [uint32][math]::Round($Y * 65535 / ($screen.Height - 1))
    [PinDragNative]::mouse_event([PinDragNative]::MOVE -bor [PinDragNative]::ABSOLUTE, $nx, $ny, 0, [IntPtr]::Zero)
}

function Save-Shot {
    param([string]$Name)
    $rect = $root.Current.BoundingRectangle
    $w = [int]$rect.Width; $h = [int]$rect.Height
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $gfx.Dispose()
    $path = Join-Path $shotDirFull "pindrag-$stamp-$Name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  截图 $path"
}

function Invoke-Click {
    param([int]$X, [int]$Y, [string]$Button = 'Left')

    [void][PinDragNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400
    Move-Mouse $X $Y
    Start-Sleep -Milliseconds 250

    $down = if ($Button -eq 'Right') { [PinDragNative]::RIGHTDOWN } else { [PinDragNative]::LEFTDOWN }
    $up = if ($Button -eq 'Right') { [PinDragNative]::RIGHTUP } else { [PinDragNative]::LEFTUP }

    [PinDragNative]::mouse_event($down, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [PinDragNative]::mouse_event($up, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
}

function Invoke-Drag {
    param([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY, [string]$ShotDuring)

    [void][PinDragNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 600
    Move-Mouse $FromX $FromY
    Start-Sleep -Milliseconds 250

    [PinDragNative]::mouse_event([PinDragNative]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150

    # 逐步移动：一次跳跃会被当成“瞬移”，拖拽根本不会启动
    $steps = 24
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($FromX + ($ToX - $FromX) * $i / $steps)
        $y = [int]($FromY + ($ToY - $FromY) * $i / $steps)
        Move-Mouse $x $y
        Start-Sleep -Milliseconds 40
    }

    Start-Sleep -Milliseconds 700

    $pos = New-Object PinDragNative+POINT
    [void][PinDragNative]::GetCursorPos([ref]$pos)
    Write-Host "  拖拽中光标位置: ($($pos.X),$($pos.Y))（目标 ($ToX,$ToY)）"

    if ($ShotDuring) { Save-Shot -Name $ShotDuring }

    [PinDragNative]::mouse_event([PinDragNative]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
}

try {
    if ([PinDragNative]::GetForegroundWindow() -eq [IntPtr]::Zero) {
        Write-Host '没有交互桌面（GetForegroundWindow 返回 0）：无法模拟拖拽，只能人工验证。' -ForegroundColor Yellow
        return
    }

    $quick = Find-Element -Name '快捷菜单' -Type 'Button'
    if ($null -eq $quick) { throw '找不到“快捷菜单”按钮，无法定位固定目录区' }
    $qr = Get-Rect $quick
    $dropX = [int]($qr.X + $qr.Width / 2) - 80
    $dropY = [int]($qr.Y + $qr.Height / 2)
    Write-Host "拖放目标: ($dropX,$dropY)"

    $failures = 0

    # ---------------- 用例 0：侧边栏「收藏夹」分组镜像工具条固定目录 ----------------
    Write-Host "`n[0] 侧边栏「收藏夹」分组镜像固定目录" -ForegroundColor Cyan
    $favoritesNode = Find-Element -Name '收藏夹' -Type 'TreeItem'
    if ($null -eq $favoritesNode) {
        Write-Host '  侧边栏找不到「收藏夹」分组' -ForegroundColor Red
        $failures++
    }
    else {
        $expected = @(Get-Pins | ForEach-Object { Get-PinName $_ })
        $actual = @(Get-FavoriteRows | ForEach-Object { $_.Current.Name })
        Write-Host "  收藏夹子项 $($actual.Count) 个 / settings 里 $($expected.Count) 个: $($actual -join ', ')"
        if (($actual -join '|') -eq ($expected -join '|')) {
            Write-Host '  子项与固定目录一致（含顺序）' -ForegroundColor Green
        }
        else {
            Write-Host "  子项不匹配（期望: $($expected -join ', ')）" -ForegroundColor Red
            $failures++
        }
    }

    # ---------------- 用例 1：文件列表里的目录行 ----------------
    $before = Get-Pins
    Write-Host "`n[1] 文件列表：'$ListFolder' → 工具条" -ForegroundColor Cyan
    $row = Find-Element -Name $ListFolder
    if ($null -eq $row) {
        # 当前目录里没有这个文件夹时只是跳过（脚本默认假定工作目录里有 .cargo）
        Write-Host "  当前目录里没有 '$ListFolder'，跳过本用例" -ForegroundColor Yellow
    }
    else {
        $r = Get-Rect $row
        # 从行中间偏右按下，避开行首的展开箭头（那里会展开目录而不是拖拽）
        $fromX = [int]($r.X + [math]::Min(240, $r.Width / 2))
        $fromY = [int]($r.Y + $r.Height / 2)
        Write-Host "  从 ($fromX,$fromY) 拖到工具条"
        Invoke-Drag -FromX $fromX -FromY $fromY -ToX $dropX -ToY $dropY -ShotDuring 'list-dragover'
        Save-Shot -Name 'list-dropped'

        $after = Get-Pins
        $added = @($after | Where-Object { $_ -notin $before })
        if ($added.Count -eq 1) {
            Write-Host "  固定目录新增: $($added[0])" -ForegroundColor Green
        }
        else {
            Write-Host "  未新增固定目录（新增 $($added.Count) 项）: $($after -join ', ')" -ForegroundColor Red
            $failures++
        }

        Show-DragLog
    }

    # ---------------- 用例 2：侧边栏树节点 ----------------
    $before = Get-Pins
    Write-Host "`n[2] 侧边栏：'$TreeFolder' → 工具条" -ForegroundColor Cyan
    $treeNode = Find-Element -Name $TreeFolder -Type 'TreeItem'
    if ($null -eq $treeNode) {
        Write-Host "  找不到树节点 '$TreeFolder'" -ForegroundColor Yellow
        $failures++
    }
    else {
        $r = Get-Rect $treeNode
        # 树节点的文字在右侧，左侧是展开箭头
        $fromX = [int]($r.X + [math]::Min(150, $r.Width * 0.8))
        $fromY = [int]($r.Y + $r.Height / 2)
        Write-Host "  从 ($fromX,$fromY) 拖到工具条"
        Invoke-Drag -FromX $fromX -FromY $fromY -ToX $dropX -ToY $dropY -ShotDuring 'tree-dragover'
        Save-Shot -Name 'tree-dropped'

        $after = Get-Pins
        $added = @($after | Where-Object { $_ -notin $before })
        if ($added.Count -eq 1) {
            Write-Host "  固定目录新增: $($added[0])" -ForegroundColor Green
        }
        else {
            Write-Host "  未新增固定目录（新增 $($added.Count) 项）: $($after -join ', ')" -ForegroundColor Red
            $failures++
        }

        Show-DragLog
    }

    # ---------------- 用例 3：右键固定目录 → 取消固定 ----------------
    Write-Host "`n[3] 右键固定目录 → 取消固定" -ForegroundColor Cyan
    $before = Get-Pins
    $target = Find-Element -Name 'Desktop' -Type 'Button'
    if ($null -eq $target) {
        Write-Host '  找不到固定的 Desktop 按钮（可能已被移除）' -ForegroundColor Yellow
        $failures++
        Show-DragLog
    }
    else {
        $r = Get-Rect $target
        Invoke-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)) -Button 'Right'
        Save-Shot -Name 'pin-contextmenu'

        # 弹出菜单在 Desktop 下是独立的窗口，不在主窗口的 UIA 子树里，要从桌面根找
        $unpin = Find-Element -Name '取消固定' -Type 'MenuItem' -From ([System.Windows.Automation.AutomationElement]::RootElement)
        if ($null -eq $unpin) {
            Write-Host '  右键没有得到“取消固定”菜单项' -ForegroundColor Red
            $failures++
        }
        else {
            $ur = Get-Rect $unpin
            Invoke-Click -X ([int]($ur.X + $ur.Width / 2)) -Y ([int]($ur.Y + $ur.Height / 2))
            Save-Shot -Name 'pin-unpinned'

            $after = Get-Pins
            $removed = @($before | Where-Object { $_ -notin $after })
            if ($after.Count -eq $before.Count - 1) {
                Write-Host "  已移除: $($removed -join ', ')" -ForegroundColor Green
            }
            else {
                Write-Host "  取消固定后剩余 $($after.Count) 项（原 $($before.Count) 项）: $($after -join ', ')" -ForegroundColor Red
                $failures++
            }
        }
    }

    # ---------------- 用例 4：工具条上拖拽固定目录按钮调整顺序 ----------------
    Write-Host "`n[4] 工具条：拖拽固定目录按钮调整顺序" -ForegroundColor Cyan
    $before = Get-Pins
    if ($before.Count -lt 2) {
        Write-Host "  固定目录不足 2 个，跳过（当前 $($before.Count) 个）" -ForegroundColor Yellow
    }
    else {
        # 只留工具条那一行的按钮：同名的文件列表行/侧边栏节点也会被 Find-Element 搜到
        $quickRect = Get-Rect (Find-Element -Name '快捷菜单' -Type 'Button')
        $buttons = @()
        foreach ($path in $before) {
            for ($i = 0; $i -lt 10; $i++) {
                $btn = Find-Element -Name (Get-PinName $path) -Type 'Button' -Index $i
                if ($null -eq $btn) { break }

                $r = Get-Rect $btn
                if (($r.Y + $r.Height) -gt $quickRect.Y -and $r.Y -lt ($quickRect.Y + $quickRect.Height)) {
                    $buttons += [pscustomobject]@{ Name = (Get-PinName $path); Rect = $r }
                }
            }
        }
        $buttons = @($buttons | Sort-Object { $_.Rect.X })
        foreach ($b in $buttons) {
            Write-Host "    候选: $($b.Name) @ ($([int]$b.Rect.X),$([int]$b.Rect.Y)) $([int]$b.Rect.Width)x$([int]$b.Rect.Height)"
        }

        if ($buttons.Count -lt 2) {
            Write-Host "  工具条上只找到 $($buttons.Count) 个固定目录按钮" -ForegroundColor Red
            $failures++
        }
        else {
            $first = $buttons[0]
            $second = $buttons[1]
            Write-Host "  把 '$($first.Name)' 拖到 '$($second.Name)' 的右半边（换到它后面）"
            $fromX = [int]($first.Rect.X + $first.Rect.Width / 2)
            $fromY = [int]($first.Rect.Y + $first.Rect.Height / 2)
            $toX = [int]($second.Rect.X + $second.Rect.Width * 0.75)
            $toY = [int]($second.Rect.Y + $second.Rect.Height / 2)
            Invoke-Drag -FromX $fromX -FromY $fromY -ToX $toX -ToY $toY -ShotDuring 'reorder-dragover'
            Save-Shot -Name 'reorder-dropped'

            $after = Get-Pins
            if ($after.Count -eq $before.Count -and $after[0] -eq $before[1] -and $after[1] -eq $before[0]) {
                Write-Host "  顺序已交换: $($after -join ', ')" -ForegroundColor Green
            }
            else {
                Write-Host "  顺序未按预期变化（拖前: $($before -join ', ') / 拖后: $($after -join ', ')）" -ForegroundColor Red
                $failures++
            }

            Show-DragLog
        }
    }

    # ---------------- 用例 5：拖动目录到侧边栏「收藏夹」分组 ----------------
    Write-Host "`n[5] 侧边栏：'$FavoriteSourceFolder' → 「收藏夹」分组" -ForegroundColor Cyan
    $before = Get-Pins
    $favoritesNode = Find-Element -Name '收藏夹' -Type 'TreeItem'
    $sourceNode = Find-Element -Name $FavoriteSourceFolder -Type 'TreeItem'
    if ($null -eq $favoritesNode -or $null -eq $sourceNode) {
        Write-Host "  找不到「收藏夹」分组或 '$FavoriteSourceFolder' 树节点" -ForegroundColor Yellow
        $failures++
    }
    elseif ($before -contains (Join-Path $env:USERPROFILE $FavoriteSourceFolder)) {
        Write-Host "  '$FavoriteSourceFolder' 已经是固定目录，无法验证“新增”" -ForegroundColor Yellow
    }
    else {
        $fr = Get-Rect $favoritesNode
        $sr = Get-Rect $sourceNode
        $fromX = [int]($sr.X + [math]::Min(150, $sr.Width * 0.8))
        $fromY = [int]($sr.Y + $sr.Height / 2)
        $toX = [int]($fr.X + [math]::Min(140, $fr.Width * 0.7))
        $toY = [int]($fr.Y + $fr.Height / 2)
        Write-Host "  从 ($fromX,$fromY) 拖到收藏夹 ($toX,$toY)"
        Invoke-Drag -FromX $fromX -FromY $fromY -ToX $toX -ToY $toY -ShotDuring 'favorites-dragover'
        Save-Shot -Name 'favorites-dropped'

        $after = Get-Pins
        $added = @($after | Where-Object { $_ -notin $before })
        if ($added.Count -eq 1) {
            Write-Host "  收藏新增: $($added[0])" -ForegroundColor Green
        }
        else {
            Write-Host "  未新增收藏（新增 $($added.Count) 项）: $($after -join ', ')" -ForegroundColor Red
            $failures++
        }

        # 侧边栏也应该同步出现这个新的收藏项
        $addedName = if ($added.Count -eq 1) { Get-PinName $added[0] } else { $FavoriteSourceFolder }
        $rowNames = @(Get-FavoriteRows | ForEach-Object { $_.Current.Name })
        if ($rowNames -contains $addedName) {
            Write-Host "  侧边栏收藏夹里已出现 '$addedName'（现为: $($rowNames -join ', ')）" -ForegroundColor Green
        }
        else {
            Write-Host "  侧边栏收藏夹里没找到 '$addedName'（现为: $($rowNames -join ', ')）" -ForegroundColor Red
            $failures++
        }

        Show-DragLog
    }

    Write-Host "`n结论: $(if ($failures -eq 0) { '全部通过' } else { "$failures 个用例失败" })" -ForegroundColor $(if ($failures -eq 0) { 'Green' } else { 'Red' })
}
finally {
    try { $proc.Kill() } catch { }
    Start-Sleep -Milliseconds 800

    if ($hadSettings) { Copy-Item $backupPath $settingsPath -Force; Remove-Item $backupPath -Force }
    Write-Host 'config.json 已还原'
}
