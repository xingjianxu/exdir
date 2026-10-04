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

    // ------------------------------------------------------------------ 读成菜单项树（供内置菜单合并）

    /// <summary>
    /// 缓存上限：键里带着目录，所以一个目录一份。满了按最近最少使用淘汰，
    /// 淘汰时连它背后的 HMENU / <c>IContextMenu</c> 一起释放。
    /// </summary>
    private const int MaxCachedMenus = 24;

    private readonly Dictionary<string, ShellMenuSnapshot> _cache = new(StringComparer.Ordinal);

    public ShellMenuSnapshot? GetMenuItems(IReadOnlyList<string> paths, bool isBackground)
    {
        if (OwnerWindow == IntPtr.Zero)
        {
            return null;
        }

        var signature = BuildSignature(paths, isBackground);
        if (signature is null)
        {
            return null;
        }

        if (_cache.TryGetValue(signature, out var cached))
        {
            cached.LastUsedTicks = Environment.TickCount64;
            Log.Write($"系统右键菜单：命中缓存（{cached.Scope} 上下文 {cached.Items.Count} 项）");
            return cached;
        }

        var snapshot = BuildSnapshot(paths, isBackground, signature);
        if (snapshot is null)
        {
            return null;
        }

        _cache[signature] = snapshot;
        Evict();

        Log.Write(
            $"系统右键菜单：读出菜单项（{snapshot.Scope} 上下文 {snapshot.Items.Count} 项，"
            + $"含子菜单共 {CountEntries(snapshot.Items)} 项，其中 {CountIcons(snapshot.Items)} 项带图标，缓存 {_cache.Count} 份）");
        return snapshot;
    }

    /// <summary>数一下项树里有几个带图标的项（包含各级子菜单）—— 日志里能看出“图标到底抄到没”。</summary>
    private static int CountIcons(IReadOnlyList<ShellMenuEntry> entries)
    {
        var total = 0;

        foreach (var entry in entries)
        {
            if (entry.Icon is not null)
            {
                total++;
            }

            total += CountIcons(entry.Children);
        }

        return total;
    }

    /// <summary>把项树里的所有项（含各级子菜单）数一遍 —— 日志里能看出子菜单是不是真的被填上了。</summary>
    private static int CountEntries(IReadOnlyList<ShellMenuEntry> entries)
    {
        var total = 0;

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                continue;
            }

            total += 1 + CountEntries(entry.Children);
        }

        return total;
    }

    public bool InvokeMenuEntry(ShellMenuSnapshot snapshot, ShellMenuEntry entry, int screenX, int screenY)
    {
        // 偏移只在**那一张 HMENU** 上有意义，所以快照连同背后的会话一起活在缓存里；
        // 子菜单项本身没有命令，点它不算执行
        if (snapshot.Session is not ContextMenuSession session || entry.IsSeparator || entry.Children.Count > 0)
        {
            return false;
        }

        try
        {
            // 与弹真菜单那条路一样：先把本窗口置前，免得外壳命令弹出的对话框没有属主 / 抢不到焦点
            SetForegroundWindow(OwnerWindow);
            Invoke(session.MenuObject, entry.Offset, screenX, screenY);
            snapshot.LastUsedTicks = Environment.TickCount64;
            Log.Write($"内置右键菜单：执行系统菜单项「{entry.Text}」（偏移 {entry.Offset}，{snapshot.Scope} 上下文）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception("系统右键菜单：执行菜单项", ex);
            return false;
        }
    }

    public void Preheat()
    {
        if (OwnerWindow == IntPtr.Zero)
        {
            return;
        }

        var started = Environment.TickCount64;
        var warmed = 0;

        var sampleFile = EnsureSampleFile();
        var sampleFolder = _settings.DataDirectory;

        try
        {
            Directory.CreateDirectory(sampleFolder);
        }
        catch (Exception)
        {
            // 配置目录建不出来就不预热文件夹那两份
        }

        // 预热本身不是为了“这几份以后能命中”（键里带着目录，样本目录很少再被右键），
        // 而是把第三方 shell 扩展先 Load 进本进程 —— 用户第一次真右键时就不用等了
        if (sampleFile is not null && GetMenuItems(new[] { sampleFile }, false) is not null)
        {
            warmed++;
        }

        if (Directory.Exists(sampleFolder))
        {
            if (GetMenuItems(new[] { sampleFolder }, false) is not null)
            {
                warmed++;
            }

            if (GetMenuItems(new[] { sampleFolder }, true) is not null)
            {
                warmed++;
            }
        }

        Log.Write($"系统右键菜单：预热完成，用时 {Environment.TickCount64 - started} ms，读到 {warmed} 份，缓存 {_cache.Count} 份");
    }

    private ShellMenuSnapshot? BuildSnapshot(IReadOnlyList<string> paths, bool isBackground, string signature)
    {
        var session = Open(paths, isBackground, out var scope);
        if (session is null)
        {
            return null;
        }

        try
        {
            var contextMenu = (IContextMenu)session.MenuObject;
            var discovered = new List<ShellMenuItem>();
            var icons = new IconStats();
            var items = ReadTree(session.Menu, contextMenu, scope, string.Empty, discovered, icons);

            TrimSeparators(items);

            // 顺手把这次见到的项并进“清单”（设置页下次打开就能看到，不必再枚举一遍）
            Remember(discovered);

            if (icons.WithIcon > 0 || icons.ShellDraw > 0 || icons.Failed > 0)
            {
                Log.Write(
                    $"系统右键菜单：图标抄到 {icons.WithIcon} 个，外壳没给位图（HBMMENU_CALLBACK / 特殊值）{icons.ShellDraw} 个，"
                    + $"读取失败 {icons.Failed} 个{(icons.FirstFailure is null ? string.Empty : "（" + icons.FirstFailure + "）")}");
            }

            return new ShellMenuSnapshot(signature, scope, items)
            {
                Session = session,
                LastUsedTicks = Environment.TickCount64,
            };
        }
        catch (Exception ex)
        {
            Log.Exception("系统右键菜单：读取菜单项", ex);
            session.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 把一张装好系统菜单项的 HMENU 读成项树（递归到子菜单）。
    ///
    /// 两个关键动作：
    ///   * **替系统代发 <c>WM_INITMENUPOPUP</c>**（<see cref="InitializeSubMenu" />）：<c>打开方式</c> 这类
    ///     子菜单是“即将展开时才填内容”的，真菜单靠 <c>TrackPopupMenu</c> 的跟踪循环发这条消息，
    ///     我们自己画就只能自己发，否则子菜单里永远只有一个同名占位项；
    ///   * **过滤**：用户在设置里关掉的项（<see cref="IsDisabled" />）连同整棵子菜单都不产出，
    ///     owner-draw 且连文本都读不到的项也跳过（渲染出来只会是一行空白）。
    /// </summary>
    private List<ShellMenuEntry> ReadTree(
        IntPtr menu,
        IContextMenu contextMenu,
        string scope,
        string menuPath,
        List<ShellMenuItem> discovered,
        IconStats icons)
    {
        var entries = new List<ShellMenuEntry>();
        var count = GetMenuItemCount(menu);

        for (var index = 0; index < count; index++)
        {
            if (!TryGetItemInfo(menu, index, out var info))
            {
                continue;
            }

            if ((info.fType & MfSeparator) != 0)
            {
                entries.Add(new ShellMenuEntry { IsSeparator = true });
                continue;
            }

            var text = CleanText(GetMenuText(menu, index));
            var verb = GetVerb(contextMenu, info.wID);
            var key = BuildKey(verb, menuPath, text);

            var children = new List<ShellMenuEntry>();
            if (info.hSubMenu != IntPtr.Zero)
            {
                // 顺序很重要：先代发 INITMENUPOPUP 让外壳把子菜单填上，再去读
                InitializeSubMenu(contextMenu, info.hSubMenu, index);

                var childPath = string.IsNullOrEmpty(text)
                    ? menuPath
                    : string.IsNullOrEmpty(menuPath) ? text : menuPath + " › " + text;

                children = ReadTree(info.hSubMenu, contextMenu, scope, childPath, discovered, icons);
            }

            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(verb))
            {
                // owner-draw 项的文本在它自己的 dwItemData（指针）里，不敢按字符串去解引用；
                // 既没文本也没动词就渲染不出东西来，记一行日志跳过
                Log.Write(
                    $"系统右键菜单：跳过一项读不到文本的菜单项（owner-draw={(info.fType & MftOwnerDraw) != 0}，子项 {children.Count} 个）");
                continue;
            }

            var display = string.IsNullOrEmpty(text) ? verb! : text;

            discovered.Add(new ShellMenuItem
            {
                Key = key,
                Text = display,
                MenuPath = menuPath,
                Scopes = new List<string> { scope },
            });

            if (IsDisabled(key))
            {
                continue;
            }

            if (children.Count == 0 && info.hSubMenu != IntPtr.Zero)
            {
                // 子菜单是空的（扩展没填 / INITMENUPOPUP 没被理）—— 留一个点不出东西的子菜单只会让人困惑
                continue;
            }

            var entry = new ShellMenuEntry
            {
                Key = key,
                Text = display,
                Verb = verb,
                MenuPath = menuPath,
                Offset = info.wID >= IdCmdFirst && info.wID <= IdCmdLast ? info.wID - IdCmdFirst : 0,
                IsEnabled = (info.fState & (MfsDisabled | MfsGrayed)) == 0,
                IsChecked = (info.fState & MfsChecked) != 0,
                IsDefault = (info.fState & MfsDefault) != 0,
                IsOwnerDraw = (info.fType & MftOwnerDraw) != 0,
                Icon = ReadMenuIcon(info.hbmpItem, icons),
            };

            entry.Children.AddRange(children);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// 替系统把 <c>WM_INITMENUPOPUP</c> 交给 <c>IContextMenu2/3</c>。
    ///
    /// 外壳与不少扩展是懒填子菜单的（<c>打开方式</c> 最典型），它们要等这条消息才知道该往子菜单里塞什么。
    /// 真菜单由 <c>TrackPopupMenu</c> 的跟踪循环发，我们不用它了，所以在这里自己发一次。
    /// 代发只是“尽力”：少数扩展假定自己在真正的跟踪循环里，会拒或不理 —— 那就留个空子菜单（上面会跳过）。
    /// </summary>
    private static void InitializeSubMenu(IContextMenu contextMenu, IntPtr subMenu, int parentIndex)
    {
        var menu3 = contextMenu as IContextMenu3;
        var menu2 = contextMenu as IContextMenu2;

        if (menu3 is null && menu2 is null)
        {
            return;
        }

        // lParam 低 16 位 = 这个弹出项在**父菜单**里的位置，高 16 位 = 是不是窗口菜单（不是，所以 0）
        var lParam = (IntPtr)(parentIndex & 0xFFFF);

        try
        {
            if (menu3 is not null && menu3.HandleMenuMsg2(WmInitMenuPopup, subMenu, lParam, out _) >= 0)
            {
                return;
            }

            menu2?.HandleMenuMsg(WmInitMenuPopup, subMenu, lParam);
        }
        catch (Exception ex)
        {
            Log.Exception("系统右键菜单：初始化子菜单", ex);
        }
    }

    /// <summary>
    /// 把系统给这一项配的图标（<c>MENUITEMINFO.hbmpItem</c>）抄成像素。
    ///
    /// <b>必须在这里抄</b>：那个位图是外壳建出来挂在 HMENU 上的，HMENU 一销毁（快照被 LRU 淘汰时）就作废，
    /// 而这份快照是要留着执行的 —— 延后到渲染时再去 GetDIBits 只能是随机失败。
    /// 外壳没给图标（Windows 10 的剪切 / 复制 / 删除 / 属性这类标准动词就是）或让宿主自己画
    /// （<c>HBMMENU_CALLBACK</c>，Windows 11 上有）时返回 null，那一项就不带图标。
    /// </summary>
    private static IconBitmap? ReadMenuIcon(IntPtr hbmpItem, IconStats icons)
    {
        if (hbmpItem == IntPtr.Zero)
        {
            return null;
        }

        if (IsSpecialMenuBitmap(hbmpItem))
        {
            icons.ShellDraw++;
            return null;
        }

        var result = ShellIconExtractor.FromMenuBitmap(hbmpItem, out var failure);
        if (result is null)
        {
            icons.Failed++;
            icons.FirstFailure ??= failure;
            return null;
        }

        var value = result.Value;
        icons.WithIcon++;
        return new IconBitmap(value.Width, value.Height, value.Pixels, value.ContentHash);
    }

    /// <summary>一次快照里“图标抄到 / 抄不到”的计数（只给日志用）。</summary>
    private sealed class IconStats
    {
        public int WithIcon;

        /// <summary>外壳把图标留给宿主自己画（<c>HBMMENU_CALLBACK</c>）或用了其它 <c>HBMMENU_*</c> 特殊值，我们抄不到。</summary>
        public int ShellDraw;

        public int Failed;

        public string? FirstFailure;
    }

    /// <summary>去掉开头 / 结尾 / 连续重复的分隔符（用户关掉几项之后就会出现）。</summary>
    private static void TrimSeparators(List<ShellMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            TrimSeparators(entry.Children);
        }

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (!entries[i].IsSeparator)
            {
                continue;
            }

            if (i == 0 || i == entries.Count - 1 || entries[i - 1].IsSeparator)
            {
                entries.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 缓存的键：**作用域 + 所在目录 + 每个选中项是目录还是什么扩展名**。
    ///
    /// 为什么不能省掉目录：菜单内容本来就跟着目录走（仓库里才有 Git 那几项、目录背景的「新建」、
    /// 每个文件夹自己的自定义动词），而**执行时用的偏移只在那一张 HMENU 上有意义** ——
    /// 复用了别的目录那一份，点了「在此处打开终端」就会开到别的目录去。
    /// </summary>
    private static string? BuildSignature(IReadOnlyList<string> paths, bool isBackground)
    {
        if (isBackground)
        {
            var directory = paths.FirstOrDefault(static p => !string.IsNullOrEmpty(p));
            return directory is null ? null : "bg|" + directory.TrimEnd('\\');
        }

        var items = paths.Where(static p => !string.IsNullOrEmpty(p)).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        // 与 Open() 保持一致：GetUIObjectOf 一次只针对一个文件夹，只认与第一项同目录的那些
        var parent = Path.GetDirectoryName(items[0]) ?? string.Empty;
        var parts = items
            .Where(p => string.Equals(Path.GetDirectoryName(p), parent, StringComparison.OrdinalIgnoreCase))
            .Select(static p => Directory.Exists(p) ? "d" : "f" + Path.GetExtension(p).ToLowerInvariant())
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToList();

        return parts.Count == 0 ? null : "it|" + parent.TrimEnd('\\') + "|" + string.Join(',', parts);
    }

    /// <summary>超过上限就丢掉最久没用过的那几份（连同背后的 HMENU / IContextMenu 一起释放）。</summary>
    private void Evict()
    {
        while (_cache.Count > MaxCachedMenus)
        {
            var oldest = _cache.Values.OrderBy(static s => s.LastUsedTicks).First();
            _cache.Remove(oldest.Signature);
            oldest.Dispose();
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
            fMask = MiimId | MiimSubMenu | MiimFType | MiimState | MiimBitmap,
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
