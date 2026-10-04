using System.Text.Json.Serialization;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// <c>recents.json</c> 的 System.Text.Json **源生成**序列化上下文。
///
/// 与 <see cref="SettingsJsonContext" /> 同样的理由：交付版是裁剪过的（见 exdir.csproj 的
/// PublishTrimmed），反射式序列化在裁剪后可能拿不到属性元数据、静默失败。
/// 新增会写进 recents.json 的类型时，往这里加一行 <c>[JsonSerializable]</c>。
/// </summary>
[JsonSerializable(typeof(RecentItems))]
internal sealed partial class RecentItemsJsonContext : JsonSerializerContext;
