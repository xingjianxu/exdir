using System;
using System.Globalization;
using System.Reflection;

namespace Exdir.Helpers;

/// <summary>
/// 应用版本号：**唯一事实来源是 <c>exdir.csproj</c> 里的 <c>&lt;Version&gt;</c>**
/// （它在编译期变成程序集上的 AssemblyInformationalVersion / AssemblyFileVersion / AssemblyVersion）。
/// GitHub Release 的 tag（<c>tools\release.ps1</c> 生成的 <c>v0.0.&lt;yyyyMMdd&gt;</c>）与它只差一个前缀 <c>v</c>；
/// release.ps1 会在发布前校验两者一致，别处不要再写死版本号。
///
/// 在线更新（<see cref="Exdir.Services.IUpdateService" />）用这里的 <see cref="IsNewer" /> 判断
/// “远端那个 tag 是不是比我现在这份新”，因此比较逻辑要**只看数字段**：
/// tag 里可能带 <c>-beta.1</c> 这类预发布后缀，而 GitHub 的 <c>/releases/latest</c> 本来就不给预发布，
/// 认不出来的 tag 一律当成“不比当前新”（宁可不提示更新，也不要提示用户降级）。
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// 环境变量：覆盖“当前版本”（值形如 <c>0.0.20260901</c>）。
    /// 只给回归脚本用 —— 本机装的就是最新版，不假装成旧版本就测不出“发现新版本”这条路。
    /// </summary>
    public const string OverrideVariable = "EXDIR_UPDATE_VERSION";

    private static readonly string CurrentText = Resolve();

    /// <summary>当前版本，形如 <c>0.0.20261001</c>（界面直接显示这个串）。</summary>
    public static string Current => CurrentText;

    /// <summary>
    /// 把 GitHub 的 tag（<c>v0.0.20261002</c>）规整成版本串（<c>0.0.20261002</c>）。
    /// 不认识的（没有数字前缀 / 段数不对）返回 <c>null</c>。
    /// </summary>
    public static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        // 去掉预发布（-beta.1）与构建元数据（+abc123）：数字段之后的东西都不参与比较
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return TryToQuad(text, out _) ? text : null;
    }

    /// <summary>
    /// <paramref name="latest" /> 是否比 <paramref name="current" /> 新。
    /// 任一侧认不出来都返回 false（不提示更新）。
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
    {
        if (!TryToQuad(latest, out var a) || !TryToQuad(current, out var b))
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] > b[i];
            }
        }

        return false;
    }

    /// <summary>界面显示用：空值显示“未知”，其余原样。</summary>
    public static string Display(string? version) => string.IsNullOrWhiteSpace(version) ? "未知" : version;

    /// <summary>
    /// 把版本串拆成固定四段再比较。<c>0.0.20261001</c> 与 <c>0.0.20261001.0</c> 必须算相等
    /// （前者来自 tag，后者来自 AssemblyVersion），所以不能直接用 <see cref="Version" /> 比 —— 它对
    /// 缺失的段按 -1 处理，会把 <c>0.0.20261001</c> 判成比 <c>0.0.20261001.0</c> 小。
    /// </summary>
    private static bool TryToQuad(string? text, out int[] quad)
    {
        quad = [0, 0, 0, 0];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                return false;
            }

            quad[i] = value;
        }

        return true;
    }

    private static string Resolve()
    {
        var overridden = NormalizeTag(Environment.GetEnvironmentVariable(OverrideVariable));
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        var assembly = typeof(AppVersion).Assembly;

        try
        {
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var normalized = NormalizeTag(info);
            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized;
            }
        }
        catch (Exception)
        {
            // 拿不到特性就退回 AssemblyVersion（下面的兜底）
        }

        // AssemblyVersion 是程序集元数据里的必填项，永远拿得到；它比 <Version> 多一段 .0，去掉
        var fallback = assembly.GetName().Version;
        if (fallback is null)
        {
            return "0.0.0";
        }

        var text = fallback.Revision == 0 && fallback.Build >= 0
            ? $"{fallback.Major}.{fallback.Minor}.{fallback.Build}"
            : fallback.ToString();

        return NormalizeTag(text) ?? text;
    }
}
