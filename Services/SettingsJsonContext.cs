using System.Text.Json.Serialization;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// <c>config.json</c> 的 System.Text.Json **源生成**序列化上下文。
///
/// 为什么必须有它：exdir 交付版是裁剪过的（见 exdir.csproj 的 PublishTrimmed）。
/// 反射式 <c>JsonSerializer</c> 依赖运行时反射拿 <see cref="AppSettings" /> 的属性元数据，
/// 裁剪后这些元数据可能被摘掉，于是序列化/反序列化会抛异常 —— 而
/// <see cref="SettingsService" /> 为了保证“设置坏了也不影响启动”是把异常吞掉的，
/// 结果就是「设置里拨一下开关，界面立刻生效，但 config.json 没变、重启又变回去」
/// 这种极难查的症状（实测：裁剪后 tools\test-settings.ps1 挂 9 条断言，全是“没落盘”）。
/// 源生成把元数据在编译期生成成代码，裁剪与 NativeAOT 下都稳。
///
/// 新增会写进 config.json 的类型时，往这里加一行 <c>[JsonSerializable]</c>。
/// </summary>
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
