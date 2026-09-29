namespace Exdir.Models;

/// <summary>
/// 设置对话框左侧导航里的配置大类（一个分类 = 右侧一页正文，见 <c>Views/SettingsDialog.xaml</c>）。
///
/// 分页而不是一条长列表，是因为后面还要往设置里加东西（固定目录管理、快捷命令编辑器、
/// 主题 / 紧凑度……），一页塞不下；有了分类就能按“跟什么有关”归位。
/// </summary>
public enum SettingsCategory
{
    /// <summary>文件列表：名称 / 扩展名 / 排序等列表显示规则。</summary>
    FileList,

    /// <summary>外观：动画等纯视觉效果。</summary>
    Appearance,

    /// <summary>布局：工具条 / 侧边栏 / 双窗格 / 列宽。</summary>
    Layout,

    /// <summary>右键菜单：逐项开关系统（含第三方扩展）的右键菜单项。</summary>
    ShellMenu,
}
