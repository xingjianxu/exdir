# 生成 Release 版本并镜像到 dist\win-x64。
#
# 用法: pwsh -NoProfile -File tools\publish.ps1
#
# 为什么不直接用 dotnet publish：见 exdir.csproj 里的说明
# —— publish 会丢掉 .xbf / exdir.pri，导致发布版本无法启动。
# 这里改为「Release 构建 + 镜像输出目录」，产物已验证可独立运行。

param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Platform = 'x64',
    [string]$OutDir = "$PSScriptRoot\..\dist\win-x64"
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$buildDir = Join-Path 'bin' "$Platform\$Configuration\net8.0-windows10.0.19041.0\$RuntimeIdentifier"
$outDir = [System.IO.Path]::GetFullPath($OutDir)

Write-Host "=== 1/3 Release 构建 ($Platform / $RuntimeIdentifier) ==="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet build exdir.csproj -c $Configuration -p:Platform=$Platform -r $RuntimeIdentifier --self-contained true --nologo 2>&1 |
    Select-String -Pattern 'error|warning CS|->'
$sw.Stop()

if (-not (Test-Path (Join-Path $buildDir 'exdir.exe'))) {
    throw "构建输出缺少 exdir.exe：$buildDir"
}

Write-Host "=== 2/3 校验 XAML 资源齐全 ==="
foreach ($item in 'exdir.pri', 'App.xbf', 'MainWindow.xbf', 'Views\SidebarView.xbf', 'Views\PaneView.xbf', 'Views\DriveBarView.xbf', 'Views\DetailsView.xbf', 'Themes\ExdirTheme.xbf') {
    if (-not (Test-Path (Join-Path $buildDir $item))) { throw "构建输出缺少 $item（程序将无法启动）" }
}

Write-Host "=== 3/3 镜像到 $outDir ==="
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item -Path (Join-Path $buildDir '*') -Destination $outDir -Recurse -Force

$files = Get-ChildItem $outDir -Recurse -File
Write-Host ("完成：{0} 个文件, {1:N0} MB, 耗时 {2:N1}s" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $sw.Elapsed.TotalSeconds)
Write-Host ("可执行文件：{0}" -f (Join-Path $outDir 'exdir.exe'))
