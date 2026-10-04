using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;

namespace Exdir.Services;

/// <inheritdoc cref="IUpdateService" />
public sealed class UpdateService : IUpdateService
{
    /// <summary>本项目在 GitHub 上的位置（releases 与 API 的地址都由它拼出来）。</summary>
    private const string Owner = "xingjianxu";

    private const string Repo = "exdir";

    /// <summary>
    /// 认哪个资产是“给本机用的”：release.ps1 打出来的 zip 叫
    /// <c>exdir-&lt;tag&gt;-win-x64.zip</c>（exdir 只发布 win-x64）。
    /// 认不到它时退回任意一个 zip（老 Release 或手工上传的包），再没有就只能打开发布页。
    /// </summary>
    private const string AssetSuffix = "-win-x64.zip";

    /// <summary>
    /// 环境变量：覆盖“最新 Release 的 JSON 从哪拿”。默认走 GitHub 的公开接口。
    /// 回归脚本用它指向本机的 mock feed（见 tools/update-test-server），
    /// 这样“发现新版本 → 下载 → 替换 → 重启”整条路能在不发真实 Release 的情况下验完。
    /// </summary>
    public const string FeedVariable = "EXDIR_UPDATE_FEED";

    private const string ExeName = "exdir.exe";

    /// <summary>发布产物里的构建信息（也用来判断“当前目录是不是一个发布版安装目录”）。</summary>
    private const string BuildInfoFileName = "build-info.txt";

    private const string ZipFileName = "exdir.zip";

    private const string PayloadDirectoryName = "payload";

    /// <summary>检查更新那一次请求的超时（只是取几 KB 的 JSON，超了就当网络不可用）。</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 一个共用的 <see cref="HttpClient" />：下载几十 MB 的包可能要好几分钟，
    /// 所以整体超时设成无限，检查那一次自己用 CTS 限时（见 <see cref="CheckAsync" />）。
    /// </summary>
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>“能不能自替换”只探测一次（安装目录的权限不会在运行期间变）。</summary>
    private static readonly Lazy<string?> SelfUpdateBlocked = new(ProbeSelfUpdate);

    public string CurrentVersion => AppVersion.Current;

    public bool IsReleaseLayout => IsReleaseLayoutCore.Value;

    /// <summary>“是不是发布版目录”只判一次（运行期间不会变）。</summary>
    private static readonly Lazy<bool> IsReleaseLayoutCore = new(() =>
    {
        try
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, BuildInfoFileName));
        }
        catch (Exception)
        {
            return false;
        }
    });

    public string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>在线更新的中转目录（zip 与解出来的 payload 都放这儿，见 <see cref="CleanupTemp" />）。</summary>
    private static string UpdateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "exdir",
        "update");

    /// <summary>默认的 Release 接口地址（<see cref="FeedVariable" /> 可以覆盖它）。</summary>
    private static string DefaultFeedUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CheckTimeout);

        var url = Environment.GetEnvironmentVariable(FeedVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            url = DefaultFeedUrl;
        }

        var release = await FetchLatestReleaseAsync(url, cts.Token).ConfigureAwait(false);
        if (release is null)
        {
            // 远端一个 Release 都没有（接口 404）——不是错误，就当“没有新版本”
            Log.Write("检查更新：远端还没有任何 Release");
            return null;
        }

        var version = AppVersion.NormalizeTag(release.TagName);
        if (version is null)
        {
            Log.Write($"检查更新：认不出远端 tag「{release.TagName}」，当作没有新版本");
            return null;
        }

        if (!AppVersion.IsNewer(version, CurrentVersion))
        {
            Log.Write($"检查更新：远端最新是 {release.TagName}，不比当前 {CurrentVersion} 新");
            return null;
        }

        var assets = release.Assets ?? [];
        var asset = assets.FirstOrDefault(a => Matches(a, AssetSuffix))
                    ?? assets.FirstOrDefault(a => Matches(a, ".zip"));

        if (asset is null)
        {
            Log.Write($"检查更新：{release.TagName} 里没有 zip 资产，只能手动下载");
        }

        var blocked = SelfUpdateBlocked.Value;
        var info = new UpdateInfo
        {
            Tag = release.TagName ?? version,
            Version = version,
            AssetName = asset?.Name ?? string.Empty,
            AssetUrl = string.IsNullOrWhiteSpace(asset?.BrowserDownloadUrl) ? null : asset.BrowserDownloadUrl,
            Sha256 = ParseDigest(asset?.Digest),
            Size = asset?.Size ?? 0,
            ReleaseNotes = release.Body ?? string.Empty,
            ReleasePageUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasesPageUrl : release.HtmlUrl,
            PublishedAt = release.PublishedAt,
            CanSelfUpdate = blocked is null,
            SelfUpdateBlockedReason = blocked,
        };

        Log.Write(
            $"检查更新：发现新版本 {info.Tag}（当前 {CurrentVersion}），"
            + $"资产={info.AssetName}（{(info.CanDownload ? "可下载" : "无 zip")}），"
            + $"自更新={(info.CanSelfUpdate ? "可以" : "不可以：" + info.SelfUpdateBlockedReason)}，"
            + $"校验={(info.Sha256 is null ? "无摘要" : "SHA256")}");

        return info;
    }

    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!info.CanDownload)
        {
            throw new InvalidOperationException("这个版本没有可下载的安装包，请打开发布页手动下载。");
        }

        var target = Path.Combine(UpdateRoot, SafeDirectoryName(info.Tag));
        var zipPath = Path.Combine(target, ZipFileName);
        var payloadPath = Path.Combine(target, PayloadDirectoryName);

        // 同一次检查只会下载一次；重复点「立即更新」时覆盖上一份，免得磁盘上攒一堆
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);

        try
        {
            await DownloadToFileAsync(info, zipPath, progress, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(info.Sha256))
            {
                var actual = ComputeSha256(zipPath);
                if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"下载到的安装包校验失败（期望 {info.Sha256[..Math.Min(12, info.Sha256.Length)]}…，实际 {actual[..12]}…），已放弃这次更新。");
                }
            }
            else
            {
                Log.Write("在线更新：远端没有给资产摘要，跳过 SHA256 校验");
            }

            progress?.Report(1);

            ExtractZip(zipPath, payloadPath);

            if (!File.Exists(Path.Combine(payloadPath, ExeName)))
            {
                throw new InvalidDataException($"安装包里没有 {ExeName}，可能下错了资产。");
            }

            Log.Write($"在线更新：{info.Tag} 已下载并解开到 {payloadPath}");
            return payloadPath;
        }
        catch
        {
            // 失败就把这次的中转目录清掉（半份 zip 留着没有任何用处），异常照旧往上抛
            TryDeleteDirectory(target);
            throw;
        }
    }

    public void ApplyAndRestart(string payloadDirectory)
    {
        if (string.IsNullOrWhiteSpace(payloadDirectory) || !Directory.Exists(payloadDirectory))
        {
            throw new InvalidOperationException("更新包还没有下载好。");
        }

        UpdateApplier.ApplyAndRestart(
            payloadDirectory,
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            ExeName,
            UpdateRoot);
    }

    public void CleanupTemp()
    {
        try
        {
            if (!Directory.Exists(UpdateRoot))
            {
                return;
            }

            // 只删**一天前**的：正常流程里替换脚本会用完就删，留得久的都是上次没走完的
            // （下载中被杀、点了「立即更新」又没重启……）。刚下好的那份不能删 ——
            // 用户可能正停在「重启并完成更新」上，甚至替换脚本正在等本进程退出。
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var dir in Directory.GetDirectories(UpdateRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        Log.Write($"在线更新：清理过期中转目录 {dir}");
                    }
                }
                catch (Exception)
                {
                    // 被占用就留着，下次再说
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception("在线更新：清理中转目录", ex);
        }
    }

    // ------------------------------------------------------------------ 内部

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        // GitHub 要求带 User-Agent（不带直接 403），顺手带上版本号便于对方识别
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"exdir/{AppVersion.Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    private static async Task<GitHubReleaseDto?> FetchLatestReleaseAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new InvalidOperationException("GitHub 拒绝了这次请求（多半是未登录的接口配额用完了），请稍后再试。");
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // 源生成的元数据（见 UpdateJsonContext）：裁剪过的发布版上反射式反序列化会抛异常
            return JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubReleaseDto);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("检查更新超时（网络不通？），请稍后再试。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"连不上 GitHub：{ex.Message}", ex);
        }
    }

    private static async Task DownloadToFileAsync(UpdateInfo info, string zipPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await Http
            .GetAsync(info.AssetUrl!, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? info.Size;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            if (total > 0)
            {
                progress?.Report(Math.Min(1d, (double)received / total));
            }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 解压发布包。**故意不用 <c>ZipFile.ExtractToDirectory</c>**：那个类在
    /// <c>System.IO.Compression.ZipFile.dll</c> 里，而裁剪会把有用不到的类型的程序集从
    /// deps.json 里整条删掉（自包含应用按 deps.json 建 TPA，磁盘上有也没用）——
    /// 症状是点「立即更新」时无日志猝死，很难联想到裁剪（同 AGENTS.md 第 6 节第 71 条）。
    /// 这里直接用已经 root 住的 <see cref="ZipArchive" />（右键「压缩」也在用它），
    /// 顺便自己守住 zip-slip（条目名里的 <c>..</c> 不得跑出目标目录）。
    /// </summary>
    private static void ExtractZip(string zipPath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        var root = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var file = File.OpenRead(zipPath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            // 目录条目（以 / 结尾）只需要把目录建出来
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(Path.Combine(root, entry.FullName));
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装包里有越界的路径（{entry.FullName}），已放弃这次更新。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);

            // 保持发布时的文件时间（"资源管理器里看到的安装日期"才正常）。
            // 有些包里的时间戳不合法（早年工具写出来的），设不上去就算了 —— 反正
            // 替换脚本用的是 /IS，时间戳不一致也照样覆盖。
            try
            {
                File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// GitHub 的资产摘要是 <c>sha256:&lt;hex&gt;</c>（算法可能不止 sha256）；
    /// 不是 sha256 的摘要直接忽略（宁可跳过校验，也不要拿别的算法的值去当 SHA256 比）。
    /// </summary>
    private static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim().ToLowerInvariant()
            : null;
    }

    private static bool Matches(GitHubAssetDto asset, string suffix)
        => !string.IsNullOrEmpty(asset.Name)
           && asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl);

    /// <summary>tag 直接当目录名用：过滤掉路径分隔符等（tag 来自远端，不能信）。</summary>
    private static string SafeDirectoryName(string tag)
    {
        var chars = tag.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var name = new string(chars).Trim('.', '_', ' ');
        return name.Length == 0 ? "latest" : name;
    }

    /// <summary>
    /// 探测“能不能自己替换安装目录”。两个前提：
    /// 1) 当前目录看起来是发布产物（有 build-info.txt）—— 从 bin\ 下直接跑 Debug 时不给替换，
    ///    免得把开发用的输出目录换成 Release 包；
    /// 2) 目录可写（装在 Program Files 之类的地方时不行；这里**不做**提权重启 ——
    ///    提权起来的 exdir 会连拖放都用不了，见 AGENTS.md 第 6 节第 21 条）。
    /// </summary>
    private static string? ProbeSelfUpdate()
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            if (!File.Exists(Path.Combine(directory, BuildInfoFileName)))
            {
                return "当前不是发布版安装目录（缺 build-info.txt），只能手动下载替换";
            }

            if (!UpdateApplier.Exists())
            {
                return "系统里找不到 Windows PowerShell，无法自动替换文件，请手动下载替换";
            }

            var probe = Path.Combine(directory, ".exdir-update-probe");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return $"安装目录不可写（{directory}）：{ex.Message}。请把 exdir 放到用户目录下，或手动下载替换";
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 删不掉就留给 CleanupTemp
        }
    }
}
