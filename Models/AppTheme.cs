namespace Exdir.Models;

/// <summary>
/// 应用主题（设置窗口「外观 → 主题」，也可以直接点标题栏左侧那个太阳 / 月亮图标）。
///
/// 三态：<see cref="System" /> 跟随系统（默认，保持旧行为不变）、<see cref="Light" /> 浅色、<see cref="Dark" /> 深色。
/// 标题栏那个开关只在浅 / 深之间切：一旦拨动就从「跟随系统」固定成显式的浅色或深色。
/// 落盘时存的是枚举数值；换算与兜底见 <see cref="Helpers.ThemeHelper" />。
/// </summary>
public enum AppTheme
{
    /// <summary>跟随系统（<c>ElementTheme.Default</c>）。</summary>
    System = 0,

    /// <summary>固定浅色。</summary>
    Light = 1,

    /// <summary>固定深色。</summary>
    Dark = 2,
}
