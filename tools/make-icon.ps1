# 从仓库根目录的 icon.svg 生成程序图标：
#   Assets\exdir.ico   —— 程序图标本体：exe 内嵌图标（<ApplicationIcon>）、标题栏/任务栏图标
#                         （AppWindow.SetIcon）、托盘图标（System.Drawing.Icon），三处都用它
#   Assets\*.png       —— MSIX 打包用的徽标（非打包部署读不到，但顺手一起生成，
#                         免得它们还是模板自带的灰色占位图，和新图标对不上）
#
# 用法: pwsh -NoProfile -File tools\make-icon.ps1
#       pwsh -NoProfile -File tools\make-icon.ps1 -Svg D:\path\other.svg
#       pwsh -NoProfile -File tools\make-icon.ps1 -Edge "C:\...\msedge.exe"   # 或设 EXDIR_EDGE
#
# 为什么用 Edge 渲染：
#   .ico 里装的是位图，SVG 必须先栅格化。.NET 没有内置的 SVG 渲染器，
#   而 Edge（Chromium）是 Windows 自带、无需额外安装的渲染器：
#   把 SVG 根元素的宽高改成目标像素（viewBox 保留），用 `--headless=new --screenshot` 截图，
#   透明底靠 --default-background-color=00000000。这样就不依赖 ImageMagick / Inkscape / VS 工具链。
#   **每个尺寸都按原尺寸单独渲染一次**（不是渲染一张大图再缩），小尺寸才不会糊。
#
# 两个必须注意的坑（都实测踩过）：
#   1. headless Edge 的主进程退出后，renderer / gpu / network / crashpad 等子进程有时还挂着，
#      它们占着 user-data-dir；下一次用同一个 profile 启动时 Edge 会把任务交给那个僵尸实例，
#      于是 --screenshot 根本没执行（文件不存在）——残留几十个之后次次失败。所以每次截完图
#      都要按 profile 路径把这一批 msedge 进程收掉（见 Remove-ProfileProcesses）。
#   2. 即使进程正常退出，headless 也偶尔在页面还没画出来时截图，产出一张尺寸正确但全透明的 PNG。
#      所以每张图都要校验（尺寸对 + 有内容），不合格就重试。
#
# 写 .ico 容器：手工拼 BITMAPINFOHEADER + 32bpp BGRA（自下而上）+ AND 掩码，不依赖第三方库。

[CmdletBinding()]
param(
    [string]$Svg,
    [string]$Edge
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assetsDir = Join-Path $root 'Assets'
$icoTarget = Join-Path $assetsDir 'exdir.ico'

if (-not $Svg) { $Svg = Join-Path $root 'icon.svg' }
$Svg = [System.IO.Path]::GetFullPath($Svg)
if (-not (Test-Path -LiteralPath $Svg)) { throw "找不到 SVG 源文件：$Svg" }

# ICO 里的尺寸：16/24/32 给标题栏与托盘（太小的话外壳会自己缩，缩出来更糊），
# 48 给"中图标"视图，256 给大图标。128/64 是 Windows 会挑到的中间档。
$icoSizes = @(16, 24, 32, 48, 64, 128, 256)

# MSIX 徽标（Name / 画布宽 / 画布高）：方形的一比一放图标，宽扁的居中放（高度定尺寸）
$logoSizes = @(
    [pscustomobject]@{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Width = 24;   Height = 24 },
    [pscustomobject]@{ Name = 'LockScreenLogo.scale-200.png';                       Width = 48;   Height = 48 },
    [pscustomobject]@{ Name = 'StoreLogo.png';                                      Width = 50;   Height = 50 },
    [pscustomobject]@{ Name = 'Square44x44Logo.scale-200.png';                      Width = 88;   Height = 88 },
    [pscustomobject]@{ Name = 'Square150x150Logo.scale-200.png';                    Width = 300;  Height = 300 },
    [pscustomobject]@{ Name = 'Wide310x150Logo.scale-200.png';                      Width = 620;  Height = 300 },
    [pscustomobject]@{ Name = 'SplashScreen.scale-200.png';                         Width = 1240; Height = 600 }
)

# --------------------------------------------------------------------------
# Edge 定位
# --------------------------------------------------------------------------
function Resolve-EdgePath {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "找不到 Edge：$Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    if ($env:EXDIR_EDGE -and (Test-Path -LiteralPath $env:EXDIR_EDGE)) { return $env:EXDIR_EDGE }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\Application\msedge.exe')
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return $c } }

    foreach ($key in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe')) {
        try {
            $v = (Get-ItemProperty -Path $key -ErrorAction Stop).'(default)'
            if ($v -and (Test-Path -LiteralPath $v)) { return $v }
        } catch { }
    }
    $cmd = Get-Command msedge.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw '找不到 msedge.exe（SVG 栅格化要用它）。用 -Edge <路径> 或环境变量 EXDIR_EDGE 指定。'
}

# --------------------------------------------------------------------------
# SVG 预处理
# --------------------------------------------------------------------------
# c2pa 出处清单（<metadata>）是一大段 base64，渲染用不到，先摘掉（只影响内存里的副本，源文件不动）
function Get-SvgBody {
    param([string]$Text)
    return [regex]::Replace($Text, '(?s)<metadata\b.*?</metadata>', '')
}

# 把根 <svg> 的 width/height 改成目标像素（viewBox 原样保留），这样整幅图正好铺满截图窗口
function Set-SvgSize {
    param([string]$Text, [int]$Size)

    $m = [regex]::Match($Text, '(?s)<svg\b[^>]*>')
    if (-not $m.Success) { throw 'SVG 里找不到根 <svg> 元素' }

    $tag = $m.Value
    $tag = [regex]::Replace($tag, '(?i)\swidth\s*=\s*"[^"]*"', '')
    $tag = [regex]::Replace($tag, '(?i)\sheight\s*=\s*"[^"]*"', '')
    $tag = [regex]::Replace($tag, '(?i)^<svg\b', ('<svg width="{0}" height="{0}"' -f $Size))
    return $Text.Remove($m.Index, $m.Length).Insert($m.Index, $tag)
}

# --------------------------------------------------------------------------
# 截图
# --------------------------------------------------------------------------
# 收掉某一批 headless Edge 的残留进程（见文件头"坑 1"）。按 profile 路径过滤，
# 不会碰到用户自己开着的 Edge（它的 user-data-dir 在 %LOCALAPPDATA%\Microsoft\Edge）。
function Remove-ProfileProcesses {
    param([string]$Profile)
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($Profile) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Invoke-EdgeShot {
    param(
        [string]$EdgePath,
        [string]$SvgPath,
        [int]$Size,
        [string]$OutFile,
        [string]$WorkDir
    )

    $profile = Join-Path $WorkDir ("profile-{0}-{1}" -f $Size, [Guid]::NewGuid().ToString('N').Substring(0, 8))
    if (Test-Path -LiteralPath $OutFile) { Remove-Item -LiteralPath $OutFile -Force }

    $arguments = @(
        '--headless=new', '--disable-gpu', '--hide-scrollbars',
        '--no-first-run', '--no-default-browser-check', '--disable-extensions',
        '--disable-background-networking', '--disable-sync', '--disable-component-update',
        '--default-background-color=00000000', '--force-device-scale-factor=1',
        "--user-data-dir=$profile",
        "--window-size=$Size,$Size",
        "--screenshot=$OutFile",
        ([System.Uri]$SvgPath).AbsoluteUri
    )
    # Start-Process 的参数数组是按空格拼起来的，带空格的参数要自己加引号（TEMP 路径里可能有空格）
    $quoted = @($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })

    # 连着起 Edge 时偶发"起来但什么都没画"（新 profile 冷启动更明显），隔一下再起能少踩一些
    Start-Sleep -Milliseconds 200
    $proc = Start-Process -FilePath $EdgePath -PassThru -ArgumentList $quoted
    try {
        if (-not $proc.WaitForExit(60000)) { try { $proc.Kill($true) } catch { } }
        # 正常情况进程退出时截图已落盘，偶尔会晚一点点
        for ($i = 0; $i -lt 50 -and -not (Test-Path -LiteralPath $OutFile); $i++) { Start-Sleep -Milliseconds 100 }
    } finally {
        Remove-ProfileProcesses -Profile $profile
    }
}

# 校验截图：尺寸对 + 真的画了东西（按网格抽样看有没有不透明像素，见文件头"坑 2"）
function Test-Shot {
    param([string]$Path, [int]$Size)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $bmp = $null
    try { $bmp = [System.Drawing.Bitmap]::FromFile($Path) } catch { return $false }
    try {
        if ($bmp.Width -ne $Size -or $bmp.Height -ne $Size) { return $false }
        $step = [Math]::Max(1, [int]($Size / 32))
        for ($y = 0; $y -lt $Size; $y += $step) {
            for ($x = 0; $x -lt $Size; $x += $step) {
                if ($bmp.GetPixel($x, $y).A -gt 8) { return $true }
            }
        }
        return $false
    } finally {
        $bmp.Dispose()
    }
}

# 渲染某个尺寸，返回一张"已经不占文件"的位图（临时目录随后会被删掉）
function Get-SvgRender {
    param([string]$EdgePath, [string]$SvgText, [int]$Size, [string]$WorkDir)

    $svgFile = Join-Path $WorkDir ("icon-{0}.svg" -f $Size)
    $pngFile = Join-Path $WorkDir ("icon-{0}.png" -f $Size)
    [System.IO.File]::WriteAllText($svgFile, (Set-SvgSize $SvgText $Size), (New-Object System.Text.UTF8Encoding $false))

    for ($attempt = 1; $attempt -le 6; $attempt++) {
        Invoke-EdgeShot -EdgePath $EdgePath -SvgPath $svgFile -Size $Size -OutFile $pngFile -WorkDir $WorkDir
        if (Test-Shot -Path $pngFile -Size $Size) {
            $loaded = [System.Drawing.Bitmap]::FromFile($pngFile)
            try {
                $copy = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $g = [System.Drawing.Graphics]::FromImage($copy)
                $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($loaded, 0, 0, $Size, $Size)
                $g.Dispose()
            } finally {
                $loaded.Dispose()
            }
            if ($attempt -gt 1) { Write-Host ("  {0}x{0}：第 {1} 次才截到（headless 偶发空白）" -f $Size, $attempt) }
            return $copy
        }
        Write-Host ("  {0}x{0}：截图不合格，重试（{1}/6）" -f $Size, $attempt)
    }
    throw ("Edge 连试 6 次都没能给出 {0}x{0} 的截图（SVG 有问题？）" -f $Size)
}

# --------------------------------------------------------------------------
# ICO 写入
# --------------------------------------------------------------------------
function Write-Dib {
    param([System.IO.BinaryWriter]$Bw, [System.Drawing.Bitmap]$Bmp)

    $w = $Bmp.Width; $h = $Bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $raw = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    }
    finally { $Bmp.UnlockBits($data) }

    # BITMAPINFOHEADER：高度写两倍（XOR 位图 + AND 掩码）
    $Bw.Write([uint32]40)
    $Bw.Write([int32]$w)
    $Bw.Write([int32]($h * 2))
    $Bw.Write([uint16]1)
    $Bw.Write([uint16]32)
    $Bw.Write([uint32]0)
    $Bw.Write([uint32]($w * $h * 4))
    $Bw.Write([int32]0); $Bw.Write([int32]0)
    $Bw.Write([uint32]0); $Bw.Write([uint32]0)

    # XOR 位图：自下而上、BGRA（锁成 Format32bppArgb 时内存里正好是 B,G,R,A）
    for ($y = $h - 1; $y -ge 0; $y--) {
        $offset = $y * $stride
        for ($x = 0; $x -lt $w; $x++) {
            $i = $offset + $x * 4
            $Bw.Write([byte]$raw[$i + 0])
            $Bw.Write([byte]$raw[$i + 1])
            $Bw.Write([byte]$raw[$i + 2])
            $Bw.Write([byte]$raw[$i + 3])
        }
    }

    # AND 掩码：全 0（透明度走 alpha 通道），每行按 4 字节对齐
    $maskRow = [int]([Math]::Ceiling($w / 32.0) * 4)
    $Bw.Write((New-Object byte[] ($maskRow * $h)))
}

# --------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------
$edgePath = Resolve-EdgePath -Explicit $Edge
$svgText = Get-SvgBody ([System.IO.File]::ReadAllText($Svg))

Write-Host ("从 {0} 生成程序图标" -f $Svg)
Write-Host ("  渲染器：{0}" -f $edgePath)

$work = Join-Path $env:TEMP ("exdir-icon-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

$renders = @{}
try {
    # 要渲染哪些尺寸：ICO 的各档 + 徽标用到的方形边长
    $need = New-Object 'System.Collections.Generic.List[int]'
    foreach ($s in $icoSizes) { if (-not $need.Contains($s)) { $need.Add($s) } }
    foreach ($logo in $logoSizes) {
        $side = [Math]::Min($logo.Width, $logo.Height)
        if (-not $need.Contains($side)) { $need.Add($side) }
    }
    foreach ($size in ($need | Sort-Object)) { $renders[$size] = Get-SvgRender -EdgePath $edgePath -SvgText $svgText -Size $size -WorkDir $work }

    # ---- 徽标 PNG ----
    foreach ($logo in $logoSizes) {
        $side = [Math]::Min($logo.Width, $logo.Height)
        $canvas = New-Object System.Drawing.Bitmap $logo.Width, $logo.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($renders[$side], [int](($logo.Width - $side) / 2), [int](($logo.Height - $side) / 2), $side, $side)
        $g.Dispose()
        $target = Join-Path $assetsDir $logo.Name
        try {
            $canvas.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        } catch {
            throw ("写不进 {0}（文件被占用？）: {1}" -f $target, $_.Exception.Message)
        }
        $canvas.Dispose()
        Write-Host ("  Assets\{0,-46} {1}x{2}" -f $logo.Name, $logo.Width, $logo.Height)
    }

    # ---- exdir.ico ----
    try { $fs = [System.IO.File]::Create($icoTarget) }
    catch { throw ("写不进 {0}（exdir 正在运行、占着这个图标文件？先退出 exdir 再跑）: {1}" -f $icoTarget, $_.Exception.Message) }

    $bw = New-Object System.IO.BinaryWriter $fs
    try {
        $bw.Write([uint16]0)                 # 保留
        $bw.Write([uint16]1)                 # 1 = ICO
        $bw.Write([uint16]$icoSizes.Count)

        $dirPos = $fs.Position
        $bw.Write((New-Object byte[] (16 * $icoSizes.Count)))   # 目录占位，稍后回填

        $entries = @()
        foreach ($size in $icoSizes) {
            $off = $fs.Position
            Write-Dib -Bw $bw -Bmp $renders[$size]
            $entries += [pscustomobject]@{ Size = $size; Offset = [uint32]$off; Length = [uint32]($fs.Position - $off) }
        }

        $fs.Position = $dirPos
        foreach ($e in $entries) {
            $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }   # 256 在目录里写 0
            $bw.Write([byte]$dim)
            $bw.Write([byte]$dim)
            $bw.Write([byte]0)               # 调色板数
            $bw.Write([byte]0)               # 保留
            $bw.Write([uint16]1)             # 平面数
            $bw.Write([uint16]32)            # 位深
            $bw.Write([uint32]$e.Length)
            $bw.Write([uint32]$e.Offset)
        }
        $bw.Flush()
    } finally {
        $fs.Dispose()
    }

    Write-Host ("  Assets\{0,-46} {1} 档，{2} 字节" -f 'exdir.ico', $icoSizes.Count, (Get-Item $icoTarget).Length)
    foreach ($e in $entries) { Write-Host ("      {0,4}x{0,-4} {1} 字节" -f $e.Size, $e.Length) }
} finally {
    foreach ($bmp in $renders.Values) { $bmp.Dispose() }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host '图标已更新（exdir.exe 的图标要重新 build/publish 才会换）。'
