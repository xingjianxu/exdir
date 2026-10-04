using System;

namespace Exdir.Helpers;

/// <summary>
/// 把「用户输入 + 搜索范围」拼成 Everything 的查询串。
///
/// 抽出成纯函数是为了能脱离 Everything 单测（<c>tools\everything-smoke</c> 直接断言这些字符串）——
/// 查询串一拼错，表现就不是“报错”而是“一个结果都没有”，很难查。
///
/// 范围限定用 <c>path:"&lt;目录&gt;\</c>：
/// <list type="bullet">
/// <item>Everything 的 <c>path:</c> 是“条目的完整路径里包含这个串”，所以它会**递归**覆盖子目录
///       （要只列直接子项得用 <c>parent:</c>，本功能不用）；</item>
/// <item>结尾必须补一个反斜杠：不补的话 <c>D:\a\b</c> 也会匹配到 <c>D:\a\bc\...</c> 里的东西
///       （前缀相同但不属于这个目录）；</item>
/// <item>盘根（<c>D:\</c>）去掉尾部反斜杠后是 <c>D:</c>，补回来正好还是 <c>D:\</c>。</item>
/// </list>
/// 整机范围不加任何限定词。引号里的内容 Everything 按字面理解，双引号用两个双引号转义。
///
/// 关于结尾的反斜杠是不是会被 Everything 当成转义符：实测 1.4.1.1032 里反斜杠在引号内就是普通字符
/// （<c>path:"D:\a\b\" needle</c> 与去掉尾反斜杠的版本给出同样的结果集，而如果 <c>\"</c> 是转义，
/// 引号会一直吃到字符串结尾、整个查询会变成单个词、结果必然为 0）——见 <c>tools\everything-smoke</c>。
/// </summary>
public static class EverythingQuery
{
    /// <summary>一次查询最多取回多少条（Everything 是全局索引，整机搜一个字母能出几十万条）。</summary>
    public const int MaxResults = 5000;

    /// <summary>
    /// 拼查询串。<paramref name="directory" /> 为 null / 空 = 整机范围。
    /// <paramref name="text" /> 为空时只给范围限定词（= 列该目录下的全部后代，Everything 里合法）。
    /// </summary>
    public static string Build(string? text, string? directory)
    {
        var query = Escape(text?.Trim() ?? string.Empty);
        var scope = BuildScope(directory);

        if (scope.Length == 0)
        {
            return query;
        }

        return query.Length == 0 ? scope : $"{scope} {query}";
    }

    /// <summary>范围限定词；没有范围时返回空串。</summary>
    public static string BuildScope(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        var trimmed = directory.Trim().Trim('"').TrimEnd('\\', '/');

        // 纯根路径（"\" / "/"）没有意义，当作没有范围
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // 结果形如 path:"<目录>\"，结尾那个反斜杠是必须的（见类注释），用拼接写清楚转义
        return "path:\"" + Escape(trimmed) + "\\\"";
    }

    /// <summary>Everything 的引号里用两个双引号表示一个字面双引号。</summary>
    private static string Escape(string text) => text.Replace("\"", "\"\"", StringComparison.Ordinal);
}
