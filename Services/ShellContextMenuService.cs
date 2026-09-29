using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Exdir.Diagnostics;
using Exdir.Models;
using Exdir.Services.Native;
using static Exdir.Services.Native.ShellContextMenuInterop;

namespace Exdir.Services;

/// <inheritdoc cref="IShellContextMenuService" />
public sealed class ShellContextMenuService : IShellContextMenuService
{
    /// <summary>外壳分配命令 id 的起始值（0 保留给“没选中任何项”）。</summary>
    private const uint IdCmdFirst = 1;

    /// <summary>外壳分配命令 id 的结束值（id 要能装进一个 WORD）。</summary>
    private const uint IdCmdLast = 0x7FFF;

    /// <summary>样本文件名（放在 %TEMP% 下，用来枚举“文件”上下文里的菜单项）。</summary>
    private const string SampleFileName = "exdir-context-menu-sample.txt";

    /// <summary>把“打开(&amp;O)”这类加速键后缀整段去掉，而不是只去掉 <c>&amp;</c>（否则会剩下一个光秃秃的“(O)”）。</summary>
    private static readonly Regex AcceleratorSuffix = new(@"\s*\(&.\)\s*$", RegexOptions.Compiled);

    private readonly ISettingsService _settings;

    public ShellContextMenuService(ISettingsService settings) => _settings = settings;

    public IntPtr OwnerWindow { get; set; }

    // ------------------------------------------------------------------ 菜单项清单（设置页）

    public IReadOnlyList<ShellMenuItem> GetCatalog()
    {
        try
        {
            Remember(EnumerateSamples());
        }
        catch (Exception ex)
        {
            // 枚举只是“把清单填得更全”，失败时用已经记下来的部分即可
            Log.Exception("系统右键菜单：枚举样本", ex);
        }

        return Sort(_settings.Current.ShellMenuKnownItems);
    }

    public bool IsDisabled(string key)
        => !string.IsNullOrEmpty(key) && _settings.Current.ShellMenuDisabledItems.Contains(key);

    // ------------------------------------------------------------------ 弹出

    public void Show(IReadOnlyList<string> paths, bool isBackground, int screenX, int screenY)
    {
        if (OwnerWindow == IntPtr.Zero)
        {
            Log.Write("系统右键菜单：窗口句柄还没准备好，跳过");
            return;
        }

        using var session = Open(paths, isBackground, out var scope);
        if (session is null)
        {
            return;
        }

        try
        {
            var contextMenu = (IContextMenu)session.MenuObject;

            // 顺手把这次见到的项记进清单（用户可能刚装了新扩展，设置页下次打开就能看到）
            var discovered = new List<ShellMenuItem>();
            Walk(session.Menu, contextMenu, scope, string.Empty, discovered, IsDisabled);
            Remember(discovered);

            // 这一行也是回归脚本的断言依据：Win32 弹出菜单里没有 UIA 能读的菜单项
            var disabledNames = discovered.Where(i => IsDisabled(i.Key)).Select(i => i.Text).ToList();
            Log.Write(
                $"系统右键菜单：{scope} 上下文共 {discovered.Count} 项"
                + (disabledNames.Count > 0 ? $"，已关闭 {string.Join('、', disabledNames)}" : string.Empty));

            // 先把本窗口置前：不这么做的话菜单可能一弹出来就因为没有前台窗口而立即被关掉
            SetForegroundWindow(OwnerWindow);

            uint selected;
            using (var host = new ShellMenuHost(OwnerWindow, contextMenu))
            {
                selected = TrackPopupMenuEx(
                    session.Menu,
                    TpmReturnCmd | TpmRightButton,
                    screenX,
                    screenY,
                    OwnerWindow,
                    IntPtr.Zero);
            }

            // 菜单关闭后补一条 WM_NULL：否则系统可能仍认为菜单还在，导致紧接着的点击被吃掉
            PostMessage(OwnerWindow, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (selected >= IdCmdFirst && selected <= IdCmdLast)
            {
                Invoke(session.MenuObject, selected - IdCmdFirst, screenX, screenY);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("系统右键菜单：弹出", ex);
        }
    }

    /// <summary>把选中的偏移写回 <c>IContextMenu</c>，由外壳自己执行命令（exdir 不实现任何命令）。</summary>
    private void Invoke(object menuObject, uint offset, int screenX, int screenY)
    {
        var contextMenu = (IContextMenu)menuObject;

        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CmicMaskUnicode | CmicMaskPtInvoke,
            hwnd = OwnerWindow,
            lpVerb = (IntPtr)(long)offset,
            lpVerbW = (IntPtr)(long)offset,
            nShow = SwShowNormal,
            ptInvokeX = screenX,
            ptInvokeY = screenY,
        };

        var hr = contextMenu.InvokeCommand(ref info);
        if (hr < 0)
        {
            Log.Write($"系统右键菜单：InvokeCommand 失败（0x{hr:X8}，偏移 {offset}）");
        }
    }

    // ------------------------------------------------------------------ 构建 / 枚举

    /// <summary>
    /// 建一张装好系统菜单项的 HMENU。
    /// 选中项走 <c>IShellFolder.GetUIObjectOf</c>，目录背景走目录自己的
    /// <c>IShellFolder.CreateViewObject</c>。
    /// </summary>
    private ContextMenuSession? Open(IReadOnlyList<string> paths, bool isBackground, out string scope)
    {
        scope = ShellMenuItem.FileScope;

        var session = new ContextMenuSession();
        object? contextMenu = null;
        object? boundObject = null;

        try
        {
            var hwnd = OwnerWindow;

            if (isBackground)
            {
                scope = ShellMenuItem.BackgroundScope;

                var directory = paths.FirstOrDefault(static p => !string.IsNullOrEmpty(p));
                if (directory is null)
                {
                    return null;
                }

                var pidl = ParsePidl(directory);
                if (pidl == IntPtr.Zero)
                {
                    return null;
                }

                session.Track(pidl);

                // 背景菜单属于“这个目录自己的文件夹视图”，而不是它所在目录的：
                // 先把这个绝对 pidl 绑定成目录本身的 IShellFolder（SHBindToObject 传 null = 从桌面开始绑），
                // 再向它要 IContextMenu —— 这才是“文件夹背景菜单”
                var iidFolder = IidShellFolder;
                if (SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, ref iidFolder, out boundObject) < 0
                    || boundObject is null)
                {
                    return null;
                }

                var iidMenu = IidContextMenu;
                if (((IShellFolder)boundObject).CreateViewObject(hwnd, ref iidMenu, out contextMenu) < 0
                    || contextMenu is null)
                {
                    return null;
                }
            }
            else
            {
                var items = paths.Where(static p => !string.IsNullOrEmpty(p)).ToList();
                if (items.Count == 0)
                {
                    return null;
                }

                // GetUIObjectOf 一次只能针对一个文件夹：只取与第一项同目录的那些
                // （就地展开出来的子项可能属于另一个目录，多余的选择直接忽略）
                var parentDirectory = Path.GetDirectoryName(items[0]);
                var group = items
                    .Where(p => string.Equals(Path.GetDirectoryName(p), parentDirectory, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                scope = group.All(Directory.Exists) ? ShellMenuItem.FolderScope : ShellMenuItem.FileScope;

                var firstPidl = ParsePidl(group[0]);
                if (firstPidl == IntPtr.Zero)
                {
                    return null;
                }

                session.Track(firstPidl);

                var iidFolder = IidShellFolder;
                if (SHBindToParent(firstPidl, ref iidFolder, out boundObject, out var firstChild) < 0
                    || boundObject is null)
                {
                    return null;
                }

                var children = new List<IntPtr> { firstChild };
                for (var i = 1; i < group.Count; i++)
                {
                    var pidl = ParsePidl(group[i]);
                    if (pidl == IntPtr.Zero)
                    {
                        continue;
                    }

                    session.Track(pidl);

                    var iid = IidShellFolder;
                    if (SHBindToParent(pidl, ref iid, out _, out var child) >= 0)
                    {
                        children.Add(child);
                    }
                }

                var iidMenu = IidContextMenu;
                var hr = ((IShellFolder)boundObject).GetUIObjectOf(
                    hwnd,
                    (uint)children.Count,
                    children.ToArray(),
                    ref iidMenu,
                    IntPtr.Zero,
                    out contextMenu);

                if (hr < 0 || contextMenu is null)
                {
                    return null;
                }
            }

            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return null;
            }

            if (((IContextMenu)contextMenu).QueryContextMenu(menu, 0, IdCmdFirst, IdCmdLast, 0) < 0)
            {
                DestroyMenu(menu);
                return null;
            }

            session.Menu = menu;
            session.MenuObject = contextMenu;
            contextMenu = null;
            return session;
        }
        catch (Exception ex)
        {
            Log.Exception("系统右键菜单：构建菜单", ex);
            return null;
        }
        finally
        {
            Release(contextMenu);
            Release(boundObject);

            // 没建成功（Menu 仍是 0）时也要把 pidl 释放掉
            if (session.Menu == IntPtr.Zero)
            {
                session.Dispose();
            }
        }
    }

    /// <summary>样本目标：%TEMP% 下的样本文件、设置目录（文件夹 + 目录背景）。</summary>
    private List<ShellMenuItem> EnumerateSamples()
    {
        var found = new List<ShellMenuItem>();

        var sampleFile = EnsureSampleFile();
        var sampleFolder = _settings.DataDirectory;

        try
        {
            Directory.CreateDirectory(sampleFolder);
        }
        catch (Exception)
        {
            // 目录建不出来就不枚举文件夹那两份
        }

        if (sampleFile is not null)
        {
            Enumerate(found, new[] { sampleFile }, isBackground: false, ShellMenuItem.FileScope);
        }

        if (Directory.Exists(sampleFolder))
        {
            Enumerate(found, new[] { sampleFolder }, isBackground: false, ShellMenuItem.FolderScope);
            Enumerate(found, new[] { sampleFolder }, isBackground: true, ShellMenuItem.BackgroundScope);
        }

        return found;
    }

    private void Enumerate(List<ShellMenuItem> found, IReadOnlyList<string> paths, bool isBackground, string scope)
    {
        if (OwnerWindow == IntPtr.Zero)
        {
            return;
        }

        using var session = Open(paths, isBackground, out _);
        if (session is null)
        {
            return;
        }

        // 只读一遍菜单项，不做任何删除（isDisabled 传 null）
        Walk(session.Menu, (IContextMenu)session.MenuObject, scope, string.Empty, found, isDisabled: null);
    }

    /// <summary>
    /// 递归遍历菜单：把菜单项记进 <paramref name="found" />；<paramref name="isDisabled" /> 不为 null 时
    /// 把被关掉的项从 HMENU 里删掉。
    /// </summary>
    /// <returns>这个菜单现在空了（且原来是有项的）—— 调用方要把这个子菜单项本身也删掉。</returns>
    private static bool Walk(
        IntPtr menu,
        IContextMenu contextMenu,
        string scope,
        string menuPath,
        List<ShellMenuItem> found,
        Func<string, bool>? isDisabled)
    {
        var hadItems = false;
        var index = 0;

        while (index < GetMenuItemCount(menu))
        {
            if (!TryGetItemInfo(menu, index, out var info) || (info.fType & MfSeparator) != 0)
            {
                // 分隔符本身不能删（删了会把位置算乱），最后统一清理
                index++;
                continue;
            }

            hadItems = true;

            var text = CleanText(GetMenuText(menu, index));

            if (info.hSubMenu != IntPtr.Zero)
            {
                var childPath = string.IsNullOrEmpty(text)
                    ? menuPath
                    : string.IsNullOrEmpty(menuPath) ? text : menuPath + " › " + text;

                if (Walk(info.hSubMenu, contextMenu, scope, childPath, found, isDisabled))
                {
                    // 子菜单里的项全被关掉了：连子菜单项一起去掉，免得留一个空菜单
                    RemoveMenu(menu, (uint)index, MfByPosition);
                    continue;
                }
            }

            var verb = GetVerb(contextMenu, info.wID);
            var key = BuildKey(verb, menuPath, text);

            if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(verb))
            {
                found.Add(new ShellMenuItem
                {
                    Key = key,
                    Text = string.IsNullOrEmpty(text) ? verb! : text,
                    MenuPath = menuPath,
                    Scopes = new List<string> { scope },
                });
            }

            if (isDisabled is not null && isDisabled(key))
            {
                RemoveMenu(menu, (uint)index, MfByPosition);
                continue;
            }

            index++;
        }

        TrimSeparators(menu);
        return hadItems && !HasRealItems(menu);
    }

    /// <summary>删掉开头 / 结尾 / 连续重复的分隔符（关掉几项之后就可能出现）。</summary>
    private static void TrimSeparators(IntPtr menu)
    {
        var changed = true;

        while (changed)
        {
            changed = false;
            var count = GetMenuItemCount(menu);

            for (var i = 0; i < count; i++)
            {
                if (!IsSeparator(menu, i))
                {
                    continue;
                }

                if (i == 0 || i == count - 1 || IsSeparator(menu, i - 1))
                {
                    RemoveMenu(menu, (uint)i, MfByPosition);
                    changed = true;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ 菜单项识别

    /// <summary>取外壳给的规范动词；拿不到（或 id 不在本次分配的范围内）返回 null。</summary>
    private static string? GetVerb(IContextMenu contextMenu, uint id)
    {
        if (id < IdCmdFirst || id > IdCmdLast)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(260);
            var hr = contextMenu.GetCommandString(
                new IntPtr((long)(id - IdCmdFirst)),
                GcsVerbW,
                IntPtr.Zero,
                buffer,
                (uint)buffer.Capacity);

            return hr >= 0 && buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 菜单项的稳定标识：优先用规范动词（<c>open</c> / <c>7-Zip.Compress</c>…），
    /// 这样“打开”在文件 / 文件夹 / 背景三个上下文里是同一个开关；
    /// 拿不到动词的项（标准和多数扩展的子菜单项）退回“上级菜单 + 文本”。
    /// </summary>
    private static string BuildKey(string? verb, string menuPath, string text)
    {
        if (!string.IsNullOrEmpty(verb))
        {
            return "verb:" + verb.ToLowerInvariant();
        }

        var path = string.IsNullOrEmpty(menuPath) ? string.Empty : menuPath.ToLowerInvariant() + " › ";
        return "text:" + path + text.ToLowerInvariant();
    }

    /// <summary>去掉 <c>&amp;</c> 加速键与结尾的省略号，得到给人看的名字。</summary>
    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = AcceleratorSuffix.Replace(text, string.Empty);
        value = value.Replace("&", string.Empty);
        return value.Trim().TrimEnd('.', '…').Trim();
    }

    private static string GetMenuText(IntPtr menu, int index)
    {
        var buffer = new StringBuilder(512);
        var length = GetMenuString(menu, (uint)index, buffer, buffer.Capacity, MfByPosition);
        return length > 0 ? buffer.ToString(0, Math.Min(length, buffer.Length)) : string.Empty;
    }

    private static bool TryGetItemInfo(IntPtr menu, int index, out MENUITEMINFO info)
    {
        info = new MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
            fMask = MiimId | MiimSubMenu | MiimFType,
        };

        return GetMenuItemInfo(menu, (uint)index, true, ref info);
    }

    private static bool IsSeparator(IntPtr menu, int index)
        => TryGetItemInfo(menu, index, out var info) && (info.fType & MfSeparator) != 0;

    private static bool HasRealItems(IntPtr menu)
    {
        var count = GetMenuItemCount(menu);

        for (var i = 0; i < count; i++)
        {
            if (TryGetItemInfo(menu, i, out var info) && (info.fType & MfSeparator) == 0)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ 清单维护

    /// <summary>把这次见到的项并进清单（按 <see cref="ShellMenuItem.Key" /> 去重，作用域取并集）。</summary>
    private void Remember(IEnumerable<ShellMenuItem> items)
    {
        var known = _settings.Current.ShellMenuKnownItems;

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Key))
            {
                continue;
            }

            var existing = known.FirstOrDefault(k => string.Equals(k.Key, item.Key, StringComparison.Ordinal));
            if (existing is null)
            {
                known.Add(item);
                continue;
            }

            // 文本 / 子菜单可能随语言或版本变化，以这次看到的为准
            existing.Text = item.Text;
            existing.MenuPath = item.MenuPath;

            foreach (var scope in item.Scopes)
            {
                if (!existing.Scopes.Contains(scope))
                {
                    existing.Scopes.Add(scope);
                }
            }
        }
    }

    private static IReadOnlyList<ShellMenuItem> Sort(IEnumerable<ShellMenuItem> items)
        => items
            .OrderBy(static i => i.MenuPath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static i => i.Text, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ 杂项

    private static IntPtr ParsePidl(string path)
    {
        try
        {
            return SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) >= 0 ? pidl : IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>枚举“文件”上下文用的样本文件：没有就现建一个（几十字节，留在 %TEMP% 里）。</summary>
    private static string? EnsureSampleFile()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), SampleFileName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "exdir 右键菜单样本 / exdir context menu sample");
            }

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    /// <summary>一次菜单构建的产物：HMENU + 背后的 IContextMenu + 需要顺带释放的 pidl。</summary>
    private sealed class ContextMenuSession : IDisposable
    {
        private readonly List<IntPtr> _pidls = new();

        public IntPtr Menu { get; set; }

        public object MenuObject { get; set; } = null!;

        /// <summary>记下自己分配的 pidl（<c>SHParseDisplayName</c> 的产物由调用方 <c>CoTaskMemFree</c>）。</summary>
        public void Track(IntPtr pidl)
        {
            if (pidl != IntPtr.Zero)
            {
                _pidls.Add(pidl);
            }
        }

        public void Dispose()
        {
            if (Menu != IntPtr.Zero)
            {
                DestroyMenu(Menu);
                Menu = IntPtr.Zero;
            }

            foreach (var pidl in _pidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }

            _pidls.Clear();

            Release(MenuObject);
            MenuObject = null!;
        }
    }
}
