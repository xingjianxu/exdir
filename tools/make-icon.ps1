# 从 Assets\Square44x44Logo.scale-200.png 生成多尺寸 Assets\exdir.ico
# 用法: pwsh -NoProfile -File tools\make-icon.ps1
#
# 说明: 直接手工写 ICO 容器（BITMAPINFOHEADER + 32bpp BGRA + AND 掩码），
#       不依赖任何第三方库，避免 ImageMagick / VS 工具链依赖。
#       所有字节直接写入 FileStream（回填目录项），避免 PowerShell 数组返回时的展开问题。

param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Assets\Square44x44Logo.scale-200.png'),
    [string]$Target = (Join-Path $PSScriptRoot '..\Assets\exdir.ico')
)

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Target = [System.IO.Path]::GetFullPath((Join-Path $root 'Assets\exdir.ico'))
$Source = [System.IO.Path]::GetFullPath($Source)

$src = [System.Drawing.Bitmap]::FromFile($Source)

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

    # XOR 位图：自下而上、BGRA
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

    # AND 掩码：全 0，每行 4 字节对齐
    $maskRow = [int]([Math]::Ceiling($w / 32.0) * 4)
    $zeroMask = New-Object byte[] ($maskRow * $h)
    $Bw.Write($zeroMask)
}

$fs = [System.IO.File]::Create($Target)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$dirPos = $fs.Position
$bw.Write((New-Object byte[] (16 * $sizes.Count)))   # 占位，稍后回填

$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $s, $s))
    $g.Dispose()

    $off = $fs.Position
    Write-Dib -Bw $bw -Bmp $bmp
    $len = $fs.Position - $off
    $bmp.Dispose()

    $entries += [pscustomobject]@{ Size = $s; Offset = [uint32]$off; Length = [uint32]$len }
}
$src.Dispose()

$fs.Position = $dirPos
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$e.Length)
    $bw.Write([uint32]$e.Offset)
}
$bw.Flush()
$fs.Dispose()

$total = (Get-Item $Target).Length
Write-Host ("生成 {0}  共 {1} 个尺寸，{2} 字节" -f $Target, $sizes.Count, $total)
foreach ($e in $entries) { Write-Host ("  {0,4}x{0,-4} {1} 字节" -f $e.Size, $e.Length) }
