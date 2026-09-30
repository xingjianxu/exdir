using CommunityToolkit.Mvvm.ComponentModel;
using Exdir.Models;

namespace Exdir.ViewModels;

/// <summary>
/// 设置窗口「右键菜单」页里的一行：标题 + 灰色说明 + 右侧开关。
/// 直接包着 <see cref="ShellMenuItem" />，改动由 <see cref="SettingsViewModel" /> 汇总后写回设置。
/// </summary>
public sealed class ShellMenuItemViewModel : ObservableObject
{
    private bool _isEnabled;

    public ShellMenuItemViewModel(ShellMenuItem item, string title, bool isEnabled)
    {
        Item = item;
        Title = title;
        _isEnabled = isEnabled;
    }

    /// <summary>对应的菜单项（含 <c>Key</c>，关闭时存的就是它）。</summary>
    public ShellMenuItem Item { get; }

    /// <summary>显示名（也是开关的 UIA 名字）。重名时构建方会加上子菜单/作用域后缀。</summary>
    public string Title { get; }

    /// <summary>灰色说明：出现在哪些上下文（文件 / 文件夹 / 背景），以及是不是子菜单里的项。</summary>
    public string Description
    {
        get
        {
            var scopes = Item.Scopes.Count == 0 ? "未知" : string.Join(" / ", Item.Scopes);
            return string.IsNullOrEmpty(Item.MenuPath)
                ? scopes
                : $"{scopes} · {Item.MenuPath} 子菜单";
        }
    }

    /// <summary>
    /// 开关状态：true = 保留在右键菜单里（默认全开）。
    /// 必须是会发通知的属性：设置窗口把它绑到 <c>ToggleSwitch.IsOn</c> 上，
    /// <see cref="SettingsViewModel" /> 也靠这条通知知道用户拨了哪一项。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}
