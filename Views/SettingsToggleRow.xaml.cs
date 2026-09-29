using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exdir.Views;

/// <summary>
/// 设置对话框里的一行开关（标题 + 说明 + 右侧 ToggleSwitch）。
///
/// 三个属性都是依赖属性：<see cref="IsOn" /> 由对话框用 <c>{x:Bind ..., Mode=TwoWay}</c>
/// 绑到 <see cref="ViewModels.SettingsViewModel" /> 的对应项上，用户拨开关时回写到快照。
/// 这份快照是普通 CLR 属性、不做变更通知也没关系 —— TwoWay 的 x:Bind 只需要目标端（DP）会通知。
/// </summary>
public sealed partial class SettingsToggleRow : UserControl
{
    public SettingsToggleRow()
    {
        InitializeComponent();
    }

    /// <summary>这一行的标题（也是开关的 UIA 名字）。</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsToggleRow), new PropertyMetadata(string.Empty));

    /// <summary>标题下面的灰色说明文字。</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsToggleRow), new PropertyMetadata(string.Empty));

    /// <summary>开关状态。</summary>
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(SettingsToggleRow), new PropertyMetadata(false));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }
}
