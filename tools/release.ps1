# 把 dist\win-x64 打包并发到 GitHub Release 页面。
#
# 用法:
#   pwsh -NoProfile -File tools\release.ps1                                # 版本号 = exdir.csproj 里的 <InformationalVersion>，先 publish 再上传
#   pwsh -NoProfile -File tools\release.ps1 -Tag v1.2.0
#   pwsh -NoProfile -File tools\release.ps1 -Tag v1.2.0 -DryRun            # 只构建 + 打包 + 打印将要执行的 gh 命令
#   pwsh -NoProfile -File tools\release.ps1 -Tag v1.2.0 -SkipPublish       # 用现有 dist（不重新构建）
#   pwsh -NoProfile -File tools\release.ps1 -Tag v1.2.0 -Draft             # 建草稿，人工检查后再到网页上点发布
#   pwsh -NoProfile -File tools\release.ps1 -Tag v1.2.0 -Clobber           # tag 已有 Release 时覆盖它的资产与说明
#
# 流程 = 校验（git 干净 + gh 已登录）→ tools\publish.ps1 → 打 zip → 生成 release 说明
#        → gh release create（tag 由 gh 在远端创建、指向本次 HEAD）。
#
# 只传一个资产：dist\exdir-<tag>-win-x64.zip（zip 根目录就是 exdir.exe，解压即用），
# 不再把 165 个文件逐个传上去 —— 页面干净、下载/上传都快。
# release 说明里会带上 SHA256 与 build-info.txt 的内容（构建时间 / 源码提交 / 文件数 / 占用空间）。
#
# 前置条件:
#   * gh（GitHub CLI）已安装并 `gh auth login` 过（需要 repo 权限）；
#   * 工作区干净（发布出去的“源码提交”要对得上，否则会带上未提交改动）——确实要发脏工作区就加 -AllowDirty；
#   * 远端不要有指向别的提交的同名 tag（脚本会自己查，查到就报错让你换版本号）。

param(
    [string]$Tag,                 # 缺省 v0.0.<yyyyMMdd>
    [switch]$SkipPublish,         # 跳过 tools\publish.ps1，直接用现有 dist
    [switch]$AllowDirty,          # 允许工作区有未提交改动 / dist 不是当前 HEAD 构建的
    [switch]$AllowVersionMismatch,# 允许 tag 与代码里的版本号（exdir.csproj 的 <InformationalVersion>）不一致
    [switch]$Draft,               # 建草稿
    [switch]$Prerelease,          # 标为预发布
    [switch]$Clobber,             # 远端已有该 tag 的 Release 时覆盖资产与说明
    [switch]$DryRun               # 不发任何网络写请求，只打印将要执行的 gh 命令
)

$ErrorActionPreference = 'Stop'
# 原生命令（git / gh）的报错交给 $LASTEXITCODE 判断，不要变成异常
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) { $PSNativeCommandUseErrorActionPreference = $false }

Set-Location (Join-Path $PSScriptRoot '..')
$root = (Get-Location).Path
$distDir = Join-Path $root 'dist\win-x64'
$artifacts = Join-Path $root '.artifacts'

function Get-Native {
    # 跑原生命令、按行返回 stdout（stderr 默认丢掉：失败与否只看退出码）；不抛异常，
    # 退出码放 $script:lastExit；-MergeStdErr 时把 stderr 也接上（gh 的报错写在 stderr）。
    #
    # 为什么不用 `& git ... 2>$null` 直接捕获：PowerShell 捕获原生命令的输出时按
    # [Console]::OutputEncoding 解码（本机控制台码页 936 = GBK），而 git / gh 写出来的是 UTF-8 字节
    # → 提交信息里的中文会变成「鍦ㄧ嚎鏇存柊…」这种乱码，写进 Release 说明一眼就看得出
    # （发 v0.0.20261004 时真踩过一次）。这里显式按 UTF-8 解码，不看控制台码页的脸色；
    # 参数逐个塞进 ArgumentList，含空格的 --pretty=format:- %s 不会被拆开，也不用自己拼引号。
    param([string]$Exe, [string[]]$Arguments, [switch]$MergeStdErr)

    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Exe
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::Start($info)
    # 两个流都异步读：先同步读完一个再读另一个，输出量大时可能互相堵住
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $script:lastExit = $process.ExitCode
    $text = if ($MergeStdErr) { $stdoutTask.Result + "`n" + $stderrTask.Result } else { $stdoutTask.Result }
    $process.Dispose()

    return @($text -split "`r?`n" | Where-Object { $_ -ne '' })
}

function Invoke-Gh {
    # 跑 gh；-DryRun 时只打印。返回 gh 的 stdout（成功时 gh release create 会输出 Release URL）。
    param([string[]]$Arguments)
    $display = 'gh ' + (($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    if ($DryRun) {
        Write-Host "  [DryRun] $display"
        return ''
    }
    Write-Host "  $display"
    # stdout + stderr 一起收，失败时把两段都拼进异常（走 Get-Native：UTF-8 解码，见那里）
    $out = Get-Native -Exe 'gh' -Arguments $Arguments -MergeStdErr
    if ($script:lastExit -ne 0) { throw ("gh 失败（退出码 {0}）：{1}`n{2}" -f $script:lastExit, $display, ($out -join "`n")) }
    return (($out -join "`n").Trim())
}

# ---------------------------------------------------------------- 1/6 前置校验
Write-Host '=== 1/6 校验环境 ==='

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw '找不到 gh（GitHub CLI）：装好后 gh auth login，再跑本脚本。'
}
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'gh 未登录：先跑 gh auth login。' }

$dirty = (Get-Native git @('status', '--porcelain')) -join "`n"
if (-not $AllowDirty -and $dirty.Trim()) {
    # 注意：这里的提示文案只能用「」，不能用弯引号 —— PS 7.4+ 把弯引号当字符串定界符，
    # 双引号字符串里出现一对弯引号会直接把脚本拆成语法错误（工具脚本里只在注释里用弯引号）。
    throw ("工作区有未提交改动，发布出去的「源码提交」会对不上。先提交，或用 -AllowDirty 强制发布。`n{0}" -f $dirty.Trim())
}

$head = ((Get-Native git @('rev-parse', 'HEAD')) -join '').Trim()
$shortHead = ((Get-Native git @('rev-parse', '--short', 'HEAD')) -join '').Trim()
if (-not $head) { throw '这不是一个 git 仓库（git rev-parse HEAD 失败）。' }

if (-not $Tag) {
    # 默认 tag = 代码里的版本（exdir.csproj 的 <InformationalVersion>）：这样 tag 与
    # 程序里显示的版本天然一致（否则下面那道校验会直接报错）。发新版本 = 先改那里的版本号并提交。
    $csproj = (Get-Content (Join-Path $root 'exdir.csproj') -Raw)
    $version = [regex]::Match($csproj, '<InformationalVersion>\s*([^<\s]+)\s*</InformationalVersion>').Groups[1].Value
    if (-not $version) { throw 'exdir.csproj 里读不到 <InformationalVersion>：请用 -Tag 显式指定版本号。' }
    $Tag = 'v' + $version
    Write-Host "  未指定 -Tag：用 exdir.csproj 里的版本 $version"
}
if ($Tag -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw "版本号 '$Tag' 不合法：只允许字母/数字/. _ -，且要字母或数字开头（也别带 /，它要进 zip 文件名）。"
}

# 远端 tag：指向别的提交就报错（否则资产会挂到旧提交的 Release 上、说明却写着新提交）。
# 外面那个 @() 不能省：函数返回的单元素数组会被 PowerShell 拆成字符串，那样 $remoteTag[0] 拿到的是
# 第一个字符（曾经就因此把“远端 tag 指向别的提交”报成「（7，当前 HEAD 是 7d7d37b）」）。
$remoteTag = @(Get-Native git @('ls-remote', '--tags', 'origin', "refs/tags/$Tag"))
if ($script:lastExit -ne 0) {
    Write-Host '  警告：git ls-remote 失败，跳过「远端 tag 是否指向别的提交」这项检查'
}
if ($remoteTag.Count -gt 0 -and "$remoteTag".Trim()) {
    $remoteSha = ($remoteTag[0] -split "`t")[0]
    if ($remoteSha -ne $head) {
        throw ("远端已存在指向其它提交的 tag {0}（{1}，当前 HEAD 是 {2}）：换个版本号，或先删掉那个 tag。" -f `
            $Tag, $remoteSha.Substring(0, [Math]::Min(7, $remoteSha.Length)), $shortHead)
    }
    Write-Host "  远端已有 tag $Tag（指向当前 HEAD），将直接挂 Release 上去"
}

# 同名 Release 是否已存在
& gh release view $Tag *> $null
$releaseExists = ($LASTEXITCODE -eq 0)
if ($releaseExists) {
    if (-not $Clobber) {
        throw "远端已存在 $Tag 的 Release：换个 -Tag，或用 -Clobber 覆盖它的资产与说明。"
    }
    Write-Host "  $Tag 的 Release 已存在：-Clobber 生效，将覆盖资产与说明"
}
Write-Host "  版本号=$Tag  源码提交=$shortHead$(if ($Draft) { '  [草稿]' })"

# ---------------------------------------------------------------- 2/6 构建
if ($SkipPublish) {
    Write-Host '=== 2/6 跳过 tools\publish.ps1（-SkipPublish）==='
} else {
    Write-Host '=== 2/6 tools\publish.ps1（dotnet publish + 裁剪 + 补 .xbf/.pri + 镜像到 dist）==='
    & (Join-Path $PSScriptRoot 'publish.ps1')
}

# ---------------------------------------------------------------- 3/6 校验 dist
Write-Host '=== 3/6 校验 dist ==='
foreach ($item in 'exdir.exe', 'exdir.dll', 'exdir.pri', 'Assets\exdir.ico', 'build-info.txt') {
    if (-not (Test-Path (Join-Path $distDir $item))) {
        throw "dist 里缺少 $item（先跑 tools\publish.ps1 生成）"
    }
}
$buildInfo = (Get-Content (Join-Path $distDir 'build-info.txt') -Encoding utf8) -join "`n"
$builtCommit = [regex]::Match($buildInfo, '源码提交\s*:\s*(\S+)').Groups[1].Value
# 构建时间形如 “构建时间 : 2026-09-30 21:35:31”，按第一个冒号切（时间本身还有冒号）
$builtTime = [regex]::Match($buildInfo, '构建时间\s*:\s*(.+)').Groups[1].Value.Trim()
if (-not $builtCommit) { throw 'build-info.txt 里读不到“源码提交”那一行。' }

# 版本号必须与要发的 tag 一致：不一致的话，用户装上这个包后「检查更新」会一直把同一个版本当成新版本
# （或反复提示有新版本）。唯一事实来源是 exdir.csproj 里的 <InformationalVersion>，
# 它编译进 exe 的“产品版本”（SDK 会在后面追加 +<git 提交>，切掉）。
$appVersion = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Join-Path $distDir 'exdir.exe')).ProductVersion -split '\+')[0]
$tagVersion = $Tag -replace '^[vV]', ''
if ($appVersion -ne $tagVersion) {
    if ($AllowVersionMismatch) {
        Write-Host ("  警告：tag {0} 与代码里的版本 {1} 不一致（-AllowVersionMismatch）" -f $Tag, $appVersion)
    } else {
        throw ("要发的 tag 是 {0}，而代码里的版本是 {1}：先把 exdir.csproj 的 <InformationalVersion> 改成 {2} 并提交，再用 -Tag {0} 发布（或加 -AllowVersionMismatch 强制发布，不推荐）。" -f $Tag, $appVersion, $tagVersion)
    }
} else {
    Write-Host "  版本号=$appVersion（与 tag 一致）"
}
if ($builtCommit -ne $shortHead -and -not $AllowDirty) {
    throw ("dist 是 {0} 构建的、不是当前 HEAD（{1}）：先跑 tools\publish.ps1，或用 -AllowDirty 强制发布。" -f $builtCommit, $shortHead)
}
$fileCount = @(Get-ChildItem $distDir -Recurse -File).Count
$distMB = (Get-ChildItem $distDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("  {0} 个文件 / {1:N1} MB（源码提交 {2}）" -f $fileCount, $distMB, $builtCommit)

# ---------------------------------------------------------------- 4/6 打包 zip
Write-Host '=== 4/6 打包 zip ==='
$zipName = "exdir-$Tag-win-x64.zip"
$zipPath = Join-Path $root ("dist\$zipName")
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$sw = [System.Diagnostics.Stopwatch]::StartNew()
# includeBaseDirectory=$false：zip 根目录就是 exdir.exe，解压到一个文件夹里即可用。
# 用 ZipFile.CreateFromDirectory 而不是 Compress-Archive：165 个文件 / 80+ MB 快得多。
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $distDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$sw.Stop()

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object FullName)
} finally { $archive.Dispose() }
if ($entryNames -notcontains 'exdir.exe') { throw 'zip 根目录里没有 exdir.exe（打包参数写错了？）' }
if ($entryNames.Count -ne $fileCount) {
    throw ("zip 条目数 {0} 与 dist 文件数 {1} 不一致，打包不完整。" -f $entryNames.Count, $fileCount)
}

$zipSize = (Get-Item $zipPath).Length
$zipMB = $zipSize / 1MB
$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Write-Host ("  {0}：{1} 个条目 / {2:N1} MB / 耗时 {3:N1}s" -f $zipName, $entryNames.Count, $zipMB, $sw.Elapsed.TotalSeconds)
Write-Host ("  SHA256 {0}" -f $sha256)

# ---------------------------------------------------------------- 5/6 release 说明
Write-Host '=== 5/6 生成 release 说明 ==='
# 上一个 tag 到 HEAD 的提交清单（没有 tag 就取最近 20 条）；上一个 tag 恰好是本版本号时也退回最近 20 条
$prevTag = ((Get-Native git @('describe', '--tags', '--abbrev=0', 'HEAD')) -join '').Trim()
if ($prevTag -eq $Tag) { $prevTag = '' }
if ($prevTag) {
    $commits = @(Get-Native git @('log', "$prevTag..HEAD", '--no-merges', '--pretty=format:- %s'))
    $rangeText = "$prevTag..HEAD"
} else {
    $commits = @(Get-Native git @('log', '-n', '20', '--no-merges', '--pretty=format:- %s'))
    $rangeText = 'HEAD 最近 20 条提交'
}
$commits = @($commits | Where-Object { $_.Trim() } | Select-Object -First 60)
if (-not $commits) { $commits = @('- 无新提交（与上一个 tag 相同）') }

# 说明是 markdown、里面有大量反引号（代码片段），反引号在双引号 here-string 里要转义，
# 所以这里用单引号 here-string + 占位符替换；改模板时别把 '@ ... '@ 的收尾写错。
$notesTemplate = @'
Windows 文件管理器（WinUI 3 / 非打包自包含版），解压后双击 `exdir.exe` 即可运行。

### 下载

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `@ZIPNAME@` | @ZIPMB@ MB | 解压到任意目录，双击 `exdir.exe`；无需预装 .NET 或 Windows App Runtime |

SHA256：`@SHA256@`

### 本次变更（@RANGE@）

@COMMITS@

### 构建信息

- 版本：`@TAG@`
- 源码提交：`@COMMIT@`
- 构建时间：@BUILDTIME@
- 文件数量：@FILECOUNT@
- 占用空间：@DISTMB@ MB（zip @ZIPMB@ MB）
- 仅提供 **win-x64**；x86 / ARM64 未验证，不发布
- 已知问题：见仓库 `AGENTS.md`（系统右键菜单在非打包模式下部分菜单项缺失等）

> 首次启动若异常退出，先看 `%LOCALAPPDATA%\exdir\exdir.log`。
'@

$notes = $notesTemplate.
    Replace('@ZIPNAME@', $zipName).
    Replace('@ZIPMB@', ('{0:N1}' -f $zipMB)).
    Replace('@SHA256@', $sha256).
    Replace('@RANGE@', $rangeText).
    Replace('@COMMITS@', ($commits -join "`n")).
    Replace('@TAG@', $Tag).
    Replace('@COMMIT@', $shortHead).
    Replace('@BUILDTIME@', $builtTime).
    Replace('@FILECOUNT@', "$fileCount").
    Replace('@DISTMB@', ('{0:N1}' -f $distMB))

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$notesPath = Join-Path $artifacts "release-notes-$Tag.md"
# 显式写不带 BOM 的 UTF-8（gh 按 UTF-8 读这个文件）：Set-Content -Encoding utf8 在
# Windows PowerShell 5.1 下会带 BOM，那个 BOM 会变成 Release 正文开头的隐形字符。
[System.IO.File]::WriteAllText($notesPath, $notes, [System.Text.UTF8Encoding]::new($false))
Write-Host "  $notesPath"

# ---------------------------------------------------------------- 6/6 上传
Write-Host '=== 6/6 上传到 GitHub Release ==='
$url = ''
if ($releaseExists) {
    # tag 上已有 Release：只覆盖资产与说明，不动 tag 指向
    Invoke-Gh @('release', 'upload', $Tag, $zipPath, '--clobber') | Out-Null
    $edit = @('release', 'edit', $Tag, '--title', "exdir $Tag", '--notes-file', $notesPath,
        "--draft=$($Draft.IsPresent.ToString().ToLower())",
        "--prerelease=$($Prerelease.IsPresent.ToString().ToLower())")
    if (-not $Draft -and -not $Prerelease) { $edit += '--latest' }
    Invoke-Gh $edit | Out-Null
} else {
    $create = @('release', 'create', $Tag, $zipPath, '--title', "exdir $Tag",
        '--notes-file', $notesPath, '--target', $head)
    if ($Draft) { $create += '--draft' }
    if ($Prerelease) { $create += '--prerelease' }
    if (-not $Draft -and -not $Prerelease) { $create += '--latest' }
    $url = Invoke-Gh $create
}

if ($DryRun) {
    Write-Host '=== DryRun：没有对 GitHub 做任何写操作 ==='
} else {
    # 回读一次，确认资产真的挂上去了（gh 的 stdout 不保证给出 URL）
    $view = Invoke-Gh @('release', 'view', $Tag, '--json', 'url,assets') | ConvertFrom-Json
    $names = @($view.assets | ForEach-Object name)
    if ($names -notcontains $zipName) { throw "Release 上没找到资产 $zipName（实际有：$($names -join ', ')）" }
    $url = $view.url
    Write-Host "  资产已就位：$($names -join ', ')"
}

Write-Host ''
Write-Host "完成：$Tag"
Write-Host "  zip    : $zipPath（$([math]::Round($zipMB, 1)) MB，SHA256 $sha256）"
Write-Host "  说明   : $notesPath"
if ($url) { Write-Host "  Release: $url" }
