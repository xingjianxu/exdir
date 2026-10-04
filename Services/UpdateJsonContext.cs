using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Exdir.Services;

/// <summary>
/// GitHub「最新 Release」接口（<c>/repos/{owner}/{repo}/releases/latest</c>）的响应里我们用得到的字段。
/// 字段名照 GitHub 的 snake_case 来，别改（<see cref="JsonPropertyNameAttribute" /> 已经映射好了）。
/// </summary>
internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; set; }
}

/// <summary>Release 里的一个资产（我们只关心那个 win-x64 的 zip）。</summary>
internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    /// <summary>
    /// GitHub 从 2025 起为资产算的摘要，形如 <c>sha256:93cc04ca…</c>。
    /// 有它就做一次完整性校验（下载到的 zip 与发布时那个字节一致），没有就跳过校验并记一行日志。
    /// </summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

/// <summary>
/// 在线更新那条路上的 JSON（GitHub Release 响应）的 **System.Text.Json 源生成**上下文。
///
/// 为什么必须有它：交付版是裁剪过的（exdir.csproj 的 PublishTrimmed），
/// 反射式 <c>JsonSerializer</c> 在裁剪后拿不到属性元数据会直接抛异常 —— 而这个异常发生在
/// “检查更新”这条路上，用户看到的就是“检查更新失败”，很难联想到裁剪（同 config.json 那次，
/// 见 Services/SettingsJsonContext.cs 与 AGENTS.md 第 6 节第 66 条）。
/// 源生成把元数据在编译期变成代码，裁剪与 NativeAOT 下都稳。
/// </summary>
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
