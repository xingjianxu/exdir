using Exdir.Models;

namespace Exdir.ViewModels;

/// <summary>设置窗口左侧导航的一项（一个配置大类）。</summary>
public sealed class SettingsCategoryViewModel
{
    public SettingsCategoryViewModel(SettingsCategory key, string name)
    {
        Key = key;
        Name = name;
    }

    /// <summary>分类标识；右侧正文按它决定显示哪一页。</summary>
    public SettingsCategory Key { get; }

    /// <summary>导航里显示的名字（也是这一项的 UIA 名字）。</summary>
    public string Name { get; }

    /// <summary>
    /// <c>NavigationViewItem</c> 的 UIA 名字取自数据项自己的 <c>ToString()</c>（内容就是它），
    /// 回归脚本（tools/test-settings.ps1）靠这个名字选左侧导航，所以这里返回名字而不是类型名。
    /// </summary>
    public override string ToString() => Name;
}
