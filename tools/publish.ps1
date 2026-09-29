# 生成 Release 版本并镜像到 dist\win-x64 —— 这是交付给用户的最终产物。
#
# 用法: pwsh -NoProfile -File tools\publish.ps1
#       pwsh -NoProfile -File tools\publish.ps1 -OutDir dist\win-x64-test   # 临时目录
#
# 为什么不用 dotnet publish：见 exdir.csproj 里的说明
# —— publish 会丢掉 .xbf / exdir.pri，导致发布版本无法启动。
# 这里改为「Release 构建 + 镜像输出目录」，产物已验证可独立运行。
#
# 只出 win-x64（唯一验证过的目标）；x86 / ARM64 不生成。

param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Platform = 'x64',
    [string]$OutDir = "$PSScriptRoot\..\dist\win-x64"
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$root = (Get-Location).Path
$buildDir = Join-Path 'bin' "$Platform\$Configuration\net8.0-windows10.0.19041.0\$RuntimeIdentifier"
$outDir = [System.IO.Path]::GetFullPath($OutDir)

Write-Host "=== 1/4 Release 构建 ($Platform / $RuntimeIdentifier) ==="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet build exdir.csproj -c $Configuration -p:Platform=$Platform -r $RuntimeIdentifier --self-contained true --nologo 2>&1 |
    Select-String -Pattern 'error|warning CS|->'
if ($LASTEXITCODE -ne 0) { throw "构建失败（dotnet build 退出码 $LASTEXITCODE）" }

if (-not (Test-Path (Join-Path $buildDir 'exdir.exe'))) {
    throw "构建输出缺少 exdir.exe：$buildDir"
}

Write-Host "=== 2/4 校验 XAML 资源齐全 ==="
# 少一个 .xbf（或 exdir.pri），发布版启动时就是 XamlParseException。
# 源码里每个 .xaml 都必须能在构建输出里找到同名 .xbf，避免新增视图后漏检。
foreach ($item in 'exdir.pri', 'exdir.dll', 'exdir.runtimeconfig.json') {
    if (-not (Test-Path (Join-Path $buildDir $item))) { throw "构建输出缺少 $item（程序将无法启动）" }
}
$sources = @(Get-ChildItem -Path $root -Recurse -Filter *.xaml -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist|\.artifacts|\.git|\.vs)[\\/]' })
foreach ($xaml in $sources) {
    $rel = $xaml.FullName.Substring($root.Length + 1)
    $xbf = [System.IO.Path]::ChangeExtension($rel, '.xbf')
    if (-not (Test-Path (Join-Path $buildDir $xbf))) { throw "构建输出缺少 $xbf（$rel 没被编译进 XAML 资源）" }
}
Write-Host ("  已校验 exdir.pri + {0} 个 .xaml -> .xbf" -f $sources.Count)

Write-Host "=== 3/4 镜像到 $outDir ==="
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item -Path (Join-Path $buildDir '*') -Destination $outDir -Recurse -Force

Write-Host "=== 4/4 写入 build-info.txt（用于判断 dist 是否为最新） ==="
$commit = (& git rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $commit = '(非 git 工作区)' }
$dirty = ''
if ($commit -ne '(非 git 工作区)') {
    $pending = & git status --porcelain 2>$null
    if ($pending) { $dirty = '  (构建时工作区有未提交改动)' }
}
$files = @(Get-ChildItem $outDir -Recurse -File)
$sizeMB = ($files | Measure-Object Length -Sum).Sum / 1MB
$info = @(
    "exdir Release 产物"
    "构建时间 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "构建配置 : $Configuration / $Platform / $RuntimeIdentifier / self-contained"
    "源码提交 : $commit$dirty"
    "文件数量 : $($files.Count)"
    "占用空间 : $([math]::Round($sizeMB, 1)) MB"
    "启动方式 : 双击 exdir.exe（无需预装 .NET 或 Windows App Runtime）"
) -join [Environment]::NewLine
Set-Content -Path (Join-Path $outDir 'build-info.txt') -Value $info -Encoding UTF8

$sw.Stop()
Write-Host ("完成：{0} 个文件, {1:N0} MB, 耗时 {2:N1}s" -f $files.Count, $sizeMB, $sw.Elapsed.TotalSeconds)
Write-Host ("可执行文件：{0}" -f (Join-Path $outDir 'exdir.exe'))
