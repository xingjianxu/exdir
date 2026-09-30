# AGENTS.md — exdir 开发指南（供 AI 编码助手使用）

> 本文件是本仓库的“事实来源”。动手改代码前先读完本文件；跨步骤的功能规划见 `plan.md`。

## 1. 项目是什么

`exdir` 是一个类似 **Directory Opus** 的 Windows 文件管理器，界面追求**紧凑**。

技术栈：

| 项 | 值 |
| --- | --- |
| UI 框架 | WinUI 3（Windows App SDK 2.5.1 / WinUI 2.3.9） |
| 语言 | C# 12，`net8.0-windows10.0.19041.0`，`TargetPlatformMinVersion 10.0.17763.0` |
| MVVM | CommunityToolkit.Mvvm 8.4.2（`[ObservableProperty]` / `[RelayCommand]` / `[NotifyCanExecuteChangedFor]`） |
| DI | Microsoft.Extensions.DependencyInjection 8.0.1 |
| 托盘图标 | H.NotifyIcon.WinUI 2.3.2（关闭按钮只隐藏窗口，进程常驻托盘，见第 4 节“托盘驻留”） |
| 设置卡片 | CommunityToolkit.WinUI.Controls.SettingsControls 8.2.251219（`SettingsCard`：Windows 11 设置界面的那行卡片，见第 4 节“设置窗口”） |
| 部署 | **非打包（unpackaged）**：`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true` |
| 平台 | x64（`Platforms` 里也声明了 x86/ARM64，未验证） |

界面语言：**简体中文硬编码**（不引入 .resw 资源）。

## 2. 环境与命令（重要）

本机运行在 WSL 里，通过 Windows PowerShell 调用 Windows 工具链。
**bash 里没有 coreutils**（`ls`/`head`/`cat` 都不可用），所以几乎所有命令都要走 pwsh：

```bash
# 统一的 pwsh 调用方式（bash 中）
"/mnt/c/Users/xingjian/AppData/Local/Microsoft/WindowsApps/pwsh.exe" -NoProfile -Command "<PowerShell 代码>"
# 或执行脚本
"/mnt/c/Users/xingjian/AppData/Local/Microsoft/WindowsApps/pwsh.exe" -NoProfile -File "D:\prj\exdir\tools\xxx.ps1"
```

已安装并验证过的工具链：dotnet SDK 10.0.401（可编译 net8.0 项目）、Windows App SDK 2.5.1 运行时、NuGet `communitytoolkit.mvvm 8.4.2`。

### 常用命令

```powershell
cd D:\prj\exdir

# 1) 日常开发构建（Debug）
dotnet build exdir.csproj -c Debug -p:Platform=x64 --nologo

# 2) 出 Release（dotnet publish + 裁剪，再镜像到 dist\win-x64）
pwsh -NoProfile -File tools\publish.ps1

# 3) 启动 + 截图（验证 UI 渲染）
pwsh -NoProfile -File tools\capture.ps1                              # Debug 版
pwsh -NoProfile -File tools\capture.ps1 -Exe dist\win-x64\exdir.exe  # Release 版
pwsh -NoProfile -File tools\capture.ps1 -Keys '{F10}','^{t}'         # 先发快捷键再截图

# 4) 导出/操作真实控件树（无法肉眼看界面时的主要验证手段）
pwsh -NoProfile -File tools\inspect-ui.ps1                      # 打印控件树（名称 + 物理坐标 + 尺寸）
pwsh -NoProfile -File tools\inspect-ui.ps1 -Filter Desktop      # 按名称查元素坐标
pwsh -NoProfile -File tools\inspect-ui.ps1 -Click "快捷菜单"      # 真实鼠标点击并截图到 .artifacts
pwsh -NoProfile -File tools\inspect-ui.ps1 -Hover "Documents"   # 真鼠标移上去（不点击）并截图，看悬停高亮
pwsh -NoProfile -File tools\inspect-ui.ps1 -HoverAt "157,37"    # 同上，但按窗口内坐标悬停（中文名不好传参时用）

# 5) 重新生成应用图标：从仓库根的 icon.svg 渲染出 Assets\exdir.ico（exe / 标题栏 / 任务栏 / 托盘
#    图标都用它）以及 Assets 下的各尺寸徽标 PNG；每个尺寸都按原尺寸单独栅格化，
#    栅格化用 Windows 自带的 Edge（headless 截图，透明底），找不到脑 Edge 用 -Edge 或 EXDIR_EDGE 指定
#    改了 icon.svg 之后跑一次，然后要重新 build/publish 才会换掉 exe 里的图标
pwsh -NoProfile -File tools\make-icon.ps1

# 6) 拖放回归（文件列表 / 侧边栏 → 工具条固定目录，含右键取消固定）
#    需要交互桌面；当前 shell 提权时会自动改用 explorer.exe 以普通权限启动 exdir（见第 6 节第 21 条）
pwsh -NoProfile -File tools\test-pin-drag.ps1

# 7) 设置窗口回归（左导航五个分类 / 每页只显示本分类的项 / 拨一下就立即生效并落盘 /
#    跨分类读回 / 关窗重开仍是新值 / 「右键菜单」页的系统菜单项默认全开且能逐项关掉 /
#    「行高」滑块拖完列表行真的变高 / 「侧边栏」页的分组开关关掉后树里真的少一个分组 /
#    「侧边栏」页里「主目录」的标准文件夹开关（桌面 / 文档 / 下载…）关掉后树里真的少一个 /
#    「标签页使用直角」拨一下就落盘并当场应用（exdir.log 里能看到 标签页=圆角/直角） /
#    「主题」下拉框选浅色 / 深色 / 跟随系统都会落盘并当场应用，标题栏的太阳 / 月亮开关改的是同一个设置）
#    全程走 UIA 模式，不需要真鼠标、不需要前台窗口；跑完还原 settings.json
pwsh -NoProfile -File tools\test-settings.ps1

# 7b) 设置窗口截图（左侧每个分类各一张，肉眼验证“左导航 + 右侧设置卡片”的布局）
#     需要交互桌面（要真把窗口提到前台才截得到内容）；输出 .artifacts\settings-<分类名>.png
pwsh -NoProfile -File tools\shot-settings.ps1

# 8) 状态栏回归（只有一条 / 一行高 / 贴底 / 项数 / 选中摘要 + 合计大小 / 磁盘可用空间 / 跟随活动窗格）
#    需要交互桌面；脚本会把 exdir 置顶（终端铺满屏幕时才拍得到 exdir）
pwsh -NoProfile -File tools\test-status-bar.ps1

# 9) 真实外壳图标回归（每行都有图标 / 不同程序图标不同 / .lnk 带小箭头 / 同扩展名只提取一次 / 滚动后仍有图标）
#    需要交互桌面；同样会把 exdir 置顶，跑完还原 settings.json
pwsh -NoProfile -File tools\test-shell-icons.ps1

# 10) 行内图标与文字是否垂直居中对齐（截图 + UIA 量“墨迹中心”，断言偏差在容差内）
#     需要交互桌面；需要测“图标该不该再上下微调”时用这个，不要靠肉眼
pwsh -NoProfile -File tools\measure-row-align.ps1

# 11) 系统右键菜单回归（文件行 / 列表空白处各自弹出 #32768 系统菜单；
#     菜单项记进清单；关掉 verb:properties 后菜单里不再有「属性」）
#     需要交互桌面（真鼠标右键 + 截图）；跑完还原 settings.json
pwsh -NoProfile -File tools\test-context-menu.ps1

# 12) 文件列表选择回归（单击行 / Ctrl+A 全选 / 单击空白处取消选择 / 地址栏的 Ctrl+A 不抢）
#     需要交互桌面（真鼠标点击 + SendKeys），跑之前桌面上不能有窗口盖住 exdir；跑完还原 settings.json
pwsh -NoProfile -File tools\test-list-selection.ps1

# 12b) 双击命中范围回归（整条行高亮区内任意位置双击都算：行内边距 / 名称文字右侧空白 /
#      类型列与大小列的空白处 / 行首展开箭头只展开不进目录 / 列表下方空白处不导航）
#      需要交互桌面（真鼠标双击），跑完还原 settings.json
pwsh -NoProfile -File tools\test-row-dblclick.ps1

# 12c) 列宽拖动回归（拖每个列边界都真的改宽度 / 列头与数据行仍对齐 / 双击复位 /
#      最右一列也拖得动 / 拖完落盘 / 列头排序按钮仍可用）
#      需要交互桌面（真鼠标拖拽）；跑之前桌面上不能有窗口盖住 exdir；跑完还原 settings.json
pwsh -NoProfile -File tools\test-column-resize.ps1

# 12d) 托盘驻留回归（点系统关闭按钮 → 窗口隐藏、进程驻留、托盘图标进通知区域 /
#      隐藏时再启动 exe → 第二个进程退出、已有窗口被唤回 / 菜单「隐藏到托盘」+ 点托盘图标唤回 /
#      菜单「退出」→ 进程真的结束；需要交互桌面（真鼠标点关闭按钮与托盘图标）
#      跑完还原 settings.json
pwsh -NoProfile -File tools\test-tray.ps1

# 12e) 磁盘热插拔回归（用 subst 造一个“U 盘”盘符 + 给主窗口发一条 WM_DEVICECHANGE：
#      侧边栏「此电脑」与工具条磁盘区都实时出现新盘、拔出后立刻消失，
#      而且不重建整棵树（节点清单前后一致）、不动收藏夹）
#      全程 UIA + SendMessage，不需要交互桌面；跑完删掉 subst 映射并还原 settings.json
pwsh -NoProfile -File tools\test-drive-hotplug.ps1

# 12f) 复制 / 剪切 / 粘贴 / 删除 与“拖到目录里移动”回归（真鼠标 + 真键盘 + 真实剪贴板）
#      A. 内置右键菜单的文件行有「剪切/复制/粘贴/删除」、空白处有「粘贴」
#      B. Ctrl+C 之后剪贴板上真的出现 CF_HDROP（用 System.Windows.Forms 读回来，
#         与资源管理器读剪贴板是同一条路），Preferred DropEffect = 1（复制）
#      C. 进子目录 Ctrl+V → 文件被复制过去（源还在）
#      D. Ctrl+X（DropEffect = 2）→ 进子目录 Ctrl+V → 文件被移动过去（源没了）
#      E. 把文件行拖到目录行上 → 文件被移动过去（拖拽中有整行强调色高亮，截图 .artifacts\file-ops-drag.png）
#      F/G. 外部来源（脚本往剪贴板放的 CF_HDROP，分别带复制 / 剪切标志）→ Ctrl+V 能复制 / 移动进来
#      H. Delete → 点掉外壳确认框 → 文件真的进了回收站（用 Shell.Application 的回收站名字空间读回来），
#         Shift+Delete 的同名文件则不在回收站里（永久删除）
#      需要交互桌面；当前 shell 是管理员时脚本会自动改用 explorer.exe 以普通权限启动 exdir
#      —— Windows 直接禁止提权进程参与拖放（见第 6 节第 21 条），否则用例 E 永远过不了
#      跑完还原 settings.json 并删掉测试目录
pwsh -NoProfile -File tools\test-file-ops.ps1

# 12g) 侧边栏「此电脑」里的 Windows「网络位置」回归（自己造一个指向临时目录的假网络位置：
#      启动时它出现在「此电脑」里、排在磁盘之后；点它导航到 target.lnk 的目标目录；
#      收到 WM_DEVICECHANGE 刷新后它还在（差量刷新不误删）；删掉目录再刷新它就消失）
#      全程 UIA + SendMessage，不需要交互桌面；跑完删掉假网络位置并还原 settings.json
pwsh -NoProfile -File tools\test-network-locations.ps1
```

### 任务收尾（每个任务都必须做）

**任何代码改动做完后，固定用一条命令收尾：**

```powershell
pwsh -NoProfile -File tools\publish.ps1
```

它会把最新 Release 产物镜像到 **`dist\win-x64`**——这就是交付给用户的版本。

* **只出 win-x64**，不要生成 x86 / ARM64（未验证）。
* `dist\win-x64\exdir.exe` 自包含，双击即可运行（目标机无需预装 .NET / Windows App Runtime）。
* 产物是**裁剪过的**（2026-09 起）：管线 = `dotnet publish`（PublishTrimmed + TrimMode=partial）
  → 从构建输出补回 publish 丢掉的 `exdir.pri` / `*.xbf` → 镜像到 dist。
  交付体积 **225 MB → 83 MB**（548 个文件 → 165 个），其中：
  AI/ML/Search/Widgets 组件约 55 MB、语言资源 3.2 MB（只留 zh-* / en-*）、裁剪约 97 MB。
  细节与“为什么不能开 NativeAOT”见“踩过的坑”第 63～66 条。
* 交付前确认 `dist` 是最新的：看 `dist\win-x64\build-info.txt`（记录源码提交、构建时间、文件数、
  总大小），并与 `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\exdir.exe` 的时间戳对照；
  两者不一致说明忘了 publish。
* `publish.ps1` 会校验：`exdir.exe` / `exdir.dll` / `exdir.pri` 都在、源码里每个 `.xaml` 都有
  对应 `.xbf`、`Assets\exdir.ico` 在、语言目录只剩 `zh-*` / `en-*`。任一条不满足直接报错。
* **日常 `dotnet build` 不受影响**（裁剪只在 publish 生效），但 **`dotnet publish` 现在就是发布流程本身**，
  不要再改回“build + 镜像”（见“踩过的坑”第 3 条）。

**测试范围（2026-09）**：改完只跑与本次改动直接相关的交互式回归脚本（改托盘就只跑 `tools\test-tray.ps1`，改列宽就只跑 `tools\test-column-resize.ps1`），
**不要每次把 `tools\test-*.ps1` 全跑一遍**；只有用户明确要求“全部测试”时才全跑。
就是不依赖鼠标模拟的非交互检查（`dotnet build`、单测、脚本语法）不受此限制，该跑照跑。

### 运行期日志

非打包 WinUI 应用崩溃时没有控制台输出，`Diagnostics/Log.cs` 会把异常写到：

```
%LOCALAPPDATA%\exdir\exdir.log        # 崩溃 / 启动 / 窗口位置恢复记录
%LOCALAPPDATA%\exdir\settings.json    # 全部设置与会话
```

排查启动崩溃的**第一步永远是看 exdir.log**。

### 验证循环（推荐）

改完 UI 后不要凭感觉判断，按这个顺序验证：

1. `dotnet build` 确认无编译错误；
2. `capture.ps1` 截图，用 read 工具直接看图；
3. 元素位置/存在性有疑问时用 `inspect-ui.ps1` 看真实坐标（物理像素）；
4. 有异常就 `Get-Content $env:LOCALAPPDATA\exdir\exdir.log`。

`shot-*.png` 会自动编号累加，方便对比多次迭代；`-Name xxx` 可指定后缀。
`tools\capture.ps1` / `inspect-ui.ps1` 都会**先杀掉已有 exdir 进程**再启动。

## 3. 目录结构

```
exdir/
├─ Program.cs                 自定义入口点（DISABLE_XAML_GENERATED_MAIN）：单实例闸门跑在 XAML 初始化之前
├─ App.xaml(.cs)              DI 容器、全局异常日志、创建主窗口
├─ MainWindow.xaml(.cs)       外壳：顶部菜单栏(TitleBar) / 工具条 / 侧边栏 / 1~2 个窗格 + 托盘图标（关闭即隐藏）
├─ Themes/ExdirTheme.xaml     紧凑密度覆盖 + 布局常量 + 扁平按钮样式 + 强调色悬停色刷（合并顺序在 XamlControlsResources 之后）
├─ Models/                    POCO：FileSystemEntry / DriveModel / AppSettings / AppTheme / QuickCommand / CloudSyncState / ShellMenuItem / IconBitmap / 枚举（含 SettingsCategory）
├─ Services/                  I/O 与系统交互（接口 + 实现成对出现）
│   ├─ IFileSystemService     目录枚举（异步、跳过无权限项）、路径规整、云目录条目附带同步状态
│   ├─ IDriveService          DriveInfo 枚举
│   ├─ IKnownFolderService    用户标准目录 + 云存储同步根（注册表探测）
│   ├─ INetworkLocationService  Windows「网络位置」快捷方式（%APPDATA%\Microsoft\Windows\Network Shortcuts）枚举
│   ├─ ICloudSyncService      云同步根判定 + 单个条目的同步状态（状态列）
│   ├─ IShellIconService      系统外壳图标（SHGetFileInfo 提取 + 两级缓存，见第 4 节“名称列图标”）
│   ├─ ISettingsService       settings.json 读写（含结构版本迁移）+ 源生成序列化上下文
│   │                          （SettingsJsonContext：裁剪过的发布版不能靠反射式 JsonSerializer）
│   ├─ IShellService          默认程序打开 / 终端 / 剪贴板文本 / 命令行
│   ├─ IClipboardService      文件剪贴板（写/读 CF_HDROP + Preferred DropEffect，与资源管理器互通）
│   ├─ IFileOperationService  复制 / 移动（外壳 SHFileOperation：进度对话框 + 同名冲突询问）
│   ├─ IShellContextMenuService  系统右键菜单（IContextMenu：弹出真菜单 + 枚举菜单项供设置页，见第 4 节“右键菜单”）
│   ├─ IDeviceChangeService  卷（驱动器 / U 盘 / 光驱）插拔通知：侧边栏与工具条磁盘区实时刷新
│   └─ Native/                Win32 互操作（ShellPropertyStore：属性系统 + 占位符兼容模式；
│                             ShellIconExtractor：图标提取 / HICON → BGRA 像素；
│                             ShellContextMenuInterop：IShellFolder / IContextMenu(2/3) + HMENU 操作；
│                             VolumeChangeWatcher：WM_DEVICECHANGE 的卷插拔监听；
│                             ClipboardInterop：CF_HDROP / Preferred DropEffect；
│                             FileOperationInterop：SHFileOperation 的 FO_COPY / FO_MOVE）
│                             ShellLinkInterop：.lnk 快捷方式目标解析（IShellLinkW + IPersistFile）
├─ ViewModels/
│   ├─ MainViewModel          磁盘、固定目录、快捷命令、侧边栏、两个窗格、全局命令
│   ├─ PanelViewModel         一个窗格（标签页集合）
│   ├─ FolderTabViewModel     一个标签页（当前目录、条目、选中、历史、排序、地址栏编辑态）
│   ├─ PathSegmentViewModel   地址栏面包屑里的一段路径（显示名 + 完整路径 + 是否当前段）
│   ├─ SidebarViewModel       文件夹树（懒加载；含镜像工具条固定目录的「收藏夹」分组；
│   │                         四个分组的显示开关由 ApplyGroupVisibility 控制，
│   │                         「主目录」里显示哪些标准文件夹由 ApplyHomeFolders 控制）
│   ├─ FileItemViewModel      列表一行（带 Depth/IsExpanded/Children，可展开）
│   ├─ PinnedFolderViewModel  title 栏上的固定目录
│   ├─ StatusBarViewModel     文件列表区底部状态栏（项数 / 选中摘要 + 合计大小 / 卷容量）
│   ├─ SettingsCategoryViewModel  设置窗口左侧导航的一项（Key + Name）
│   ├─ ShellMenuItemViewModel 设置窗口「右键菜单」页里的一行（包着 ShellMenuItem + 开关状态）
│   └─ SettingsViewModel      设置窗口的编辑模型（绑到界面，任何改动发 Changed → 即时生效 + 落盘）
├─ Views/                     SidebarView / DriveBarView / PaneView / NavigationBarView / PathBreadcrumb / DetailsView
│                             （DetailsView 还负责文件列表的拖放：拖到目录行 / 空白处即移动或复制）
│                             StatusBarView（文件列表区底部一行）
│                             SettingsWindow（设置窗口外壳：默认 860×800、居中、即时生效的接线）
│                             SettingsView（设置窗口正文：NavigationView 左导航 + Windows 11 设置卡片）
├─ Controls/PaneSplitter.cs   自研分隔条（WinUI 没有 GridSplitter）
│           ColumnResizeHandle.cs 列头右边界拖动把手（调列宽 / 双击复位）
├─ Helpers/                   ColumnLayout(列宽 requested/rendered + 自适应 + 行高) / ThemeHelper(三态主题 ⇄ ElementTheme) / CloudSyncStateHelper(状态字形+文案) / DpiHelper / FileTypeHelper(类型名 + 图标字形兜底) / IconImageHelper(图标像素 → ImageSource + 共享缓存) / SizeFormatter / DragDropHelper(内部拖放格式) / SingleInstance(托盘驻留的单实例闸门)
├─ Converters/CommonConverters.cs
├─ Diagnostics/Log.cs
├─ icon.svg                   程序图标的唯一源文件（改图标就改它，再跑 tools\make-icon.ps1）
├─ Assets/                    图标等（exdir.ico 与各尺寸徽标 PNG 都由 tools\make-icon.ps1 从 icon.svg 生成）
└─ tools/                     capture / inspect-ui / shot-settings / test-pin-drag / test-settings / test-status-bar / test-shell-icons /
                              test-context-menu / test-list-selection / test-row-dblclick / test-column-resize / test-tray / test-drive-hotplug /
                              test-file-ops / test-network-locations / measure-row-align / publish / make-icon 脚本
```

## 4. 界面布局约定（改动前务必对齐）

窗口自上而下三行：

```
┌─────────────────────────────────────────────────────────────────┐
│ 行0 TitleBar 控件:  ☰ ☀|MenuBar(文件/编辑/查看/转到/工具/配置/帮助)| … │ 系统窗口按钮 ─┐
│                              中间: 当前目录名                     │  (AppWindow)  │
├─────────────────────────────────────────────────────────────────┤
│ 行1 工具条:  左=磁盘/网络盘/可移动盘      右=固定目录 + 快捷菜单⋯  │
│            （固定目录区是拖放区：把目录从列表/侧边栏拖上来即固定）      │
├────────────┬───┬────────────────────────────────────────────────┤
│ 行2 侧边栏 │ ║ │ 窗格（1 或 2 个）：TabView，每个标签页内部自上而下为   │
│ 文件夹树   │ ║ │ 导航条(← → ↑ ⟳ + 面包屑地址栏) + 详细信息列表        │
│ （全高）   │ ║ ├────────────────────────────────────────────────┤
│            │ ║ │ 状态栏：一行高（ExRowHeight=24 DIP），跨 1~2 个窗格   │
└────────────┴───┴────────────────────────────────────────────────┘
```

关键实现点：

* 行 0 用 **`Microsoft.UI.Xaml.Controls.TitleBar` 控件**，配合
  `ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);`。
  系统窗口按钮（最小化/最大化/关闭）由 AppWindow 原生绘制，不要自绘。
* `TitleBar` 的分区属性是 **`LeftHeader` / `Content` / `RightHeader`**。
  `ContentBefore` / `ContentAfter` 虽然在 winmd 里存在，但被标记为 experimental，
  XAML 编译器会报 `WMC0011: Unknown member`，**不要用**。
* `LeftHeader` 里是一个 `StackPanel`：**主题开关（太阳/月亮）在「文件」菜单左边**，然后才是 `MenuBar`；
  整个程序的深浅主题见下面“主题（深色 / 浅色）”那一条。
* 侧边栏分组（自上而下）：`收藏夹`（工具条固定目录的镜像，可从文件列表/侧边栏拖目录进来收藏，见下面“固定目录”那条；
  排在最上面，`AllGroups()` 与 `RefreshRoots()` 里的 `desired` 顺序必须保持一致）、
  `主目录`（可点击，指向 %USERPROFILE%，子项为桌面/文档/下载/图片/音乐/视频）、
  `云存储`（注册表探测到的同步根）、`此电脑`（各磁盘 + 「网络位置」快捷方式，见下面“网络位置”那条）。分组节点本身可导航当且仅当它有路径
  （`SidebarNodeViewModel.IsNavigable`）。

  四个分组各自可以在设置窗口「侧边栏」页里关掉不显示（`AppSettings.SidebarShowHome` /
  `SidebarShowFavorites` / `SidebarShowCloud` / `SidebarShowComputer`，默认全开）：
  `MainViewModel.ApplySidebarGroups()` 把设置推给 `SidebarViewModel.ApplyGroupVisibility()`，
  后者只增删 `Roots` 里的差异项、不整表重建，所以重新打开一个分组时它之前折叠/展开的状态
  与已懒加载的子节点全都还在（整表重建会把这些全丢掉）。

  「主目录」分组里显示哪几个标准文件夹也可以单独配（`AppSettings.SidebarHomeDesktop` /
  `SidebarHomeDocuments` / `SidebarHomeDownloads` / `SidebarHomePictures` / `SidebarHomeMusic` /
  `SidebarHomeVideos`，**默认只开「桌面」与「下载」**）：`ApplySidebarGroups()` 同时推给
  `SidebarViewModel.ApplyHomeFolders()`。筛选按 `SpecialFolderModel.Key`（`UserFolderKey`，稳定标识，
  不依赖会随系统语言变的显示名）做，而不是按显示名或路径匹配；
  `SyncHomeFolders()` 只增删这一个分组的差异子项、复用未变化的节点对象，
  所以关掉再打开一个文件夹时它已展开的子目录与展开状态都还在（与分组开关同一个理由）。
* 紧凑密度靠 `Themes/ExdirTheme.xaml` 里覆盖 WinUI 数值型资源实现：
  `TitleBarExpandedHeight=36`、`ListViewItemMinHeight=24`、`TreeViewItemMinHeight=24`。
  **只覆盖数值/颜色类资源键**，不要覆盖控件隐式样式（会丢掉默认 ControlTemplate）。
  同一处还关掉了选中行的左侧蓝色竖条：`ListViewItemSelectionIndicatorVisualEnabled=False`（只看整行底色）。
  例外：**文件列表数据行的行高不用固定资源**，它是可配的（见下面“行高可配”那条）。
* **标签条紧凑、且顶到窗格顶部齐平**（`TabView`，2026-09）：WinUI 默认一条标签栏高 32 DIP，
  上面还压着 8 DIP 空白（`TabViewHeaderPadding`，它被模板传给列表与 `ItemsPresenter`），
  窗格顶部到标签标题之间一共空了 17.5 DIP。现在标签条 24 DIP（≈ `ExRowHeight`）、
  圆角顶边直接贴住窗格的上边框，标题上方只剩 5 DIP（标签自身 `Padding` + 文字气泡的居中留白）。
  改的全在 `Themes/ExdirTheme.xaml`：`TabViewHeaderPadding=0`、`TabViewItemMinHeight=24`、
  `TabViewItemHeaderCloseButtonHeight=16`（**标签高度的真正决定项**，见第 6 节第 36 条）、
  `TabViewItemAddButtonContainerPadding` 与左右滚动按钮容器内边距去掉下边距（否则“+ / ◀ ▶”
  会把标签栏撑到 27 DIP）、`TabViewItemSeparatorMargin=0,4,0,4`（分隔竖线默认上下各缩进 8，
  在 24 高的标签里只剩 8 DIP 长，改成 4 后是 16 DIP）。
  标签内的文字/图标仍是 16 DIP，上下各留 4 DIP；关闭按钮变成 32×16（宽度不动，点得中）。
  新增“标签条上的东西”时请沿用这组尺寸，不要往回调。
  * **标签的圆角是直角（默认，可配）**：`AppSettings.SquareTabCorners`（默认 true = 直角），
    设置窗口「外观 → 标签页使用直角」里改，关掉就回到 WinUI 默认的圆角（只圆上面两个角）。
    实现在 `Views/PaneView.xaml` 的 `TabViewItem` 上绑 `CornerRadius="{x:Bind TabCornerRadius, Mode=OneWay}"`
    （值由 `FolderTabViewModel` 从设置算出来），WinUI 模板里 `TabBackground.CornerRadius` 是
    `TemplateBinding`，所以设置一改就会当场重画，不需要重建标签页；
    上面那个“圆角顶边贴住窗格上边框”的描述在默认的直角模式下就是一条水平直线。
* **主题（跟随系统 / 浅色 / 深色）**（2026-09）：`AppSettings.Theme`（枚举 `Models/AppTheme`，默认 `System`），
  两个入口共用同一个值：标题栏左侧那个太阳 / 月亮图标（`MainWindow` 的 `ThemeToggle`，
  `ExToolbarToggleButtonStyle`）与设置窗口「外观 → 主题」下拉框（三态）。
  * 真正换主题只有一处：`MainWindow.ApplyTheme()` 给 `RootGrid` 设 `RequestedTheme`
    （`Application.RequestedTheme` 启动后不允许再改）。根元素一变，菜单栏 / 工具条 / 侧边栏 /
    窗格 / 状态栏连同弹层（`MenuFlyout`、`ContentDialog` 的 XamlRoot 都是它）一起换。
  * 图标与悬停提示按 **`RootGrid.ActualTheme`** 刷（`SyncThemeToggle` + `ActualThemeChanged`），
    所以「跟随系统」时图标与画面永远一致；标题栏那个开关不带勾选底色，状态由字形表达
    （`ExToolbarToggleButtonStyle` 的 `CheckStates` 故意全空，避免与悬停色抢 `Background`）。
    拨动时总是固定成**显式**的浅 / 深（`MainViewModel.SetDarkMode`），不会停在「跟随系统」。
  * **系统窗口按钮（最小化/最大化/关闭）不跟着 ElementTheme 走**：系统主题与 exdir 主题不一致时
    要自己给 `AppWindow.TitleBar.Button*Color` 上色（`UpdateCaptionButtons`），否则“深色系统 + 浅色 exdir”
    下那三个按钮会是白的、几乎看不见。
  * 设置窗口是另一个 `Window`，不继承主窗口的主题：`SettingsWindow.ApplyTheme()` 自己设
    `View.RequestedTheme`，并订阅主 ViewModel 的 `Theme`，主窗口一边拨它一边跟着换（关窗时退订）。
  * **设置窗口必须有 Mica 背板**：`NavigationView` 左侧导航用的是半透明的“应用内亚克力”，
    窗口背后没有背板时它采样到窗口自身的黑底 —— 深色主题下看不出来，浅色主题下就是黑底 + 深色文字。
    详见第 6 节第 68 条。
  * 回归：`tools/test-settings.ps1` 用例 9（下拉框三态 + 标题栏开关 + 两个入口互相同步）。
* **扁平控件的悬停/按下/选中底色全部是强调色**（`Themes/ExdirTheme.xaml`）：
  WinUI 默认都是“灰底上叠 8% 白”（`ButtonBackgroundPointerOver`、`MenuBarItemBackgroundPointerOver`、
  `ListViewItemBackgroundPointerOver`…），几乎看不出鼠标停在哪里。现在统一换成强调色，
  深色主题用 `SystemAccentColorLight2`、浅色用 `SystemAccentColorDark1`（与 `AccentFillColorDefaultBrush` 同源）。
  两档不透明度：小控件（工具条/导航条/列头、菜单项、标题栏菜单）用 `ExToolbar*`
  （地址栏与行内展开箭头例外，见下）；
  铺满整行的大表面（文件列表行 / 侧边栏树节点 / 标签页头）用 `ExSurface*`（压低一档）。
  **选中态也一并改成强调色**（`ExSurfaceBackgroundSelected` 比悬停重），
  否则“悬停比选中还显眼”，层次是反的。
  * 小控件走具名样式：`ExToolbarButtonStyle`（预设 `MinHeight=26` + `Padding=8,1,8,1`）
    与 `ExFlatButtonStyle`（在其基础上把 `MinHeight`/`Padding` 归零，给列头、行内箭头这种
    尺寸自定的按钮用）。**是具名样式、不是隐式样式**：对话框里的普通按钮仍是 WinUI 默认外观，
    新加扁平按钮写 `Style="{StaticResource ExToolbarButtonStyle}"`，不要再手写 `Background="Transparent"`；
    已挂样式的：`DriveBarView`（磁盘/固定目录/快捷菜单）、`NavigationBarView`（← → ↑ ⟳）、
    `DetailsView`（5 个列头排序按钮）。
  * **例外：地址栏与行内展开箭头不用强调色**（`ExSubtleButtonStyle` / `ExFlatSubtleButtonStyle`）：
    面包屑分段、文件列表行内那个 18px 展开箭头，悬停/按下都是普通灰色
    （色刷 `ExSubtleButtonBackgroundPointerOver/Pressed`：深色主题叠白、浅色主题叠黑）。
    面包屑分段与行内箭头一上强调色就喧宾夺主，反而看不清当前指着的是哪段路径/哪一行。
    这两个样式与 `ExToolbar*` 只差悬停/按下的画刷，但 Storyboard 里的 `{ThemeResource}` 键
    是写死在模板里的（`BasedOn` 改不了），所以模板另写了一份，改模板时两处要同步。
  * **地址栏右侧的空白区（`BlankArea`）连灰色悬停都不要**（`ExGhostButtonStyle`）：
    它几乎铺满整条地址栏的剩余宽度，给一点悬停底色都会像“整条地址栏被按住了”，
    所以这个样式的 `PointerOver` / `Pressed` 是空状态（只保留 `Disabled` 的默认观感）；
    点击、工具提示、系统焦点视觉照旧，只是鼠标划过它时不变色。
  * 表/树/菜单/标签这些改不了样式的（模板在 WinUI 里），就在 `ThemeDictionaries` 里覆写
    WinUI 的资源键（键名抄 `generic.xaml`）：`TitleBarPaneToggleButtonBackground*`、
    `MenuBarItemBackground*`、`MenuFlyoutItemBackground*`、`TabViewItemHeaderBackground*`、
    `TabViewButtonBackground*`、`TabViewItemHeaderCloseButtonBackgroundPointerOver`、
    `TreeViewItemBackground*`、`ListViewItemBackground*`（含 `Selected` / `SelectedPointerOver` / `SelectedPressed`）。
    这些键**只在当前主题对应的 ThemeDictionaries 里生效，不跨主题合并**，
    所以深/浅两份都要写全（高对比主题故意不覆盖，让它落到系统的高对比色）。
* 活动窗格的边框用强调色（`PaneView.UpdateActiveVisual`），点击窗格会把自己设为活动窗格。
* **详细信息列表是“可展开的树形列表”**（不是 TreeView）：
  * 数据：`FileItemViewModel` 带 `Depth/IsExpanded/Children`，`FolderTabViewModel` 持有根层节点 `_rootNodes`，
    `Items` 永远是“当前可见行”的扁平集合；展开子项是**懒加载 + 增量 `Items.Insert/RemoveAt`**
    （整体替换会连滚动位置和选中项一起清掉），排序/刷新才整体重建成 `ObservableCollection`。
  * 展开状态存在 `_expandedPaths`，刷新/重进同一个目录后按路径恢复（父级路径一定比子级短，按长度升序展开）。
  * 行内：缩进 = `Depth * 14`，行首 18px 展开箭头（`CanExpand` 为 false 时不可点、字形为空串占位），
    双击行仍然是“进入目录 / 打开文件”，箭头只负责展开/折叠，`←/→` 方向键也能展开/折叠。
  * **双击的命中范围 = 整条行高亮区**（2026-09）：行内任意位置（左边距、名称文字右侧的空白、
    日期/类型/大小列里的空白、行内上下留白）双击都能进入目录 / 打开文件，不再只有“双击到名称文字
    或图标上”才有效。行模版本该用的是 `Grid` 的 `DoubleTapped`，但**行里那些空白处没有可命中的元素**，
    命中的是 `ListViewItem` 自己，事件从 `ListViewItem` 直接往上冒、经过不了行模板那个 `Grid` ——
    所以双击处理器挂在 `DetailsRoot` 上（`AddHandler(..., handledEventsToo: true)`）再用 `FindRowItem`
    反查行；行模板的 `Grid` 另外补上 `Background="Transparent"`（没有背景的 Grid 在空白处不参与
    命中测试，那样连行内悬停提示框都弹不出来）。行首展开箭头是 `Button`，双击它仍是展开/折叠。
    回归：`tools/test-row-dblclick.ps1`；踩过的坑见第 6 节第 45 条。
  * 排序对树的**每一层**生效（`CompareNodes`）；排序/刷新后由 `FolderTabViewModel.PendingSelection`
    （一组路径，视图读，不清空）让视图在新集合里把行选回来；返回上一级用 `selectPath` 选中来源目录。
* **选择**（2026-09）：单击文件列表的空白处（列头以下、任何一行之外）取消当前选择，
  单击行仍然是 ListView 自己的选择行为（含 Ctrl 加选）；`Ctrl+A` 全选列表里当前可见的行
  （就地展开出来的子行也算，它们就是列表里的行）。
  * 空白处没有可复用的控件，所以 `PointerPressed` 和右键一样挂在 `DetailsView` **最外层的 Grid** 上
    （`handledEventsToo: true`，行上的点击可能被 ListView 标成 Handled），再用
    `FindRowItem(e.OriginalSource)` 把“落在某一行上”的点击排掉；列头（y ≤ `HeaderRow.ActualHeight`）也不算空白处；
  * 清空选择后顺手 `EntryList.Focus(FocusState.Pointer)`：空白处自身不可聚焦，不抢这一下的话焦点会
    留在点空白之前的控件上，紧接着按 `Ctrl+A` / 方向键就不作用于文件列表；
  * `Ctrl+A` 是 `KeyboardAccelerator`，挂在 `DetailsRoot` 上而不是 `ListView` 上 —— 这样焦点在列头按钮 /
    行内展开箭头时也生效（它们的焦点路径不经过 `ListView`）。地址栏在 `NavigationBarView` 里、
    不在本控件子树内，所以那里按 `Ctrl+A` 仍是 `TextBox` 自己的“全选文本”（见 `tools/test-list-selection.ps1` 用例 5）；
  * 全选用 `ListViewBase.SelectAll()`（`SelectionMode=Extended` 下就是全选），不要自己遍历
    `SelectedItems.Add`（几千行会退化成 O(n²)）；
  * 回归：`tools/test-list-selection.ps1`（5 个用例 11 条断言：单击行只选中它 / `Ctrl+A` 全选且状态栏同步 /
    单击空白处清空 / 行上的点击不算空白处 / 地址栏的 `Ctrl+A` 仍是文本框全选）。
* **列宽**：`Helpers/ColumnLayout` 的实例是**每个标签页一个**（`FolderTabViewModel.Columns`），
  同时被列头与每一行绑定，因此列头与数据行永远对齐。要区分 requested（用户拖的，落盘）与
  rendered（按窗格可用宽度算出来的，见 `FitTo`）：
  * 自动模式（`AutoFit`，默认）：富余宽度给名称列，装不下时各列按余量等比压缩——
    任何窗格尺寸下都能看到全部列（但各列都有最小宽度，5 列全在时下限约 264 DIP，
    窗格比这还窄就只能横向滚动，见 `ColumnLayout.Minimums`）；
  * 手动模式：用户拖过任意列边界就进入（`SetRequestedWidth` 会关掉 `AutoFit`），列宽就是拖出来的值，
    装不下就横向滚动（`ScrollViewer.HorizontalScrollMode=Enabled`，行 Grid 的
    `MinWidth={x:Bind Columns.RowMinWidth}` 提供滚动范围）；
  * 手动模式下列头靠 `TranslateTransform` 跟随 `ScrollViewer.HorizontalOffset`，
    `HeaderRow.Clip` 负责裁掉平移出去的部分；
  * 拖动把手 `Controls/ColumnResizeHandle` 由 `DetailsView` code-behind 追加到 `HeaderLayer.Children`
    （`HeaderLayer` = 表头里那个**没有列定义**的整宽父层，XAML 里就包着 `HeaderContent`），
    位置用 `TranslateTransform.X` 推到列边界（**不要用 Canvas**：Canvas 子元素实测高度为 0，命中区会是空的），
    双击把手 = 该列恢复默认宽度 + 回到自动模式；宽度为 0 的列（下面说的“状态”列）会把把手
    `Visibility=Collapsed`，否则它会压在名称列左边界上抢走点击。
    **不要把把手放进 `HeaderContent`**：它带列定义，第 0 列正是“状态”列，非云目录里宽度为 0，
    而零宽单元格里的子元素收不到指针事件（把手会彻底拖不动，见第 6 节）。
    横向滚动的 `HeaderTransform` 也挂在 `HeaderLayer` 上，所以按钮与把手一起平移。
  * **列头排序按钮铺满整列**（`ExColumnHeaderButtonStyle`）：整列宽度 + 整个表头高度都是排序的命中区，
    悬停/按下底色是直角矩形（普通扁平按钮那 4 DIP 圆角铺满一列后像一张“卡片”）；
    最左可见列往左、最右的“大小”列往右各再铺 6 DIP（= `ExRowPadding` 的左右内边距），
    这样表头的高亮范围与数据行的行高亮一样从窗格边缘铺到边缘。
    外扩在 `DetailsView.UpdateHeaderInsets()` 里用负 `Margin` + 等量 `Padding` 实现（内容位置不变、
    列头与数据行仍然对齐；改列宽会连数据行一起错位，所以不能用改列宽的办法）；
    拖动把手在 XAML 的按钮**之后**加进 `HeaderLayer`，仍在按钮之上，列宽拖动不受影响。
    回归：`tools/test-column-resize.ps1`（8 个用例 20 条断言，真鼠标拖每个列边界）。
* **列头排序按钮铺满整列**（2026-09，见第 4 节“列宽”最后一条）：点列头的**任意位置**（不再只有文字那一小块）
  都能排序，悬停/按下是铺满整列、整个表头高度的直角矩形（不再是 4 DIP 圆角的“按钮块”）；
  最左/最右列各再往外铺 6 DIP，高亮范围与数据行的行高亮一致（窗格边缘到边缘）；
* **行高可配**（2026-09，默认 28 DIP）：设置窗口「文件列表 → 行高」一个滑块（20~48 DIP、步进 2）。
  * 值存在 `AppSettings.RowHeight`（默认 `ColumnLayout.DefaultRowHeight = 28`，比原来的固定 24 更舒展），
    真实上下限/夹取在 `ColumnLayout.NormalizeRowHeight`（settings.json 被手改过也不会出界面外）；
  * 运行时值是**每个标签页一份**的 `ColumnLayout.RowHeight`（行模板本来就绑着这个共享对象，
    见上面“列宽”那条），行模板写 `Height="{x:Bind Columns.RowHeight, Mode=OneWay}"`；
    所以只需要在 `MainViewModel.ApplySettings` 里给每个标签页赋一次值，几千行会一起变；
  * `ListView.ItemContainerStyle` 的 `MinHeight` 必须归零（原来是 `ExRowHeight`）：
    容器锁着 24 的话，把行高调到 24 以下根本不生效（见第 6 节第 44 条）；`ExRowHeight=24` 现在只给状态栏用；
  * **列头不跟着变**（仍是 `ExListHeaderHeight=26`）：表头是控件不是数据行，分开更紧凑；
  * 行内每一格依旧 `VerticalAlignment="Center"`，所以行高只改变上下留白：
    图标与名称文字在任何行高下都垂直居中（图标那 1 DIP 下推不受行高影响，
    28 与 48 两种行高下 `tools/measure-row-align.ps1` 的中位数偏差都是 2.5 物理像素）；
  * 回归：`tools/test-settings.ps1` 用例 6（初值一致 / 切分类读不到 / 拖完立即落盘 /
    用 UIA 量 `ListItem` 的高度确认数据行真的变成设定的值）。
* **名称列的行首图标是真实的外壳图标**（2026-09，S14）：`.exe` 显示程序自带图标、`.lnk` 显示目标图标
  + 快捷方式小箭头、文件夹/文件类型按系统关联，与资源管理器一致；
  `Helpers/FileTypeHelper` 的 Segoe 字形**降级成兜底占位**（图标还没到、或系统里查不到图标时显示）：
  * 取图标：`Services/ShellIconService` 用 `SHGetFileInfo`（`Services/Native/ShellIconExtractor`）
    拿 HICON，再用 `GetDIBits` 读成 32bpp BGRA，交给 `Helpers/IconImageHelper` 在 UI 线程建 `WriteableBitmap`；
  * **懒加载**：图标由 `DetailsView` 的 `ContainerContentChanging` 触发（行容器真的被创建时才取），
    `FileItemViewModel.IconRequested` 保证同一行只排一次队；
  * 缓存两级：`ShellIconService` 里“图标键 → Task”（并发合并）与“内容哈希 → 像素”，
    `IconImageHelper` 里“内容哈希 → ImageSource”。所以几千个普通文件夹只占一张位图，
    滚动、排序、刷新、重进目录都不会重复取；
  * 图标键：目录与“图标写在文件自身里”的类型（`.exe/.lnk/.url/.ico/.msi/.scr/.cpl/.com/.pif`）按**路径**，
    其余按**扩展名**（一个目录里几千个 .txt 只问外壳一次）；
  * 成本：单次提取约 16~20 ms（外壳内部开销），只在后台线程做；**不要**在 UI 线程上调
    `IShellIconService.GetIconAsync`。
  * **与文字的对齐**：图标盒子与名称文字盒子在布局上都是“行高居中”的，但文字的行盒底部还留着
    一段降部（descent，Segoe UI 约 3.5 DIP）空白，字形的视觉中心比行盒中心低约 1 DIP。
    所以图标用一个 `RenderTransform`（`TranslateTransform Y="1"`）**下推 1 DIP** 去对文字的视觉中心；
    用 RenderTransform 而不是 Margin，是为了不影响行高/列宽（Margin 会改变布局盒高度，实际只推得动 0.5 DIP）。
    改这个数值前先跑 `tools/measure-row-align.ps1`。
    回归：`tools/test-shell-icons.ps1`（13 条断言）与 `tools/measure-row-align.ps1`（对齐偏差），
    踩过的坑见第 6 节第 30/31 条。
* **“状态”列（云同步状态）**：`DetailsView` 的第 0 列，只在**云同步目录**里出现，
  列出的是资源管理器“状态 / 可用性”列的含义（已同步 / 仅在云端 / 已固定 / 正在同步 / 同步错误 / 未同步）：
  * 数据来源：`FileSystemService.Enumerate` 在目录位于云同步根下时，额外为每个条目读
    `System.StorageProviderState` 与 `System.FilePlaceholderStatus`（`Services/Native/ShellPropertyStore`），
    映射成 `Models.CloudSyncState` 存到 `FileSystemEntry.SyncState`；
  * 显隐：`FolderTabViewModel.NavigateAsync` 里按“有没有条目带状态”设置
    `Columns.ShowSyncColumn`（隐藏时该列算 0 宽，列定义不动，所以其它列的下标与落盘的列宽都不受影响）；
    离开云目录时如果正在“按状态排序”会退回按名称排序；
  * 单元格：一个 `Grid` 里放 6 个 `TextBlock`（Segoe Fluent Icons 字形 + 主题画刷），
    用 `CloudSyncStateVisibilityConverter` + `ConverterParameter` 只显示当前那一个。
    **用 TextBlock 而不是 FontIcon**：TextBlock 会进 UIA 树，`AutomationProperties.Name` 里写着
    “仅在云端 / 已同步”，自动化脚本可以直接断言（FontIcon 不是控件，UIA 里根本没有它）。
  * 成本：每个条目要建一次属性存储（实测约 2~3 ms/项），所以只在云目录里做、并行读，
    并有 1500 ms 时间预算（超预算的条目保持无状态），超时会在 exdir.log 留一行 `云同步状态：…`；
    侧边栏只建目录树（`EnumerateSubDirectoriesAsync`），不读状态。
* **工具条右侧的“固定目录”是一个拖放区**（`DriveBarView` 的 `PinnedDropZone`）：
  * 拖源：文件列表（`DetailsView.EntryList`，`CanDragItems=True`）与侧边栏文件夹树（`SidebarView.FolderTree`），
    两者在 `DragItemsStarting` 里用 `Helpers/DragDropHelper.SetPaths` 把**目录**路径写进
    自定义格式 `exdir/paths`（换行分隔的纯文本）；拖到文件/分组标题（没有路径的节点）会直接 `Cancel` 掉拖拽；
  * 落点：`PinnedDropZone`（`AllowDrop=True` + `Background="Transparent"`，两者缺一不可）+ 一个
    平时 `Visibility=Collapsed` 的高亮层（画在按钮**下面**，只露出一圈强调色边框与淡底色），
    `DragOver` 里设 `AcceptedOperation=Copy` 与 `DragUIOverride.Caption="固定到工具条"`；
  * 落下来后走 `MainViewModel.PinFolders`（去重、只收已存在的目录、上限 `MaxPinnedFolders=12`）
    并**立即** `_settings.Save()` 落盘（不等退出，崩溃/强杀也不丢）；
  * 右键固定目录按钮 → 动态 `MenuFlyout` 的“取消固定”（`ContextRequested`，不要用 `ContextFlyout`：
    模板里拿不到 `DataContext`）；
  * 外部来源（资源管理器等）只有 `StandardDataFormats.StorageItems`，`DragOver` 里同步判断不了内容，
    所以先接受、`Drop` 里再筛目录（`DragDropHelper.GetPathsAsync`）。
  * **拖拽排序**：固定目录按钮自己也是拖源（拖到兄弟按钮上换位）。它写的是另一个格式
    `exdir/pinned-reorder`（值是那一个目录的路径），`Drop` 里按鼠标横坐标算出插入位置后交给
    `MainViewModel.MovePinnedFolder`（`RemoveAt` + `Insert`，不用 `ObservableCollection.Move`；
    改集合用 `DispatcherQueue.TryEnqueue` 推到下一轮消息循环，避免在拖放宿主的回调里摘掉源按钮）：
    这样“带 `exdir/paths` = 新增固定”与“带 `exdir/pinned-reorder` = 排序”两条路径互不干扰。
    拖动时在按钮之间的边界画一条 2px 强调色插入提示条（`InsertionIndicator`，位置用
    `Margin.Left` 设，它和按钮不在同一棵子树里所以要 `TransformToVisual` 换算到 `PinnedItemsHost`）。
    **Button 上的 `CanDrag` 在 WinUI 3 里是无效的**，必须自己识别手势，见第 6 节第 25 条。
* **侧边栏里有「收藏夹」分组，镜像工具条上的固定目录**（2026-09）：它排在侧边栏**最上面**
  （在 `主目录` 之上），子项与 `MainViewModel.PinnedFolders` **同序同名**（增删、工具条上拖拽排序后立刻同步：
  `MainViewModel` 订阅 `PinnedFolders.CollectionChanged` → `SidebarViewModel.SyncFavorites`，
  `RefreshDrives` 重建整棵树后也要再灌一次）。收藏项本身是普通目录节点（可展开、可导航）。
  * 把目录从文件列表 / 侧边栏拖到「收藏夹」分组或其任意子行上即收藏，提示是“收藏到侧边栏”，
    悬停时整行强调色高亮（`SidebarNodeViewModel.IsDropTarget`）。落点是**行级**的：
    `AllowDrop` + `DragOver`/`DragLeave` 写在 `TreeViewItem`（模板根）上，命中哪一行就用
    `FolderTree.ItemFromContainer(sender)` 反查（不要走 `e.OriginalSource`，见第 6 节第 54 条）；
    `TreeView` 那一层再用 `AddHandler(..., handledEventsToo: true)` 接 `DragOver`/`Drop`，
    在整条路由最后重新确认一次 `AcceptedOperation`（否则 `TreeViewList` 会把它改回 `None`，松手没有 Drop）。
  * 落到其它树节点上、或拖工具条固定目录按钮（排序格式）经过侧边栏时一律 `None`，不会误收藏。
  * 右键收藏项 → 「取消收藏」（`SidebarViewModel.UnpinRequested` → `MainViewModel.UnpinFolderByPath`）；
    工具条被隐藏时这是唯一的移除入口。
  * 回归：`tools/test-pin-drag.ps1` 用例 0（收藏夹子项与 `settings.json` 一致）与用例 5（拖到收藏夹）。
* **磁盘（U 盘 / 光驱 / 网络盘）插拔是实时反映的**（2026-09）：`Services/DeviceChangeService`
  （实现 `IDeviceChangeService`，内部是 `Services/Native/VolumeChangeWatcher`）把主窗口子类化，
  接系统**广播给所有顶层窗口**的 `WM_DEVICECHANGE`（卷到达 / 移除，外加设备树变化
  `DBT_DEVNODES_CHANGED`），再由 `MainWindow` 安排两次延迟刷新
  （600 ms 合并 + 2500 ms 兜底：一条插拔会连发好几条消息，盘符又往往比消息晚几百毫秒才可用）。
  刷新走 `MainViewModel.RefreshDrives()`：工具条磁盘区（`Drives`）与侧边栏「此电脑」分组都做**差量更新** ——
  清单没变就一个控件都不动，变了也只增删 / 挪动那一个盘节点（没变化的盘**复用同一个节点对象**），
  所以不会重建整棵树、不会丢侧边栏的展开状态，也不会清掉「收藏夹」。
  窗口隐藏到托盘时仍是顶层窗口，消息照样收得到；只有真的「退出」才 `Detach()`。
  「工具 → 重新扫描磁盘」是同一个 `RefreshDrives`。
  坑见第 6 节第 56 条；回归：`tools/test-drive-hotplug.ps1`（subst 造盘符 + 发消息，不需要交互桌面）。

* **侧边栏「此电脑」里也列出 Windows 的「网络位置」**（2026-09）：资源管理器里用「添加一个网络位置」
  造出来的东西，在磁盘上就是 `%APPDATA%\Microsoft\Windows\Network Shortcuts` 下的一个快捷方式容器目录
  （里面是隐藏的 `target.lnk`，目标通常是 `\\server\share`）。`Services/INetworkLocationService`
  逐个子目录读 `target.lnk` 的目标（`Services/Native/ShellLinkInterop` 的 `IShellLinkW`），
  以目录名为显示名、目标路径为 `FullPath`，作为「此电脑」分组里**排在磁盘之后**的普通可展开节点
  （点击即导航到目标，UNC 也能走现有的路径规整与面包屑）。
  * 目标解析不出来（快捷方式损坏 / 指向 shell 虚拟项）的项直接跳过；**不判断目标是否在线** ——
    离线的网络位置在资源管理器里也照样列出来，打开时才报错（由现有导航错误处理兜底）。
  * **`SidebarViewModel.RefreshDrives` 必须把网络位置一起算进“期望清单”**：它原本只从磁盘列表重建，
    下面“摘掉不在期望清单里的节点”那一步会把网络位置全删掉（一次插拔 / 「重新扫描磁盘」就没了）。
  * 回归：`tools/test-network-locations.ps1`（4 个用例 5 条断言，自己造一个指向临时目录的假网络位置，
    UIA + `SendMessage`，不需要交互桌面）。

* **文件列表区底部有一条状态栏**（`Views/StatusBarView.xaml` + `ViewModels/StatusBarViewModel`）：
  它挂在 `MainWindow` 里窗格那一列的**第 1 行**（第 0 行才是放 1~2 个窗格的 Grid，`Height="*"`），
  所以：侧边栏保持全高、状态栏只占文件列表区（不跨侧边栏）、窗格拿掉它以外的全部高度；
  高度用 `{StaticResource ExRowHeight}`（= 一行 24 DIP）。**不要再回到“DetailsView 里每标签页一条”**
  （那是状态栏做出来之前的临时“选中摘要”条，已删除）。
  三段内容（左→右）：
  * `N 项`：`FolderTabViewModel.ItemCount`，只数当前目录的直接子项，不含就地展开出来的行；
  * 选中摘要：`选中 2 项（合计 3.00 KB）`。**只累加文件**（目录要递归枚举才知道大小，
    选中一堆目录时会把磁盘拖住），所以全会目录时显示「均为文件夹」，
    含目录时用悬停提示说明“合计不含文件夹”；
  * 卷容量：`D: 可用 120 GB / 共 512 GB`，走新增的 `IDriveService.GetDriveForPath(path)`
    （每次导航后台重读，同一个卷 3 秒内复用；读盘可能卡很久，**不要在 UI 线程上调**），
    UNC / 未就绪 / 路径为空时整段留空。
  状态栏始终显示**活动窗格的活动标签页**，所以 `StatusBarViewModel` 自己订阅了
  `MainViewModel.ActivePane` → `PanelViewModel.ActiveTab` → 标签页的
  `ItemCount` / `Selection` / `CurrentPath`，换窗格（F6）、换标签页、换目录、改选中都会跟着变。
  UIA 里只有最外层 Grid 有 `AutomationProperties.Name="状态栏"`，**里面三个 TextBlock 故意不加**：
  这样它们的 UIA 名字就是自己的文本，`tools/test-status-bar.ps1` 才能直接断言内容
  （容器给名字、叶子 TextBlock 不给 —— 想在 UIA 里读到文本就这么做）。
* **导航条属于标签页，不属于窗格**：`Views/NavigationBarView` 是 `TabView.TabItemTemplate` 里
  `TabViewItem` 内容的第 0 行（第 1 行是 `DetailsView`），VM 类型是 `FolderTabViewModel`。
  因此每个标签页各自拥有后退/前进/上一级/刷新按钮与地址栏（历史和编辑态都跟着标签页走）。
  要加“导航条上的新东西”（视图切换、过滤器），改 `NavigationBarView.xaml` 而不是 `PaneView.xaml`。
* **地址栏 = 面包屑 + 可编辑输入框**（`Views/PathBreadcrumb`，仍是每个标签页一份）：
  * 面包屑由 `FolderTabViewModel.PathSegments` 渲染（`ItemsControl` + 水平 `StackPanel`，
    段与段之间是 chevron 字形 `E76C`，第一段不画）；
  * 点某一段：非当前段 → `NavigateToSegmentCommand` 导航过去，当前目录段 → 进入编辑态；
  * 点地址栏右侧空白区域（`BlankArea` 按钮，位置/宽度按面包屑实际宽度算）→ 进入编辑态；
    这个按钮用 `ExGhostButtonStyle`（可点但悬停/按下无底色），不要把样式换回 `ExSubtleButtonStyle`；
  * 编辑态由 `FolderTabViewModel.IsPathEditing` 持有（不在视图里），回车 `NavigatePathCommand` 前往、
    `Esc` 或失焦 `CancelPathEdit()` 取消；**任何一次成功导航都会自动退出编辑态**；
  * 路径比地址栏宽时 `CrumbScroll` 滚到最右（当前目录永远可见），左端露省略号提示还有被裁掉的分段。
* **右键菜单有两种风格，可在设置里切换**（2026-09）：在文件列表里右键行 → 该（批）条目的菜单，
  右键空白处 → 当前目录的背景菜单。「配置 → 设置… → 右键菜单 → 使用内置的轻量右键菜单」
  决定用哪一种（**默认开 = 内置菜单**；关掉则回到系统外壳菜单）。两种菜单的内容**故意不一样**。
  * **内置菜单（默认）**：`Views/DetailsView` 现场搭一个 WinUI `MenuFlyout`，只绑 exdir 自己实现的命令，
    不建 COM 对象、不问外壳，所以弹出几乎瞬时：
    * 文件行：`打开` / `在资源管理器中显示` /（分隔）/ `剪切`(Ctrl+X) / `复制`(Ctrl+C) / `粘贴`(Ctrl+V) /
      `删除`(Del) /（分隔）/ `复制路径` / `属性`；
    * 背景：`粘贴`(Ctrl+V) / `新建文件夹` / `刷新` / `全选` /（分隔）/ `复制当前路径` / `在此处打开终端`。
    * 剪贴板上没有文件时「粘贴」是灰的（建菜单时现查一次 CF_HDROP，不读内容）；
      `剪切`/`复制` 的可点状态跟着选中项走（`CanExecute = HasSelection`）。
    * `属性` 走 `IShellService.ShowProperties`（`ProcessStartInfo.Verb = "properties"`，不建 `IContextMenu`）；
      `新建文件夹` 由 `FolderTabViewModel.CreateNewFolderAsync` 自己建目录（重名依次 `(2)(3)…`）并选中；
      其余项直接复用标签页已有的命令与视图的 `SelectAll`。
    * 风格在**每次右键时现读** `FolderTabViewModel.UseBuiltInContextMenu`，所以改设置立即生效。
  * **系统菜单**（把开关关掉才用）：菜单内容完全来自系统外壳（`IContextMenu`），
    所以 7-Zip / Git / VS Code / WPS 这些第三方项、“发送到 / 打开方式”这类子菜单都在，
    而且**默认全部开启**，只是可以在同一页里逐项关掉（这份逐项开关只对系统菜单生效）。
  * 系统菜单的实现：`Services/ShellContextMenuService` + `Services/Native/ShellContextMenuInterop`。
    选中项走 `IShellFolder.GetUIObjectOf`，目录背景走**目录自己**的
    `IShellFolder.CreateViewObject`（不是它所在目录的，`SHBindToObject` 传 null 从桌面绑），
    然后 `QueryContextMenu` 填 HMENU → `TrackPopupMenuEx(TPM_RETURNCMD)` 弹出 →
    `InvokeCommand(偏移 = id − idCmdFirst)` 把选中的项交回外壳执行（exdir 自己一个命令都不实现）；
  * **弹出期间要在宿主窗口上挂一个 `ShellMenuHost`**（`SetWindowSubclass`）：把
    `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MEASUREITEM` 转给 `IContextMenu2/3`，
    否则“打开方式”这类子菜单是空的、owner-draw 菜单项会画成空白（见第 6 节第 43 条）；
  * **菜单项的稳定标识 `ShellMenuItem.Key`**：优先用外壳给的规范动词
    （`GetCommandString(GCS_VERBW)`，如 `open` / `7-Zip.Compress`），拿不到动词的退回
    “上级菜单文本 + 菜单文本”。用动词才能让“打开”在文件 / 文件夹 / 背景三个上下文里共用同一个开关。
    `AppSettings.ShellMenuDisabledItems` 存的就是这些 key（空 = 全部开启）；
  * **清单从哪来**（设置页里那一串开关）：一头是打开设置页时用样本目标现枚举
    （%TEMP% 下的样本 .txt、配置目录本身、配置目录的背景），
    一头是每次真的弹菜单时顺手记住的（`AppSettings.ShellMenuKnownItems`，退出/保存时落盘），
    两份合并去重。设置页里每一项的说明文字就是它的上下文（文件 / 文件夹 / 背景 + 子菜单路径）
    和它在列表里的位置；同名的项会补上后缀，因为开关的 UIA 名字就是标题；
  * 关掉的项在 **`TrackPopupMenu` 之前**从 HMENU 里按位置 `RemoveMenu` 删掉
    （按位置删不会打乱其它项的 id），顺带把空掉的子菜单、重复/首尾分隔符清理掉；
  * 右键事件挂在 `DetailsView` **最外层的 Grid** 上，不是挂在 ListView 上：
    列表下面的空白处根本不会命中 ListView 内部的 ScrollViewer（它没背景、不参与命中测试），
    事件会从外层 Grid 往上冒（见第 6 节第 39/40 条）；行上右键会先把该行选中再弹菜单；
  * `Helpers.DpiHelper.ToScreenPoint` 负责 DIP → 屏幕物理像素（先 `TransformToVisual(null)`
    拿客户区坐标，再 `ClientToScreen`；不要自己用窗口位置 + 缩放算，系统边框会让它对不上）。
  * 回归：`tools/test-context-menu.ps1`（5 个用例：系统菜单 3 个 + 内置菜单 2 个，真鼠标右键 + 截图；
    系统菜单是自绘的、UIA 里读不到菜单项，所以“弹没弹出来”看进程里有没有 `#32768` 窗口、
    “有哪些项/关掉了哪些项”看 `exdir.log`；内置菜单是 `MenuFlyout`，直接按名字从 `RootElement`
    找 `MenuItem` 断言，并且用 `InvokePattern` 点「新建文件夹」验磁盘上真的建出了目录）。

### 托盘驻留（单窗口模式，2026-09）

**exdir 是常驻托盘的单窗口程序**：点窗口右上角的关闭按钮（或 `Alt+F4`）**不会**退出，只是把窗口隐藏起来。
进程、DI 容器、两个窗格、标签页、已枚举的目录、图标缓存全部原封不动地留着，所以再打开就是一次
`ShowWindow`（瞬时的），不需要重建任何一个窗格；**只有菜单里的「退出」才真的结束进程**。

* 托盘图标用 NuGet 包 `H.NotifyIcon.WinUI`（版本锁 **2.3.2**：2.4.x 只出 `net10.0`，跟本项目的
  `net8.0-windows` 不兼容）。图标本体是 `Shell_NotifyIcon`，非打包部署可用，不需要包标识。
* 图标在 `MainWindow.xaml` 里声明（`<tb:TaskbarIcon x:Name="TrayIcon">`，`xmlns:tb="using:H.NotifyIcon"`），
  它是 0×0 的 `FrameworkElement`、不占布局（UIA 里它的矩形是空的）；图标位图在 code-behind 里用
  `System.Drawing.Icon(Assets\exdir.ico, DpiHelper.GetSmallIconSize())` 设置（不走 `ms-appx://`）。
* **托盘菜单只能用 `Command`，不能挂 `Click`**：H.NotifyIcon 的默认 `ContextMenuMode=PopupMenu` 是把
  `MenuFlyout` 转成 Win32 弹出菜单再执行 `Command` 的（见第 6 节第 47 条）；菜单只有两项：
  「显示主窗口」/「退出 exdir」（非打包模式下子菜单不可用，所以不要做二级菜单）。
* 左键单击 = 唤回窗口（`LeftClickCommand` + `NoLeftClickDelay="True"`）；右键 = 上面那张菜单。
* 关窗口的拦截在 `MainWindow.OnAppWindowClosing`：`args.Cancel = true` + `HideToTray()`（
  `H.NotifyIcon.WindowExtensions.Hide()` = `ShowWindow(SW_HIDE)` + 打开效率模式）。
  隐藏/显示前都会 `SaveWindowPlacement()` + `ViewModel.SaveSession()` 落盘，所以隐藏状态下被杀
  也不会丢列宽/会话（`tools/test-*.ps1` 的收尾才可以直接 `Kill`）。
* **最大化的窗口藏起来再唤回不能被还原成普通尺寸**：`Show()` 走的是 `ShowWindow(SW_SHOWNORMAL)`，
  它会顺手取消最大化，所以隐藏时要把 “之前是不是最大化” 记下来，唤回后自己 `presenter.Maximize()`。
* **单实例闸门在 `Program.cs` 里**（exdir.csproj 里定义了 `DISABLE_XAML_GENERATED_MAIN`）：
  `Helpers/SingleInstance` 用一个命名内核事件（`Local\exdir.activate`）同时做两件事——
  `EventWaitHandle` 的 `createdNew` 就是“我是不是第一个实例”，而第二个实例 `Set()` 它就等于
  “把已有窗口叫出来”。这一步在 `Application.Start` **之前**，所以第二次双击 exe 只跑几十毫秒
  （不会白初始化一遍 WinUI，也不会多出第二个托盘图标/第二份会话互相覆盖 settings.json）。
* UIA 里能看到托盘图标的方法与不要踩的坑见 `tools/test-tray.ps1` 的注释；回归：
  `pwsh -NoProfile -File tools\test-tray.ps1`（4 个用例 17 条断言，真鼠标点关闭按钮与托盘图标）。

### 复制 / 剪切 / 粘贴 / 删除与“拖动移动”（2026-09）

文件列表支持资源管理器那一套剪贴板操作与删除（进回收站），也支持把文件拖到目录里移动。

* **剪贴板用系统标准格式，与资源管理器互通**（`Services/IClipboardService` +
  `Services/Native/ClipboardInterop`）：写的是 `CF_HDROP`（HDROP 文件列表）+ `Preferred DropEffect`
  （一个 DWORD，1 = 复制、2 = 剪切），读的时候先走 Win32 `GetClipboardData`、拿不到再退回
  OLE `OleGetClipboard` + `IDataObject`。所以 exdir 里 Ctrl+C、然后到资源管理器里 Ctrl+V 能粘贴，反之亦然。
  * 写入走 Win32 `SetClipboardData`：内存所有权交给系统，进程退出后内容仍有效（不需要
    `OleFlushClipboard` 那一套）。`Preferred DropEffect` 是**运行时注册**的格式，
    必须 `RegisterClipboardFormat("Preferred DropEffect")`——`0x000C` 是 `CF_WAVE`（见第 6 节第 57 条）。
  * 读到“剪切”时写入方就在源目录里把文件搬走（`MoveAsync`），搬完 `Clear()` 清空剪贴板
    （与资源管理器一致：剪切粘贴只能生效一次）。
* **真正的复制 / 移动 / 删除交给外壳**（`Services/IFileOperationService` + `Services/Native/FileOperationInterop`）：
  `SHFileOperation` 的 `FO_COPY` / `FO_MOVE` / `FO_DELETE`，因此进度对话框、同名冲突的是/否/全部、
  “目标目录不存在就建”都是资源管理器同款，exdir 自己一行文件搬运代码都没有。
  * `SHFileOperation` 必须在 **STA** 线程上跑（它要自己起一个模态进度对话框），
    而线程池线程是 MTA，所以每次操作开一条专用的 STA 线程（主窗口消息循环不阻塞，
    进度对话框用主窗口当属主）；操作完成在 `Completed` 事件里汇总。
  * 完成后由 `MainViewModel.OnFileOperationCompleted` **按路径找**受影响的标签页（源所在目录 + 目标目录）
    重新枚举，而不是只刷新发起操作的那个 —— 拖到另一个窗格、粘到另一个标签页也要跟着变；
    删除时如果某个标签页正开在被删掉的目录里，则退到上一级（刷新它只会得到一条错误）。
* **删除**（`FileOperationCompletedEventArgs.IsDelete`）：`Delete` 进回收站、`Shift+Delete` 永久删除。
  回收站靠 `FOF_ALLOWUNDO` 实现，另外带上 `FOF_WANTNUKEWARNING` —— 文件大到装不进回收站（或所在卷没有回收站）
  时外壳仍会警告一次，否则这类文件会被**静默抹掉**而用户以为只是“丢进回收站”。
  `FO_DELETE` 不看 `pTo`，必须传 `null`（给空串会被外壳当成非法目标）。
  确认框（“确实要将其移至回收站吗？ / 确实要永久性地删除吗？”）由外壳弹，等在那里不点的话，
  删除就卡在 STA 线程上（日志里能看到“删除：1 项 → 永久删除”却没有“文件操作：…”的收尾行）；
  它是 **owned 的 `#32770` 窗口**，UIA 里得从主窗口的 `Descendants` 里找（不在桌面子窗口那一层）；
  用户点“否”时 `SHFileOperation` 返回 `fAnyOperationsAborted != 0`，`FileOperationResult.Canceled` 为 true。
* **命令与快捷键**：`FolderTabViewModel.CopySelectionCommand` / `CutSelectionCommand` / `PasteCommand` /
  `DeleteSelectionCommand`（进回收站）/ `DeleteSelectionPermanentlyCommand`（永久删除）
  （`MainWindow` 的「编辑」菜单与 `MainViewModel` 里的同名命令只是转发到活动标签页）。
  `Ctrl+C` / `Ctrl+X` / `Ctrl+V` 是挂在 `DetailsView` 根 Grid（`DetailsRoot`）上的
  `KeyboardAccelerator`，和已有的 `Ctrl+A` 一个道理：焦点在地址栏（不在本控件子树里）时
  仍然是 `TextBox` 自己的复制/粘贴/剪切。
  `Delete` / `Shift+Delete` 则用 `DetailsRoot.PreviewKeyDown`（隧道事件）自己判 Shift ——
  **带 Shift 的 `KeyboardAccelerator` 对 `Delete` 根本不触发**（实测），不要回去改成加速器。
* **拖动移动**：把行拖到列表里的某个**目录行**上（或拖到列表空白处 = 当前目录，
  也可以拖到另一个窗格）即移动；按住 `Ctrl` 是复制。
  * 拖拽源是 `DetailsRoot` 自己识别的手势（按下 + 移动超过 4 DIP 后 `StartDragAsync`），
    **不用** `ListView.CanDragItems`（它自带的拖拽在模拟鼠标下只能走到 `DragItemsStarting`
    就没了下文，见第 6 节第 58 条）。数据包用 `exdir/paths` 格式（`DragDropHelper.SetPaths`），
    额外带一个 `exdir/folders-only` 属性让工具条“固定目录”区能区分“拖的是文件”；
    允许的效果带上 `Move`（同盘拖动默认就是移动，和资源管理器一样）。
  * 落点是**整个文件列表**（`DetailsRoot`，`AllowDrop=True`）：行容器的拖放被关掉
    （`ItemContainerStyle` 里 `AllowDrop=False`），指针在行间移动时目标不再变来变去。
    鼠标下是哪一行用“光标位置 + 已生成行容器的实际矩形”算（`RowAt`），
    **不用** `e.GetPosition`（拖拽期间它给的坐标不可靠，见第 6 节第 59 条）、也不用
    `e.OriginalSource`（拖放事件的源永远是带 `AllowDrop` 的那个元素）。
    命中行高亮：`FileItemViewModel.IsDropTarget` → 行模板里那层强调色 `Border`。
  * **拖拽结束的兜底**：WinUI 有时不会把 `Drop` 冒泡到列表上（行高亮着、松手却什么都没发生），
    所以 `StartRowDragAsync` 在 `StartDragAsync` 返回之后会再判一次：
    左键还按着（= 用户按了 Esc 取消）或者光标已经不在本列表里（= 落到别的窗格/工具条/别的程序了）就不管，
    否则按松开时的光标位置自己把这次移动做完（日志里的 `拖放兜底：…`）。
  * 回归：`tools/test-file-ops.ps1`（9 个用例；需要交互桌面，且当前 shell 是管理员时会改用
    `explorer.exe` 以普通权限启动 exdir —— 提权进程根本不能参与拖放，见第 6 节第 21 条）。

### 键盘快捷键（定义在 MainWindow.xaml 的 `Grid.KeyboardAccelerators`）

| 快捷键 | 动作 |
| --- | --- |
| `Alt+←` / `Alt+→` / `Alt+↑` | 后退 / 前进 / 上一级 |
| `F5` | 刷新活动窗格 |
| `Ctrl+T` / `Ctrl+W` | 新建 / 关闭标签页 |
| `Ctrl+H` | 显示/隐藏隐藏文件 |
| `Ctrl+B` | 显示/隐藏侧边栏 |
| `Ctrl+A` | 全选活动窗格文件列表里当前可见的行（焦点在地址栏时仍是文本框全选） |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | 复制 / 剪切 / 粘贴（挂在 `DetailsView` 根 Grid 上，焦点在地址栏时仍是文本框自己的行为） |
| `Delete` / `Shift+Delete` | 删除到回收站 / 永久删除（走 `DetailsRoot.PreviewKeyDown` 隧道事件，仅作用于文件列表） |
| `F6` | 切换活动窗格 |
| `F10` | 单窗格 / 双窗格切换 |
| `Ctrl+L` / `Alt+D` | 编辑活动窗格的地址栏（等价于点地址栏空白处） |

另外：`文件列表 / 侧边栏文件夹树` 里的**目录**可以直接拖到工具条右侧的“固定目录”区固定下来；
文件列表里的文件 / 目录拖到某个**目录行**上（或拖到另一个窗格）就是移动，按住 `Ctrl` 是复制。

### 设置窗口（所有配置项的唯一入口，2026-09 从 ContentDialog 改为独立窗口）

* 菜单栏**「配置 → 设置…」**打开 `Views/SettingsWindow`（一个普通 `Window`，标题「设置」）。
  **同一时刻只开一个**（`MainWindow._settingsWindow` 持有，已开就 `Activate()`），`Closed` 时清掉引用。
* **改动即时生效**：窗口里没有「保存 / 取消」，任何一项被改动都立刻写回 `AppSettings` 并落盘
  （`SettingsViewModel.Changed` → `MainWindow` 侧调 `MainViewModel.ApplySettings`）。
* **Windows 11 风格**：左侧 `NavigationView` 选分类，右侧一列设置卡片 ——
  每行是社区工具包的 `SettingsCard`（`CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219），
  标题 + 灰色说明在左、控件在右、悬停/圆角/高对比主题全跟系统走，**不再自己写行模板**。
  当前五个分类：

  | 分类 | 配置项 |
  | --- | --- |
  | 文件列表 | 显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面 / 行高（滑块，默认 28） |
  | 外观 | 主题（下拉框：跟随系统 / 浅色 / 深色，默认跟随系统）/ 标签页使用直角（默认开）/ 过渡动画 |
  | 布局 | 列宽自动适应窗格宽度 / 显示工具条 / 显示侧边栏 / 双窗格模式 |
  | 侧边栏 | 显示「主目录」分组 / 显示「收藏夹」分组 / 显示「云存储」分组 / 显示「此电脑」分组（各分组的显示开关，默认全开）；「主目录」里显示「桌面」/「文档」/「下载」/「图片」/「音乐」/「视频」六个标准文件夹（**默认只开桌面与下载**） |
  | 右键菜单 | 使用内置的轻量右键菜单（开关，默认开）/ 系统右键菜单项逐项开关（动态清单，见第 4 节“右键菜单”） |

  * 分类是 `Models/SettingsCategory`（枚举）+ `SettingsViewModel.Categories`（列表顺序即导航顺序），
    绑到 `NavigationView.MenuItemsSource`（`MenuItemTemplate` 的根必须是 `NavigationViewItem`，同 TabView / TreeView）。
    切换分类**用 `SelectionChanged` 而不是 `ItemInvoked`**：后者只在“用户点了 / 按了”时触发，
    键盘方向键与程序化选中（自动化脚本的 `SelectionItemPattern`、直接赋 `SelectedItem`）都不触发，
    那样右侧页面不会跟着换（已踩过）。
  * 右侧五页的可见性绑 `IsFileListPageVisible` / `IsAppearancePageVisible` / `IsLayoutPageVisible` /
    `IsSidebarPageVisible` / `IsShellMenuPageVisible`（`SelectedCategory` 的 setter 里一次性通知这五个，省得每页各写一个枚举转换器）。
    非当前页是 `Visibility=Collapsed`，**UIA 树里根本没有它们**：所以回归脚本
    “切到某分类后只看得到该分类的开关”本身就是“切页真的生效”的验证。
  * 每一行用 `{x:Bind}` 把 VM 属性绑到卡片里的控件上（`ToggleSwitch.IsOn` / `Slider.Value` / `ComboBox.SelectedIndex` 都是 TwoWay）；
    开关的 `AutomationProperties.Name` 就是卡片标题（工具包本来也会把 Header 设成内容的名字，显式写一遍更明确），
    回归脚本按这个名字找它，用 `TogglePattern` 拨；
    滑块的 UIA 名字也是标题，用 `RangeValuePattern` 读写（`Slider` 没有 `TogglePattern`）；
    「主题」是三态，用 `ComboBox`（`SelectedIndex` 直接对应 `ThemeHelper` 的 0/1/2），
    回归脚本靠 `ExpandCollapse` + `SelectionItemPattern` 选、`SelectionPattern` 读 ——
    **它没有 `TogglePattern`，所以不会干扰上面那套“这一页有几个开关”的计数**
    （这也是选 `ComboBox` 而不是三个 `RadioButton` 的原因之一）。
  * **窗口默认 860×800 DIP**（`SettingsWindow.DefaultWidthDips/DefaultHeightDips`），交给 `SettingsView`，
    在工作区居中；工作区放不下就退让（先夹再定）。
    宽度不能小：`SettingsCard` 在**卡片宽度 < 476 DIP**（工具包里的 `SettingsCardWrapThreshold`）时
    会把右侧控件换行到标题下方，那就不是 Windows 11 的观感了；
    按当前布局（左导航 180 + 两侧留白 48）860 宽给卡片 ~625 DIP，留有余量。
  * **正文是 `Views/SettingsView`（UserControl），不是直接写在 Window 里**：
    `Window` 不是 `FrameworkElement`，WinUI 为它生成的 `x:Bind` 代码做不了 `{StaticResource}` 转换器查找
    （编译期报 `CS1503: 无法从 SettingsWindow 转换为 FrameworkElement`），而四页的 `Visibility` 都要用
    `BoolToVisibility`。`SettingsView.ViewModel` 是依赖属性，由窗口赋值（普通 CLR 属性在
    `InitializeComponent` 之后赋值时 x:Bind 不会重新求值）。
  * **「右键菜单」页是动态清单**：一行行不是写死在 XAML 里的，而是绑
    `SettingsViewModel.ShellMenuItems`（`ItemsControl` + `DataTemplate`，每行仍然是 `SettingsCard`）。
    构造时先用已记下来的清单填一遍（不碰 COM），`SettingsView.Loaded` 后再
    `DispatcherQueue.TryEnqueue(RefreshShellMenuItems)` 现枚举一次补全，所以打开窗口不会卡一下；
    现枚举完的那一次也算“改动”，会被落盘（`ShellMenuKnownItems`），下次就不用重新枚举。
    重建清单时已有的项保留用户刚拨过的开关。
    用 `ItemsControl` 而不是 `ListView`：后者会把没显示出来的行虚拟化掉，回归脚本数不全。
    新增一项要同时改三处：`ShellMenuItemViewModel`（`Title`/`Description`/`IsEnabled` —— 它必须是
    会发通知的 `ObservableObject`，否则窗口与 VM 都不知道用户拨了它）、
    `MainViewModel.ApplySettings`（写回 `ShellMenuDisabledItems` / `ShellMenuKnownItems`）、
    `tools/test-settings.ps1` 的用例 5。
* **编辑的是 `SettingsViewModel`（`MainViewModel.CreateSettingsEditor()` 造一份）**，
  它不再是“快照 + 取消回滚”，而是当前值 + 变更通知：每个属性走 `SetAndNotify`（先 `SetProperty`，
  真变了才 `Changed?.Invoke`），`ShellMenuItems` 里每行的 `PropertyChanged` 也汇总到同一个 `Changed`。
  写回只发生在 `MainViewModel.ApplySettings` 一处（写 `AppSettings` → 刷新界面 → `_settings.Save()`）。
* `MainViewModel.ApplySettings` 里先把值写进 `AppSettings`、再统一刷新界面：
  隐藏文件 / 扩展名会重新枚举目录，所以这两项是“最后只刷一次”；
  行高 / 列宽自适应只改每个标签页的 `ColumnLayout`（赋同样的值不会重排）；
  过渡动画只改视图行为（`tab.ApplyAnimationSettings()`），不重载目录。
  拨一次开关会调一次（含一次落盘）—— 滑块拖到底是几十次，实测没有卡顿，所以不做防抖。
* 「查看」菜单里的工具条 / 侧边栏 / 双窗格（含快捷键）保留：它们是**命令型**菜单项
  （`MenuFlyoutItem` + `ToggleXxxCommand`，本来就不显示勾选标记），和设置窗口切的是同一份设置，
  两边不会各说各话（`ApplySettings` 会 `OnPropertyChanged(ShowHiddenFiles/ShowExtensions/…)`，
  设置窗口里改完再点菜单项，切的就是新状态）。
* **新增配置项要动七个地方**：`AppSettings` 字段 → `SettingsViewModel` 属性（放进对应分类的注释段，
  用 `SetAndNotify`）→ `SettingsView.xaml` 里**对应分类页**加一张 `SettingsCard`
  （开关就放 `ToggleSwitch`，连续值放 `Slider` + 数值文本）→
  `MainViewModel.ApplySettings` 应用（别忘了把值推给所有已存在的标签页）→
  `tools/test-settings.ps1` 的 `$KeyMap`（开关：UIA 名字 → 字段名）与 `$CategoryMap`（开关：分类 → 该页的项）
  ——**滑块类的项不进这两个映射**（它不是 `ToggleSwitch`，会打乱“这一页有几个开关”的计数），另写用例断言 →
  需要新分类时再往 `SettingsCategory` / `Categories` 里加一项。
* 回归：`tools/test-settings.ps1`（6 个用例 48 条断言：分类齐全 / 每页只显示本分类的项 / 初始值一致 /
  拨一下立即落盘 / 立刻作用到文件列表 / 跨分类与关窗重开读回 / 同一时刻只开一个窗口 /
  「右键菜单」页的系统菜单项默认全开、关掉「属性」后落盘 `verb:properties`、重开仍为关、再拨回来就清空 /
  「行高」滑块的初值、切分类读不到、拖完列表行真的变高）。全程走 UIA 模式，不需要前台窗口。
  肉眼看布局用 `tools/shot-settings.ps1`（每个分类截图到 `.artifacts\settings-<分类名>.png`，需要交互桌面）。

## 5. 必须遵守的编码约定

* **分层**：`Views` 不直接做 I/O，一律经由 ViewModel → `Services` 接口。
* **入口点不要改回 XAML 生成的那份**：exdir.csproj 定义了 `DISABLE_XAML_GENERATED_MAIN`，入口点在
  `Program.cs`（为了把单实例闸门放在 WinUI 初始化之前）。升级 Windows App SDK 时要拿
  `obj\...\App.g.i.cs` 里生成的那份 `Program.Main` 对一下（初始化 COM Wrappers / 切 SynchronizationContext
  那几行不能少），见第 4 节“托盘驻留”。
* **服务成对**：新增能力先加 `IXxxService`，再写实现，最后在 `App.ConfigureServices()` 注册。
* **异步**：耗时 I/O 走 `Task.Run`（见 `FileSystemService`），
  从 UI 线程 `await` 时**不要** `ConfigureAwait(false)`，让续体回到 UI 线程后再更新 `ObservableCollection`。
* **UserControl 的 ViewModel 必须是 DependencyProperty**，由父级用 `{x:Bind ...}` 赋值；
  子控件内部用 `{x:Bind ViewModel.X, Mode=OneWay}`。
  （普通 CLR 属性在 `InitializeComponent` 之后赋值时绑定不会生效。）
* **主窗口的 ViewModel 用普通只读属性，且必须在 `InitializeComponent()` 之前赋值**，
  因为 `x:Bind` 在 `InitializeComponent` 期间求值。
  `SettingsWindow` 同理（`ViewModel` 在构造函数里先赋值再 `InitializeComponent()`，界面用 `x:Bind ViewModel.X` 绑它），
  正文 `Views/SettingsView` 则按上一条走依赖属性。
* **新增配置项要动七个地方**（详见第 4 节“设置窗口”）：`AppSettings` 字段 → `SettingsViewModel` 属性
  （用 `SetAndNotify`，改动会发 `Changed`）→ `SettingsView.xaml` 对应分类页里的 `SettingsCard` →
  `MainViewModel.ApplySettings` 应用 → `tools/test-settings.ps1` 的 `$KeyMap` 与 `$CategoryMap` →
  需要时再往 `SettingsCategory` 加分类。
  落盘与应用只在 `MainViewModel.ApplySettings` 一处发生（设置窗口自己不写 `settings.json`）。
* **凡是会被持久化（或跨 WinRT ABI）的对象，都不能只靠反射**：交付版是裁剪过的。
  写进 `settings.json` 的类型要在 `Services/SettingsJsonContext.cs` 里加一行 `[JsonSerializable]`，
  否则序列化会在运行时静默失败（见“踩过的坑”第 66 条）；
  实现 WinRT 接口的自定义控件要 `partial`（CsWinRT 源生成器才能把 vtable 生成出来）。
  这类问题只在**裁剪过的 dist 产物**里出现，改完记得用 `dist\win-x64\exdir.exe` 跑一遍回归。
* **数据集合整体替换而非增量 Add**：`FolderTabViewModel.Items` 每次导航/排序都新建
  `ObservableCollection` 再赋值，避免逐条 Add 造成 O(n²) 的 UI 开销。
* **中文注释**、中文 UI 文案。注释解释“为什么”，不要复述代码。
* 新增 XAML 控件时顺手加 `AutomationProperties.Name`，否则 UI Automation 测试脚本找不到它。

## 6. 踩过的坑（请勿重复踩）

1. **`x:Double` 资源不能赋给 `ColumnDefinition.Width`**
   WinUI 的 XAML 不会对资源值做类型转换，会抛
   `Failed to assign to property 'ColumnDefinition.Width' because the type 'Windows.Foundation.Double'
   cannot be assigned to the type 'Microsoft.UI.Xaml.GridLength'`。
   列宽统一定义在 `Helpers/ColumnLayout.cs`（强类型 `GridLength` 静态属性），
   通过 `{x:Bind helpers:ColumnLayout.DateWidth}` 共享，保证列头与数据行一致。
2. **列头与数据行对齐**：列宽不能用 XAML 资源（见第 1 条），统一走 `Helpers/ColumnLayout`：
   列头与每一行绑定同一个 `ColumnLayout` 实例的 rendered 宽度；
   可用宽度必须用 `ScrollViewer.ViewportWidth`（而不是 ListView 宽度）算——
   自定义竖滚动条会占掉视口宽度（`HorizontalOffset` 同步列头时也一样）。
3. **`dotnet publish` 会把 `.xbf` 和 `exdir.pri` 丢掉**，发布版启动即
   `XamlParseException: XAML parsing failed`（两者只有 `CopyToOutputDirectory`，
   没进发布文件列表）。
   手动往 `ResolvedFileToPublish` 里补会更糟（整个发布目录被写成同一个文件的内容），
   **不要往项目文件里加这种 target**。
   **现在的发布流程 = `dotnet publish` + 从构建输出把这两个东西拷回发布目录**（`tools\publish.ps1` 的第 2 步），
   并用 `dotnet publish` 是因为裁剪（`PublishTrimmed`）只在 publish 阶段生效 —— 两者必须同时存在。
4. **`PublishReadyToRun=true` 不能开**，会导致启动期 XAML 失败。
   **`PublishTrimmed=true` 可以开，但必须配齐四个开关**（缺一个不是启动崩就是特定窗口崩），
   见第 64 条。
5. **窗口刚开始 `Activated` 时 `XamlRoot` 是 null**，`RasterizationScale` 取不到，
   会把 DIP 当物理像素（高分屏下窗口过小）。
   现在在 `RootGrid.Loaded` 里才恢复窗口位置，并用 `Helpers/DpiHelper`（优先 XamlRoot，
   回退 `GetDpiForWindow`）。
6. **抓窗口截图前必须先声明 DPI 感知**（`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`），
   否则 PowerShell 拿到的坐标是虚拟化的，会出现“控件看起来缺失”的误判
   （本机 200% 缩放下曾把 1400x850 物理像素误当成 DIP）。
7. **`ElementName` 绑定在 `DataTemplate` 里不可靠**：模板内部要用命令时，
   改为在 `Click` 事件里从 `FrameworkElement.DataContext` 取数据再调用 ViewModel。
8. **TabView 的项容器模板**：`TabItemsSource` + `TabView.TabItemTemplate`，
   模板根必须是 `TabViewItem`；`TreeView` 的分层绑定同理（模板根为 `TreeViewItem`，其 `ItemsSource` 绑子集合）。
9. **`PanelViewModel.NewTabAsync` 必须先取 `CurrentPath` 再 `CreateTab()`**，
   因为创建标签会切换活动标签，之后再读就是空路径（曾导致新标签页导航到空路径报“无法打开:”）。
10. **`TextBox.Text` 的 `{x:Bind ..., Mode=TwoWay}` 默认在失焦时才回写**（和 `{Binding}` 一样是 `LostFocus`）。
    路径框曾因此“回车没反应”：`KeyDown` 里读到的 `PathInput` 还是上一个路径，实际导航到的是原目录。
    凡是“按回车/按钮就要读输入框内容”的场合，必须写 `UpdateSourceTrigger=PropertyChanged`。
11. **`DataTemplate` 内部的 `x:Name` 在 code-behind 里访问不到**：模板里需要事件处理或
    自己的状态时，用 `UserControl` 包一层（例：`NavigationBarView`），再把数据用 DP 传进去。
12. **`TextBox` 的文字永远顶对齐**：控件模板根本不使用 `VerticalContentAlignment`
    （[microsoft-ui-xaml#5369](https://github.com/microsoft/microsoft-ui-xaml/issues/5369)），
    所以 `Height="24"` + `VerticalContentAlignment="Center"` 的结果是“框比 `Height` 更高、文字贴顶”；
    框压不下去是因为模板内层 `BorderElement` 的 `MinHeight` 用了
    `ThemeResource TextControlThemeMinHeight`（默认 32），不受控件自身 `Height`/`MinHeight` 约束。
    地址栏的做法（`Views/PathBreadcrumb.xaml`）：在 `Themes/ExdirTheme.xaml` 里把
    `TextControlThemeMinHeight` 覆盖为 0，文本框**不设 `Height`**、只给上下对称的
    `ExTextControlPadding`，让框“刚好包住一行文字”，再由外层 `VerticalAlignment="Center"` 居中；
    文字随字体/文本缩放变高时仍保持居中。新增 TextBox 请沿用这个模式。
13. **用脚本模拟“拖动”时，`SetCursorPos` 不会让 WinUI 收到 `PointerMoved`**（只会在下一次
    `mouse_event(LEFTDOWN)` 时把位置带过去）。拖拽类交互必须用
    `mouse_event(MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE, x*65535/(屏幕宽-1), y*65535/(屏幕高-1))`
    逐步移动，否则测试会得出“拖动没反应”的假结论。双击同理，两次按下的间隔要小于系统双击时间。
    同理，**用 `mouse_event` 模拟“悬停”时，如果光标本来就在目标点上，同坐标的移动不会产生
    `PointerMoved`**（会得出“悬停高亮没生效”的假结论）——先移到屏幕角落再移回来
    （`tools/inspect-ui.ps1 -Hover` 就是这么做的）。
14. **`new DirectoryInfo("D:")` 是“D 盘的当前目录”**，会按进程工作目录解析，不是驱动器根。
    `GetParentDirectory` 曾因此在 `D:\` 上返回进程工作目录的父目录（“上一级”从盘符根
    跳到了 exe 所在目录）。判断根路径不能只看 `TrimEnd('\\').Length == 0`，还要看末尾是不是 `:`。
15. **`Click` 事件的参数是 `RoutedEventArgs`，没有 `Handled`**（只有 `PointerRoutedEventArgs` /
    `TappedRoutedEventArgs` 等才有）；想在 `Click` 里阻断冒泡得换事件或换控件。
16. **空内容的 `Button` 会缩成 0×0**：WinUI 默认 `Button` 样式把 `HorizontalAlignment` 设成 `Left`、
    `VerticalAlignment` 设成 `Center`，所以“拿一个透明按钮当整条区域的点击目标”时必须显式写
    `HorizontalAlignment="Stretch" VerticalAlignment="Stretch"`，否则 `ActualWidth/Height` 都是 0，
    既不显示也点不到（UIA 里它的 `BoundingRectangle` 会是空）。
17. **`VisualTreeHelper.FindElementsInHostCoordinates` 在 WinUI 3 下传 `null` 会抛异常**
    （DesktopWindowXamlSource 模式要求必须传 `UIElement subtree`），传了 subtree 时坐标要按窗口算；
    而且它**不会返回 `ScrollViewer` 内滚动内容里的元素**（只会返回 ScrollViewer/ContentPresenter 本身），
    所以不能拿它当“点这里会不会命中那个按钮”的笔据。想知道谁拿到点击，只能真鼠标点。
18. **没有交互桌面时本机没法模拟输入/截图**：`GetForegroundWindow()` 返回 0 时，
    `SendKeys`/`keybd_event`/`mouse_event` 都送不到窗口（`SendKeys` 抛异常，
    `mouse_event` 静默无效），`Graphics.CopyFromScreen` 报“句柄无效”（`-PrintWindow` 只能抓到
    系统窗口按钮，WinUI 内容全白）。这种情况下只能靠 UIA 读控件树 + `InvokePattern` 触发控件：
    `tools/inspect-ui.ps1` 可以验证布局/状态转换，但验证不了真实的指针命中与键盘事件。
19. **`PROPVARIANT` 在 x64 上是 24 字节，不是按字段算出来的 16 字节**。
    自己写个只有 `vt` + `u`（`FieldOffset(8)`）的结构去接 `IPropertyStore.GetValue`，
    native 写回 24 字节会把栈踩坏——表现是**整个进程没有任何日志/异常就消失**（托管异常处理器
    根本没机会跑），只能靠“功能一跑就死”察觉。`Services/Native/ShellPropertyStore` 的
    `PropVariant` 显式写了 `Size = 24`（x86 上是 16，给大一点无害）。
    排查“无日志猝死”时，先怀疑互操作结构体大小，而不是业务代码。
20. **往详细信息列表加列要同时改五个地方**，漏一个就会出现“四列宽度算了五个列”这类偏移：
    `ColumnLayout` 的 `Defaults`/`Minimums`/下标常量与 `GridLength` 属性、`DetailsView.xaml` 的
    **列头 Grid 与行模板 Grid 两处 `ColumnDefinitions`** 以及它们的 `Grid.Column`、
    `FitTo` 里的 `total`/`others` 求和（当初就是漏了最后一项导致压缩量算少、列溢出窗格）、
    以及 `AppSettings.ColumnWidths` 的顺序（改了顺序就要像“状态列”那样提 `CurrentSchemaVersion`
    并在 `SettingsService.Migrate` 里补，否则用户拖过的列宽会整体错位）。
21. **提权（高完整性级别）的进程根本不能参与拖放**：`DragItemsStarting` 会正常触发，但之后就再也没有
    `DragOver`/`Drop`（甚至整窗口 `AllowDrop=True` 的根元素也收不到 `DragEnter`，`DragItemsCompleted` 也不触发），
    看起来像“拖拽开始了但落点没反应”。`tools/test-pin-drag.ps1` 在检测到当前 shell 已提权时会改用
    `explorer.exe` 启动 exdir，把完整性级别降下来（这是 Windows 的安全策略，不是应用代码的问题）。
22. **拖放的 `DragEventArgs` 在 `await` 之后不能再碰**：在 `DragOver` 里 `await e.DataView.GetStorageItemsAsync()`
    之后再设 `e.DragUIOverride.Caption` / `e.AcceptedOperation` 会抛一个没有消息的 `COMException`，
    而且**之后所有拖放都失效**（[#9296](https://github.com/microsoft/microsoft-ui-xaml/issues/9296)、
    [#8108](https://github.com/microsoft/microsoft-ui-xaml/issues/8108)：DataExchangeHost 已经失效）。
    所以 `DragOver` 必须全程同步（只做 `Contains` 这类同步判断），要读内容留到 `Drop`；
    `Drop` 里也要先把 `e.DataView` 取到局部变量再 `await`，`await` 之后只改自己的 UI 状态。
23. **`AllowDrop` 的元素必须有 `Background`（哪怕是 `Transparent`）才有命中区**，
    否则空白/无背景的区域收不到 `DragOver`/`Drop`。只挂在祖先上就够（子元素上的拖拽会往上冒泡），
    不需要给每个子控件都写 `AllowDrop`（实测只写在外层 `Border` 上即可）。
24. **`MenuFlyout` 等弹出菜单不在主窗口的 UIA 子树里**（Desktop 下它是另一个 XAML 岛/窗口）：
    `inspect-ui.ps1` 那套 `root.FindAll(...)` 找不到菜单项，要从 `AutomationElement.RootElement` 往下找
    （`tools/test-pin-drag.ps1` 的 `Find-Element -From` 就是这么用的）。
25. **WinUI 3 的 `Button` 会把左键的 `PointerPressed`/`PointerMoved` 标成 `Handled`**：
    所以 `CanDrag="True"` 形同虚设（框架的拖拽手势识别根本收不到事件，`DragStarting` 永不触发），
    挂在 Button 上的 `PointerPressed="..."` 也只有**右键**会跑到（左键被 Button 内部吞了，
    曾据此误判成“拖拽没开始”）。要在 Button 上做拖拽手势只能自己识别：在**容器**上
    `AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(...), handledEventsToo: true)`
    （`PointerMoved`/`PointerReleased`/`PointerCaptureLost` 同理），按下后指针移动超过阈值再调
    `UIElement.StartDragAsync(point)`；它收的是 **`Microsoft.UI.Input.PointerPoint`**
    （`e.GetCurrentPoint()` 的返回类型，不是 `Windows.UI.Input.PointerPoint`），
    调用后照常触发 `DragStarting`，在那里填 `DataPackage`。（提权进程不支持 `StartDragAsync`，
    与第 21 条一致，要 `try/catch` 住不要把进程搞挂。）
26. **菜单/弹层有入场动画，UIA 脚本“先查坐标再点”会打空（尤其是第二次打开）**：
    `MenuBarItem.Expand()` 之后菜单项刚出现的那几帧里 `BoundingRectangle` 还在动，
    而脚本往往“先拿到元素 → 切前台 → 等几百毫秒 → 按之前读到的坐标点击”，这一串下来就点空了；
    更坑的是上一次关掉的 `MenuFlyout` 的菜单项**仍会留在 UIA 树里**（只是没了坐标/变成 offscreen），
    按名字查到的可能是那个旧元素——症状是“菜单项找得到、点了却什么都不发生”。
    所以自动化脚本优先用 UIA 模式：`MenuBarItem` 用 `ExpandCollapsePattern` 打开、
    菜单项用 `InvokePattern`；实在要真鼠标点击就先过滤 `IsOffscreen == false` 且宽高 > 0，
    并在点击前那一刻才读坐标（`tools/test-settings.ps1`）。
27. **`ContentDialog` 是独立的弹出岛窗口**：在 UIA 里它是 `RootElement` 下的顶层 `Window`
    （名字取对话框的 `AutomationProperties.Name`/`Title`，这里是“设置”），**不在主窗口的子树里**，
    所以断言对话框内容要从这个窗口往下找；而且对话框内容超出可视区时，
    屏幕外的控件 `IsOffscreen=true`、`BoundingRectangle` 是空的，
    **按“是不是对话框窗口的后代”筛选才能数全**（按可见性筛会少见几个）。
28. **`ToggleMenuFlyoutItem` 在 UIA 里没有 `TogglePattern`**（只有 Invoke / ScrollItem / VirtualizedItem，
    `CheckBox` 才支持 Toggle）。所以“菜单项的勾选状态对不对”没法用 UIA 断言，
    只能截图肉眼看（或改用 `CheckBox` 类控件）。
29. **一个 `ResourceDictionary` 里 `ThemeDictionaries` 必须放在最后**：只要写了
    `ResourceDictionary.ThemeDictionaries` 属性元素，后面就不能再有隐式资源条目，
    否则 XAML 编译器报 `WMC0035: Duplication assignment to the '_Items' property`
    （`Themes/ExdirTheme.xaml` 里主题相关色刷就因此挪到了文件末尾）。
30. **`SHGetFileInfo(SHGFI_ICON)` 并发调用时会偶发“只给图标索引、不给 HICON”**：
    返回值非 0、`iIcon` 有效，但 `hIcon == 0`（实测：一批行并行取图标时，第一个碰上的那个文件命中，
    典型是 `.txt` 这种扩展名图标）。**解决办法：把“问外壳要 HICON”这一步串行化**
    （`ShellIconExtractor.Gate`），并**失败时重试一次**；串行+重试之后 3 次冷启动跑下来哈希完全一致。
    读像素（`GetDIBits`）各用各的位图，不需要在锁里。
31. **`calc.exe` 这个名字取不到外壳图标**：它是 Windows 的“应用执行别名”，外壳会转去 AppX 包里取图标，
    非打包进程里这一步只给出图标索引、给不出 HICON（**同一个文件改名叫 `calculator.exe` 就一切正常**）。
    因此 `ShellIconExtractor` 在“外壳给不出 HICON”时会退回 `ExtractIconEx` 直接读文件自身的图标资源
    （对 exe 有效，对目录/lnk 自然返回 0，正好什么都不影响）。
    排查这类问题的日志形如 `外壳图标：xxx 提取失败（SHGetFileInfo 没有返回图标（iIcon=130 …））`。
32. **图标的尺寸要按 `SM_CXSMICON` 取（`SHGFI_SMALLICON`）**：它正好等于“16 DIP 在当前 DPI 下的物理像素数”
    （100% → 16、150% → 24、200% → 32），跟列表里 16×16 DIP 的 `Image` 是 1:1，任何缩放下都清晰。
    用 `SHGFI_LARGEICON` 在 200% 缩放下拿到的是 64×64（`SM_CXICON`），白多 4 倍内存。
    另外 **`WriteableBitmap.PixelBuffer` 要的是预乘 alpha**：`GetDIBits` 读出来的是直通 alpha，
    必须自己预乘（否则半透明边缘会发白）；`GetDIBits` 还要按“自下而上”请求（`biHeight` 给正数）再自己翻行，
    别指望 GDI 给你换方向。
33. **UIA 里“有没有真实图标”可以断言**：`DetailsView` 行模板里那个 `Image` 带
    `AutomationProperties.Name="程序图标"`，并且 `Visibility` 绑在 `HasIcon` 上——
    没拿到图标时它是 `Collapsed`，UIA 树里根本找不到它，所以脚本可以“数不到就是没图标”地断言。
34. **写“量像素”的脚本时，UIA 的 `BoundingRectangle` 是屏幕坐标，截图是窗口内的相对坐标**：
    `CopyFromScreen(rect.Left, rect.Top, ...)` 截出来的图里，(0,0) 是窗口左上角，
    所以 `GetPixel` 必须减掉 `GetWindowRect` 的原点。不减的话量的就是屏幕坐标处的内容——
    会得出“图标墨迹和文字墨迹一模一样”这类看似合理、其实是在量文字的结论
    （本项目曾因此差点把“图标与文字对齐”改成反向）。
    `tools/measure-row-align.ps1` 里就是 `$x - $originX` 这么做的。
35. **“图标与文字垂直居中”不能只看布局盒子**：两边盒子都在行高里居中，图标仍会显得偏上，
    因为文字行盒的底部有一段降部（descent）空白。要用“墨迹中心”比（`tools/measure-row-align.ps1`），
    并且看**中位数**而不是单行——图标自己的画稿在 16×16 盒子里不一定居中，
    带降部的名字（g/p/q/y）墨迹也会被尾巴拉低。当前取值（图标下推 1 DIP）下中位数约 2.5 物理像素、
    平均约 2.0（200% 缩放）；去掉那 1 DIP 会恶化到 4.5 / 4.0，断言会失败。
    另：下推只能用 `RenderTransform` —— 给 16 DIP 高的元素加 `Margin` 会同时把布局盒变成 17，
    实际只向下移 0.5 DIP，而且非整数偏移会把位图重采样、图标发虚。
36. **`TabView` 的标签高度不是 `TabViewItemMinHeight` 说了算，而是被“关闭”按钮撑出来的**：
    `TabViewItem` 隐式样式里虽然有 `MinHeight={ThemeResource TabViewItemMinHeight}`（默认 32），
    但就算把资源改小、或直接在 `TabViewItem` 上写 `MinHeight="24"`，标签实测仍是 32 DIP ——
    模板里 `TabContainer` 的 `Padding` 是 `8,3,4,3`，而 `TabViewCloseButtonStyle` 把关闭按钮设成
    `TabViewItemHeaderCloseButtonWidth × TabViewItemHeaderCloseButtonHeight = 32 × 24`，
    `3 + 24 + 4（选中态的下内边距） + 1（选中态的下外边距）` 正好 32，顶在 MinHeight 之上。
    想让标签变矮就必须同时把 `TabViewItemHeaderCloseButtonHeight` 调小（16 与图标/文字同高，
    宽度留 32 保证点得中），标签才会落到 `MinHeight` 上。
    排查手段：临时在 `PaneView` 的 `LayoutUpdated` 里把 `Tabs` 的视觉树连
    `ActualHeight/MinHeight/Padding/Margin` 一起打到 `%LOCALAPPDATA%\exdir\tabtree.txt`
    （只盯 `ActualHeight` 容易误判成“MinHeight 没生效”）。
    另：标签条装不下时出现的 ◀ ▶ 按钮容器默认带 3 DIP 下内边距
    （`TabViewItemLeft/RightScrollButtonContainerPadding`），会把**只有溢出窗格**的标签栏撑到 27 DIP ——
    双窗格左右两条标签栏高度不一致就是这么来的。
37. **`ContentDialog` 里的 `FontIcon` 渲染发献（尺寸被算成接近 0）、里面的 `Margin` 负值会被裁切**：
    2026-09 重构设置对话框时，左侧导航项原本想放一个 Segoe 字形图标（`FontIcon` + `Glyph`，
    写法与 `SidebarView` 里的完全一样），结果在对话框里字形只画出左边约 4 DIP 的一小条，
    而且它后面的 `TextBlock` 也挤了上来（相当于 `FontIcon` 的 DesiredSize 几乎是 0）。
    换掉横向 `StackPanel`、改成 `Grid(Auto,*)`、甚至给 `FontIcon` 显式写 `Width="16"` 都没用；
    同一个写法在主窗口的侧边栏树里一切正常，只有 `ContentDialog` 里不行。
    同一个对话框里用 `Margin="-24,0,-24,-24"` 想“两栏通到对话框边缘”时，左侧内容也会被裁掉一截
    （截在对话框左边界上，导航文字只剩右半边）。
    排查手段：截图后用 `GetPixel` 扫一行/一列的颜色分段（UIA 的 `BoundingRectangle` 在这种场景下
    **也不能信**：同一份布局里 `ToggleSwitch` 报的是 `x≈-1118`，而实际画在对话框右侧）。
    结论/做法：设置对话框的左侧导航**只放文字、不放图标**；也不要靠负 `Margin` 去消除
    `ContentDialogPadding`（想要“通到底”的观感，宁可接受 24 DIP 的卡片式留白）。
    （2026-09 S17d 之后设置界面已经不是 `ContentDialog` 而是独立窗口 `Views/SettingsWindow`，
    导航也从 `ListView` 换成了 `NavigationView`，所以这两条现在只是历史记录；
    以后若再把设置放回 `ContentDialog`，先看这一条。）
38. **同一个主题资源键，在 `ContentDialog` 里可能读不到 App 级的值**：
    `Themes/ExdirTheme.xaml` 里已经把 `ListViewItemSelectionIndicatorVisualEnabled` 设成 `False`
    （主窗口的文件列表确实没有那条左侧竖条，见第 4 节），但设置对话框里的 `ListView`
    还是画出了强调色竖条；把同一个键再写进该对话框的 `<ContentDialog.Resources>` 就消失了。
    所以“在某个弹层里发现某个主题资源像是没生效”时，先在**这个弹层自己的 `Resources` 里再覆写一遍**，
    而不是去怀疑 `ExdirTheme.xaml` 的合并顺序。（设置界面现在不是 `ContentDialog` 了，见第 37 条末尾的说明。）
39. **`TabView` 的默认样式把 `VerticalAlignment` 设成了 `Top`**：
    这不是我们的 XAML 写的，是 WinUI 自带的。后果是**标签页内容只占“内容自己的高度”**：
    列上只有两三个文件时，`DetailsView` 只有 74 DIP 高（表头 24 + 两行 48），
    列表下面那一大片空白既点不到也收不到右键（它是 `PaneRoot` 而不是列表）。
    排查方法：从出问题的控件往上逐级打 `ActualHeight / DesiredSize.Height / VerticalAlignment`
    （见 `AGENTS.md` 第 4 节）——一行就能看出“某个祖先被压成了 DesiredSize”。
    修法：在 `Views/PaneView.xaml` 的 `TabView` 上显式写
    `VerticalAlignment="Stretch"`（顺便把 `Horizontal/VerticalContentAlignment` 也写成 Stretch）。
40. **列表空白处收不到 `RightTapped` / `ContextRequested`，即使用 `handledEventsToo` 也收不到**：
    空白处命中的不是列表，而是列表外面那个有背景的 Grid——`ListView` 内部的
    `ScrollViewer` / `ScrollContentPresenter` 自己没背景，不参与命中测试，事件根本不会沿着
    `EntryList` 这条路由上去。
    所以右键菜单的监听要挂在 `DetailsView` **最外层那个有背景的 Grid** 上，
    再在处理器里用 `OriginalSource` 反查“是不是在某一行的 DataContext 里”。
    另：`UIElement.ContextRequested` 在有些控件上压根不冒泡（跟 `ContextFlyout` 有关），
    `RightTapped` 更可靠；两个都挂上、拿时间戳防一下重复（菜单是模态弹出的，
    第一个处理完用户关掉菜单后第二个才会被调用）。
41. **窗口位置/尺寸存坏了会让整个界面“看起来没做出来”**：曾经 settings.json 里被写下
    `WindowWidth=157 / WindowHeight=25 / WindowX=-16000 / WindowY=-16000`，
    下次启动窗口就只有 360×240 DIP（侧边栏就占掉一大半），列表里几乎什么都点不到，
    很容易误判成“右键菜单没生效 / 控件找不到”。两个原因都在 `MainWindow`：
    * `RestoreWindowPlacement` 把 `720 / 480` 这个 **DIP 下限**写在了换算成物理像素之后，
      200% 缩放下就只给了 720×480 物理像素（= 360×240 DIP）——下限要先夹再乘缩放；
    * `SaveWindowPlacement` 在**窗口最小化时**会把 `(-32000,-32000)` 存下来
      （`AppWindow.Position` 的哨兵值），下次启动就跑到屏幕外——要跳过最小化状态与哨兵值。
    调试时如果界面尺寸明显不对，**第一件事是看 `exdir.log` 里那行 `恢复窗口位置: 设置=…`**。
42. **Win11 的外壳右键菜单在 UIA 里读不到菜单项**：它是一张自绘的 Win32 弹出菜单，
    `AutomationElement` 只能看到主窗口自己的菜单栏（文件 / 编辑 / …）和一团 `Pane`，
    `MenuItem` 一个都找不到（按 `ProcessId` 从 `RootElement` 往下找也一样）。
    所以自动化只能：① 用 `EnumWindows` 按进程找 `#32768` 窗口证明“菜单弹出来了”；
    ② 让 exdir 自己把菜单项写进 `exdir.log`（`系统右键菜单：<上下文> 上下文共 N 项，已关闭 X`）。
    想给“关掉的项真的不在菜单里”留证据，就只能截图（`.artifacts\context-menu-filtered.png`）。
43. **宿主自己弹系统菜单时必须转发菜单消息，否则子菜单是空的**：
    `IContextMenu` 的背后常常是 `IContextMenu3`（甚至 `IContextMenu2`），
    壳扩展要靠 `WM_INITMENUPOPUP`（典型：“打开方式”）才知道该往子菜单里放什么，
    owner-draw 的项要靠 `WM_DRAWITEM` / `WM_MEASUREITEM`。这些消息系统只会发给菜单的
    宿主窗口，所以 `TrackPopupMenu` 期间要用 `SetWindowSubclass`（comctl32）挂一个钩子，
    把这三条消息转给 `IContextMenu3::HandleMenuMsg2` / `IContextMenu2::HandleMenuMsg`，
    弹完立刻 `RemoveWindowSubclass`。
    另外“用 `ShowWindow` 把窗口最小化后再关”会让 `AppWindow.Position` 返回 `-32000` 级哨兵值，
    `SaveWindowPlacement` 必须跳过最小化状态与舘兵值（否则下次启动窗口跑到屏幕外）。
44. **文件列表的行高不能用 `ListViewItem` 的 `MinHeight` 来“配”**：
    原来 `DetailsView` 的 `ItemContainerStyle` 把 `MinHeight` 固定成 `ExRowHeight`(24)，
    而 `MinHeight` 是**下限**：把行高设置调成 20 时容器依旧 24，界面没反应（看起来像“设置没生效”）。
    所以：① 容器 `MinHeight` 归零；② 行高绑在**行模板的 `Grid.Height`**
    （`{x:Bind Columns.RowHeight}`）上，由内容决定容器高度。
    另：`Style` 的 `Setter.Value` 不能写 `x:Bind`/`Binding`（只能是资源或字面量），
    所以想让容器高度跟设置走，也没法在样式里绑——归零 + 内容定高是唯一干净的做法。
    顺带：行高放在 `ColumnLayout`（每标签页一个、行模板本来就绑着的对象）上，
    而不是每个 `FileItemViewModel` 上，否则改一次设置要给几千行各发一次通知。
    滑块类的设置项在 UIA 里用 `RangeValuePattern` 读写（`Find-VisibleFirst` 找名字时要按
    `ControlType.Slider` 过滤：左边的标题 `TextBlock` 也叫“行高”）。
45. **行里的“空白处”不是行模板的一部分，挂在行模板上的事件收不到**：
    文件列表的行（`ListView.ItemTemplate` 的 `Grid`）里，只有文字 / 图标所在的那几小块是
    可命中的元素，行内上下留白、名称文字右侧、日期/类型/大小列里的空白都没有元素可命中——
    命中的是 `ListViewItem` 本身。而 `DoubleTapped` 是**冒泡**事件：命中 `ListViewItem` 时它从
    `ListViewItem` 直接往 `DetailsRoot` 冒，永远不会经过作为其后代的行模板 `Grid`，于是
    “只有双击到文字/图标上才进目录”。**要么把处理器挂到外层（再用 `FindRowItem` 反查行），
    要么给行模板根元素加 `Background="Transparent"`**（没有背景的 `Grid` 在空白处不参与命中测试，
    连 `ToolTipService.ToolTip` 都弹不出来）；`exdir` 两个都做了。
    同一个道理适用于任何“看起来是整块、其实只有内容是实心”的容器（例如 `ListViewItem` 的
    `ContextFlyout` / `RightTapped`，见第 40 条）。
    另：调试这类问题时不要拿“双击某个点”下结论就完事——同一行的不同落点命中路径完全不同，
    回归脚本 `tools/test-row-dblclick.ps1` 会逐个落点各跑一遍（真鼠标双击）。
46. **零宽度 Grid 单元格里的子元素收不到指针事件（列宽把手拖不动）**：
    详细信息列表的拖动把手原本被追加到列头那个带列定义的 `Grid` 里，而它们没写 `Grid.Column`、
    默认在第 0 列 —— 第 0 列后来变成了“状态”列（云同步状态），**非云目录里宽度为 0**。
    结果：列头看上去完全正常（把手用 `RenderTransform` 照样画在列边界上、UIA 里也找得到这个元素），
    但整个把手（包括它自己 layout 所在的 [0,6] 那几像素）都不参与命中测试 —— 鼠标悬停不变鼠标形状、
    按下去拿到事件的是列头那个铺满整列的排序按钮，任何列都拖不动。
    云目录里（状态列宽 56）反倒一切正常，所以“偶尔能拖、偶尔不能”很有欺骗性。
    根治办法：把把手放进一个**没有列定义、整宽**的父层（`HeaderLayer`），把横向滚动的
    `TranslateTransform` 也挂在这一层上，按钮与把手就始终共享同一个坐标原点。
    另：不要用 UIA 的 `BoundingRectangle` 判断这类元素“在哪/能不能点”——它是**布局值**、
    不含 `RenderTransform`（而且被窗格/屏幕裁切后会变成空矩形），判断命中必须真鼠标点。
    回归：`tools/test-column-resize.ps1`（修前用例 2/3/4 全部没反应）。

47. **`TaskbarIcon.ContextFlyout` 里的菜单项挂 `Click` 是无效的**：
    H.NotifyIcon 默认的 `ContextMenuMode=PopupMenu` 是把 WinUI 的 `MenuFlyout` **抄成一张 Win32 弹出菜单**
    （`CreatePopupMenu` + `TrackPopupMenu`），只抄 `Text` / `IsEnabled` / `Command` / `CommandParameter`，
    然后在 `PopupMenuItem.Click` 里执行 `flyoutItem.Command`。所以托盘菜单项写 `Click="..."` 永远不触发
    （不报错，只是点了没反应）；必须给 `Command`（本次就是 MainWindow 上的 `ShowWindowCommand` /
    `ViewModel.ExitCommand`）。顺带：菜单是弹出前现抄的，所以 `Command` 绑的是当时的状态。

48. **关窗口只是隐藏的托盘程序，不能用 `CloseMainWindow()` 收尾**：
    `Process.CloseMainWindow()` 发的是 `WM_CLOSE`，而 `MainWindow.OnAppWindowClosing` 会 `Cancel` 掉
    它只把窗口藏进托盘，于是 `WaitForExit(3000/4000/5000)` 白等几秒、再到 `Kill()`。
    `tools\*.ps1` 的 `Stop-Session` 已全部改成直接 `Kill()`：隐藏时已经
    `SaveWindowSettings + SaveSession` 落过盘，强杀不会丢列宽/会话（真的要验证退出路径用
    `tools/test-tray.ps1` 用例 4）。

49. **UIA 量“行高/行宽”时不能用平均值：视口底部那一行是裁过的**：
    `test-settings.ps1` 用例 6 把行高改成 40、然后统计所有可见数据行的 `BoundingRectangle.Height`，
    实测得到的是 `[80, 80, 80, 80, 80, 80, 80, 48]` —— 最后一行只露出一半（48 物理像素），
    平均值 76、离期望 80 差 4 像素，刚好超出 ±2 的容差，于是偶发 FAIL（窗口高一点、矮一点就能翻转）。
    28 DIP 时那个 4 像素的残条 UIA 干脆不报（`IsOffscreen=true`），所以同样的断言当时是准的。
    现在取**中位数**（`Get-TypicalRowHeight`）并把逐行高度也打印出来，跟“用中位数不用单行”是同一个道理
    （见第 35 条）。

50. **`Window` 不是 `FrameworkElement`：写在 `Window` 这一层的 `x:Bind` 用不了 `{StaticResource}` 转换器**：
    设置窗口原本把 NavigationView + 设置卡片直接写在 `Views/SettingsWindow.xaml` 里，
    XAML 编译器为这个根生成的代码里有一行 `bindings.SetConverterLookupRoot(this)`，
    而 `SetConverterLookupRoot` 收的是 `FrameworkElement` —— 编译直接报
    `error CS1503: 参数 1: 无法从“Exdir.Views.SettingsWindow”转换为“Microsoft.UI.Xaml.FrameworkElement”`。
    只要这个文件里任何一处 `x:Bind` 带 `Converter={StaticResource ...}` 就会踩到。
    解法：窗口只当一层薄壳（标题/尺寸/接线），界面正文放进 `Views/SettingsView`（UserControl），
    它的根是 `FrameworkElement`，转换器随便用。
    顺带：别的 `Window` 子类（包括 `MainWindow`）同理，别把带转换器的绑定写在 Window 根上。

51. **社区工具包的 `SettingsCard` 在卡片宽度 < 476 DIP 时会把控件换行到标题下方**：
    它的 `ContentAlignmentStates` 里 `RightWrapped` / `RightWrappedNoIcon` 两个状态由
    `tk:ControlSizeTrigger` 触发，阀值是资源 `SettingsCardWrapThreshold = 476`
    （更窄的 `SettingsCardWrapNoIconThreshold = 286` 连 HeaderIcon 一起收掉）。
    所以“左导航 + 设置卡片”的窗口宽度不能小：设窗口宽 W，卡片宽约 `W - 180（左导航）- 48（两侧留白）`，
    W = 600 只有 372（换行，不是 Win11 观感），W = 860 约 625（正常）。
    本机屏幕只有 485 DIP 宽（虚拟机），把窗口拉大到 860 也不会生效 —— Windows 会把窗口尺寸
    夹到屏幕大小（实测 `MoveWindow` 到 1935 物理像素，拿到手只有 1232），所以这个观感在本机无法肉眼验证，
    只能靠上面的尺寸推算。

52. **`NavigationView` 换分类要用 `SelectionChanged`，不能用 `ItemInvoked`**：
    `ItemInvoked` 只在“用户点击 / 按回车”时触发；键盘方向键、程序化赋值 `SelectedItem`、
    以及自动化脚本的 `SelectionItemPattern.Select()` 都不会触发它 —— 表现为
    “用 UIA 选中了「外观」，左右导航高亮也变了，但右侧还是「文件列表」那一页”，
    回归脚本会把上一页的开关当成当前的（当时报“「外观」页显示 3 个开关”）。
    两个都接上也行，但只接 `SelectionChanged` 就够（它覆盖点击、键盘与程序化三种路径）。

53. **桌面上别的进程也可能有叫「设置」的顶层窗口**：本机就有
    `ApplicationFrameHost.exe`（某个 UWP 的“设置”页）带着一个标题为「设置」的窗口。
    自动化脚本按名字从 `RootElement` 找窗口时会把它当成我们的，
    于是“关掉设置窗口”永远断言不过（关的是自己的，查到的是别人的，而且它的坐标/按钮都是另一套）。
    所以 `tools/test-settings.ps1` / `shot-settings.ps1` 找窗口时都再加一道
    `ProcessId -eq $Session.Proc.Id` 过滤；同理，找菜单项（弹出的 MenuFlyout）也按进程过滤。

54. **拖放事件的 `OriginalSource` 是“带 `AllowDrop` 的那个元素”，不是鼠标下面的那一行**：
    给侧边栏树做“拖到收藏夹分组上收藏”时，原本把 `DragOver` 挂在 `TreeView` 上、再用
    `FindSidebarNodeForDrag(e.OriginalSource)` 反查行，结果日志里 `e.OriginalSource` 永远是
    `TreeView`（`source=TreeView`）——因为拖放落点就是**带 `AllowDrop` 的元素**，子元素没写
    `AllowDrop` 就永远收不到。
    （指针事件不是这样：指针会命中最深层的元素，所以第 40/45 条那套 `OriginalSource` 反查对
    `RightTapped`/`DoubleTapped` 有效，对 `DragOver`/`Drop` 无效。）
    修法：把 `AllowDrop` + `DragOver`/`DragLeave` 写到行模板根那一行（`ListView.ItemTemplate` 的
    `Grid` / `TreeView.ItemTemplate` 的 `TreeViewItem`）上，用 `sender` 或
    `TreeView.ItemFromContainer(sender)` 反查数据项。
    另外 `TreeViewList` 会在自己的类处理器里按“没开重排”把 `AcceptedOperation` 写成 `None`：
    行上接受不等于最终接受，必须再在 `TreeView` 上 `AddHandler(..., handledEventsToo: true)`
    接一次 `DragOver`（在路由最后重新赋 `Copy`）与 `Drop`，否则拖过去高亮会亮、松手却什么都不发生。

55. **标签的圆角要改 `TabViewItem.CornerRadius`，不要去改 `OverlayCornerRadius`**：
    当前 WinAppSDK 的 `TabViewItem` 模板里，标签底色那个 `TabBackground` 写的是
    `CornerRadius="{TemplateBinding CornerRadius}"`（旧版模板是 `Binding ... ThemeResource OverlayCornerRadius`），
    所以给 `TabViewItem` 设 `CornerRadius`（本仓库是从 `FolderTabViewModel.TabCornerRadius` 用
    `{x:Bind}` 绑上去的 `Control.CornerRadius`）就能把上面两个角变成直角，而且属性一变当场重画，
    不用重建标签页；改 `OverlayCornerRadius` 会影响所有弹层（圆角是全局共用的一份）。
    * “圆角”应当填 `(8,8,0,0)`：模板默认值就是 `OverlayCornerRadius`(8) 经
      `TopCornerRadiusFilterConverter` 只保留上面两角；下面两角始终是直角（标签下面就是窗格内容）。
      实测“标签左上角最大内缩”在直角下 3.5 DIP、在 `(8,8,0,0)` 下 6.5 DIP，与改动前的默认外观完全一致。
    * 模板里标签下方那对“倒角”`LeftRadiusRender`/`RightRadiusRender` 靠一份 **`StaticResource`**
      （`TabViewItemRadiusRenderCornerRadius`）驱动，框架字典里的 `StaticResource` 不受 App 级
      同名键影响（第 29/38 条那套只适用于 `ThemeResource`），所以**改不了也无需改**：
      直角模式下它只是把标签条底部那条边接平，肉眼看不到残留圆弧。
    * 验证手段：四角半径不在 UIA 里，所以自动化只能靠截图量像素。在标签页的 UIA 矩形左上角
      取一小块，逐行找“第一个标签底色像素”（lum ≥ 246）相对左边缘的内缩，取最大值：
      直角 ≈ 3.5 DIP、圆角 ≈ 6.5 DIP（那 3.5 是标签顶边 1 DIP 那条淡线与抗锯齿，不是圆角）。
      UIA 只能读到标签的矩形（圆角与否它一样），所以 `tools/test-settings.ps1` 用例 8 只断言
      “拨一下就落盘 + exdir.log 里当场应用了”，真变直角/圆角靠截图人工确认。

56. **卷插拔消息只能在自家窗口上听，而且跨进程发消息时不能带 `DEV_BROADCAST_*` 指针**：
    `WM_DEVICECHANGE` 里带 `DBT_DEVTYP_VOLUME` 的到达 / 移除事件是系统**广播给所有顶层窗口**的，
    不需要 `RegisterDeviceNotification`（那是用来“按 GUID 订阅某类设备接口”的），
    所以在主窗口上 `SetWindowSubclass` 就够了；窗口隐藏到托盘不影响（它依旧是顶层窗口）。
    两条容易踩的细节：
    * `DBT_DEVICEARRIVAL` / `DBT_DEVICEREMOVECOMPLETE` 的 `lParam` 是 `DEV_BROADCAST_HDR*`，
      必须先读 `dbch_devicetype` 才知道是不是卷（同一个事件也用于设备接口、卷的句柄等）。
      **因此这类消息不能拿来跨进程 `SendMessage`**：脚本里的“假 U 盘”回归发的是
      `DBT_DEVNODES_CHANGED`（`wParam=7`、`lParam=0`，根本没有指针），否则目标进程会去读自己
      地址空间里的野指针（`Marshal.ReadInt32` 碰上无效地址直接 AV 掉进程，`try/catch` 拦不住）。
      `exdir` 把这个没有 lParam 的事件也当“卷可能变了”处理（顺带兜住“盘符可用比卷消息晚”），
      刷新本身是幂等 + 差分的，不会白刷。
    * 一次插拔常常连发好几条消息，而且 `DriveInfo` 能列出盘符的时刻比消息晚几百毫秒，
      所以刷新要“合并 + 延后 + 兜底”各来一次（`MainWindow` 里两个 `DispatcherQueueTimer`），
      收到消息就立刻枚举一次是不够的。
    刷新本身要按**差量**做：`DriveInfo` 清单没变就什么都不动，变了也只改那一个盘节点
    （`SidebarViewModel.RefreshDrives` 复用未变化的节点对象、清理 `_index`、按新顺序挪位）；
    每次插拔都 `BuildTree()` 会把用户在侧边栏里展开的目录全部折回去（整树重建的代价见第 4 节），
    也会连「收藏夹」一起重建。

57. **`CF_PREFERREDDROPEFFECT` 不是 `0x000C`**：`0x000C` 是 `CF_WAVE`（波形数据），
    “复制 / 剪切”那个标志的格式是**运行时注册**的，必须
    `RegisterClipboardFormat("Preferred DropEffect")` 拿 id 再用它当 `SetClipboardData` / `GetClipboardData` 的格式号。
    写错时的症状很有欺骗性：写剪贴板、读剪贴板都“成功”，甚至文件列表也能被资源管理器读到，
    但剪贴板里多出一个叫 `WaveAudio` 的格式，而 `DataObject.GetDataPresent("Preferred DropEffect")` 永远是 false
    （也就是剪切/复制区分彻底失效）。排查时把 `Clipboard.GetDataObject().GetFormats()` 打出来一看就知道。

58. **`ListView.CanDragItems` 的拖拽在模拟鼠标下走不完**：`DragItemsStarting` 会正常触发、
    数据包也填进去了，但之后拖拽循环收不到移动，永远没有 `DragOver`/`Drop`
    （真鼠标下未必如此，但自动化回归靠不住）。本仓库的可靠做法是**自己识别手势**：
    `CanDragItems=False`，在 `DetailsRoot` 上用 `handledEventsToo` 监听
    `PointerPressed`/`PointerMoved`/`PointerReleased`，按下时记住行与位置、移动超过 4 DIP 后
    `await element.StartDragAsync(point)`（之后照常发 `DragStarting`，在那里填 `DataPackage`）
    —— 与工具条固定目录拖拽是同一条路（见第 25 条）。
    两个容易漏的点：
    * **不要把 `PointerCaptureLost` 当成松手**：`ListViewItem` 在按下时会把指针捕获过去，
      随即发一次 `PointerCaptureLost`，那时拖拽还没开始 —— 拿它清 `_dragCandidate` 会让拖拽永远启动不了，
      而且表现为“有时能拖、有时拖不动”（取决于捕获转移的时序）。只能在 `PointerReleased`
      与 `StartDragAsync` 返回之后清。
    * 行容器取数据项要用 `ListView.ItemFromContainer(container)`：行模板用的是 `x:Bind`，
      实测 `ListViewItem.DataContext` **不是** `FileItemViewModel`（是 null），按它反查一行都命中不了。

59. **拖拽期间的 `e.GetPosition(element)` 坐标不可靠**：实测同一次拖拽里它给出的坐标与
    `GetCursorPos` 换算出来的差几十个 DIP（鼠标压在第二行上、报的却是第一行上方），
    于是“行高亮着、松手却什么都没发生”（要么判到空白处、要么判成不合法目标）。
    拖放要用**鼠标指针自己算**：`Helpers.DpiHelper.GetCursorPosition(element, hwnd)`
    （`GetCursorPos` → `ScreenToClient` → 除以 `XamlRoot.RasterizationScale` → 减去元素在客户区里的原点，
    就是 `ToScreenPoint` 的逆运算）。拖动期间鼠标指针就是拖放位置，所以这样算永远是对的。

60. **拖放事件的坐标/落点不能用 `e.OriginalSource`，还要防“框架有时不冒泡 `Drop`”**：
    拖放事件的源永远是**带 `AllowDrop` 的那个元素**（见第 54 条）；而且把 `AllowDrop` 放在
    `ListViewItem` 这一级时，`ListViewBase` 自己会把 `Drop` 吃掉（`CanReorderItems=False` 时既不重排也不冒泡），
    于是“行高亮着、松手却什么都没发生”。现在的做法是：容器 `AllowDrop=False`，
    整个文件列表只有 `DetailsRoot` 一个落点（指针在行间移动时目标不会变），
    鼠标下是哪一行用光标位置 + 容器实际矩形算。另外在 `StartDragAsync` 返回后还留了一道兜底：
    左键已经松开、光标又在本列表里、而这次拖拽的 `Drop` 没被处理过 →
    自己按光标位置把这次移动做完（左键还按着说明用户是按 Esc 取消的，不做）。

61. **`SHFileOperation(FO_DELETE)` 的 `pTo` 必须传 `null`，而且要带上 `FOF_WANTNUKEWARNING`**：
    `FO_DELETE` 根本不看 `pTo`，但给一个空串（而不是 NULL）会让外壳把参数当成非法目标
    返回一个错误码（删除“失败”，文件其实没动）；多字符串部分的 `pFrom` 仍要 `\0` 结尾 + `\0` 收尾。
    另一个坑是“装不进回收站的文件”：只带 `FOF_ALLOWUNDO` 时，太大 / 所在卷没有回收站的文件会被
    **静默永久删除**，用户以为只是丢进了回收站 —— 必须加上 `FOF_WANTNUKEWARNING`（0x4000）
    让外壳先警告一次。
    顺带：删除的确认框是**本进程**的模态窗口（`SHFileOperation` 在 STA 线程上弹的 `#32770`），
    它**不在桌面 UIA 子窗口那一层**（是被主窗口 owned 的），只在主窗口的 `Descendants` 里能找到 ——
    所以自动化脚本 `TreeScope.Children` 会“看不到确认框”，删除就永远卡在 STA 线程上；
    改成从 `Session.Root` 按 `Descendants` + `ProcessId == exdir` 找、点它的“是(Y)”即可
    （见 `tools/test-file-ops.ps1` 的 `Confirm-ShellDialog`）。而“有没有真的进回收站”不要去翻
    `$Recycle.Bin` 目录，用 `Shell.Application` 的 `NameSpace(10)` 读（与资源管理器左侧「回收站」同一份），
    而且用例文件名要每次唯一，否则上一次跑剩下的同名项会让断言假通过。

62. **带 `Shift` 的 `KeyboardAccelerator` 不触发，改用隧道事件自己判修饰键**：
    `Delete` 进回收站用 `KeyboardAccelerator Key="Delete"` 正常，但再给它配一个
    `Key="Delete" Modifiers="Shift"` 时，真实按 Shift+Delete（`SendKeys` 的 `+{DEL}`、以及
    `keybd_event` 都试过）**两个加速器都不会 Invoke**（不是匹配错命令，是根本没反应），
    而 `PreviewKeyDown`（隧道事件，一定先于列表/列头拿到这个键）能正常收到这个 Delete，
    所以在那里用 `InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)` 判 Shift 最稳：
    ※ 用这个 API 而不是 `GetAsyncKeyState`：后者是“此刻的物理键状态”，而进程从消息队列里
    取出这条键盘消息时用户（或脚本）可能已经松开 Shift 了；`GetKeyState` 一套给的是“该消息创建时”的状态。

63. **Windows「网络位置」是快捷方式容器目录，而且会被“只按磁盘重建”的刷新逻辑误删**：
    「网络位置」（资源管理器里「添加一个网络位置」造出来的）在磁盘上是
    `%APPDATA%\Microsoft\Windows\Network Shortcuts\<名字>\target.lnk`，
    目录名就是显示名，打开位置就是 `target.lnk` 的目标（通常 `\\server\share`）。
    解析要 `IShellLinkW`（`IPersistFile.Load` + `GetPath`），
    **所有字符串参数必须显式 `[MarshalAs(UnmanagedType.LPWStr)]`**：不标的话 COM 默认按 ANSI 传，中文路径会乱码；
    只想用 `GetPath` 也得把 `IShellLinkW` 的 18 个方法按 vtable 顺序写全。
    另：`target.lnk` 是隐藏文件、`desktop.ini` 只是壳的元数据，别把它们当“位置”列出来。
    最容易踩的坑在 `SidebarViewModel.RefreshDrives`：它按磁盘清单重建「此电脑」的期望子节点，
    然后“摘掉不在期望清单里的节点”——不把网络位置一并加进去，第一次 U 盘插拔（或「重新扫描磁盘」）
    就会把网络位置全删掉（节点从界面上消失，却看不出是刷新干的）。
    回归：`tools/test-network-locations.ps1`。

64. **NativeAOT 在 WinAppSDK 2.5.1 + WinUI 3 下用不了（2026-09 实测穷举过）**：
    AOT 能编译、能产出（自包含 72 MB / 框架依赖 13 MB，都比裁剪版小），
    但**启动后约 30 ms（主窗口已构造、`Activate()` 已返回、`RootGrid.Loaded` 之前）进程就 fail-fast**：
    退出码 `0xC000027B`（stowed exception），事件日志写的是
    `故障模块 Microsoft.UI.Xaml.dll (3.2.3.0)`，`Windows.ApplicationModel.LimitedAccessFeatures` 的
    ClassFactory 报 `0x80040111`；`Application.UnhandledException` 里拿到一个没有堆栈的 COMException，
    设 `e.Handled = true` 也拦不住（native fail-fast）。
    试过且都不行的变体：自包含 / 框架依赖（本机装了 WindowsAppRuntime.2 2.5.1）、net8.0 / net10.0、
    `CsWinRTAotWarningLevel=2`、`AllowUnsafeBlocks`、`BuiltInComInteropSupport=true`、
    换掉 Mica 背景、`CsWinRTUseWindowsUIXamlProjections`、只开裁剪不开 AOT。
    **关键证据：这个报出来的类名（`LimitedAccessFeatures`）在整包任何文件里都搜不到（UTF-8/UTF-16 都搜过），
    自己的代码也完全没碰过它，而同一个进程只要不开 AOT（哪怕同样裁剪）就一切正常** ——
    所以它不是本仓库代码的问题，是 WinAppSDK/WinUI 在那条路径下的 bug（上游同类报告：
    WindowsAppSDK#3905 / #6058：`WindowsAppSDKSelfContained` + 单文件/AOT 启动报 `80040111`）。
    真要上 AOT 得先等上游修，别把时间花在改我们的 XAML 上。
    （想要“小而快”就开裁剪，见下一条；裁剪已经把 225 MB 干到 83 MB。）

65. **裁剪（`PublishTrimmed`）能开，但四个开关必须同时到位**：
    ```xml
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>                      <!-- 不能是 full -->
    <CsWinRTAotWarningLevel>2</CsWinRTAotWarningLevel>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    ```
    * **`TrimMode=full` 会在“配置 → 设置…”上一打开就 0xC0000005 崩在 coreclr.dll**
      （访问冲突、没有托管异常、也没进 exdir.log，只能靠事件日志看出来）：
      设置卡片 / NavigationView 那边有东西是裁掉就找不到的。`partial` 只裁带
      `[AssemblyMetadata("IsTrimmable","True")]` 的程序集（BCL），第三方/工具包原样保留，
      体积上差别只有 2 MB（81 MB vs 83 MB）。
    * **`CsWinRTAotWarningLevel` 必须是 2**：它让 CsWinRT 源生成器跑在 **Auto** 模式，
      为“会跨 WinRT ABI 的泛型实例化”（例如 `ItemsControl.ItemsSource` 绑一个
      `ObservableCollection<T>`）生成 vtable。只给默认的 level 1 时生成器可能停在 OptIn 模式，
      CCW 查 vtable 拿到 null，启动后第一次设 `ItemsSource` 就
      `NullReferenceException @ WinRT.TypeExtensions.GetAbiToProjectionVftblPtr`。
    * **`AllowUnsafeBlocks`**：上面生成出来的代码用 unsafe。
    * **`BuiltInComInteropSupport`**：老式 COM interop 在裁剪/ AOT 下默认关掉，
      而剪贴板 OLE、外壳右键菜单（`IShellFolder`/`IContextMenu`）、云占位符属性存储（`IPropertyStore`）、
      `.lnk` 解析（`IShellLinkW`）全靠它。关掉之后的症状是这些功能一用就挂，而不是编译报错。
    * 不指望编译器帮忙把关：裁剪只在 publish 阶段发生，**改完必须重新 `tools\publish.ps1` 再跑一遍相关回归**
      （`tools\test-*.ps1 -Exe dist\win-x64\exdir.exe`，默认的 `-Exe` 是 Debug 未裁剪版，测不出问题）。

66. **裁剪过的发布版里，反射式 `JsonSerializer` 会静默失改 —— settings.json 读写得走源生成**：
    症状极具迷惑性：设置窗口里拨开关**界面当场生效**，但 `settings.json` 没变，重开又变回去；
    而 `SettingsService` 为了保证“设置坏了也不阻塞启动”是把异常吞掉的，所以**一行日志都没有**。
    实测裁剪后 `tools\test-settings.ps1` 挂 9 条断言，全是“没落盘”。
    修法：`Services/SettingsJsonContext.cs`（`[JsonSerializable(typeof(AppSettings))]`）+
    `JsonSerializerOptions.TypeInfoResolver`，并且用 `JsonSerializer.Serialize(obj, JsonTypeInfo)` /
    `Deserialize(json, JsonTypeInfo)` **而不是泛型重载**（泛型重载的“要求未裁剪代码”标记与
    TypeInfoResolver 无关，IL2026/IL3050 会一直在）。
    另：`Load()` / `Save()` 的 catch 里现在会 `Log.Exception` —— 就是为了这种“静默失败”能留下线索。
    **以后新增会写进 settings.json 的类型，要往那个 context 里加 `[JsonSerializable]`。**

67. **WinAppSDK 的两种“精简”各有官方钩子，不要用事后删文件代替**：
    * **语言资源（85 个 `*.mui` 目录）**：用 `MicrosoftWindowsAppSDKFilesExcluded`
      （`Microsoft.WindowsAppSDK.SelfContained.targets` 里就用它做 `Remove`）在
      `AddMicrosoftWindowsAppSDKPayloadFilesFromComponents` 之前把不要的语言剔掉，
      见 `exdir.csproj` 的 `ExcludeUnneededWinAppSdkLanguageResources`；这样 bin 与 dist 两边都干净。
      本仓库只留 `zh-*` / `en-*`（界面文案是简体中文硬编码，框架资源用不到其它语言）。
    * **整个组件包**：`Microsoft.WindowsAppSDK` 2.5.1 是元包，AI/ML/Search/Widgets 四个组件
      （约 55 MB：`onnxruntime.dll` 20 MB + `DirectML.dll` 18 MB + `Microsoft.Windows.Search.dll` …）
      exdir 一行都没用。用 `ExcludeAssets="all"` 把它们自己的全部资产排除：组件的 props 不再被导入
      → 不会往 `WindowsAppSdkComponentPackages` 里登记 → 自包含部署就不会拷它们的负载。
      两个坑：① **不能把元包删掉改成“只引用需要的组件包”** —— H.NotifyIcon 与 CommunityToolkit
      的 SettingsCard 都声明了 `Microsoft.WindowsAppSDK >= 1.6`，没有这份直接引用 NuGet 会去装那份
      1.6 的旧包，它的 props 和 WinUI 2.3.9 重复导入，构建直接报错；
      ② 还得排除 **`Microsoft.Windows.AI.MachineLearning`**（`onnxruntime.dll`/`DirectML.dll` 其实是
      它 `runtimes\win-x64\native` 里的），它自己的 targets 会因 `_WindowsAppSDKML` 没被置位而报
      “需要 19H1+”，所以那份排除不是可选优化。

68. **切到浅色主题才看得出来：NavigationView 左侧导航是半透明的，窗口背后必须有东西可采样**：
    exdir 的浅色主题（标题栏那个太阳 / 月亮开关，见第 4 节“主题”）一加就发现设置窗口的左侧导航
    是一块**纯黑底 + 深色文字**（看不见导航项），而右侧卡片、窗口标题栏都是正常的浅色。
    逐像素量过：深色主题下导航区也是 `#000000`，同一窗口的内容区是 `#1D1D1D` —— 也就是说
    那块黑底一直在，只是深色主题下看不出来。原因是 `NavigationView` 的窗格用的是“应用内亚克力”
    （半透明，采样同一窗口里它背后的内容），而 `SettingsWindow` 没有 `SystemBackdrop`，
    背后就是窗口自身的黑色底（`SettingsView` 的根就是那个 `NavigationView`，没有别的背景层）。
    修法：给 `SettingsWindow` 加上和主窗口一样的
    `<Window.SystemBackdrop><MicaBackdrop Kind="Base" /></Window.SystemBackdrop>`：
    窗格改成采样 Mica 背板，深浅两套主题下都跟主窗口侧边栏一致。
    教训：**浅色主题下“某块区域黑得不对劲”先怀疑“半透明控件背后没有背板”**，
    而不是去翻主题资源字典（主题资源其实都是对的，`ThemeDictionaries` 里 Light 一套也生效了）。
    另：本机系统就是深色（`AppsUseLightTheme=0`），所以“浅色 exdir + 深色系统”这种组合必须特地
    把 `settings.json` 的 `Theme` 改成 1（或点标题栏开关）才能复现 —— 验证换肤时两套主题都要看。

## 7. 非打包模式下的 API 限制

没有 Package Identity，因此**不要**使用：`Windows.Storage.KnownFolders`、
`ApplicationData.Current`、`StorageLibrary` 等依赖包标识的 API。
已用的等价 Win32/注册表方案见 `KnownFolderService`：

* 标准目录 → `Environment.GetFolderPath(SpecialFolder.*)`；
* OneDrive → 环境变量 `OneDrive*` + `HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder`；
* 其它云盘 → `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\*\UserSyncRoots`
  （值名是 SID，值是路径；显示名优先取路径最后一段，注册表项名只是内部标识）。

## 8. 当前状态（本文件编写时）

已完成（主体框架，可运行，Release 产物已生成）：

* **交付产物精简到 83 MB**（2026-09，见第 2 节“任务收尾”与第 6 节第 64～67 条）：
  `tools\publish.ps1` 改成「`dotnet publish`（开裁剪）+ 从构建输出补回 `.pri`/`.xbf` + 镜像」，
  dist 从 **225 MB / 548 个文件** 降到 **83 MB / 165 个文件**：
  ① 去掉完全用不到的 WinAppSDK AI / ML / Search / Widgets 四个组件（约 55 MB：
  `onnxruntime.dll`、`DirectML.dll`、`Microsoft.Windows.Search.dll`、Widgets…）；
  ② 语言资源只留中/英（85 个 `*.mui` 目录 → 4 个，3.3 MB）；
  ③ `PublishTrimmed`（`TrimMode=partial`）裁掉用不到的 BCL（约 97 MB）。
  功能未退化：`test-settings` / `test-file-ops` / `test-tray` / `test-shell-icons` /
  `test-list-selection` / `test-row-dblclick` / `test-column-resize` / `test-pin-drag` /
  `test-status-bar` / `test-drive-hotplug` / `test-network-locations` / `measure-row-align`
  全部在 `dist\win-x64\exdir.exe` 上重跑通过（`test-context-menu` 的 4 条失败在未裁剪版上一样挂，属仓库现有待办）。
  **NativeAOT 不可用**（共试了自包含/框架依赖、net8/net10、各类 CsWinRT/COM 开关），
  原因不在本仓库，见第 6 节第 64 条。
* **单窗口 + 常驻托盘**（2026-09，见第 4 节“托盘驻留”）：点关闭按钮 / `Alt+F4` 只把窗口隐藏到
  通知区域，进程、两个窗格、标签页与会话全部留着，再打开（左键点托盘图标 / 第二次双击 exe）
  就是一次 `ShowWindow`；托盘右键菜单是「显示主窗口 / 退出 exdir」，菜单里的「退出」才是真退出；
  单实例闸门跑在 XAML 初始化之前（`Program.cs` + `Helpers/SingleInstance`），第二次双击 exe 只跑几十毫秒。
  依赖 `H.NotifyIcon.WinUI 2.3.2`（见 exdir.csproj）；回归：`tools/test-tray.ps1`（4 个用例 17 条断言）。
* **磁盘热插拔实时刷新**（2026-09，见第 4 节与第 6 节第 56 条）：插上 U 盘 / 光驱换盘 / 映射网络盘后，
  工具条磁盘区与侧边栏「此电脑」分组**立刻**出现新盘，拔掉立刻消失（窗口藏在托盘里也照样更新）；
  实现是 `IDeviceChangeService` + `Services/Native/VolumeChangeWatcher`（`WM_DEVICECHANGE` 子类化，
  卷到达 / 移除 + 设备树变化）→ `MainWindow` 的两次延迟刷新（600 ms 合并 + 2500 ms 兜底）→
  `MainViewModel.RefreshDrives` / `SidebarViewModel.RefreshDrives` 的差量更新
  （不重建整棵树，展开状态与收藏夹都不受影响）；「工具 → 重新扫描磁盘」与之同一入口，
  磁盘清单变化时「转到 → 所有位置」也会重列；
  回归：`tools/test-drive-hotplug.ps1`（3 个用例 12 条断言，`subst` 造盘符 + `SendMessage`，不需要交互桌面）。
* **侧边栏「此电脑」里列出 Windows「网络位置」**（2026-09）：`%APPDATA%\Microsoft\Windows\Network Shortcuts`
  下的每个快捷方式（「添加一个网络位置」造出来的）作为「此电脑」里**排在磁盘之后**的节点，
  显示名是容器目录名、点击进入 `target.lnk` 的目标（`IShellLinkW` 解析，UNC 也能走现有导航与面包屑）；
  离线的网络位置照样列出，打开时才报错；
  `SidebarViewModel.RefreshDrives` 的差量刷新一并带上它们（否则一次插拔就没了）；
  回归：`tools/test-network-locations.ps1`（4 个用例 5 条断言，不需要交互桌面）。
* **复制 / 剪切 / 粘贴 / 删除 + 拖动移动**（2026-09，见第 4 节同名小节与第 6 节第 57—62 条）：
  内置右键菜单里多了「剪切 / 复制 / 粘贴 / 删除」（快捷键 `Ctrl+X` / `Ctrl+C` / `Ctrl+V` / `Delete`，
  永久删除是 `Shift+Delete`，前三个挂在 `DetailsView` 根 Grid 的加速器上、后两个走 `PreviewKeyDown`，
  地址栏里仍是文本框自己的行为）；
  剪贴板用的是系统标准格式
  `CF_HDROP` + `Preferred DropEffect`，因此**与资源管理器 / 7-Zip 双向互通**
  （在资源管理器里复制一批文件，回到 exdir 按 `Ctrl+V` 就能粘；反之也一样；剪切粘完只生效一次）；
  真正的复制 / 移动 / 删除交给外壳的 `SHFileOperation`（`FO_COPY` / `FO_MOVE` / `FO_DELETE`），
  所以有资源管理器同款的进度对话框、同名冲突询问与删除确认框，删除默认**进回收站**（`FOF_ALLOWUNDO`，
  可从回收站恢复），只有 `Shift+Delete` 才是永久删除；
  完成后按“源目录 + 目标目录”刷新受影响的标签页（拖到另一个窗格也跟得上，
  被删目录里开着的标签页则退到上一级）；
  文件列表里的文件 / 目录可以直接拖到某个**目录行**上（鼠标下那一行整条高亮）、拖到列表空白处
  （= 当前目录）或拖到另一个窗格即**移动**，按住 `Ctrl` 是复制；
  回归：`tools/test-file-ops.ps1`（9 个用例：菜单项 / 剪贴板互读 / Ctrl+C+V 复制 / Ctrl+X+V 移动 /
  拖到目录行移动 / 外部来源复制进来 / 外部来源剪切进来 / Delete 进回收站 / Shift+Delete 永久删除）。
* 非打包工程改造、单实例主窗口、Mica 背景、自定义标题栏、图标与窗口位置持久化；
* 磁盘条、固定目录、快捷菜单（按需求留空，仅设置驱动）、侧边栏文件夹树（懒加载）；
* 1/2 窗格 + 自研分隔条、TabView 多标签；
* 详细信息列表：**目录可就地展开的树形列表**（行内箭头 / `←→` 方向键展开、懒加载、刷新后恢复展开）、
  状态/名称/修改日期/类型/大小五列、点列头排序（含树的每一层）、**列宽可拖动+双击复位+持久化**、
  多选、双击进入目录、无选中蓝色竖条、选中行不随排序/刷新丢失；
* **行高可配**（2026-09，见第 4 节“行高可配”）：设置窗口「文件列表 → 行高」滑块（20~48 DIP、步进 2，
  默认 **28 DIP**——比原来固定的 24 更舒展，仍属紧凑密度）；列头保持 26 DIP 不变，
  图标与名称文字在任何行高下都行内垂直居中；回归：`tools/test-settings.ps1` 用例 6（UIA 量数据行高度）；
* **行首显示真实的外壳图标**（2026-09，S14，见第 4 节“名称列图标”）：`.exe` / `.lnk` 各自显示
  程序自带的图标（快捷方式还带小箭头覆盖层），文件夹与文件类型与资源管理器一致；
  字形只在“图标还没取到 / 系统里查不到”时兜底；图标懒加载 + 两级缓存，滚动不重复取；
  图标与名称文字垂直居中对齐（行盒降部导致的 1 DIP 视觉差用 `TranslateTransform` 补掉了）；
  回归：`tools/test-shell-icons.ps1`（13 条断言全过）、`tools/measure-row-align.ps1`（对齐偏差在中位数 3 物理像素内）；
* **云文件夹的同步状态列**（2026-09，见第 4 节“状态”列）：位于云同步根（OneDrive / WPS 云盘 /
  其它 CFAPI 同步目录）下的目录会在最前面多出一列图标（已同步 / 仅在云端 / 已固定 / 正在同步 /
  同步错误 / 未同步），数据来自系统属性 `System.StorageProviderState` +
  `System.FilePlaceholderStatus`（属性系统读不到时退回占位符属性位），不弹窗、不下载内容；
  非云目录整列隐藏，普通目录的枚举不为此多花一次系统调用；
* 导航条（← → ↑ ⟳ + **面包屑地址栏**）在**每个标签页内部**（`Views/NavigationBarView` +
  `Views/PathBreadcrumb`），标签页之间历史与编辑态互不影响；
  路径按目录分段显示（chevron 分隔）、点分段跳转、点当前目录段或右侧空白区就地编辑，回车跳转、Esc 取消；
  超长路径自动滚到最右（当前目录永远可见）并在左端提示省略，编辑入口也有 `Ctrl+L` / `Alt+D`；
* 前进/后退/上一级历史、路径框回车跳转、显示隐藏文件、显示扩展名、会话恢复；
* **所有配置项集中在设置窗口**（2026-09 从 ContentDialog 改成独立窗口，S17d）：菜单栏「配置 → 设置…」
  打开 `Views/SettingsWindow`（`Window`，默认 860×800、工作区居中，同一时刻只开一个）。
  左侧 `NavigationView` 五个分类（文件列表 / 外观 / 布局 / 侧边栏 / 右键菜单），右侧一列 Windows 11 风格设置卡片
  （社区工具包 `SettingsCard`，不再自己写行模板），只显示当前分类那一页；
  前四个分类共 21 项：文件列表（显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面 / **行高**）、
  外观（**主题**：跟随系统 / 浅色 / 深色，默认跟随系统；过渡动画 / **标签页使用直角**，默认开）、布局（列宽自动适应窗格宽度 / 显示工具条 / 显示侧边栏 / 双窗格模式）、
  侧边栏（显示「主目录」/「收藏夹」/「云存储」/「此电脑」四个分组，均默认开；
  以及「主目录」里显示哪几个标准文件夹——桌面 / 文档 / 下载 / 图片 / 音乐 / 视频，**默认只开桌面与下载**）；
  「右键菜单」页除了「使用内置的轻量右键菜单」这一个开关（默认开）之外，
  还是系统菜单项的动态清单（见下面那条与第 4 节）。
  **改动即时生效并立即落盘**（没有「保存 / 取消」）：`SettingsViewModel.Changed` →
  `MainViewModel.ApplySettings`（写 `AppSettings` + 刷界面 + `Save()`）。
  新增固定配置项要同时改 `AppSettings`、`SettingsViewModel`、`SettingsView.xaml`、`ApplySettings`
  与 `tools/test-settings.ps1` 的 `$KeyMap` / `$CategoryMap`（见第 4 节）。
  回归：`tools/test-settings.ps1`（9 个用例：分类齐全 / 每页只显示本分类的项 / 初始值一致 /
  拨一下立即落盘并作用到文件列表 / 跨分类与关窗重开读回 / 同一时刻只开一个窗口 /
  菜单风格开关默认开（内置）且拨一下就落盘、系统菜单项默认全开且能逐项关闭 /
  行高滑块改完列表行真的变高 / 侧边栏分组开关改完树里真的少一个分组 /
  「主目录」标准文件夹开关改完（打开「文档」真的出现、关掉「桌面」真的消失）/
  标签页直角默认开、拨一下就当场应用 /
  主题下拉框三态 + 标题栏的太阳 / 月亮开关改的是同一个设置），全程走 UIA 模式（不需要前台窗口）；
  布局截图：`tools/shot-settings.ps1`（.artifacts\settings-<分类名>.png，需要交互桌面）。
  原先的「配置 → 文件列表」子菜单（三个 `ToggleMenuFlyoutItem`）已移除，
  「查看」菜单里的工具条 / 侧边栏 / 双窗格三项保留（与设置窗口共享同一份设置）；
* **右键菜单有两种风格**（2026-09，见第 4 节“右键菜单”）：文件列表里右键行 → 该（批）条目的菜单，
  右键空白处 → 当前目录的背景菜单；用哪一种由「设置 → 右键菜单 → 使用内置的轻量右键菜单」决定
  （**默认开 = 内置菜单**）。
  * **内置菜单（默认，快）**：`Views/DetailsView` 现场搭的 WinUI `MenuFlyout`，只含 exdir 自己的命令 ——
    文件行是「打开 / 在资源管理器中显示 / 复制路径 / 属性」，背景是
    「新建文件夹 / 刷新 / 全选 / 复制当前路径 / 在此处打开终端」；不碰 COM，弹出几乎瞬时。
    `新建文件夹` 是 exdir 自己建目录（`FolderTabViewModel.CreateNewFolderAsync`），
    `属性` 走 `IShellService.ShowProperties`；
  * **系统菜单（关掉开关才用，慢但完整）**：内容来自系统外壳（7-Zip / Git / VS Code / WPS /
    打开方式 / 发送到 等第三方项与子菜单全在），默认**全部开启**，可在同一页里逐项关掉
    （关掉的项在弹出前从 HMENU 里删掉）。服务端是 `IShellContextMenuService` +
    `Services/Native/ShellContextMenuInterop`（`IContextMenu` + `IContextMenu2/3` + `SetWindowSubclass`）；
    清单 = 打开设置页时的样本枚举 ∪ 实际右键过的项（`AppSettings.ShellMenuKnownItems`）。
  * 回归：`tools/test-context-menu.ps1`（5 个用例：系统菜单 3 个 + 内置菜单 2 个）；
    顺带修掉两个拦路的既有 bug：`TabView` 被 WinUI 默认样式压成 `VerticalAlignment=Top`
    （文件列表只占“内容那么高”）与窗口位置/尺寸存坏（见第 6 节第 39/41 条）；
* **工具条“固定目录”支持拖放固定**（2026-09）：文件列表 / 侧边栏树里的目录可以直接拖到工具条右侧的
  固定目录区（拖拽时强调色高亮 + “固定到工具条”提示，松手即写 `settings.json`），
  右键固定目录按钮可“取消固定”；最多固定 12 个（`MainViewModel.MaxPinnedFolders`）；
  验证脚本 `tools/test-pin-drag.ps1`（列表 → 固定、侧边栏 → 固定、右键 → 取消固定 三个用例）；
* **工具条固定目录支持拖拽排序**（2026-09）：按住固定目录按钮横向拖到兄弟按钮上就换位
  （拖动时按钮之间画 2px 强调色插入位置提示条 + “调整固定目录顺序”提示，松手即写 `settings.json`）；
  实现见第 4 节“拖拽排序”与第 6 节第 25 条（`Button` 的 `CanDrag` 在 WinUI 3 里无效，
  要在 `PinnedItemsHost` 上 `AddHandler(..., handledEventsToo: true)` + `StartDragAsync`），
  排序落点走 `MainViewModel.MovePinnedFolder`；`tools/test-pin-drag.ps1` 第 4 个用例验证
  （拖 Documents 到 Downloads 右半边 → 顺序互换）；
* **侧边栏「收藏夹」镜像工具条固定目录**（2026-09，见第 4 节“收藏夹”那条）：它排在侧边栏**最上面**
  （在 `主目录` 之上），子项与工具条固定目录同序同名（增删/排序即时同步）；把目录从文件列表或侧边栏
  拖到「收藏夹」分组或其子行上即收藏（悬停整行强调色高亮），右键收藏项可「取消收藏」；
  行级落点判定 + `TreeView` 层的 `handledEventsToo` 兜底见第 6 节第 54 条；
  回归：`tools/test-pin-drag.ps1` 用例 0（镜像一致）与用例 5（拖到收藏夹）；
* **侧边栏「主目录」里显示哪些标准文件夹可配**（2026-09，见第 4 节“侧边栏分组”那条）：
  设置窗口「侧边栏」页里为桌面 / 文档 / 下载 / 图片 / 音乐 / 视频各一个开关，
  **默认只开「桌面」与「下载」**（`AppSettings.SidebarHomeDesktop` 等六个）；
  筛选按 `UserFolderKey`（稳定标识）做；改完即时增删树里的子项，复用的节点保留展开状态；
  回归：`tools/test-settings.ps1` 用例 7；
* **悬停/按下/选中高亮统一改成强调色**（2026-09）：原来是 WinUI 默认的 8% 白（灰底上几乎看不出来），
  现在工具条按钮悬停是强调色 35%（深色）/ 25%（浅色）、按下 60% / 50%；
  文件列表行 / 侧边栏树 / 标签页头 / 菜单项等大表面用低一档的 `ExSurface*`，
  选中态也是强调色、比悬停略重（层次：选中 > 悬停），见第 4 节；
  验证：`tools/inspect-ui.ps1 -Hover Documents` / `-HoverAt "608,191"` 截图对比；
  例外：地址栏左侧的面包屑分段与文件列表行内的展开箭头保持普通灰色悬停，见第 4 节 `ExSubtleButtonStyle`；
  地址栏右侧的空白区（可点进编辑态）连灰色都不要（`ExGhostButtonStyle`，悬停/按下无底色）；
* **文件列表区底部状态栏**（2026-09，S3）：一行高（`ExRowHeight`=24 DIP）、贴底显示，
  横跨 1~2 个窗格（侧边栏保持全高），窗格占满其余高度；左边 `N 项`，
  中间是选中摘要 + 合计大小（只统计文件），右边是当前卷的可用 / 总容量；
  跟随**活动窗格**（F6 切换、切标签页、换目录、改选中都会刷新）。
  回归：`tools/test-status-bar.ps1`（16 条断言全过）。注意第 4 个用例是拿 `Get-ChildItem $env:TEMP`
  的数量去比对状态栏的“N 项”，而 %TEMP% 里的东西随时在变（构建产物、XAML 编译器临时目录、
  系统临时文件……），偶发差 1 项就 FAIL —— 用 `git stash` 在改动前复现过同样的失败，属脚本自身的
  时序问题，与产品代码无关，重跑即可（要根治得改用自己造的固定目录）。
* **标签条紧凑化**（2026-09）：标签栏高 24 DIP（含底部 1 DIP 的标签条分隔线）、顶到窗格顶部齐平，
  见第 4 节“标签条紧凑”与第 6 节第 36 条；双窗格（含标签溢出时的 ◀ ▶）两边标签栏高度一致。
  验证：`tools/capture.ps1` 截图后量像素（标签条上边紧贴窗格上边框、总高 48 物理像素 @200%），
  `tools/inspect-ui.ps1 -Filter <标签名>`（`TabItem` 高 48 物理像素 = 24 DIP）。
* **标签页默认是直角**（2026-09，见第 4 节“标签的圆角是直角”与第 6 节第 55 条）：
  标签头上面两个角不再用 WinUI 默认的 8 DIP 圆角，而是直角（与紧凑的 24 DIP 标签条更协调）；
  想回到系统默认的圆角就在「设置 → 外观 → 标签页使用直角」里关掉（`AppSettings.SquareTabCorners`，
  默认 true），改完当场重画、无需重启；
  验证：`tools/capture.ps1` 截图量像素（标签左上角最大内缩：直角 ≈ 3.5 DIP、圆角 ≈ 6.5 DIP），
  回归：`tools/test-settings.ps1` 用例 8。
* **深色 / 浅色主题**（2026-09，见第 4 节“主题”与第 6 节第 68 条）：
  标题栏左侧（「文件」菜单左边）多了一个太阳 / 月亮图标开关，点一下就在浅色 / 深色之间切；
  设置窗口「外观 → 主题」里还有三态的「跟随系统 / 浅色 / 深色」（`AppSettings.Theme`，默认跟随系统，
  即保持改动前的行为）；两个入口共用同一个值，改完当场重画（无需重启）并立即落盘。
  实现只有一处：给 `RootGrid` 设 `RequestedTheme`（根元素一变，菜单栏 / 工具条 / 侧边栏 / 窗格 /
  状态栏 / 弹层全部跟着换）；系统窗口按钮要自己上色（详见第 4 节）。
  设置窗口是另一个 `Window`，自己接一份主题，而且顺带修掉了它在浅色主题下左侧导航“黑底黑字”
  的问题（加 Mica 背板，见第 6 节第 68 条）。
  回归：`tools/test-settings.ps1` 用例 9（13 条断言：下拉框三态、标题栏开关、两个入口互相同步、
  落盘与当场应用）；两套主题的渲染用 `tools/capture.ps1` / `tools/shot-settings.ps1` 截图人工确认。
* **选择**（2026-09）：单击文件列表空白处（列头以下、任何一行之外）取消选择并把焦点留在列表上；
  `Ctrl+A` 全选列表里当前可见的行（就地展开出来的子行也算）；文件列表里**双击行的任意位置**
  （不只是名称文字 / 图标那一小块）都进入目录 / 打开文件——行内空白处命中的是 `ListViewItem`，
  所以双击处理器挂在 `DetailsView` 最外层 Grid 上反查行（见第 4 节“双击的命中范围”与第 6 节第 45 条）；
  `Ctrl+A` 加速器挂在 `DetailsView` 根 Grid 上，只作用于文件列表 —— 焦点在地址栏时 `Ctrl+A` 仍是文本框自己的全选（见第 4 节“选择”）；
  回归：`tools/test-list-selection.ps1`（5 个用例 11 条断言，真鼠标点击 + `SendKeys`）、
  `tools/test-row-dblclick.ps1`（8 个用例 28 条断言，真鼠标双击：6 个落点都能进目录 +
  展开箭头不进目录 + 列表下方空白处不导航；在改动前的版本上跑会挂 2 个落点）；
* **列宽拖动**（2026-09 修）：把手移进整宽的 `HeaderLayer`（原来落在隐藏时宽度为 0 的“状态”列单元格里，
  于是非云目录下列边界完全拖不动），见第 4 节“列宽”与第 6 节第 46 条；
  回归：`tools/test-column-resize.ps1`（8 个用例 20 条断言，真鼠标拖每个列边界 + 双击复位 +
  列头与数据行对齐 + 落盘 + 列头排序仍可用；修前用例 2/4/5 全部没反应）；
* 键盘导航的其余部分（S2：`Ctrl+Shift+A` 反选、回车打开、type-ahead 等）尚未实现；右键菜单只做了文件列表
  （条目 + 空白处），侧边栏 / 固定目录 / 磁盘按钮还没有；
* 文件操作已有**复制 / 移动 / 删除（进回收站，`Shift+Delete` 永久删除）**（剪贴板、右键菜单、
  拖放与快捷键都通）；重命名 / 重命名以外的新建 / 压缩 / 哈希还没做（新建文件夹算已有）；
  拖拽只支持“拖到本应用的目录行 / 另一个窗格 / 工具条固定目录 / 侧边栏收藏夹”，
  还不能拖到资源管理器（数据包里只有自定义格式与纯文本，没放 `StorageItems`）。

**未实现的功能与后续步骤全部在 `plan.md`。**
