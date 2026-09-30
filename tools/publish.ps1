# 生成 Release 版本并镜像到 dist\win-x64 —— 这是交付给用户的最终产物。
#
# 用法: pwsh -NoProfile -File tools\publish.ps1
#       pwsh -NoProfile -File tools\publish.ps1 -OutDir dist\win-x64-test   # 临时目录
#
# 流程 = dotnet publish（开启裁剪）+ 把 publish 丢掉的 XAML 资源补回来 + 镜像到 dist。
#
# 1) 为什么用 dotnet publish 而不是 dotnet build：
#    裁剪（exdir.csproj 里的 PublishTrimmed=true）只在 publish 阶段生效，它把用不到的
#    BCL 整个摘掉，产物从 ~181 MB 降到 ~84 MB（去 pdb，实测）。日常 dotnet build 不受影响。
#    当前项目不做 NativeAOT：WinUI 的 Microsoft.UI.Xaml.dll 会在首个布局/渲染回合里
#    以 0x80040111 失败（stowed exception，故障模块 Microsoft.UI.Xaml.dll），
#    详见 AGENTS.md 第 6 节最后一条；因此这里不传 PublishAot。
#
# 2) 为什么 publish 之后还要补文件：
#    dotnet publish 会丢掉 XAML 编译器产出的 .xbf 与应用资源索引 exdir.pri（它们只有
#    CopyToOutputDirectory，没进发布文件列表）。缺了它们启动即 XamlParseException，
#    所以从构建输出（bin\...）把它们补进发布目录 —— 这和 WindowsAppSDK 自带的 .pri
#    （Microsoft.UI.Xaml.Controls.pri 等，走 ReferenceCopyLocalPaths）不冲突。
#    补完会逐个校验（源码里每个 .xaml 都要能在发布目录里找到同名 .xbf）。
#
# 3) 语言资源（85 个 *.mui 目录）在构建期就已经裁到 zh-*/en-*，见 exdir.csproj。
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
$stageDir = Join-Path $root '.artifacts\publish-win-x64'
$outDir = [System.IO.Path]::GetFullPath($OutDir)

Write-Host "=== 1/5 dotnet publish（Release / $Platform / $RuntimeIdentifier / 裁剪） ==="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
dotnet publish exdir.csproj -c $Configuration -p:Platform=$Platform -r $RuntimeIdentifier `
    --self-contained true -o $stageDir --nologo 2>&1 |
    Select-String -Pattern 'error|warning CS|warning IL|-> '
if ($LASTEXITCODE -ne 0) { throw "发布失败（dotnet publish 退出码 $LASTEXITCODE）" }

if (-not (Test-Path (Join-Path $stageDir 'exdir.exe'))) {
    throw "发布输出缺少 exdir.exe：$stageDir"
}

Write-Host "=== 2/5 从构建输出补回 .xbf / exdir.pri ==="
# 源码里每个 .xaml 都必须能对应一个 .xbf，避免新增视图后漏检。
$sources = @(Get-ChildItem -Path $root -Recurse -Filter *.xaml -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dist|\.artifacts|\.git|\.vs)[\\/]' })
$priSource = Join-Path $buildDir 'exdir.pri'
if (-not (Test-Path $priSource)) { throw "构建输出缺少 exdir.pri：$buildDir" }
Copy-Item $priSource $stageDir -Force

$copied = 0
foreach ($xaml in $sources) {
    $rel = $xaml.FullName.Substring($root.Length + 1)
    $xbf = [System.IO.Path]::ChangeExtension($rel, '.xbf')
    $src = Join-Path $buildDir $xbf
    if (-not (Test-Path $src)) { throw "构建输出缺少 $xbf（$rel 没被编译进 XAML 资源）" }

    $dest = Join-Path $stageDir $xbf
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    Copy-Item $src $dest -Force
    $copied++
}
Write-Host ("  已补回 exdir.pri + {0} 个 .xbf" -f $copied)

Write-Host "=== 3/5 校验发布目录 ==="
foreach ($item in 'exdir.exe', 'exdir.dll', 'exdir.pri') {
    if (-not (Test-Path (Join-Path $stageDir $item))) { throw "发布目录缺少 $item（程序将无法启动）" }
}
foreach ($xaml in $sources) {
    $xbf = [System.IO.Path]::ChangeExtension($xaml.FullName.Substring($root.Length + 1), '.xbf')
    if (-not (Test-Path (Join-Path $stageDir $xbf))) { throw "发布目录缺少 $xbf" }
}
if (-not (Test-Path (Join-Path $stageDir 'Assets\exdir.ico'))) {
    throw "发布目录缺少 Assets\exdir.ico（托盘图标与窗口图标都靠它）"
}
# 语言资源必须只剩中文/英文（见 exdir.csproj 的 ExcludeUnneededWinAppSdkLanguageResources）
$langDirs = @(Get-ChildItem $stageDir -Directory |
    Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z]{2,4})+$' })
$unexpected = @($langDirs | Where-Object { $_.Name -notmatch '^(zh|en)-' })
if ($unexpected.Count -gt 0) {
    throw ("发布目录里有多余的语言资源：{0}" -f (($unexpected | ForEach-Object Name) -join ', '))
}
Write-Host ("  已校验 exdir.pri + {0} 个 .xbf + Assets；语言目录 {1} 个（{2}）" -f `
    $sources.Count, $langDirs.Count, (($langDirs | ForEach-Object Name) -join ', '))

Write-Host "=== 4/5 镜像到 $outDir ==="
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item -Path (Join-Path $stageDir '*') -Destination $outDir -Recurse -Force

Write-Host "=== 5/5 写入 build-info.txt（用于判断 dist 是否为最新） ==="
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
    "构建配置 : $Configuration / $Platform / $RuntimeIdentifier / self-contained / PublishTrimmed=True"
    "AOT      : 未启用（WinUI 3 + NativeAOT 实测启动即崩，见 AGENTS.md 第 6 节）"
    "源码提交 : $commit$dirty"
    "文件数量 : $($files.Count)"
    "占用空间 : $([math]::Round($sizeMB, 1)) MB"
    "启动方式 : 双击 exdir.exe（无需预装 .NET 或 Windows App Runtime）"
) -join [Environment]::NewLine
Set-Content -Path (Join-Path $outDir 'build-info.txt') -Value $info -Encoding UTF8

$sw.Stop()
Write-Host ("完成：{0} 个文件, {1:N0} MB, 耗时 {2:N1}s" -f $files.Count, $sizeMB, $sw.Elapsed.TotalSeconds)
Write-Host ("可执行文件：{0}" -f (Join-Path $outDir 'exdir.exe'))
