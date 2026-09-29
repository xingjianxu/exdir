using Exdir.Models;

namespace Exdir.ViewModels;

/// <summary>
/// 设置对话框「右键菜单」页里的一行：标题 + 灰色说明 + 右侧开关。
/// 直接包着 <see cref="ShellMenuItem" />，点“保存”时把整份清单连同开关状态写回设置。
/// </summary>
public sealed class ShellMenuItemViewModel
{
    public ShellMenuItemViewModel(ShellMenuItem item, string title, bool isEnabled)
    {
        Item = item;
        Title = title;
        IsEnabled = isEnabled;
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

    /// <summary>开关状态：true = 保留在右键菜单里（默认全开）。</summary>
    public bool IsEnabled { get; set; }
}
