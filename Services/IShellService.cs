using System.Collections.Generic;

namespace Exdir.Services;

/// <summary>与操作系统外壳交互（打开文件、打开终端、在资源管理器中显示等）。</summary>
public interface IShellService
{
    /// <summary>用系统默认程序打开文件或目录。</summary>
    void OpenWithDefaultApp(string path);

    /// <summary>在资源管理器中定位并选中该项。</summary>
    void RevealInFileExplorer(string path);

    /// <summary>弹出该项的“属性”对话框（内置右键菜单用；不经过 IContextMenu，所以很快）。</summary>
    void ShowProperties(string path);

    /// <summary>用系统默认程序打开多个项。</summary>
    void OpenAll(IEnumerable<string> paths);

    /// <summary>在指定目录打开终端。</summary>
    /// <param name="directory">工作目录。</param>
    /// <param name="asAdministrator">是否以管理员身份运行（会弹出 UAC）。</param>
    /// <param name="preferWindowsTerminal">为 true 时优先使用 Windows Terminal。</param>
    void OpenTerminal(string directory, bool asAdministrator = false, bool preferWindowsTerminal = true);

    /// <summary>把文本写入剪贴板。</summary>
    void CopyTextToClipboard(string text);

    /// <summary>执行一条 shell 命令行（供快捷菜单使用）。</summary>
    /// <param name="commandLine">命令行，例如 <c>code .</c>。</param>
    /// <param name="workingDirectory">工作目录。</param>
    /// <param name="asAdministrator">是否以管理员身份运行。</param>
    void RunCommand(string commandLine, string workingDirectory, bool asAdministrator = false);
}
