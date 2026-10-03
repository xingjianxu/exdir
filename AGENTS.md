# AGENTS.md — exdir 开发指南（供 AI 编码助手使用）

> 本仓库的“事实来源”。动手前读完；跨步骤规划见 `plan.md`。文中 `第 N 条` = 第 6 节“踩过的坑”。

## 1. 项目是什么

`exdir`：类 **Directory Opus** 的 Windows 文件管理器，界面**紧凑**。

| 项 | 值 |
| --- | --- |
| UI | WinUI 3（Windows App SDK 2.5.1 / WinUI 2.3.9） |
| 语言 | C# 12，`net8.0-windows10.0.19041.0`，`TargetPlatformMinVersion 10.0.17763.0` |
| MVVM | CommunityToolkit.Mvvm 8.4.2（`[ObservableProperty]` / `[RelayCommand]` / `[NotifyCanExecuteChangedFor]`） |
| DI | Microsoft.Extensions.DependencyInjection 8.0.1 |
| 托盘 | H.NotifyIcon.WinUI **2.3.2**（2.4.x 只出 net10.0） |
| 设置卡片 | CommunityToolkit.WinUI.Controls.SettingsControls 8.2.251219（`SettingsCard`） |
| 部署 | **非打包**：`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true`，publish 裁剪 |
| 平台 | x64（x86/ARM64 只声明、未验证） |

界面语言：**简体中文硬编码**（不引入 .resw）。

**尚未实现**（见 `plan.md`）：键盘导航其余部分（`Ctrl+Shift+A` 反选、回车打开、type-ahead）、重命名、重命名以外
的新建、哈希；拖拽不能拖到资源管理器（数据包里只有自定义格式与纯文本，没放 `StorageItems`）；右键菜单只做了
文件列表（条目 + 空白处），侧边栏 / 固定目录 / 磁盘按钮还没有。

## 2. 环境与命令（重要）

本机在 WSL 里，通过 Windows PowerShell 调 Windows 工具链。**bash 里没有 coreutils**，命令都要走 pwsh：

```bash
"/mnt/c/Users/xingjian/AppData/Local/Microsoft/WindowsApps/pwsh.exe" -NoProfile -Command "<PowerShell 代码>"
"/mnt/c/Users/xingjian/AppData/Local/Microsoft/WindowsApps/pwsh.exe" -NoProfile -File "D:\prj\exdir\tools\xxx.ps1"
```

**不要把中文写在 `-Command "…"` 里**：WSL 传来的参数是 UTF-8、Windows 按系统 ANSI 码页（本机 936）解码，中文会
变成不可逆乱码（`锛?` 这类）。要传中文就**写进文件**：`-File 脚本.ps1`、`git commit -F msg.txt`、`gh --notes-file`；
脚本里的中文输出不受影响。工具链：dotnet SDK 10.0.401（可编译 net8.0）、Windows App SDK 2.5.1 运行时。

### 常用命令

```powershell
cd D:\prj\exdir
dotnet build exdir.csproj -c Debug -p:Platform=x64 --nologo    # 日常 Debug 构建
pwsh -NoProfile -File tools\publish.ps1                        # 出 Release → dist\win-x64（收尾必跑）
pwsh -NoProfile -File tools\capture.ps1 [-Exe dist\win-x64\exdir.exe] [-Name 后缀] [-Keys '{F10}','^{t}']
pwsh -NoProfile -File tools\inspect-ui.ps1 [-Filter 名] [-Click 名] [-Hover 名] [-HoverAt "157,37"]
pwsh -NoProfile -File tools\make-icon.ps1                      # icon.svg → Assets 图标（改完要重新 build/publish）
pwsh -NoProfile -File tools\shot-settings.ps1                  # 设置窗口各分类截图（需交互桌面）
dotnet run -c Debug --project tools\archive-smoke              # 压缩包服务级冒烟（不需交互桌面；裁剪版加 --no-iso，见第 89 条）
pwsh -NoProfile -File tools\release.ps1 -Tag v0.1.0 [-DryRun] [-SkipPublish] [-Clobber] [-Draft] [-Prerelease]
```

* `capture.ps1` / `inspect-ui.ps1` 都**先杀掉已有 exdir 进程**再启动；`shot-*.png` 自动编号，`-Name xxx` 指定后缀。
* `inspect-ui.ps1`：打印控件树（名称 + 物理坐标 + 尺寸）、按名字查元素、真鼠标点击/悬停并截图到 `.artifacts`。
* `make-icon.ps1`：`icon.svg` → `Assets\exdir.ico`（exe / 标题栏 / 任务栏 / 托盘都用它）+ 各尺寸徽标 PNG；栅格化用
  系统 Edge（headless、透明底），找不到用 `-Edge` / `EXDIR_EDGE` 指定。

### 回归脚本（改哪块跑哪块，**不要每次全跑**）

| 脚本 | 覆盖 | 桌面 |
| --- | --- | --- |
| `test-settings.ps1`（10 用例） | 分类齐全 / 每页只显示本分类的项 / 初值一致 / 拨一下立即落盘并生效 / 跨分类与关窗重开读回 / 只开一个窗口 / 「右键菜单」页系统菜单项默认全开且能逐项关掉（拨 `verb:properties` 落盘、重开仍关、拨回清空）/ 「行高」滑块（初值、跨分类读不到、拖完列表行真的变高）/ 侧边栏分组与「主目录」文件夹开关当场作用到树 / 「标签页使用直角」当场落盘应用 / 「主题」三态下拉框与标题栏太阳月亮开关同步 / 「启动」页开关真的写删 `HKCU\...\Run` 的 `"<exe>" --preload` | 不需 |
| `test-status-bar.ps1`（16 断言） | 只有一条 / 一行高 / 贴底 / 项数 / 选中摘要 + 合计大小 / 磁盘可用空间 / 跟随活动窗格。**用例 4 拿 `%TEMP%` 数量比对状态栏“N 项”，偶发差 1 项就 FAIL，属脚本时序问题，重跑即可** | 需 |
| `test-shell-icons.ps1`（13 断言） | 每行都有图标 / 不同程序不同 / `.lnk` 带小箭头 / 同扩展名只提取一次 / 滚动后仍有图标 | 需 |
| `measure-row-align.ps1` | 图标与文字是否垂直居中（截图 + UIA 量墨迹中心）。判断“图标该不该再微调”用它，别靠肉眼 | 需 |
| `test-pin-drag.ps1`（6 用例） | 收藏夹子项与 config.json 一致 / 列表→固定目录 / 侧边栏→固定目录 / 拖拽排序 / 右键取消固定 / 拖到收藏夹。**shell 提权时自动用 explorer.exe 降权启动**（第 21 条） | 需 |
| `test-context-menu.ps1`（5 用例） | 系统菜单（文件行 / 空白处各弹 `#32768`、菜单项记清单、关掉 `verb:properties` 后不再有「属性」）+ 内置菜单（按名字断言 `MenuItem`、`InvokePattern` 点「新建文件夹」验磁盘上真的建出目录）。系统菜单自绘、UIA 读不到项 → “弹没弹”看 `#32768` 窗口、“有哪些项”看 `exdir.log` | 需 |
| `test-list-selection.ps1`（5 用例） | 单击行只选中它 / `Ctrl+A` 全选且状态栏同步 / 点空白处清空 / 行上的点击不算空白处 / 地址栏的 `Ctrl+A` 仍是文本框全选 | 需 |
| `test-row-dblclick.ps1`（8 用例） | 6 个落点双击都进目录（行内边距 / 名称右侧空白 / 类型与大小列的空白…）/ 行首展开箭头只展开不进目录 / 列表下方空白处不导航 | 需 |
| `test-column-resize.ps1`（8 用例） | 每个列边界都拖得动 / 列头与数据行仍对齐 / 双击复位 / 最右一列也拖得动 / 拖完落盘 / 列头排序仍可用 | 需 |
| `test-tray.ps1`（4 用例） | 点关闭按钮 → 窗口隐藏、进程驻留、托盘图标进通知区域 / 隐藏时再启动 exe → 第二个进程退出、已有窗口被唤回 / 菜单「隐藏到托盘」+ 点托盘图标唤回 / 菜单「退出」→ 进程真的结束 | 需 |
| `test-drive-hotplug.ps1`（3 用例） | `subst` 造“U 盘”+ 发 `WM_DEVICECHANGE`：侧边栏「此电脑」与工具条磁盘区实时出现/消失，且不重建整棵树（节点清单前后一致）、不动收藏夹 | 不需 |
| `test-file-ops.ps1`（9 用例） | 内置菜单有剪切/复制/粘贴/删除、空白处有粘贴 / `Ctrl+C` 后剪贴板上真有 `CF_HDROP`（DropEffect=1）/ `Ctrl+V` 复制 / `Ctrl+X`+`Ctrl+V` 移动 / 拖文件行到目录行移动（截图 `.artifacts\file-ops-drag.png`）/ 外部来源（带复制与剪切标志）能复制移动进来 / `Delete` + 点掉外壳确认框 → 真进回收站 / `Shift+Delete` 的同名文件不在回收站。**shell 是管理员时自动降权**（第 21 条） | 需 |
| `test-network-locations.ps1`（4 用例） | 假网络位置启动时出现在「此电脑」、排磁盘之后 / 点它导航到 `target.lnk` 目标 / `WM_DEVICECHANGE` 刷新后它还在（差量刷新不误删）/ 删掉目录再刷新就消失 | 不需 |
| `test-command-line.ps1`（10 用例） | 带路径启动 → 新标签页 / 已在运行时转发给已有实例 / 工作目录是程序或系统目录 → 只唤回窗口 / path 是文件 → 打开所在目录并选中 / 路径不存在 → 当前标签页「无法打开」 | 不需 |
| `test-autostart.ps1`（3 用例） | `--preload` 起来后进程驻留、无可见主窗口、日志有「预热启动：不显示主窗口」「预热完成：… 目录=… 图标=…」/ 预热进程在跑时再启动 → 第二个进程瞬时退出、预热进程窗口真的显示 / 带路径的请求照样能转发进来新开标签页。靠进程句柄 + `exdir.log`，不改注册表 | 不需 |
| `test-archive.ps1`（13 用例） | 双击 `.zip`/`.7z` 进包 / 上一级 / 点面包屑里压缩包那段回包根 / 包内目录行内展开 / 包内排序与图标 / 只读守卫（菜单无写操作、`Ctrl+V` 只弹提示、拖拽不启动）/ 双击包内文件解到临时目录 / `.tar.gz` 透明解开 / 会话恢复到包内 / 命令行进包 / 用例 13：**真实目录里点压缩包行的箭头就地展开**（行数只多两行、无「只读」徽标、双击包内条目仍解到临时目录、再点一次折叠回去） | 需 |
| `test-archive-copy.ps1`（9 用例） | 包内复制只记在内存里（不动磁盘、清空系统剪贴板）→ 上一级 → 粘贴 → 文件真落到磁盘、日志有「压缩包复制：sample.zip 解出 1 项」/ 多选（文件 + 目录）时目录按整棵子树解出来 / 中转副本用完就删 / 系统剪贴板优先 / `.iso` 也当目录进、也能复制出来（测试 ISO 用系统 IMAPI2FS 现造，造不出就 SKIP）/ 用例 7～9：真实目录里就地展开压缩包，展开出来的行仍是只读的（「复制」进内存、「删除」被拦、折叠后行集合复原） | 不需 |
| `test-archive-extract.ps1`（4 用例） | 压缩包行的内置菜单里有「使用 7-Zip 打开」与「解压到下载文件夹」（没装 7-Zip 时那一项置灰且标题写明原因，与本机实际情况比对）/ 普通文件行没有这两项 / 点「解压到下载文件夹」→ `Downloads\<包名>\` 里真的出现包内文件与子目录、绿色「解压完成」InfoBar（带「打开目录」）、日志有「解压：…」/ 同一个包再解一次落到 `<包名> (2)`。收尾删掉解出来的目录并还原 config.json | 需 |
| `test-compress.ps1`（4 用例） | 文件行 / 目录行的内置菜单里有「压缩」，空白处没有 / 点「压缩」→ 默认输出目录（「下载」文件夹）出现 `<名字>.zip`、包内条目与源一致、**剪贴板里就是这个 zip**（`CF_HDROP` + DropEffect=1）、绿色「压缩完成」InfoBar（带「打开目录」）、日志有「压缩：…」「压缩产物已复制到剪贴板：…」/ 多选时包名 = 当前文件夹名、再压一次落到 `… (2).zip` / 设置里的「压缩输出目录」指向别处后压缩落到那里。收尾删掉生成的 zip 并还原 config.json。等待条件用「zip 能真的打开」/「剪贴板里已出现它」（第 94 条）| 需 |

真鼠标那条路（右键菜单里的「复制」）在 `test-archive.ps1` 用例 12。

### 任务收尾（每个任务都必须做）

```powershell
pwsh -NoProfile -File tools\publish.ps1     # 把最新 Release 产物镜像到 dist\win-x64（交付版本）
```

* **只出 win-x64**，不要 x86 / ARM64。`dist\win-x64\exdir.exe` 自包含，双击即可运行（目标机无需预装 .NET /
  Windows App Runtime）。
* 管线（2026-09 起）：`dotnet publish`（`PublishTrimmed` + `TrimMode=partial`）→ 从构建输出补回 publish 丢掉的
  `exdir.pri` / `*.xbf` → 镜像到 dist。**225 MB / 548 文件 → 84 MB / 171 文件**：AI/ML/Search/Widgets 组件约 55 MB、
  语言资源 3.2 MB（只留 `zh-*`/`en-*`）、裁剪约 97 MB。细节见第 64～67 条。
* `publish.ps1` 会校验 `exdir.exe` / `exdir.dll` / `exdir.pri` 都在、每个 `.xaml` 都有 `.xbf`、`Assets\exdir.ico` 在、
  语言目录只剩 `zh-*` / `en-*`，任一条不满足直接报错。
* **日常 `dotnet build` 不受裁剪影响**（裁剪只在 publish 生效），但 **`dotnet publish` 现在就是发布流程**，不要再改回
  “build + 镜像”（第 3 条）。
* 交付前看 `dist\win-x64\build-info.txt`（源码提交 / 构建时间 / 文件数 / 总大小），与
  `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\exdir.exe` 的时间戳对照；不一致就是忘了 publish。
* **发到 GitHub Release**：`tools\release.ps1 -Tag v0.1.0`（缺省 `v0.0.<yyyyMMdd>`）= 校验（git 干净 / gh 已登录 /
  远端没有指向别的提交的同名 tag）→ `publish.ps1`（可 `-SkipPublish`）→ 把 `dist\win-x64` 打成
  `dist\exdir-<tag>-win-x64.zip`（zip 根目录就是 `exdir.exe`；171 文件 84 MB → 约 33 MB）→ 生成说明（变更清单 +
  SHA256 + `build-info.txt`，存 `.artifacts\release-notes-<tag>.md`）→ `gh release create`。tag 由 gh 在远端创建、
  指向当前 HEAD，所以**工作区必须干净**（否则 build-info 的提交号对不上源码，默认报错；确实要发加 `-AllowDirty`）。
  `-DryRun` 只构建 + 打包 + 打印 gh 命令；`-Draft` / `-Prerelease`；tag 上已有 Release 要 `-Clobber` 才覆盖。
  前置：`gh auth login` 过、只传一个 zip。

**测试范围**：改完只跑与本次改动直接相关的交互式回归脚本（改托盘只跑 `test-tray.ps1`，改列宽只跑
`test-column-resize.ps1`），**不要每次全跑**；只有用户明确要求“全部测试”时才全跑。非交互检查（`dotnet build`、单测、
脚本语法）不受限制。**涉及裁剪的改动（新增 BCL 引用、新持久化类型等）必须在 `dist\win-x64\exdir.exe` 上重跑相关
脚本**，未裁剪版测不出来（第 71 条）。

### 运行期日志

```
%LOCALAPPDATA%\exdir\exdir.log              # 崩溃 / 启动 / 窗口位置恢复（日志留在 LocalAppData）
%USERPROFILE%\.config\exdir\config.json     # 全部设置与会话（设了 XDG_CONFIG_HOME 时以它为准）
```

非打包 WinUI 崩溃没有控制台输出，`Diagnostics/Log.cs` 写到上面这个日志里，排查启动崩溃的**第一步永远是看
exdir.log**。配置 2026-09 从 LocalAppData 搬到用户主目录（首次启动自动迁移，第 76 条）。

### 验证循环

1. `dotnet build` 无编译错误；2. `capture.ps1` 截图，用 read 工具看图；3. 位置/存在性有疑问用 `inspect-ui.ps1`
看真实坐标（物理像素）；4. 有异常看 `$env:LOCALAPPDATA\exdir\exdir.log`。不要凭感觉判断 UI 改动。

## 3. 目录结构

```
exdir/
├─ Program.cs                 自定义入口点（DISABLE_XAML_GENERATED_MAIN）：单实例闸门 + 命令行解析跑在 XAML 初始化之前
├─ App.xaml(.cs)              DI 容器、全局异常日志、创建主窗口
├─ MainWindow.xaml(.cs)       外壳：TitleBar（菜单栏）/ 工具条 / 侧边栏 / 1~2 个窗格 + 托盘图标
├─ Themes/ExdirTheme.xaml     紧凑密度覆盖 + 布局常量 + 扁平按钮样式 + 强调色悬停色刷（合并顺序在 XamlControlsResources 之后）
├─ Models/                    POCO：FileSystemEntry / DriveModel / AppSettings / AppTheme / QuickCommand / CloudSyncState /
│                             ShellMenuItem / IconBitmap / ArchivePath / 枚举（含 SettingsCategory）
├─ Services/                  接口 + 实现成对；Native/ 放 Win32 互操作
│   ├─ IFileSystemService     目录枚举（异步、跳过无权限项）、路径规整、云目录条目附带同步状态
│   ├─ IDriveService / IKnownFolderService / INetworkLocationService / ICloudSyncService / IShellIconService
│   ├─ IArchiveService / IArchiveClipboardService / ICompressionService / IDialogService / ISettingsService
│   ├─ IShellService / IClipboardService / IFileOperationService / IShellContextMenuService / IDeviceChangeService
│   └─ Native/                ShellPropertyStore / ShellIconExtractor / ShellContextMenuInterop / VolumeChangeWatcher /
│                             ClipboardInterop / FileOperationInterop / SevenZipInterop / ShellLinkInterop
├─ ViewModels/                MainViewModel / PanelViewModel / FolderTabViewModel / PathSegmentViewModel /
│                             SidebarViewModel（懒加载 + 收藏夹镜像 + ApplyGroupVisibility / ApplyHomeFolders /
│                             差量 RefreshDrives）/ FileItemViewModel / PinnedFolderViewModel / StatusBarViewModel /
│                             SettingsCategoryViewModel / ShellMenuItemViewModel / SettingsViewModel
├─ Views/                     SidebarView / DriveBarView / PaneView / NavigationBarView / PathBreadcrumb / DetailsView
│                             （DetailsView 还管文件列表的拖放与右键菜单）/ StatusBarView / SettingsWindow / SettingsView
├─ Controls/                  PaneSplitter.cs（自研分隔条，WinUI 没有 GridSplitter）；ColumnResizeHandle.cs
├─ Helpers/                   ColumnLayout / ThemeHelper / CloudSyncStateHelper / DpiHelper / FileTypeHelper /
│                             IconImageHelper / SizeFormatter / DragDropHelper / CommandLine / AutoStart /
│                             SingleInstance / ArchiveFormats / CompressTargets / FolderPicker /
│                             SevenZipLocator（找系统的 7zFM.exe）
├─ Converters/CommonConverters.cs / Diagnostics/Log.cs（可多进程同时追加，第 72 条）
├─ icon.svg                   程序图标唯一源文件（改它再跑 tools\make-icon.ps1）
├─ Assets/                    图标（exdir.ico + 各尺寸徽标 PNG，都由 make-icon.ps1 生成）
├─ native/                    原生组件（x64\7z.dll + LICENSE-7z.txt + 来源/升级说明 README.md）
└─ tools/                     capture / inspect-ui / shot-settings / test-*.ps1 / measure-row-align / publish /
                              release / make-icon，以及 archive-smoke（不需交互桌面的服务级冒烟工程）
```

## 4. 界面布局约定（改动前务必对齐）

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

* 行 0 用 `Microsoft.UI.Xaml.Controls.TitleBar` 控件 + `ExtendsContentIntoTitleBar = true;`
  `SetTitleBar(AppTitleBar);`；系统窗口按钮由 AppWindow 原生绘制，不要自绘。
* `TitleBar` 分区属性 = `LeftHeader` / `Content` / `RightHeader`；`ContentBefore` / `ContentAfter` 被标 experimental、
  XAML 编译报 `WMC0011`，**不要用**。`LeftHeader` 里是 `StackPanel`：**主题开关（太阳/月亮）在「文件」菜单左边**。
* **侧边栏分组**（自上而下）：`收藏夹`（固定目录镜像，可拖目录进来收藏；排最上面，`AllGroups()` 与
  `RefreshRoots()` 的 `desired` 顺序必须一致）、`主目录`（点它去 %USERPROFILE%，子项桌面/文档/下载/图片/音乐/视频）、
  `云存储`（注册表探测的同步根）、`此电脑`（磁盘 + 「网络位置」）。节点可导航当且仅当有路径
  （`SidebarNodeViewModel.IsNavigable`）。
  * 四个分组可在设置「侧边栏」页关掉（`SidebarShowHome` / `SidebarShowFavorites` / `SidebarShowCloud` /
    `SidebarShowComputer`，默认全开）：`ApplySidebarGroups()` → `SidebarViewModel.ApplyGroupVisibility()` 只增删
    `Roots` 差异项、不整表重建（整表重建会丢折叠状态与已懒加载子节点）。
  * 「主目录」显示哪几个标准文件夹也可配（`SidebarHomeDesktop` / `Documents` / `Downloads` / `Pictures` / `Music` /
    `Videos`，**默认只开桌面与下载**）：按 `SpecialFolderModel.Key`（`UserFolderKey` 稳定标识，不是显示名/路径）筛；
    `SyncHomeFolders()` 只增删差异子项、复用未变节点。
* **紧凑密度**：`Themes/ExdirTheme.xaml` 覆盖 `TitleBarExpandedHeight=36`、`ListViewItemMinHeight=24`、
  `TreeViewItemMinHeight=24`。**只覆盖数值/颜色键，不要覆盖控件隐式样式**（会丢默认 ControlTemplate）。同一处还关掉
  选中行的左侧蓝竖条（`ListViewItemSelectionIndicatorVisualEnabled=False`）。例外：**文件列表行高可配**（见下）。
* **标签条**：24 DIP（≈ `ExRowHeight`）、顶边贴住窗格上边框、标题上方只留 5 DIP。改的全在 `ExdirTheme.xaml`：
  `TabViewHeaderPadding=0`、`TabViewItemMinHeight=24`、`TabViewItemHeaderCloseButtonHeight=16`（**标签高度的真正决定
  项**，第 36 条）、`TabViewItemAddButtonContainerPadding` 与左右滚动按钮容器内边距去掉下边距（否则“+/◀▶”把标签栏撑
  到 27 DIP）、`TabViewItemSeparatorMargin=0,4,0,4`。标签内文字/图标 16 DIP、上下各留 4 DIP；关闭按钮 32×16。
  新增“标签条上的东西”沿用这组尺寸。
  * **标签圆角默认直角**（`AppSettings.SquareTabCorners`，设置「外观 → 标签页使用直角」）：`PaneView.xaml` 的
    `TabViewItem` 绑 `CornerRadius="{x:Bind TabCornerRadius}"`（`FolderTabViewModel` 从设置算），WinUI 模板里
    `TabBackground.CornerRadius` 是 `TemplateBinding` → 设置一改当场重画。第 55 条。
* **主题**（`AppSettings.Theme`，默认 `System`；跟随系统 / 浅色 / 深色）：两个入口共用同一值——标题栏左侧太阳/月亮
  图标（`MainWindow.ThemeToggle`，`ExToolbarToggleButtonStyle`）+ 设置「外观 → 主题」三态下拉框。
  * 换主题只有一处：`MainWindow.ApplyTheme()` 给 `RootGrid` 设 `RequestedTheme`（`Application.RequestedTheme` 启动后
    不能改）；根元素一变，菜单栏/工具条/侧边栏/窗格/状态栏连同弹层（XamlRoot 都是它）一起换。
  * 图标与悬停提示按 `RootGrid.ActualTheme` 刷（`SyncThemeToggle` + `ActualThemeChanged`）；标题栏开关不带勾选底色
    （`ExToolbarToggleButtonStyle` 的 CheckStates 故意全空）。拨动时总是固定成**显式**浅/深（`SetDarkMode`）。
  * **系统窗口按钮不跟 ElementTheme 走**：系统主题与 exdir 不一致时要自己给 `AppWindow.TitleBar.Button*Color` 上色
    （`UpdateCaptionButtons`）。
  * 设置窗口是另一个 `Window`：`SettingsWindow.ApplyTheme()` 自己设 `View.RequestedTheme`、订阅主 VM 的 `Theme`
    （关窗退订）；**必须有 Mica 背板**（第 68 条）。回归 `test-settings.ps1` 用例 9。
* **扁平控件悬停/按下/选中底色全是强调色**（默认是“灰底上叠 8% 白”，看不出鼠标停在哪）：深色
  `SystemAccentColorLight2` / 浅色 `SystemAccentColorDark1`。两档：小控件（工具条/导航条/列头、菜单项、标题栏菜单）
  用 `ExToolbar*`；铺满整行的大表面（文件列表行 / 侧边栏树节点 / 标签页头）用 `ExSurface*`。**选中态也改强调色**
  （`ExSurfaceBackgroundSelected` 比悬停重），否则“悬停比选中还显眼”。
  * 小控件走具名样式 `ExToolbarButtonStyle`（`MinHeight=26` + `Padding=8,1,8,1`）与 `ExFlatButtonStyle`
    （MinHeight/Padding 归零，给列头、行内箭头用）。**具名样式不是隐式样式**：对话框里普通按钮仍是 WinUI 默认外观；
    新加扁平按钮写 `Style="{StaticResource ExToolbarButtonStyle}"`，不要手写 `Background="Transparent"`。已挂样式的：
    `DriveBarView`、`NavigationBarView`（← → ↑ ⟳）、`DetailsView` 的 5 个列头排序按钮。
  * **例外：地址栏与行内展开箭头不用强调色**（`ExSubtleButtonStyle` / `ExFlatSubtleButtonStyle`）：面包屑分段与行内
    18px 展开箭头悬停/按下是普通灰色（深色叠白、浅色叠黑）——一上强调色就喧宾夺主。这两个样式与 `ExToolbar*` 只差
    悬停/按下画刷，但 Storyboard 里的 `{ThemeResource}` 键写死在模板里（`BasedOn` 改不了）→ 模板另写一份，改模板时
    两处同步。
  * **地址栏右侧 `BlankArea` 连灰色悬停都不要**（`ExGhostButtonStyle`，PointerOver/Pressed 是空状态）：它铺满整条
    地址栏剩余宽度，一点底色都像“整条被按住了”。
  * 表/树/菜单/标签这些改不了样式的，在 `ThemeDictionaries` 里覆写 WinUI 资源键（键名抄 `generic.xaml`）：
    `TitleBarPaneToggleButtonBackground*`、`MenuBarItemBackground*`、`MenuFlyoutItemBackground*`、
    `TabViewItemHeaderBackground*`、`TabViewButtonBackground*`、`TabViewItemHeaderCloseButtonBackgroundPointerOver`、
    `TreeViewItemBackground*`、`ListViewItemBackground*`（含 `Selected`/`SelectedPointerOver`/`SelectedPressed`）。
    这些键**只在当前主题的 ThemeDictionaries 生效、不跨主题合并**，深浅两份都要写全（高对比主题故意不覆盖）。
* 活动窗格边框用强调色（`PaneView.UpdateActiveVisual`），点窗格即设为活动窗格。
* **详细信息列表 = 可展开的树形列表**（不是 TreeView）：
  * `FileItemViewModel` 带 `Depth/IsExpanded/Children`，`FolderTabViewModel._rootNodes` 是根层，`Items` 永远是“当前
    可见行”的扁平集合；展开是**懒加载 + 增量 Insert/RemoveAt**（整体替换会清掉滚动位置与选中项），排序/刷新才整体
    重建。展开状态在 `_expandedPaths`，刷新/重进同目录按路径恢复（按长度升序展开）。
  * 缩进 = `Depth * 14`，行首 18px 展开箭头（`CanExpand` 为 false 时不可点、字形空串占位）；双击仍是进目录/打开文件，
    箭头只管展开折叠，`←/→` 也能展开折叠。
  * **压缩包文件行与目录行同款展开**（2026-10）：`FileItemViewModel.IsExpandable = IsDirectory || IsArchive`，
    `ToggleExpandAsync` 因此对 `.zip` / `.7z` / `.tar.gz` 也生效 —— 在当前列表里就地列出包内条目，不导航进包。
    目录与包内条目的区别全在两个标记上：`IsArchive`（真实文件，可删除/拖拽）、`IsInArchive`（虚拟路径，只读）。
    详见「压缩包只读浏览」一节。
  * **双击命中范围 = 整条行高亮区**（含行内边距、名称右侧空白、日期/类型/大小列的空白）→ 处理器挂 `DetailsRoot`
    （`AddHandler(..., handledEventsToo: true)`）+ `FindRowItem` 反查行；行模板 `Grid` 另加
    `Background="Transparent"`。回归 `test-row-dblclick.ps1`；第 45 条。
  * 排序对树的**每一层**生效（`CompareNodes`）；排序/刷新后由 `FolderTabViewModel.PendingSelection`（一组路径，视图读、
    不清空）把行选回来；返回上一级用 `selectPath` 选中来源目录。
* **选择**：单击空白处（列头以下、任何一行之外）取消选择；单击行仍是 ListView 自己的行为（含 Ctrl 加选）；`Ctrl+A`
  全选当前可见行（就地展开的子行也算）。
  * `PointerPressed` 与右键一样挂 `DetailsView` **最外层 Grid**（`handledEventsToo: true`），用
    `FindRowItem(e.OriginalSource)` 排掉落在行上的点击；列头（y ≤ `HeaderRow.ActualHeight`）也不算空白处。
  * 清空选择后 `EntryList.Focus(FocusState.Pointer)`（空白处自身不可聚焦，不抢这一下焦点会留在旧控件上）。
  * `Ctrl+A` 挂 `DetailsRoot`（不是 `ListView`），这样焦点在列头按钮/行内箭头时也生效；地址栏不在本控件子树内，
    那里 `Ctrl+A` 仍是 `TextBox` 自己的全选。全选用 `ListViewBase.SelectAll()`（别遍历 `SelectedItems.Add`，几千行
    会退化成 O(n²)）。回归 `test-list-selection.ps1`。
* **列宽**：`ColumnLayout` 实例**每标签页一个**（`FolderTabViewModel.Columns`），列头与每行绑同一实例 → 永远对齐。
  requested（用户拖的，落盘）与 rendered（按窗格宽度算，`FitTo`）分开。
  * 自动模式（`AutoFit`，默认）：富余宽度给名称列；装不下各列按余量等比压缩（都有最小宽度，5 列全在时下限约 264 DIP，
    更窄就横向滚动，`ColumnLayout.Minimums`）。
  * 手动模式：拖过任意列边界即进入（`SetRequestedWidth` 关掉 `AutoFit`）；装不下横向滚动
    （`ScrollViewer.HorizontalScrollMode=Enabled`，行 Grid 的 `MinWidth={x:Bind Columns.RowMinWidth}` 提供滚动范围）。
    列头靠 `TranslateTransform` 跟随 `ScrollViewer.HorizontalOffset`，`HeaderRow.Clip` 裁掉平移出去的部分。
  * 拖动把手 `Controls/ColumnResizeHandle` 由 code-behind 追加到 `HeaderLayer`（表头里**没有列定义**的整宽父层，
    XAML 里包着 `HeaderContent`），位置用 `TranslateTransform.X`（**不要用 Canvas**：Canvas 子元素实测高度 0）。
    双击 = 该列恢复默认 + 回自动模式；0 宽列（状态列）把手 `Visibility=Collapsed`。**不要放进 `HeaderContent`**
    （第 46 条）。`HeaderTransform` 也挂 `HeaderLayer`，按钮与把手一起平移。
  * **列头排序按钮铺满整列**（`ExColumnHeaderButtonStyle`）：整列宽 + 整个表头高都是命中区，悬停/按下是直角矩形；
    最左可见列往左、最右“大小”列往右各再铺 6 DIP（= `ExRowPadding`），与数据行高亮一样从窗格边缘铺到边缘。外扩在
    `UpdateHeaderInsets()` 用负 `Margin` + 等量 `Padding`（不能改列宽）。把手在按钮**之后**加进 `HeaderLayer`。
    回归 `test-column-resize.ps1`。
* **行高可配**（默认 28 DIP）：设置「文件列表 → 行高」滑块（20~48、步进 2）。
  * 存 `AppSettings.RowHeight`（默认 `ColumnLayout.DefaultRowHeight = 28`），夹取在 `ColumnLayout.NormalizeRowHeight`
    （config.json 被手改过也不出界面外）。运行时值是**每标签页一份**的 `ColumnLayout.RowHeight`（行模板本绑着这个
    共享对象），行模板写 `Height="{x:Bind Columns.RowHeight, Mode=OneWay}"` → `ApplySettings` 给每个标签页赋一次值，
    几千行一起变。
  * `ItemContainerStyle` 的 `MinHeight` 必须归零（原来锁 24 → 调到 24 以下不生效，第 44 条）；`ExRowHeight=24` 现在
    只给状态栏用。**列头不跟着变**（仍 `ExListHeaderHeight=26`）。行内每格 `VerticalAlignment="Center"` → 任何行高下
    图标与名称文字都垂直居中。回归 `test-settings.ps1` 用例 6。
* **名称列行首是真实外壳图标**（S14）：`.exe` 显示程序自带图标、`.lnk` 显示目标图标 + 快捷方式小箭头、文件夹/文件类型
  按系统关联；`FileTypeHelper` 的 Segoe 字形只做兜底占位。
  * 取图标：`ShellIconService` 用 `SHGetFileInfo`（`Native/ShellIconExtractor`）拿 HICON，`GetDIBits` 读 32bpp BGRA，
    `IconImageHelper` 在 UI 线程建 `WriteableBitmap`。**懒加载**：由 `DetailsView.ContainerContentChanging` 触发（行
    容器真的被创建时才取），`FileItemViewModel.IconRequested` 保证同一行只排一次队。
  * 缓存两级：`ShellIconService` 的“图标键 → Task”（并发合并）与“内容哈希 → 像素”，`IconImageHelper` 的“内容哈希
    → ImageSource” → 滚动、排序、刷新、重进目录都不重复取。
  * 图标键：目录与“图标写在文件自身里”的类型（`.exe/.lnk/.url/.ico/.msi/.scr/.cpl/.com/.pif`）按**路径**，其余按
    **扩展名**（几千个 `.txt` 只问外壳一次）。单次提取约 16~20 ms，只在后台线程做。
  * **与文字对齐**：图标用 `RenderTransform`（`TranslateTransform Y="1"`）下推 1 DIP 对文字的视觉中心（文字行盒底部
    有降部）；**不要用 Margin**（会改布局盒、只推得动 0.5 DIP，且非整数偏移会让位图重采样、图标发虚）。改数值前先跑
    `measure-row-align.ps1`。回归 `test-shell-icons.ps1`；第 30/31/35 条。
* **“状态”列（云同步状态）**：`DetailsView` 第 0 列，只在云同步目录里出现（已同步 / 仅在云端 / 已固定 / 正在同步 /
  同步错误 / 未同步）。数据：`FileSystemService.Enumerate` 在目录位于云同步根下时额外读 `System.StorageProviderState`
  与 `System.FilePlaceholderStatus`（`Native/ShellPropertyStore`，属性系统读不到时退回占位符属性位），映射成
  `CloudSyncState` 存到 `FileSystemEntry.SyncState`。
  * 显隐：`NavigateAsync` 按“有没有条目带状态”设 `Columns.ShowSyncColumn`（隐藏时算 0 宽、列定义不动 → 其它列下标
    与落盘列宽不受影响）；离开云目录时若正“按状态排序”则退回按名称。
  * 单元格：一个 `Grid` 里 6 个 `TextBlock`（Segoe Fluent Icons 字形 + 主题刷），用 `CloudSyncStateVisibilityConverter`
    + ConverterParameter 只显示当前那个。**用 TextBlock 不用 FontIcon**：TextBlock 进 UIA 树、
    `AutomationProperties.Name` 写着“仅在云端/已同步”，脚本可直接断言。
  * 成本：每项约 2~3 ms，只在云目录做、并行读，1500 ms 预算（超预算的条目保持无状态），超时日志留一行
    `云同步状态：…`；侧边栏只建目录树、不读状态。
* **工具条右侧“固定目录”是拖放区**（`DriveBarView.PinnedDropZone`）：
  * 拖源：文件列表（`DetailsView.EntryList`）与侧边栏树（`SidebarView.FolderTree`），`DragItemsStarting` 用
    `DragDropHelper.SetPaths` 把**目录**路径写进自定义格式 `exdir/paths`（换行分隔纯文本）；拖到没有路径的项
    （文件/分组标题）直接 `Cancel`。
  * 落点：`PinnedDropZone`（`AllowDrop=True` + `Background="Transparent"`，缺一不可）+ 平时 Collapsed 的高亮层
    （画在按钮**下面**，只露一圈强调色边框与淡底色），`DragOver` 设 `AcceptedOperation=Copy` 与
    `DragUIOverride.Caption="固定到工具条"`。
  * 落下后走 `MainViewModel.PinFolders`（去重、只收已存在的目录、上限 `MaxPinnedFolders=12`）并**立即**
    `_settings.Save()`。右键固定目录按钮 → 动态 `MenuFlyout` 的“取消固定”（`ContextRequested`，不要用
    `ContextFlyout`：模板里拿不到 `DataContext`）。外部来源只有 `StorageItems`，`DragOver` 里同步判断不了内容 →
    先接受、`Drop` 里再筛目录（`DragDropHelper.GetPathsAsync`）。
  * **拖拽排序**：按钮自己也是拖源，写另一个格式 `exdir/pinned-reorder`（值是那个目录的路径），`Drop` 里按鼠标横坐标
    算插入位置 → `MainViewModel.MovePinnedFolder`（`RemoveAt`+`Insert`；用 `DispatcherQueue.TryEnqueue` 推到下一轮
    消息循环，避免在拖放宿主回调里摘掉源按钮）。拖动时在按钮之间画 2px 强调色插入提示条（`InsertionIndicator`，位置
    用 `Margin.Left`，它不在同一棵子树里所以用 `TransformToVisual` 换算到 `PinnedItemsHost`）。**Button 的 `CanDrag`
    在 WinUI 3 无效**（第 25 条）。
* **侧边栏「收藏夹」镜像工具条固定目录**：排最上面（`主目录` 之上），子项与 `MainViewModel.PinnedFolders` **同序同名**
  （增删/排序即时同步：订阅 `PinnedFolders.CollectionChanged` → `SidebarViewModel.SyncFavorites`；`RefreshDrives`
  重建整树后也要再灌一次）。收藏项是普通目录节点。拖目录到分组或其子行即收藏（提示“收藏到侧边栏”，悬停整行强调色
  高亮 `SidebarNodeViewModel.IsDropTarget`）；落点是**行级**的（第 54 条）。落到其它树节点、或拖排序格式经过侧边栏时
  一律 `None`。右键收藏项 → 「取消收藏」（`UnpinRequested` → `MainViewModel.UnpinFolderByPath`）；工具条被隐藏时这是
  唯一移除入口。回归 `test-pin-drag.ps1` 用例 0 与 5。
* **磁盘插拔实时反映**：`DeviceChangeService`（内部 `Native/VolumeChangeWatcher`）子类化主窗口，接**广播给所有顶层
  窗口**的 `WM_DEVICECHANGE`（卷到达/移除 + `DBT_DEVNODES_CHANGED`），`MainWindow` 安排两次延迟刷新（600 ms 合并 +
  2500 ms 兜底）。刷新走 `MainViewModel.RefreshDrives()`：工具条磁盘区与侧边栏「此电脑」做**差量**更新（清单没变一个
  控件都不动；变了只增删/挪动那个盘节点、复用未变节点），不重建整树、不丢展开状态、不清「收藏夹」。窗口藏在托盘时仍
  收得到；只有「退出」才 `Detach()`。「工具 → 重新扫描磁盘」同一入口。第 56 条；回归 `test-drive-hotplug.ps1`。
* **「此电脑」里也列 Windows「网络位置」**：`%APPDATA%\Microsoft\Windows\Network Shortcuts` 下每个子目录读
  `target.lnk` 的目标（`Native/ShellLinkInterop` 的 `IShellLinkW`），显示名 = 目录名、`FullPath` = 目标，作为**排磁盘
  之后**的普通可展开节点（UNC 也能走现有导航与面包屑）。解析不出来的跳过；**不判断目标是否在线**。`RefreshDrives`
  必须把网络位置算进期望清单（第 63 条）。回归 `test-network-locations.ps1`。
* **文件列表区底部一条状态栏**（`Views/StatusBarView` + `StatusBarViewModel`）：挂 `MainWindow` 窗格那一列的**第 1
  行**（第 0 行才是放 1~2 个窗格的 Grid），所以侧边栏保持全高、状态栏只占文件列表区、高度
  `{StaticResource ExRowHeight}` = 24 DIP。**不要再回到“DetailsView 里每标签页一条”**（临时条，已删）。
  * `N 项`：`FolderTabViewModel.ItemCount`，只数当前目录直接子项，不含就地展开的行。
  * 选中摘要：`选中 2 项（合计 3.00 KB）`。**只累加文件**（目录要递归枚举），全目录时显示「均为文件夹」，含目录时
    悬停提示“合计不含文件夹”。
  * 卷容量：`D: 可用 120 GB / 共 512 GB`，走 `IDriveService.GetDriveForPath(path)`（每次导航后台重读，同卷 3 秒
    复用；**不要在 UI 线程调**），UNC / 未就绪 / 路径空时整段留空。
  * 始终显示**活动窗格的活动标签页**：`StatusBarViewModel` 自己订阅 `MainViewModel.ActivePane` →
    `PanelViewModel.ActiveTab` → 标签页的 `ItemCount`/`Selection`/`CurrentPath`。
  * UIA 里只有最外层 Grid 有 `AutomationProperties.Name="状态栏"`，**里面三个 TextBlock 故意不加**：这样它们的 UIA
    名字就是自己的文本（容器给名字、叶子 TextBlock 不给）。
* **导航条属于标签页不属于窗格**：`Views/NavigationBarView` 是 `TabView.TabItemTemplate` 里 `TabViewItem` 内容的第 0
  行（第 1 行是 `DetailsView`），VM 是 `FolderTabViewModel` → 每个标签页各有历史与编辑态。加“导航条上的东西”改
  `NavigationBarView.xaml`。
* **地址栏 = 面包屑 + 可编辑输入框**（`Views/PathBreadcrumb`，每标签页一份）：面包屑由 `PathSegments` 渲染
  （`ItemsControl` + 水平 `StackPanel`，段间 chevron 字形 `E76C`，第一段不画）。
  * 点某段：非当前段 → `NavigateToSegmentCommand`；当前目录段 → 进编辑态；点右侧空白区（`BlankArea` 按钮，位置/宽度
    按面包屑实际宽度算）也进编辑态 —— 它用 `ExGhostButtonStyle`，不要换回 `ExSubtleButtonStyle`。
  * 编辑态在 `FolderTabViewModel.IsPathEditing`（不在视图里）：回车 `NavigatePathCommand`、`Esc` 或失焦
    `CancelPathEdit()` 取消；**任何一次成功导航都会自动退出编辑态**。路径比地址栏宽时 `CrumbScroll` 滚到最右（当前
    目录永远可见），左端露省略号提示还有被裁掉的分段。
* **右键菜单两种风格**：右键行 → 该（批）条目的菜单，右键空白处 → 当前目录背景菜单；
  「配置 → 设置… → 右键菜单 → 使用内置的轻量右键菜单」决定用哪种（**默认开 = 内置**，关掉用系统外壳菜单）。两种内容
  **故意不一样**。
  * **内置菜单（默认，几乎瞬时）**：`DetailsView` 现场搭 WinUI `MenuFlyout`，只绑 exdir 自己的命令，不建 COM、不问
    外壳。行：`打开` /（选中的是真实压缩包文件时）`使用 7-Zip 打开` / `解压到下载文件夹` / `压缩` / `在资源管理器中显示`
    /（分隔）/ `剪切`(Ctrl+X) / `复制`(Ctrl+C) / `粘贴`(Ctrl+V) / `删除`(Del)
    /（分隔）/ `复制路径` / `属性`；背景：`粘贴`(Ctrl+V) / `新建文件夹` / `刷新` / `全选` /（分隔）/ `复制当前路径` /
    `在此处打开终端`。剪贴板没有文件时「粘贴」灰（建菜单时现查一次 CF_HDROP）；`剪切`/`复制` 可点状态跟选中项走；
    「使用 7-Zip 打开」没装 7-Zip 时置灰且标题里写明原因（见下面“压缩包”一节）。
    「压缩」（任意真实文件 / 目录都能用，包内虚拟行不行）把选中项打成一个 zip（目录含整棵子树、空目录也保住）：
    输出目录 = 设置「文件列表 → 压缩输出目录」，留空则用「下载」文件夹；包名 = 单选时用条目名（文件去掉扩展名、
    目录保留全名）、多选时用当前目录名，重名先加 `(2)(3)…`；写完把 zip **复制到剪贴板**（`CF_HDROP` + 复制意图，
    顺手清掉内存里的包内条目）并弹一条带「打开目录」的绿色 InfoBar（标题「压缩完成」）。
    实现：`Services/ICompressionService` + `CompressionService`（走 BCL 的 `System.IO.Compression.ZipArchive`，
    与只读浏览那条 7z.dll 的路互不干扰）、包名 / 输出目录的选择在 `Helpers/CompressTargets`、命令在
    `FolderTabViewModel.CompressSelectionCommand`。回归 `test-compress.ps1`（需交互桌面）+ `tools/archive-smoke`。
    `属性` 走 `IShellService.ShowProperties`（`Verb = "properties"`，不建 `IContextMenu`）；`新建文件夹` 由
    `CreateNewFolderAsync` 自建（重名依次 `(2)(3)…`）并选中；其余复用标签页命令与视图 `SelectAll`。风格在**每次右键
    时现读** `UseBuiltInContextMenu` → 改设置立即生效。
  * **系统菜单**（关掉开关才用）：内容全来自 `IContextMenu`，第三方项（7-Zip / Git / VS Code / WPS）与“发送到 /
    打开方式”子菜单都在，默认全开、可在同一页逐项关掉（只对系统菜单生效）。
  * 实现：`ShellContextMenuService` + `Native/ShellContextMenuInterop`。选中项走 `IShellFolder.GetUIObjectOf`，目录
    背景走**目录自己**的 `IShellFolder.CreateViewObject`（不是它所在目录的），再 `QueryContextMenu` 填 HMENU →
    `TrackPopupMenuEx(TPM_RETURNCMD)` → `InvokeCommand(偏移 = id − idCmdFirst)`（exdir 自己不实现任何命令）。弹出期间
    要在宿主窗口挂 `ShellMenuHost`（`SetWindowSubclass`）转发 `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MEASUREITEM`
    给 `IContextMenu2/3`（第 43 条）。
  * 菜单项稳定标识 `ShellMenuItem.Key`：优先外壳给的规范动词（`GetCommandString(GCS_VERBW)`，如 `open` /
    `7-Zip.Compress`），拿不到退回“上级菜单文本 + 菜单文本”；用动词才能让“打开”在文件/文件夹/背景三个上下文共用同一
    开关。`AppSettings.ShellMenuDisabledItems` 存这些 key（空 = 全开）。清单 = 打开设置页时用样本目标现枚举（%TEMP%
    下样本 .txt、配置目录本身、配置目录背景）∪ 每次真弹菜单时顺手记住的（`ShellMenuKnownItems`，保存落盘），合并
    去重；重名项补后缀（开关的 UIA 名字就是标题）。
  * 关掉的项在 **`TrackPopupMenu` 之前**按位置 `RemoveMenu` 删（不会打乱其它 id），顺带清理空子菜单与重复/首尾分隔
    符。右键事件挂 `DetailsView` **最外层 Grid**（第 39/40 条）；行上右键先把该行选中再弹。`DpiHelper.ToScreenPoint`
    负责 DIP → 屏幕物理像素（`TransformToVisual(null)` + `ClientToScreen`）。回归 `test-context-menu.ps1`。

### 托盘驻留（单窗口模式，2026-09）

**常驻托盘的单窗口程序**：点关闭按钮（或 `Alt+F4`）**不退出**，只隐藏窗口；进程、DI 容器、两个窗格、标签页、已枚举
目录、图标缓存都原封不动留着，再打开就是一次 `ShowWindow`。**只有菜单「退出」才真的结束进程**。

* 托盘图标 = `H.NotifyIcon.WinUI`（锁 **2.3.2**：2.4.x 只出 `net10.0`）。图标本体是 `Shell_NotifyIcon`，非打包可用。
* `MainWindow.xaml` 里声明 `<tb:TaskbarIcon x:Name="TrayIcon">`（`xmlns:tb="using:H.NotifyIcon"`），它是 0×0 的
  `FrameworkElement`、不占布局（UIA 里矩形为空）；位图在 code-behind 用
  `System.Drawing.Icon(Assets\exdir.ico, DpiHelper.GetSmallIconSize())` 设（不走 `ms-appx://`）。
* **托盘菜单只能用 `Command`，不能挂 `Click`**（第 47 条）；只有「显示主窗口」/「退出 exdir」两项（非打包模式子菜单
  不可用）。左键单击 = 唤回窗口（`LeftClickCommand` + `NoLeftClickDelay="True"`）；右键弹菜单。
* 拦截在 `MainWindow.OnAppWindowClosing`：`args.Cancel = true` + `HideToTray()`（`H.NotifyIcon.WindowExtensions.Hide()`
  = `ShowWindow(SW_HIDE)` + 效率模式）。隐藏/显示前都会 `SaveWindowPlacement()` + `SaveSession()` 落盘 → 隐藏状态下被
  杀也不丢列宽/会话（测试脚本收尾才可直接 `Kill`）。
* **最大化窗口藏起来再唤回不能被还原成普通尺寸**：`Show()` 走 `ShowWindow(SW_SHOWNORMAL)` 会顺手取消最大化 → 隐藏时
  记住“之前是不是最大化”，唤回后自己 `presenter.Maximize()`。
* **单实例闸门在 `Program.cs`**（csproj 定义 `DISABLE_XAML_GENERATED_MAIN`）：`Helpers/SingleInstance` 用一个命名内核
  事件（`Local\exdir.activate`）同时做两件事 —— `EventWaitHandle` 的 `createdNew` 就是“我是不是第一个实例”，第二个
  实例 `Set()` 它 = “把已有窗口叫出来”。这一步在 `Application.Start` **之前** → 第二次双击 exe 只跑几十毫秒（不会白
  初始化一遍 WinUI，也不会多出第二个托盘图标/第二份会话互相覆盖 config.json）。
* UIA 里看托盘图标的方法与坑见 `test-tray.ps1` 注释；回归 `test-tray.ps1`（4 个用例 17 条断言）。

### 命令行调用（`exdir [path]`，2026-09）

```
exdir                  → 在已有窗口（隐藏着先唤回）里打开**当前工作目录**
exdir D:\projects      → 同上，打开指定目录
exdir D:\a\b.txt       → 打开文件所在目录并选中它
```

* **解析在 `Program.Main` 的第一件事**（`Helpers/CommandLine.Parse`）：不带路径参数时用**本进程**工作目录，带相对路径
  也按本进程工作目录解析成绝对路径。⚠ `Main(string[] args)` 收到的数组**不包含 exe 路径**（与
  `Environment.GetCommandLineArgs()` 差一位），别从 `args[1]` 开始（第 70 条）。
* **不带参数时“智能一下”**：工作目录是 exe 所在目录、`C:\Windows`、System32 / SysWOW64 这类位置时（双击 exe、点任务
  栏图标、快捷方式的默认工作目录就是这些）**不导航**，只唤回窗口（否则每次双击 exe 就把浏览位置换成安装目录）。判据
  `CommandLine.LooksLikeLauncherDirectory`。
* **已在运行的实例怎么收到请求**：命名事件 `Local\exdir.activate` 的 `createdNew` 回答“我是不是第一个实例”（不需要
  额外互斥体），命名管道 `exdir.activate` 送“请打开这个目录”。第一个实例在 `TryClaim` 里就建服务端（保证第二个实例
  连上来时管道名字已存在），后台线程用**同一个服务实例**反复 `WaitForConnection` → 读一条 → `Disconnect`（名字始终
  在）。第二个实例写完就退出（dist ~160 ms、Debug ~65 ms）。
* **请求落到哪**（`MainViewModel.HandleActivationAsync`）：目录 → **活动窗格新开标签页**并切过去；文件 → 打开它所在
  目录（同样新标签页）并选中它（`NavigateAsync(selectPath:)`）；活动标签页**已在同一目录**时不再新开（带了文件就重新
  定位并选中）；路径不存在 → **不新开**，让当前标签页显示「无法打开：…」（导航失败不改当前目录）。
* **窗口先叫到眼前、再导航**：`MainWindow.HandleActivation` → `ShowFromTray()` + 请求入队；队列串行，且要
  `await _initialized`（会话恢复完）才导航，否则新标签页会和恢复出来的标签页抢活动标签。启动时带的那一次也走同一队列
  （`RootGrid.Loaded` 里恢复完会话之后入队）。
* **`--preload`** 的载荷是约定值 `@preload`（`CommandLine.PreloadRequest`）：已有实例收到它**什么都不做**（不能弹
  窗口）——空串才是“只唤回窗口”。路径载荷永远是绝对路径，不会撞上这个以 `@` 开头的约定值。
* 回归：`test-command-line.ps1`（10 个用例，全程 UIA）；坑见第 70～74 条。

### 开机自启 / 预热启动（`--preload`，2026-09）

设置「启动 → 开机时自动启动 exdir」（`AppSettings.StartWithWindows`，默认关）：打开后往
**`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`** 写一个值（值名 `exdir`，数据 = `"<exe 绝对路径>" --preload`，
`Helpers/AutoStart`）—— 非打包没有包标识、用不了 `StartupTask`，写 HKCU 不需要管理员。

* **登录时起来的这一份不显示主窗口**：`App.OnLaunched` 看到 `CommandLine.IsPreload` 就调 `MainWindow.StartPreload()`
  而不是 `window.Activate()`（“已隐藏到托盘”这个状态要记上，否则之后 `ShowFromTray` 走错分支，第 75 条）。
* **预热做什么**：`MainViewModel.InitializeAsync`（上次打开的目录会话 + 侧边栏树，弹窗口前最贵的一步）与
  `PreloadIconsAsync`（活动标签页首屏几十行的外壳图标，提取是串行的）。窗口位置恢复 / 菜单构建留给真正显示时的
  `Loaded`（那时才拿得到真实 DPI）。
* **之后双击 exe 有多快**：单实例闸门把“唤回窗口”送进已预热的进程，`ShowWindow` 一下就行（第二个进程 ~70 ms 退出），
  窗口一出来就带着目录与图标。
* 回归：`test-autostart.ps1`（3 个用例 18 条断言）；设置页那个开关本身（真的写/删 Run 项）由 `test-settings.ps1`
  用例 10 断言。

### 复制 / 剪切 / 粘贴 / 删除与“拖动移动”（2026-09）

* **剪贴板用系统标准格式，与资源管理器互通**（`IClipboardService` + `Native/ClipboardInterop`）：写 `CF_HDROP` +
  `Preferred DropEffect`（1 = 复制、2 = 剪切），读时先 Win32 `GetClipboardData`、拿不到退回 OLE `OleGetClipboard` +
  `IDataObject`。写入走 Win32 `SetClipboardData`（内存所有权交系统，进程退出后内容仍有效，不需要 `OleFlushClipboard`）；
  `Preferred DropEffect` 必须 `RegisterClipboardFormat("Preferred DropEffect")`（`0x000C` 是 `CF_WAVE`，第 57 条）。
  读到“剪切”时源目录里搬走后 `Clear()`（与资源管理器一致：只能生效一次）。
* **真正的复制/移动/删除交给外壳**（`IFileOperationService` + `Native/FileOperationInterop`）：`SHFileOperation` 的
  `FO_COPY` / `FO_MOVE` / `FO_DELETE` → 进度对话框、同名冲突、目标不存在就建都是资源管理器同款，exdir 自己一行搬运
  代码都没有。
  * 必须在 **STA** 线程上跑（要起模态进度对话框），线程池是 MTA → 每次操作开专用 STA 线程（主窗口消息循环不阻塞，
    进度对话框以主窗口为属主），完成在 `Completed` 事件里汇总。
  * 完成后 `MainViewModel.OnFileOperationCompleted` **按路径找**受影响的标签页（源目录 + 目标目录）重新枚举，而不是
    只刷发起操作的那个；删除时若某标签页正开在被删目录里则退到上一级。
* **删除**：`Delete` 进回收站、`Shift+Delete` 永久删除。回收站靠 `FOF_ALLOWUNDO`，还要带 `FOF_WANTNUKEWARNING`；`FO_DELETE`
  不看 `pTo`、必须传 `null`（第 61 条）。确认框由外壳弹（owned 的 `#32770`，UIA 里要从主窗口 `Descendants` 找）；点
  “否”时 `fAnyOperationsAborted != 0` → `FileOperationResult.Canceled`。
* **命令与快捷键**：`CopySelectionCommand` / `CutSelectionCommand` / `PasteCommand` / `DeleteSelectionCommand` /
  `DeleteSelectionPermanentlyCommand`（「编辑」菜单与 `MainViewModel` 的同名命令只是转发到活动标签页）。
  `Ctrl+C/X/V` 是挂 `DetailsRoot` 上的 `KeyboardAccelerator`（焦点在地址栏时仍是 TextBox 自己的行为）；`Delete` /
  `Shift+Delete` 用 `DetailsRoot.PreviewKeyDown` 自己判 Shift（第 62 条）。
* **拖动移动**：把行拖到某个**目录行**上（或列表空白处 = 当前目录，或另一个窗格）即移动；按 `Ctrl` 是复制。
  * 拖拽源是 `DetailsRoot` 自己识别的手势（按下 + 移动超过 4 DIP 后 `StartDragAsync`），**不用**
    `ListView.CanDragItems`（第 58 条）。数据包用 `exdir/paths`，额外带 `exdir/folders-only` 让工具条固定目录区区分
    “拖的是文件”；允许的效果带 `Move`。
  * 落点是**整个文件列表**（`DetailsRoot`，`AllowDrop=True`）：行容器拖放被关掉（`ItemContainerStyle` 里
    `AllowDrop=False`）。鼠标下是哪一行用“光标位置 + 已生成行容器的实际矩形”算（`RowAt`），**不用** `e.GetPosition`
    （第 59 条）、也不用 `e.OriginalSource`。命中行高亮：`FileItemViewModel.IsDropTarget` → 行模板那层强调色 `Border`。
  * **兜底**：`StartDragAsync` 返回后再判一次 —— 左键还按着（= 用户按了 Esc）或光标已不在本列表就不管，否则按松开时
    光标位置自己把这次移动做完（日志 `拖放兜底：…`，第 60 条）。
  * 回归 `test-file-ops.ps1`（9 个用例；需交互桌面，shell 是管理员时自动改用 `explorer.exe` 降权，第 21 条）。

### 压缩包只读浏览（双击压缩包 = 以目录形式进入，2026-09）

双击受支持的压缩包不再交给外部程序，而是在**当前标签页把它当目录导航进去**：包根是一层目录，包内目录可继续下钻 /
行内展开，面包屑 / 前进后退 / 排序 / 状态栏都照常，导航条右侧多一个「只读」徽标
（`AutomationProperties.Name="只读压缩包"`）。

* **压缩包文件行像目录一样就地展开**（2026-10）：当前目录里的 `.zip` / `.7z` / `.tar.gz` … 行首有展开箭头
  （与目录行同款），点一下就在**当前列表里**列出包内条目（**不导航**、不出现「只读」徽标），再点一次折叠回去。
  * 靠 `FileSystemEntry.IsArchive` 判断（`FileSystemService.CreateEntry` 里用 `_archive.IsArchiveFile()` 标出；
    `IsArchiveFile` 先看扩展名，非压缩扩展名只是一次字典查找，不会多出文件系统访问）。
    `FileItemViewModel.IsExpandable = IsDirectory || IsArchive` 是展开的**唯一入口**（`ToggleExpandAsync` / `FindNode` /
    `RestoreExpansionAsync` / `Right` 方向键都用它），`CanExpand`（箭头与 `IsHitTestVisible`）也从它算。
  * 展开出来的子行 `Depth + 1`（缩进照旧）；`EnsureChildrenAsync` 走的是同一个
    `FileSystemService.EnumerateDirectoryAsync`，包内条目由 `ArchiveService.ListAsync` 给出（`IsInArchive = true`），
    所以加密包会就地弹密码框，打不开就把原因显示在 InfoBar 上、箭头留着让用户重试。F5 也会 `Invalidate` 就地展开
    的那个包（那种情况下 `ArchiveFile` 为 null，只有 `_expandedPaths` 里有它）。
  * **目录内外的区别就两个标记**：`IsArchive`（磁盘上真实存在的文件：双击进包、可删除 / 拖拽 / 属性）与
    `IsInArchive`（包内虚拟路径：只读）。守卫因此分两套：`RefuseInArchive()`（当前目录在包内，管粘贴 / 新建文件夹 /
    终端 / 拖入）与 `RefuseSelectionInArchive()`（选中项里有包内行，管剪切 / 删除 / 属性 / 在资源管理器中显示）；
    「复制」不走拒绝，而是按选中项自己解析出压缩包走内存剪贴板（`CopyArchiveSelection`），真实文件与包内行混选、
    或跨两个包混选时明确报错。
  * 就地展开出来的包内行：双击目录 → 进包（导航），双击文件 → 解到临时目录用默认程序打开；拖拽从源头取消、
    拖到包内目录行上不接受（`DetailsView.DragStarting` / `TryResolveDrop`），右键一律用只读版内置菜单。
    行内图标按 `isVirtualDirectory: IsInsideArchive || item.IsInArchive` 取（包内目录用通用文件夹图标）。
  * 包内的**嵌套压缩包**不单独展开（`ArchivePath.TryParse` 只认最外层那个包，`IsArchive` 保持 false）；
    只比目录多了一项：压缩包行本身仍是文件，排序 / 删除 / 拖拽都按文件走。
  * 回归：`test-archive-copy.ps1` 用例 7～9（UIA，不需交互桌面）、`test-archive.ps1` 用例 13（真鼠标）；第 88 条。

* **虚拟路径**：位置 = 「压缩包全路径 + `\` + 包内路径」（`D:\dl\foo.tar.gz\sub\a.txt`），就用现有路径字符串 → 所以
  `DirectoryInfo.Parent`、面包屑切分、会话落盘全都不用改；解析在 `ArchiveService.TryParse`（从左往右找第一个“存在
  且扩展名在清单里”的前缀 → 嵌套压缩包的路径仍挂在最外层文件上）。包内路径一律 `\`、无首尾分隔符，`.` 段跳过、
  `..` / 冒号 / 控制字符拒绝。
* **认哪些扩展名**：`Helpers/ArchiveFormats` 的 `Core` 清单（zip/zipx/7z/rar/tar/gz/tgz/bz2/tbz/tbz2/xz/txz/zst/tzst/
  lzma/tlz/lz/cpio/ar/deb/iso）∩ 当前 7z.dll 真认识的格式（`IsArchiveFile` 同时看两边 → 升级 7z.dll 后新增格式自动
  可用；为假时双击退回默认程序）。加格式只改那个清单（判断入口只有 `IsCoreExtension` 一处）。`.docx` / `.jar` /
  `.cab` 这类**故意不在清单里**（用户预期双击用默认程序打开）。
* **`.iso` 也当压缩包浏览**：7z.dll 的 **Iso** 与 **Udf** 两个处理器都声明了 `iso`，而一张盘上常常**两套文件系统
  的内容不一样**（Windows 刻的 UDF 盘上 ISO9660 那半只有一张写着「本盘使用 UDF」的 README.TXT）→ `BuildIndex`
  把能打开的处理器都过一遍、**条目多的那个说了算**（并列保留格式表顺序；`ItemCount` 当粗筛，不比手上这份多就不读
  属性）。纯 ISO9660 走 Iso、UDF 走 Udf（xorriso / mkisofs / Windows 自带工具造的都能进）。两个小差异：只有
  ISO9660（没有 Joliet/UDF）时条目名是**大写 8.3**（`HELLO.TXT`）；El Torito 的 `[BOOT]\Boot-NoEmul.img` 会多出一个
  隐式目录 `[BOOT]`。回归 `archive-smoke`（IMAPI2FS 现造 ~60 KB 测试 ISO + UDF 映像 + 手写 ISO9660 造的「UDF 说明
  盘」）与 `test-archive-copy.ps1` 用例 6；第 87 条。
* **引擎**：随包分发的**原生 7z.dll**（`native/x64/7z.dll`，来源/许可/升级见 `native/README.md`），互操作层
  `Services/Native/SevenZipInterop.cs`（只读 `IInArchive` + 打开/解压回调）；坑见第 77～86 条。dll 缺失 / 非 x64 时
  这一块整体关闭（双击退回默认程序，日志留一行）。
* **索引缓存**：按「路径 + 大小 + 修改时间」缓存条目表（上限 32 个），包内目录直接从它切出来 → 来回导航不反复解压；
  **F5** 先 `Invalidate` 再重读（外部改过包就能刷新到）；关闭时删掉链式解开的临时 tar。索引构建、解压都在后台线程。
* **`.tar.gz` / `.tgz` 透明解开一层**：单文件压缩器里只有一个条目、且派生出来的内层名以 `.tar` 结尾时，把中间那个 tar
  解到 `%LOCALAPPDATA%\exdir\archive-cache\tar\<hash>.tar` 再用 tar 处理器打开（内层 > 2 GB 时降级为显示 `xxx.tar`
  一行）。单文件压缩器的 `kpidPath` 是空的，内层名由压缩包名派生（第 80 条）。
* **只读**：粘贴 / 删除（Del、Shift+Del）/ 新建文件夹 / 剪切 / 拖拽（拖入拖出都不启动）/ 在此处打开终端 / 在资源管理器
  中显示 / 属性 全部禁用并提示「压缩包内不支持该操作（只读浏览）」；**唯一例外是「复制」**。内置右键菜单在包内换成
  “只读版”（行：打开 / 复制 / 复制路径；背景：刷新 / 全选 / 复制当前路径），而且包内**强制用内置菜单**（系统外壳菜单
  处理不了虚拟路径）。守卫写在 `FolderTabViewModel`（`RefuseInArchive()`）+ `DetailsView`（`DragStarting` /
  `TryResolveDrop`），键盘入口与右键入口共用同一套。
* **包内「复制」→ 外部目录粘贴**：包内条目没有真实路径、写不进 `CF_HDROP` → 分两拍：包内 `Ctrl+C` / 右键「复制」把
  「压缩包 + 包内相对路径」记在**内存**（`IArchiveClipboardService`，不动磁盘、顺手清空系统剪贴板）；到真实目录
  `Ctrl+V` / 右键「粘贴」时才 `ExtractForCopyAsync` 把选中条目（目录含整棵子树、空目录也保住）解到
  `archive-cache\copy\<guid>\<序号>\…`，再交给 `IFileOperationService.CopyAsync`（进度对话框、同名冲突与普通复制
  一致）；中转副本复制一结束就删（进程中途挂了由 `CleanupTemp` 兜底）。粘贴时**优先看系统剪贴板**（包内复制会清空它
  → 它非空就一定更新）。每个选中项解到自己的 `<序号>` 子目录 → 跨目录同名不覆盖，而交出去的文件名仍是包内那个
  （`SHFileOperation` 只看最后一段）。回归 `test-archive-copy.ps1` + `archive-smoke` + `test-archive.ps1` 用例 12；
  第 85 条。
* **包内文件双击**：解到 `archive-cache\open\<hash>-<压缩包名>\<包内相对路径>`（同名且大小一致就复用）再用默认程序
  打开；日志留一行 `打开压缩包内文件：… → …`。
* **加密包**：要密码就抛 `ArchivePasswordRequiredException`，`FolderTabViewModel` 问
  `IDialogService.RequestPasswordAsync`（最多 3 次，密码只存本次运行内存），取消或连续失败则显示「需要密码：<名字>」
  （第 82 条）。
* **会话与命令行**：包内目录随会话落盘并在重启后恢复（`RestorePaneAsync` 改用 `ResolveDirectoryAsync` → 虚拟路径也算
  “存在”）；`exdir <压缩包>` 新标签页进包，`exdir <压缩包>\<包内目录>` 直接进包内目录。
* **临时文件清理**：启动与退出各调一次 `CleanupTemp()` —— 中间 tar 与 `copy` 中转目录直接删，给默认程序打开的副本
  只删**一天前**的（第 84 条）。
* **右键两个新入口：用 7-Zip 打开 / 解压到下载文件夹**（真实压缩包文件行，2026-10）：内置菜单在选中的全是真实
  压缩包文件（`FileItemViewModel.IsArchive`，可多选；包内只读条目与目录都不算）时多两项：
  * `使用 7-Zip 打开` —— 交给系统上装的 7-Zip 图形界面：`Helpers/SevenZipLocator` 按「注册表 `Path`（HKCU / HKLM /
    WOW6432Node）→ `%ProgramFiles%\7-Zip` 等常见目录 → `PATH`（scoop / winget 在这里被找到）」找 `7zFM.exe`，
    结果用 `Lazy` 缓存一次（**exdir 自己只带只读引擎 `7z.dll`，没有界面程序**）。没装时这一项**留着但置灰、标题写成
    「使用 7-Zip 打开（未找到 7-Zip）」**（直接消失会让人以为功能没做），并在日志里留一行原因。启动外部程序走
    `IShellService.OpenWithProgram`（View/VM 不自己碰 `Process.Start`）。
  * `解压到下载文件夹` —— `IArchiveService.ExtractAllAsync` 把整包解到 `Downloads\<包名>\`（包名 = 去掉最后一个扩展名；
    同名目录已存在时加 `(2)(3)…`，不往已经解出来的目录里混），包内目录结构（含空目录）照原样重建。解压在后台线程做
    （期间列表显示转圈），失败 / 取消会把刚建出来的那个目录删掉；加密包照旧问密码（最多 3 次）。完成后文件列表顶部
    弹一条绿色 `InfoBar`（`已解压 N 个文件到 …` + 「打开目录」按钮，见 `DetailsView.xaml` 与 `FolderTabViewModel`
    的 `StatusMessage` / `OpenStatusTarget`），并触发 `IArchiveService.Extracted` → `MainViewModel.OnArchiveExtracted`
    把正开在那个目录（及其父目录）里的标签页刷新一遍。
  * 两个入口都只出现在**真实**压缩包文件行上：包内条目（虚拟路径）没有真实路径，目录行也不是压缩包。

* 回归：`test-archive.ps1`（真鼠标，需交互桌面）、`test-archive-copy.ps1`（全程 UIA，不需交互桌面）、
  `test-archive-extract.ps1`（真鼠标右键，需交互桌面）、`tools/archive-smoke`（服务级，不需交互桌面）。

### 键盘快捷键（定义在 MainWindow.xaml 的 `Grid.KeyboardAccelerators`）

| 快捷键 | 动作 |
| --- | --- |
| `Alt+←` / `Alt+→` / `Alt+↑` | 后退 / 前进 / 上一级 |
| `F5` | 刷新活动窗格 |
| `Ctrl+T` / `Ctrl+W` | 新建 / 关闭标签页 |
| `Ctrl+H` | 显示/隐藏隐藏文件 |
| `Ctrl+B` | 显示/隐藏侧边栏 |
| `Ctrl+A` | 全选活动窗格文件列表当前可见的行（焦点在地址栏时仍是文本框全选） |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | 复制 / 剪切 / 粘贴（挂在 `DetailsView` 根 Grid 上） |
| `Delete` / `Shift+Delete` | 删除到回收站 / 永久删除（走 `DetailsRoot.PreviewKeyDown`，仅作用于文件列表） |
| `F6` / `F10` | 切换活动窗格 / 单双窗格切换 |
| `Ctrl+L` / `Alt+D` | 编辑活动窗格的地址栏（等价于点地址栏空白处） |

另外：`文件列表 / 侧边栏文件夹树` 里的**目录**可直接拖到工具条右侧的“固定目录”区固定；文件列表里的文件 / 目录拖到
某个**目录行**上（或拖到另一个窗格）就是移动，按住 `Ctrl` 是复制。

### 设置窗口（所有配置项的唯一入口，2026-09 从 ContentDialog 改为独立窗口）

* 菜单栏**「配置 → 设置…」**打开 `Views/SettingsWindow`（普通 `Window`，标题「设置」）。**同一时刻只开一个**
  （`MainWindow._settingsWindow` 持有，已开就 `Activate()`；`Closed` 清引用）。
* **改动即时生效**：没有「保存 / 取消」，任何一项被改就立刻写回 `AppSettings` 并落盘（`SettingsViewModel.Changed`
  → `MainViewModel.ApplySettings`）。
* **Windows 11 风格**：左 `NavigationView` 选分类，右一列设置卡片（社区工具包 `SettingsCard`：标题 + 灰色说明在左、
  控件在右，悬停/圆角/高对比全跟系统走，**不再自己写行模板**）。六个分类：

  | 分类 | 配置项 |
  | --- | --- |
  | 文件列表 | 显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面 / 行高（滑块，默认 28）/ 压缩输出目录（可直接编辑的文本框 + 「浏览…」系统文件夹选择器，留空 = 下载文件夹） |
  | 外观 | 主题（跟随系统/浅色/深色，默认跟随系统）/ 标签页使用直角（默认开）/ 过渡动画 |
  | 布局 | 列宽自动适应窗格宽度 / 显示工具条 / 显示侧边栏 / 双窗格模式 |
  | 启动 | 开机时自动启动 exdir（登录后在后台预热，不显示主窗口） |
  | 侧边栏 | 显示「主目录」/「收藏夹」/「云存储」/「此电脑」四个分组（默认全开）；「主目录」里显示桌面 / 文档 / 下载 / 图片 / 音乐 / 视频六个标准文件夹（**默认只开桌面与下载**） |
  | 右键菜单 | 使用内置的轻量右键菜单（开关，默认开）/ 系统右键菜单项逐项开关（动态清单） |

  * 分类是 `Models/SettingsCategory`（枚举）+ `SettingsViewModel.Categories`（顺序即导航顺序），绑到
    `NavigationView.MenuItemsSource`（`MenuItemTemplate` 的根必须是 `NavigationViewItem`）。切换分类**用
    `SelectionChanged` 而不是 `ItemInvoked`**（第 52 条）。
  * 右侧六页可见性绑 `IsFileListPageVisible` / `IsAppearancePageVisible` / `IsLayoutPageVisible` /
    `IsStartupPageVisible` / `IsSidebarPageVisible` / `IsShellMenuPageVisible`（`SelectedCategory` setter 里一次性通知
    这六个，省得每页各写一个枚举转换器）。非当前页是 `Collapsed`、**UIA 树里根本没有它们** → 回归脚本“切到某分类后
    只看得到该分类的开关”本身就是“切页真的生效”的验证。
  * 每行用 `{x:Bind}` 把 VM 属性绑到卡片里的控件上（`ToggleSwitch.IsOn` / `Slider.Value` / `ComboBox.SelectedIndex` 都
    是 TwoWay）；开关的 `AutomationProperties.Name` 就是卡片标题（脚本按这个名字找它、用 `TogglePattern` 拨）；滑块用
    `RangeValuePattern` 读写；「主题」是三态 `ComboBox`（`SelectedIndex` 对应 `ThemeHelper` 的 0/1/2），脚本靠
    `ExpandCollapse` + `SelectionItemPattern` —— **它没有 `TogglePattern`**，所以不干扰“这一页有几个开关”的计数（这也是
    选 ComboBox 不选 RadioButton 的原因之一）。
  * **窗口默认 860×800 DIP**（`SettingsWindow.DefaultWidthDips/DefaultHeightDips`），工作区居中、放不下就退让（先夹
    再定）。宽度不能小：`SettingsCard` 在卡片宽 < 476 DIP 时会把控件换行到标题下方（第 51 条）。
  * **正文是 `Views/SettingsView`（UserControl），不是写在 Window 里**（第 50 条）：`SettingsView.ViewModel` 是依赖
    属性，由窗口赋值。
  * **「右键菜单」页是动态清单**（`ItemsControl` + `DataTemplate`，每行仍是 `SettingsCard`）：构造时先用已记下来的清单
    填一遍（不碰 COM），`SettingsView.Loaded` 后 `DispatcherQueue.TryEnqueue(RefreshShellMenuItems)` 现枚举补全 →
    打开不卡；现枚举那一次也算改动、会落盘（`ShellMenuKnownItems`）。重建清单时保留用户刚拨过的开关。用 `ItemsControl`
    不用 `ListView`（后者会虚拟化掉没显示的行，脚本数不全）。新增一项要同时改三处：`ShellMenuItemViewModel`（必须
    `ObservableObject`）、`MainViewModel.ApplySettings`、`test-settings.ps1` 用例 5。
* **编辑的是 `SettingsViewModel`（`MainViewModel.CreateSettingsEditor()` 造的）**：当前值 + 变更通知，每个属性走
  `SetAndNotify`（真变了才 `Changed?.Invoke`），`ShellMenuItems` 每行的 `PropertyChanged` 也汇总到同一个 `Changed`。
  写回只发生在 `MainViewModel.ApplySettings` 一处（写 `AppSettings` → 刷界面 → `_settings.Save()`）。
* `ApplySettings` 先写 `AppSettings` 再统一刷新：隐藏文件 / 扩展名会重新枚举目录（这两项“最后只刷一次”）；行高 / 列宽
  自适应只改每个标签页的 `ColumnLayout`（赋同样值不重排）；过渡动画只改视图行为，不重载目录。拨一次开关调一次（含一次
  落盘），滑块拖到底是几十次，实测不卡，不做防抖。
* 「查看」菜单里的工具条 / 侧边栏 / 双窗格保留（`MenuFlyoutItem` + `ToggleXxxCommand`，本来就不显示勾选标记），和设置
  窗口切的是同一份设置（`ApplySettings` 会 `OnPropertyChanged(ShowHiddenFiles/ShowExtensions/…)`），两边不会各说各话。
* **新增配置项要动七个地方**：`AppSettings` 字段 → `SettingsViewModel` 属性（用 `SetAndNotify`）→ `SettingsView.xaml` 里
  **对应分类页**加一张 `SettingsCard`（开关放 `ToggleSwitch`，连续值放 `Slider` + 数值文本）→
  `MainViewModel.ApplySettings` 应用（别忘了推给所有已存在的标签页）→ `test-settings.ps1` 的 `$KeyMap`（开关：UIA 名字
  → 字段名）与 `$CategoryMap`（开关：分类 → 该页的项）——**滑块类的项不进这两个映射**（会打乱“这一页有几个开关”的
  计数），另写用例断言 → 需要新分类再往 `SettingsCategory` / `Categories` 里加一项。
* 回归：`test-settings.ps1`（10 个用例，全程 UIA，不需要前台窗口）。肉眼看布局用 `shot-settings.ps1`
  （每个分类截图到 `.artifacts\settings-<分类名>.png`，需交互桌面）。

## 5. 必须遵守的编码约定

* **分层**：`Views` 不直接做 I/O，一律经由 ViewModel → `Services` 接口。
* **入口点不要改回 XAML 生成的那份**（csproj 定义 `DISABLE_XAML_GENERATED_MAIN`，入口在 `Program.cs`，为了把单实例
  闸门与命令行解析放在 WinUI 初始化之前）。升级 WinAppSDK 时拿 `obj\...\App.g.i.cs` 里生成的 `Program.Main` 对一下
  （初始化 COM Wrappers / 切 SynchronizationContext 那几行不能少）。`Main(string[] args)` 的 `args` **不含 exe 路径**
  （第 70 条）。
* **服务成对**：新增能力先加 `IXxxService`，再写实现，最后在 `App.ConfigureServices()` 注册。
* **异步**：耗时 I/O 走 `Task.Run`；从 UI 线程 `await` 时**不要** `ConfigureAwait(false)`，让续体回到 UI 线程后再更新
  `ObservableCollection`。
* **UserControl 的 ViewModel 必须是 DependencyProperty**，由父级用 `{x:Bind ...}` 赋值；子控件内部用
  `{x:Bind ViewModel.X, Mode=OneWay}`（普通 CLR 属性在 `InitializeComponent` 之后赋值时绑定不生效）。
* **主窗口的 ViewModel 用普通只读属性，且必须在 `InitializeComponent()` 之前赋值**（`x:Bind` 在 `InitializeComponent`
  期间求值）。`SettingsWindow` 同理；正文 `SettingsView` 按上一条走依赖属性。
* **新增配置项要动七个地方**（第 4 节“设置窗口”）。落盘与应用只在 `MainViewModel.ApplySettings` 一处发生（设置窗口
  自己不写 `config.json`）。
* **凡会被持久化（或跨 WinRT ABI）的对象都不能只靠反射**：写进 `config.json` 的类型要在
  `Services/SettingsJsonContext.cs` 加 `[JsonSerializable]`（否则序列化运行时静默失败，第 66 条）；实现 WinRT 接口的
  自定义控件要 `partial`（CsWinRT 源生成器才生成 vtable）。这类问题只在**裁剪过的 dist 产物**里出现，改完用
  `dist\win-x64\exdir.exe` 跑一遍回归。
* **托管类要实现 COM 接口（CCW）时必须 `public` + `[ComVisible(true)]`**，接口本身也要 `[ComVisible(true)]` +
  `[Guid]` + `[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]`（第 79 条）。托管回调里的 `out` 参数一律写 `IntPtr`
  自己判空再写值（第 78 条）。
* **随包分发的原生组件放 `native/`**（目前只有 `x64/7z.dll`），用 `Content` + `Link` 放到 exe 旁边、不静态链接也不用
  NuGet 包装包；`publish.ps1` 会校验它真的在发布目录里。csproj 里有 `Compile Remove="tools\**"`（tools 下有独立工程
  `archive-smoke`）。
* **数据集合整体替换而非增量 Add**：`FolderTabViewModel.Items` 每次导航/排序都新建 `ObservableCollection` 再赋值，
  避免逐条 Add 造成 O(n²) 的 UI 开销。
* **中文注释**、中文 UI 文案；注释解释“为什么”，不要复述代码。
* 新增 XAML 控件时顺手加 `AutomationProperties.Name`，否则 UI Automation 测试脚本找不到它。

## 6. 踩过的坑（请勿重复踩）

1. `x:Double` 资源不能赋给 `ColumnDefinition.Width`（XAML 不对资源值做类型转换）→ 列宽统一定义在
   `Helpers/ColumnLayout.cs` 的强类型 `GridLength` 静态属性里、用 `{x:Bind}` 共享，列头与数据行才一致。
2. **列头与数据行对齐**：绑同一个 `ColumnLayout` 实例的 rendered 宽度；可用宽度用 `ScrollViewer.ViewportWidth`
   （不是 ListView 宽度 —— 自定义竖滚动条会占掉视口宽度，`HorizontalOffset` 同步列头时也一样）。
3. `dotnet publish` 会丢掉 `.xbf` 和 `exdir.pri`（只有 `CopyToOutputDirectory`）→ 发布版启动即
   `XamlParseException`。手动往 `ResolvedFileToPublish` 补更糟（整个发布目录被写成同一个文件的内容），**不要加这种
   target**。现在的流程 = `dotnet publish`（裁剪只在 publish 生效）+ 从构建输出把这俩拷回去（`publish.ps1` 第 2 步）。
4. `PublishReadyToRun=true` 不能开（启动期 XAML 失败）；`PublishTrimmed=true` 可以开，但四个开关必须配齐，第 65 条。
5. 窗口刚开始 `Activated` 时 `XamlRoot` 是 null → `RasterizationScale` 取不到、把 DIP 当物理像素（高分屏窗口过小）。
   → 在 `RootGrid.Loaded` 里才恢复窗口位置，并用 `DpiHelper`（优先 XamlRoot，回退 `GetDpiForWindow`）。
6. 抓窗口截图前必须 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`，否则坐标被虚拟化（曾把 1400x850 物理像素
   误当成 DIP）。
7. `ElementName` 绑定在 `DataTemplate` 里不可靠 → 模板内要用命令时在 `Click` 里从 `FrameworkElement.DataContext` 取
   数据再调 VM。
8. `TabView` / `TreeView` 的项容器模板：模板根必须分别是 `TabViewItem` / `TreeViewItem`，`ItemsSource` 绑子集合。
9. `PanelViewModel.NewTabAsync` 必须先取 `CurrentPath` 再 `CreateTab()`（创建标签会切换活动标签，之后读到空路径 →
   新标签导航到空路径报“无法打开:”）。
10. `{x:Bind, Mode=TwoWay}` 默认失焦才回写（同 `{Binding}` 的 `LostFocus`）→ 路径框曾因此“回车没反应”。凡“按回车/
    按钮就要读输入框内容”的场合必须写 `UpdateSourceTrigger=PropertyChanged`。
11. `DataTemplate` 内的 `x:Name` 在 code-behind 访问不到 → 需要事件/状态时用 `UserControl` 包一层（如
    `NavigationBarView`），数据用 DP 传进去。
12. `TextBox` 文字永远顶对齐：模板不用 `VerticalContentAlignment`（microsoft-ui-xaml#5369），且内层 `BorderElement`
    的 `MinHeight` 用 `TextControlThemeMinHeight`（默认 32）、不受控件 `Height` 约束。→ 把
    `TextControlThemeMinHeight` 覆盖为 0，框**不设 `Height`**、只给上下对称的 `ExTextControlPadding`，外层
    `VerticalAlignment="Center"`（见 `PathBreadcrumb.xaml`）。新增 TextBox 沿用这个模式。
13. 模拟拖动时 `SetCursorPos` 不产生 `PointerMoved`（只在下一次 `mouse_event(LEFTDOWN)` 带过去）→ 必须用
    `mouse_event(MOVE|ABSOLUTE, x*65535/(屏宽-1), y*65535/(屏高-1))` 逐步移动；双击间隔要小于系统双击时间。同坐标的
    悬停移动也不产生 `PointerMoved`（先移到屏幕角落再移回，`inspect-ui.ps1 -Hover` 就这么做）。
14. `new DirectoryInfo("D:")` 是“D 盘的当前目录”（按进程工作目录解析）→ 判断根路径不能只看
    `TrimEnd('\\').Length == 0`，还要看末尾是不是 `:`。
15. `Click` 的参数是 `RoutedEventArgs`，没有 `Handled`（只有 Pointer / Tapped 等有）。
16. 空内容的 `Button` 缩成 0×0（默认样式把 `HorizontalAlignment` 设成 `Left`）→ 拿透明按钮当整区域点击目标时必须显式
    写 `HorizontalAlignment/VerticalAlignment="Stretch"`。
17. `VisualTreeHelper.FindElementsInHostCoordinates` 传 `null` 抛异常，且**不返回 `ScrollViewer` 内滚动内容里的元素**
    → 不能当“点这里会不会命中按钮”的笔据，只能真鼠标点。
18. 没有交互桌面时本机没法模拟输入/截图：`GetForegroundWindow()` 为 0 时 `SendKeys` / `keybd_event` / `mouse_event` 全
    无效，`CopyFromScreen` 报句柄无效（`-PrintWindow` 只能抓系统窗口按钮、WinUI 内容全白）→ 这时只能靠 UIA 读控件树
    + `InvokePattern`，验证不了真实指针命中与键盘。
19. `PROPVARIANT` 在 x64 上是 24 字节（不是 16）→ 自己写只有 `vt` + `u`（`FieldOffset(8)`）的结构去接
    `IPropertyStore.GetValue` 会踩栈，表现是**无日志猝死**（托管异常处理器没机会跑）。`ShellPropertyStore.PropVariant`
    显式写 `Size = 24`。**排查“无日志猝死”先怀疑互操作结构体大小。**
20. 往详细信息列表加列要同时改五处，漏一个就出现“四列宽度算了五个列”这类偏移：`ColumnLayout` 的 `Defaults` /
    `Minimums` / 下标常量 / `GridLength` 属性、`DetailsView.xaml` 的**列头 Grid 与行模板 Grid 两处** `ColumnDefinitions`
    与 `Grid.Column`、`FitTo` 里的 `total` / `others` 求和（当初就漏了最后一项导致列溢出窗格）、以及
    `AppSettings.ColumnWidths` 的顺序（改顺序要提 `CurrentSchemaVersion` 并在 `SettingsService.Migrate` 里补）。
21. 提权（高完整性级别）的进程根本不能参与拖放：`DragItemsStarting` 正常触发，但之后再没有 `DragOver` / `Drop`（根元素
    也收不到 `DragEnter`）→ `test-pin-drag.ps1` 检测到 shell 已提权时改用 `explorer.exe` 降权启动 exdir。
22. 拖放的 `DragEventArgs` 在 `await` 之后不能再碰（否则抛无消息的 `COMException`，且**之后所有拖放都失效**）→
    `DragOver` 必须全程同步（只做 `Contains` 这类判断），要读内容留到 `Drop`；`Drop` 里先把 `e.DataView` 取到局部变量
    再 `await`。
23. `AllowDrop` 的元素必须有 `Background`（哪怕 `Transparent`）才有命中区；只挂祖先上就够（子元素上的拖拽会冒泡）。
24. `MenuFlyout` 等弹出菜单不在主窗口的 UIA 子树里（是另一个 XAML 岛）→ 要从 `AutomationElement.RootElement` 往下找，
    `root.FindAll(...)` 找不到。
25. WinUI 3 的 `Button` 会把左键 `PointerPressed` / `PointerMoved` 标成 `Handled` → `CanDrag="True"` 形同虚设
    （`DragStarting` 永不触发），挂在 Button 上的 `PointerPressed` 也只有**右键**会跑到。→ 在**容器**上
    `AddHandler(UIElement.PointerPressedEvent, ..., handledEventsToo: true)` 自己识别手势，超过阈值调
    `UIElement.StartDragAsync(point)`（收 `Microsoft.UI.Input.PointerPoint`），之后照常触发 `DragStarting`。提权进程不
    支持 `StartDragAsync`（第 21 条），要 `try/catch`。
26. 菜单/弹层有入场动画，UIA 脚本“先查坐标再点”会打空，而且上次关掉的 `MenuFlyout` 菜单项**仍留在 UIA 树里**（只是
    offscreen）——症状是“菜单项找得到、点了却什么都不发生” → 优先用 UIA 模式（`ExpandCollapsePattern` +
    `InvokePattern`）；真要鼠标点就先过滤 `IsOffscreen == false` 且宽高 > 0，并在点击前那一刻才读坐标。
27. `ContentDialog` 是独立弹出岛窗口（`RootElement` 下的顶层 `Window`，不在主窗口子树）；内容超可视区时屏幕外控件
    `IsOffscreen=true`、矩形为空 → **按“是不是对话框窗口的后代”筛才能数全**。
28. `ToggleMenuFlyoutItem` 在 UIA 里没有 `TogglePattern`（只有 Invoke / ScrollItem / VirtualizedItem，`CheckBox` 才支持）
    → 勾选状态没法断言，只能截图。
29. `ResourceDictionary` 里 `ThemeDictionaries` 必须放在最后：写了属性元素后就不能再有隐式资源条目，否则报
    `WMC0035: Duplication assignment to the '_Items' property`。
30. `SHGetFileInfo(SHGFI_ICON)` 并发时偶发“只给 `iIcon`、不给 HICON”（典型是 `.txt` 这种扩展名图标）→ 把“问外壳要
    HICON”这一步**串行化**（`ShellIconExtractor.Gate`）+ **失败重试一次**；读像素（`GetDIBits`）各用各的位图，不需要在
    锁里。
31. `calc.exe` 取不到外壳图标（它是“应用执行别名”，外壳转去 AppX 包取图标，非打包进程只给索引；**改名叫
    `calculator.exe` 就正常**）→ 外壳不给 HICON 时退回 `ExtractIconEx` 直接读文件自身图标资源（对 exe 有效，对目录 /
    lnk 返回 0）。日志形如 `外壳图标：xxx 提取失败（… iIcon=130 …）`。
32. 图标尺寸按 `SM_CXSMICON`（`SHGFI_SMALLICON`）取：它正好等于“16 DIP 在当前 DPI 下的物理像素数”（100%→16、
    150%→24、200%→32），与 16×16 DIP 的 `Image` 1:1；`SHGFI_LARGEICON` 在 200% 下给 64×64，白多 4 倍内存。另外
    `WriteableBitmap.PixelBuffer` 要**预乘 alpha**（`GetDIBits` 出来的是直通 alpha，不预乘半透明边缘发白），
    `GetDIBits` 要按“自下而上”请求（`biHeight` 给正数）再自己翻行。
33. UIA 里可断言“有没有真实图标”：行模板的 `Image` 带 `AutomationProperties.Name="程序图标"`、`Visibility` 绑
    `HasIcon` → 没图标就是 `Collapsed`、UIA 树里找不到。
34. 量像素时 UIA `BoundingRectangle` 是屏幕坐标、截图是窗口内相对坐标 → `GetPixel` 必须减掉 `GetWindowRect` 原点
    （不减会得出“图标墨迹和文字墨迹一模一样”这类假结论，本项目差点因此把对齐改成反向）。
35. “图标与文字垂直居中”不能只看布局盒子：文字行盒底部有降部 → 要用**墨迹中心**比（`measure-row-align.ps1`）且看
    **中位数**。当前图标下推 1 DIP 时中位数约 2.5 物理像素、平均 2.0（200% 缩放）；去掉那 1 DIP 恶化到 4.5 / 4.0。
    下推只能用 `RenderTransform`：加 `Margin` 会把布局盒变成 17、实际只推 0.5 DIP，且非整数偏移会让位图重采样、图标
    发虚。
36. `TabView` 标签高度不是 `TabViewItemMinHeight` 说了算，是被“关闭”按钮撑出来的：模板里 `TabContainer.Padding` 是
    `8,3,4,3`，关闭按钮是 `32 × 24`，`3 + 24 + 4（选中态下内边距）+ 1（下外边距）` 正好 32 → 想让标签变矮必须同时把
    `TabViewItemHeaderCloseButtonHeight` 调小（16 与图标同高，宽度留 32）。另：装不下时的 ◀ ▶ 按钮容器默认带 3 DIP 下
    内边距（`TabViewItemLeft/RightScrollButtonContainerPadding`），会把**只有溢出窗格**的标签栏撑到 27 DIP。排查：临时把
    `Tabs` 视觉树的 `ActualHeight/MinHeight/Padding/Margin` 打到 `%LOCALAPPDATA%\exdir\tabtree.txt`。
37. `ContentDialog` 里的 `FontIcon` 渲染发献（`DesiredSize` 近 0）、负 `Margin` 会被裁切 → 设置对话框左侧导航只放文字、
    不放图标，也不要靠负 `Margin` 消除 `ContentDialogPadding`。（2026-09 起设置界面已是独立窗口，此条只是历史。）
38. 同一主题资源键在 `ContentDialog` 里可能读不到 App 级的值 → 在该弹层自己的 `Resources` 里再覆写一遍。
39. `TabView` 默认样式把 `VerticalAlignment` 设成 `Top` → 标签页内容只占“内容自己的高度”（列里只有几个文件时列表下方
    一大片空白既点不到也收不到右键）。→ `Views/PaneView.xaml` 的 `TabView` 上显式写 `VerticalAlignment="Stretch"`（含
    ContentAlignment）。排查：从问题控件往上逐级打 `ActualHeight` / `DesiredSize.Height` / `VerticalAlignment`。
40. 列表空白处收不到 `RightTapped` / `ContextRequested`（即使 `handledEventsToo`）：空白命中的不是列表，而是外面那个有
    背景的 Grid（`ListView` 内部的 `ScrollViewer` / `ScrollContentPresenter` 自己没背景、不参与命中测试）→ 监听挂
    `DetailsView` **最外层有背景的 Grid** 上，再用 `OriginalSource` 反查行。另：`ContextRequested` 在有些控件上压根不
    冒泡，`RightTapped` 更可靠，两个都挂 + 时间戳防重复。
41. 窗口位置/尺寸存坏了会让界面“看起来没做出来”（曾写下 `W=157 / H=25 / X=-16000 / Y=-16000`，窗口只剩 360×240 DIP）
    → ① 下限要先夹再乘缩放（原先把 720/480 DIP 下限写在换算物理像素之后）；② `SaveWindowPlacement` 要跳过最小化状态
    与 `-32000` 哨兵值。调试时先看日志里 `恢复窗口位置: 设置=…`。
42. Win11 外壳右键菜单在 UIA 里读不到菜单项（自绘 Win32 弹出菜单）→ 只能 ① `EnumWindows` 按进程找 `#32768` 窗口证明
    弹出；② 让 exdir 自己把菜单项写进 `exdir.log`；③ 截图看关掉的项确实不在菜单里。
43. 宿主自己弹系统菜单必须转发菜单消息，否则子菜单是空的：`IContextMenu` 背后常是 `IContextMenu3` / `2`，壳扩展要靠
    `WM_INITMENUPOPUP`（典型“打开方式”）才知道该放什么，owner-draw 项要靠 `WM_DRAWITEM` / `WM_MEASUREITEM` →
    `TrackPopupMenu` 期间用 `SetWindowSubclass`（comctl32）挂钩子把这三条转给 `IContextMenu3::HandleMenuMsg2` /
    `IContextMenu2::HandleMenuMsg`，弹完立刻 `RemoveWindowSubclass`。
44. 行高不能用 `ListViewItem.MinHeight` 配（它是下限，调成 20 时容器仍 24）→ 容器 `MinHeight` 归零 + 行高绑行模板
    `Grid.Height`（`Style` 的 `Setter.Value` 不能写 `x:Bind`）。行高放每标签页一份的 `ColumnLayout` 上，不放每个
    `FileItemViewModel`（否则改一次要给几千行发通知）。滑块类设置项用 `RangeValuePattern`，按名字找时要按
    `ControlType.Slider` 过滤（左边标题 `TextBlock` 也叫“行高”）。
45. 行里“空白处”不是行模板的一部分：命中 `ListViewItem` 时 `DoubleTapped` 从它直接往 `DetailsRoot` 冒、不经过行模板
    `Grid` → **要么把处理器挂外层再反查行，要么给行模板根加 `Background="Transparent"`**（没背景的 Grid 在空白处不参与
    命中测试，连 ToolTip 都不弹）；exdir 两个都做了。同理适用于任何“看起来是整块、其实只有内容是实心”的容器（如
    `ContextFlyout` / `RightTapped`，第 40 条）。
46. 零宽 Grid 单元格里的子元素收不到指针事件：列宽把手原落在隐藏时宽度为 0 的“状态”列里 → 非云目录拖不动、云目录正常
    （很有欺骗性）。→ 把手放进**没有列定义、整宽**的 `HeaderLayer`，横向滚动 `TranslateTransform` 也挂这层。另：不要用
    UIA `BoundingRectangle` 判断这类元素“在哪/能不能点”——它是布局值、不含 `RenderTransform`、被裁切后为空矩形。回归
    `test-column-resize.ps1`（修前用例 2/3/4 全部没反应）。
47. `TaskbarIcon.ContextFlyout` 里的菜单项挂 `Click` 无效：H.NotifyIcon 默认 `ContextMenuMode=PopupMenu` 是把
    `MenuFlyout` **抄成 Win32 弹出菜单**，只抄 `Text` / `IsEnabled` / `Command` / `CommandParameter` 再执行
    `flyoutItem.Command`，所以写 `Click="..."` 永远不触发（不报错，点了没反应）→ 必须给 `Command`（绑的是弹出那一刻的
    状态）。
48. 关窗口只是隐藏的托盘程序，不能用 `CloseMainWindow()` 收尾（发的是 `WM_CLOSE`，会被 `Cancel` 掉只藏窗口 → 白等几秒
    再 `Kill()`）→ `tools\*.ps1` 的 `Stop-Session` 全部直接 `Kill()`（隐藏时已落盘，强杀不丢列宽/会话）。
49. UIA 量行高/行宽不能用平均值：视口底部那一行是裁过的（实测 `[80×7, 48]`，平均 76 超出 ±2 容差、偶发 FAIL）→ 取
    **中位数**（`Get-TypicalRowHeight`）并打印逐行高度。
50. `Window` 不是 `FrameworkElement`，写在 Window 根上的 `x:Bind` 用不了 `{StaticResource}` 转换器（生成的代码有
    `bindings.SetConverterLookupRoot(this)`，编译报 `CS1503: 无法从 SettingsWindow 转换为 FrameworkElement`）→ 窗口只
    当薄壳，正文放 `Views/SettingsView`（UserControl）。`MainWindow` 同理。
51. `SettingsCard` 在卡片宽 < 476 DIP 时把控件换行到标题下方（`ContentAlignmentStates` 的 `RightWrapped*` 由
    `tk:ControlSizeTrigger` 触发，阀值 `SettingsCardWrapThreshold = 476`；更窄的 `SettingsCardWrapNoIconThreshold = 286`
    连 HeaderIcon 一起收）→ 左导航 + 卡片的窗口不能窄（W = 860 时卡片约 625 DIP 正常，W = 600 只有 372）。另：本机屏幕
    只有 485 DIP 宽，拉大也会被系统夹住，这个观感本机无法肉眼验证，只能靠尺寸推算。
52. `NavigationView` 换分类要用 `SelectionChanged`，不能用 `ItemInvoked`：后者只在“用户点击 / 按回车”时触发，键盘方向
    键、程序化赋值、`SelectionItemPattern.Select()` 都不触发 → 表现为“导航高亮变了但右侧还是上一页”。只接
    `SelectionChanged` 就够（覆盖点击、键盘、程序化三种路径）。
53. 桌面上别的进程也可能有叫「设置」的顶层窗口（本机就有 `ApplicationFrameHost.exe` 的“设置”页）→ 脚本找窗口时加
    `ProcessId -eq $Session.Proc.Id` 过滤；找菜单项（弹出的 MenuFlyout）同理。
54. 拖放事件的 `OriginalSource` 是“带 `AllowDrop` 的那个元素”，不是鼠标下面那一行（日志里它永远是 `TreeView`）→
    `AllowDrop` + `DragOver` / `DragLeave` 写到行模板根（`ListView.ItemTemplate` 的 `Grid` / `TreeView.ItemTemplate` 的
    `TreeViewItem`）上，用 `sender` 或 `TreeView.ItemFromContainer(sender)` 反查数据项（只有 `RightTapped` /
    `DoubleTapped` 才用 `OriginalSource` 反查，第 40/45 条）。另：`TreeViewList` 会按“没开重排”把 `AcceptedOperation`
    写成 `None` → 必须在 `TreeView` 上再 `AddHandler(..., handledEventsToo: true)` 接一次 `DragOver`（路由最后重新赋
    `Copy`）与 `Drop`，否则拖过去高亮会亮、松手什么都不发生。
55. 标签圆角要改 `TabViewItem.CornerRadius`（填 `(8,8,0,0)`），不要改全局的 `OverlayCornerRadius`：模板里
    `TabBackground.CornerRadius` 是 `TemplateBinding CornerRadius` → 属性一变当场重画；改 `OverlayCornerRadius` 会影响
    所有弹层。模板里那对“倒角”靠 `StaticResource`（`TabViewItemRadiusRenderCornerRadius`）驱动，框架字典里的
    `StaticResource` 不受 App 级同名键影响，**改不了也无需改**。验证靠截图量像素：标签左上角最大内缩直角 ≈ 3.5 DIP、
    圆角 ≈ 6.5 DIP。
56. 卷插拔消息只能在自家窗口上听，且跨进程发消息不能带 `DEV_BROADCAST_*` 指针：带 `DBT_DEVTYP_VOLUME` 的到达 / 移除
    事件是系统**广播给所有顶层窗口**的，不需要 `RegisterDeviceNotification`，主窗口 `SetWindowSubclass` 就够。
    * `DBT_DEVICEARRIVAL` / `REMOVECOMPLETE` 的 `lParam` 是 `DEV_BROADCAST_HDR*`，必须先读 `dbch_devicetype`。**因此
      这类消息不能拿来跨进程 `SendMessage`**：脚本里的“假 U 盘”回归发 `DBT_DEVNODES_CHANGED`（`wParam=7`、
      `lParam=0`），否则目标进程读自己地址空间里的野指针会 AV（`try/catch` 拦不住）；exdir 把这个没有 lParam 的事件也
      当“卷可能变了”。
    * 一次插拔常连发好几条消息，且 `DriveInfo` 能列出盘符比消息晚几百毫秒 → 刷新要“合并 + 延后 + 兜底”各来一次，并按
      **差量**做（整树重建会把用户展开的目录折回去，也会连「收藏夹」一起重建）。
57. `CF_PREFERREDDROPEFFECT` 不是 `0x000C`（那是 `CF_WAVE`）：必须 `RegisterClipboardFormat("Preferred DropEffect")` 拿
    id 再当格式号。写错的症状很有欺骗性：写读都“成功”，剪贴板里却多出一个 `WaveAudio` 格式，而
    `GetDataPresent("Preferred DropEffect")` 永远 false。排查：打印 `Clipboard.GetDataObject().GetFormats()`。
58. `ListView.CanDragItems` 的拖拽在模拟鼠标下走不完（`DragItemsStarting` 正常，但永远没有 `DragOver` / `Drop`）→
    `CanDragItems=False`，在 `DetailsRoot` 上用 `handledEventsToo` 自己识别手势，超过 4 DIP 调 `StartDragAsync`。两个易
    漏点：① **不要把 `PointerCaptureLost` 当松手**（`ListViewItem` 按下时会捕获并立即发一次，那时拖拽还没开始；拿它清
    候选会让拖拽永远启动不了，表现为“有时能拖、有时拖不动”）；② 行容器取数据项要用 `ListView.ItemFromContainer`
    （`ListViewItem.DataContext` 实测是 null）。
59. 拖拽期间 `e.GetPosition(element)` 坐标不可靠（与 `GetCursorPos` 换算差几十 DIP → “行高亮着、松手什么都没发生”）→
    用 `DpiHelper.GetCursorPosition(element, hwnd)`（`GetCursorPos` → `ScreenToClient` → 除 `RasterizationScale` → 减元素
    原点，即 `ToScreenPoint` 的逆运算）。
60. 拖放坐标/落点不能用 `e.OriginalSource`，还要防框架有时不冒泡 `Drop`：把 `AllowDrop` 放在 `ListViewItem` 这一级时
    `ListViewBase` 会把 `Drop` 吃掉（`CanReorderItems=False` 时既不重排也不冒泡）→ 容器 `AllowDrop=False`，整个列表只有
    `DetailsRoot` 一个落点，鼠标下是哪一行用光标位置 + 容器实际矩形算；`StartDragAsync` 返回后再兜底一次。
61. `SHFileOperation(FO_DELETE)` 的 `pTo` 必须传 `null`（给空串会被当非法目标、删除“失败”而文件没动），多字符串 `pFrom`
    仍要 `\0` 结尾 + `\0` 收尾；并带 **`FOF_WANTNUKEWARNING`（0x4000）**，否则装不进回收站的大文件会被**静默永久删除**。
    另：删除确认框是本进程 STA 线程弹的 owned `#32770`、**不在桌面 UIA 子窗口那一层**，要从主窗口 `Descendants` 找；
    “有没有真进回收站”用 `Shell.Application` 的 `NameSpace(10)` 读，且用例文件名要每次唯一。
62. 带 `Shift` 的 `KeyboardAccelerator` 不触发（`Key="Delete"` 正常，再加 `Modifiers="Shift"` 时两个都不 Invoke，
    `SendKeys` 的 `+{DEL}` 与 `keybd_event` 都试过）→ 用 `PreviewKeyDown`（隧道事件，一定先于列表/列头拿到这个键）+
    `InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)` 判 Shift。※ 不要用 `GetAsyncKeyState`：它给的是
    “此刻的物理键状态”，而进程从消息队列取出这条消息时用户可能已经松开 Shift。
63. 「网络位置」是快捷方式容器目录，而且会被“只按磁盘重建”的刷新逻辑误删：它在磁盘上是
    `%APPDATA%\Microsoft\Windows\Network Shortcuts\<名字>\target.lnk`，目录名就是显示名。解析要 `IShellLinkW`
    （`IPersistFile.Load` + `GetPath`），**所有字符串参数必须显式 `[MarshalAs(UnmanagedType.LPWStr)]`**（不标则按 ANSI
    传、中文乱码），且 18 个方法要按 vtable 顺序写全；`target.lnk` 是隐藏文件、`desktop.ini` 只是元数据，别当“位置”列。
    最易踩：`SidebarViewModel.RefreshDrives` 重建期望子节点后“摘掉不在期望清单里的节点”→ 不把网络位置一并算进期望清单，
    第一次插拔（或「重新扫描磁盘」）就把它们全删了。
64. **NativeAOT 在 WinAppSDK 2.5.1 + WinUI 3 下用不了（2026-09 穷举实测）**：能编译能产出（自包含 72 MB / 框架依赖
    13 MB），但**启动约 30 ms（主窗口已构造、`Activate()` 已返回、`RootGrid.Loaded` 之前）fail-fast**，退出码
    `0xC000027B`，事件日志 `故障模块 Microsoft.UI.Xaml.dll` + `LimitedAccessFeatures` 的 ClassFactory 报
    `0x80040111`；`UnhandledException` 拿到无堆栈 COMException，`e.Handled = true` 也拦不住。试过都不行：自包含 / 框架
    依赖、net8/net10、`CsWinRTAotWarningLevel=2`、`AllowUnsafeBlocks`、`BuiltInComInteropSupport=true`、换掉 Mica、
    `CsWinRTUseWindowsUIXamlProjections`、只开裁剪不开 AOT。**关键证据：报出来的类名在整包任何文件里都搜不到、自己
    代码完全没碰过，同一进程不开 AOT（哪怕同样裁剪）就正常** → WinAppSDK/WinUI 的 bug（上游 WindowsAppSDK#3905 /
    #6058）。别在 XAML 上花时间；想要“小而快”就开裁剪（第 65 条）。
65. **裁剪（`PublishTrimmed`）能开，但四个开关必须同时到位**：

    ```xml
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>                      <!-- 不能是 full -->
    <CsWinRTAotWarningLevel>2</CsWinRTAotWarningLevel>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    ```

    * `TrimMode=full` 会在“配置 → 设置…”一打开就 0xC0000005 崩在 coreclr.dll（没有托管异常、没进 exdir.log，只能靠
      事件日志看出来）；`partial` 只裁带 `[AssemblyMetadata("IsTrimmable","True")]` 的程序集（BCL），第三方 / 工具包
      原样保留，体积只差 2 MB。
    * `CsWinRTAotWarningLevel` 必须是 2（CsWinRT 源生成器跑 Auto 模式，为“会跨 WinRT ABI 的泛型实例化”生成 vtable；
      默认 level 1 时第一次设 `ItemsSource` 就 `NullReferenceException @
      WinRT.TypeExtensions.GetAbiToProjectionVftblPtr`）。`AllowUnsafeBlocks`：生成代码用 unsafe。
    * `BuiltInComInteropSupport`：老式 COM interop 在裁剪/AOT 下默认关掉，而剪贴板 OLE、外壳右键菜单、云占位符属性
      存储、`.lnk` 解析全靠它；关掉后这些功能一用就挂，而不是编译报错。
    * 裁剪只在 publish 生效 → **改完必须重新 `tools\publish.ps1` 再跑相关回归**（`-Exe dist\win-x64\exdir.exe`）。
66. **裁剪过的发布版里反射式 `JsonSerializer` 会静默失败（config.json 读写得走源生成）**：症状是设置窗口里拨开关
    **界面当场生效**但 `config.json` 没变、重开又变回去，而且 `SettingsService` 吞异常所以**一行日志都没有**。→
    `Services/SettingsJsonContext.cs` 源生成 + `JsonSerializerOptions.TypeInfoResolver`，并且用
    `JsonSerializer.Serialize(obj, JsonTypeInfo)` / `Deserialize(json, JsonTypeInfo)` **而不是泛型重载**；`Load()` /
    `Save()` 的 catch 里要 `Log.Exception`。**新增会写进 config.json 的类型要往那个 context 加
    `[JsonSerializable]`。**
67. WinAppSDK 的两种“精简”各有官方钩子，不要事后删文件：
    * **语言资源（85 个 `*.mui` 目录）**：用 `MicrosoftWindowsAppSDKFilesExcluded` 在
      `AddMicrosoftWindowsAppSDKPayloadFilesFromComponents` 之前把不要的语言剔掉（见 csproj 的
      `ExcludeUnneededWinAppSdkLanguageResources`）；本仓库只留 `zh-*` / `en-*`。
    * **整个组件包**：`Microsoft.WindowsAppSDK` 2.5.1 是元包，AI/ML/Search/Widgets 四个组件（约 55 MB）exdir 一行都
      没用，用 `ExcludeAssets="all"` 排除。两个坑：① **不能把元包删掉改成“只引用需要的组件包”** —— H.NotifyIcon 与
      SettingsCard 都声明了 `Microsoft.WindowsAppSDK >= 1.6`，没有这份直接引用，NuGet 会去装 1.6 旧包、props 与 WinUI
      2.3.9 重复导入、构建直接报错；② 还得排除 **`Microsoft.Windows.AI.MachineLearning`**（`onnxruntime.dll` /
      `DirectML.dll` 其实是它 `runtimes\win-x64\native` 里的），它自己的 targets 会因 `_WindowsAppSDKML` 没被置位而报
      “需要 19H1+”。
68. 浅色主题下设置窗口左侧导航“黑底黑字”：`NavigationView` 窗格用的是“应用内亚克力”（半透明，采样同一窗口里它背后
    的内容），而 `SettingsWindow` 没有 `SystemBackdrop` → 采样到窗口自身黑底（深色主题下看不出来）。→ 给
    `SettingsWindow` 加 `<Window.SystemBackdrop><MicaBackdrop Kind="Base" /></Window.SystemBackdrop>`。教训：**浅色下
    “某块区域黑得不对劲”先怀疑“半透明控件背后没有背板”**。另：本机系统是深色，验证换肤必须两套主题都看（改
    `config.json` 的 `Theme=1` 或点标题栏开关）。
69. PowerShell 7.4+ 把弯引号当字符串定界符：**普通双引号字符串**里出现 `“`（U+201C）会把字符串就地截断，报「字符串
    缺少终止符」，看起来像无关的解析 bug。实测：`"aaa“bbb”ccc"` 报 1~3 个语法错误，单引号字符串 / 注释 / here-string
    都是 0 个错。所以**只有普通双引号字符串里要换成「」**。这类错误是**解析期**失败、一行都不执行 → 改完先拿
    `[System.Management.Automation.Language.Parser]::ParseFile(...)` 扫一遍。
70. `Main(string[] args)` 收到的数组不包含 exe 路径（比 `Environment.GetCommandLineArgs()` 少一项）→ **`args[0]` 就是
    第一个用户参数**。写成从 `args[1]` 开始会“单参数被当成没有参数、两个参数时只认第二个”。
71. **裁剪会把“只做类型转发”的框架程序集从 `deps.json` 里整条删掉**，哪怕磁盘上还留着那个 dll：裁剪版一启动就
    **无日志猝死**、退出码 `0xE0434352`，事件日志 `.NET Runtime` 才写 `FileNotFoundException: Could not load file or
    assembly 'System.IO.Pipes'`（也踩过 `System.Text.Encoding.Extensions`）。
    * 根因：`System.IO.Pipes` 在 .NET 8 下是**纯转发的壳**（实现在 `System.Private.CoreLib`），ILLink 认为“这个程序集
      里没有用到的类型”就把它从 `exdir.deps.json` 删了；而**自包含应用是按 deps.json 建 TPA 列表加载程序集的**，
      磁盘上有也没用。编译器给 `exdir.dll` 记的 `AssemblyRef` 指向的仍是这个壳，一 JIT 到那行就抛。
    * **`try/catch` 拦不住**（异常发生在方法编译/类型解析那一拍，还没进 try，连 `Diagnostics/Log` 都来不及写）。排查：
      `Get-WinEvent -LogName Application | Where-Object ProviderName -eq '.NET Runtime'`。
    * 修法：`TrimmerRootAssembly` root 住用到的壳（本项目是 `System.IO.Pipes` 与 `System.Text.Encoding.Extensions`，
      以及右键「压缩」用的 `System.IO.Compression`）。
      规律：**“Debug / 未裁剪的 Release 都正常，只有 dist 崩”先怀疑裁剪**；新增 BCL 引用后必须在 dist 上跑关联回归。
72. 一份日志会被两个 exdir 进程同时写：`File.AppendAllText`（`FileShare.Read`）会吃共享冲突、被 `catch` 静静吐掉
    （“日志里少了一行”）；改 `FileShare.ReadWrite` 后不再丢，但**两个进程的字节会交错到同一行里**。→ `Log.Write` 用一个
    **命名互斥体**（`Local\exdir.log`，拿不到就放弃这一条、等 2 s）串起跨进程写入，并写 `Encoding.UTF8.GetBytes` 的字节
    （不用 `StreamWriter` + `new UTF8Encoding(...)`：会拖出 `System.Text.Encoding.Extensions`，见第 71 条）。
73. UIA 里数标签页 / 断言当前标签的两个坑：① `TabItem` 的 `Name` 就是标签标题，判断选中要读
    `SelectionItemPattern.Current.IsSelected`；② **不要按 `BoundingRectangle.X` 排序**（标签溢出滚动时滚出可视区的那些
    报 `NaN` / 负数）；`FindAll` 返回顺序就是标签条的**逻辑顺序**。
74. 新建的标签页可能在“导航完成之后”才挂上 ViewModel，从而错过按路径恢复选中项那一拍：`DetailsView` 的
    `RestoreSelection` 只挂在 `Items` 整体替换的通知上，而 `TabView` 要等布局那一拍才生成容器、才把 `ViewModel` 设进去
    → `exdir <文件>` 在**新标签页**里高亮选中会失败（第二次导航却正常）。→ `DetailsView.OnViewModelChanged`（DP 变化）
    里也 `DispatcherQueue.TryEnqueue(view.RestoreSelection)` 一次。**不要**改成“每行/每标签页存一份选中状态”
    （`PendingSelection` 重复应用是幂等的）。
75. 预热启动（`--preload`）的窗口自始至终没 Activate 过，几个状态得自己料理：
    * **必须自己把 `_hiddenToTray` 置上**（`MainWindow.StartPreload`）：`ShowFromTray()` 靠它决定是 `Show()` 还是
      `Restore()`；没置的话标志是 false，第二次启动 exdir 时它什么都不做 → 窗口永远出不来，看起来像“预热进程把单实例
      位置占了”。
    * **不要指望 `RootGrid.Loaded` 触发预热**（窗口从未显示，第一帧布局不一定跑）：要 `App.OnLaunched` 里直接调
      `StartPreload()` → `ViewModel.InitializeAsync()`。会话恢复要做成**幂等**（`??=` 一个共享 Task）：用户真把窗口叫
      出来时 `Loaded` 还会再调一次，用 bool 挡会在“第一次还没跑完”时把第二次误判成已完成。
    * **预热进程不能自己跑 `RestoreWindowPlacement()`**（那时 `XamlRoot` 可能还是 null，`GetDpiForWindow` 给默认 DPI、
      高分屏上位置算偏）→ 留在真正显示时的 `Loaded`（`MoveAndResize` 对未显示的窗口一样有效）。
    * 验证只能看**句柄 + 日志**：`Process.MainWindowHandle` 断言“预热时没有窗口”，日志里的「预热启动：…」「预热完成：
      … 目录=… 图标=…」断言“真的做了预热”。
76. 配置文件从 `%LOCALAPPDATA%\exdir\settings.json` 搬到 `~/.config/exdir/config.json`：老位置在 LocalAppData 里，
    “清缓存 / 重装 / 换机器”很容易把它一起清掉，而设置是用户资产。
    * **目录解析**（`SettingsService.ResolveDataDirectory`）：设了 `XDG_CONFIG_HOME` 且是**绝对路径**就用它，否则回退
      `%USERPROFILE%\.config`，最后拼 `exdir`。路径只在这一个地方算，别处（包括回归脚本）不要硬编码 LocalAppData。
    * **首次启动自动搬一次**（`MigrateLegacyConfig`，在 `Load()` 里读配置前调用）：只在“新位置没有、旧位置有”时
      `File.Move` 过去；新位置已有配置时**不动旧文件**。迁移失败只记一行日志、不阻塞启动。
    * 文件名改 `config.json`，`exdir.log` **不跟着搬**；“关于”对话框的路径改为读 `ISettingsService.ConfigFilePath`；
      15 个回归脚本的 `$settingsPath` 同步改。
77. 7-Zip 的接口 IID 换过布局，而且格式 CLSID 的 `kClassID` 不是字符串：`CreateObject` 全程报 `E_NOINTERFACE`
    （0x80004002），让人以为“clsid 不对”。实际是两件事：
    * 接口 IID 现在是 `{23170F69-40C1-278A-0000-000G 00 SS 0000}`（G = 接口组，archive 组是 6；SS = 组内编号，
      `IInArchive` = 0x60），而网上大量老例子用 `...-1000-000110060000`，早就对不上。其余：`IArchiveOpenCallback` =
      `-0000-000600100000`、`IArchiveExtractCallback` = `-0000-000600200000`、`ISequentialInStream` =
      `-0000-000300010000`、`ISequentialOutStream` = `-0000-000300020000`、`IInStream` = `-0000-000300030000`、
      `IOutStream` = `-0000-000300040000`、`IProgress` = `-0000-000000050000`、`ICryptoGetTextPassword` =
      `-0000-000500100000`、`ICryptoGetTextPassword2` = `-0000-000500110000`。权威定义在 7-Zip 源码的
      `CPP/7zip/IDecl.h` / `IArchive.h` / `IStream.h` / `IPassword.h`。
    * **`GetHandlerProperty2(i, kClassID)` 返回的是「BSTR 里装 16 字节 GUID 内存」**（7-Zip 自己的 `SetPropGUID` 就是
      `SysAllocStringByteLen((char*)&guid, 16)`），按 BSTR 读是乱码 UTF-16 → 要 `Marshal.Copy` 那 16 字节给
      `new Guid(byte[])`。
78. 7-Zip 会拿 NULL 调回调的 `out` 指针（`IInStream::Seek` 的 `newPosition`、Read/Write 的 `processedSize`）→ 托管
    实现里声明成 `out uint` / `out ulong` 会让封送层解引用空指针，报 `NullReferenceException` → HRESULT
    `0x80004003`（E_POINTER）。症状极具欺骗性：`GetStream` / `PrepareOperation` 都正常，紧接着 `Extract` 返回
    E_POINTER。→ **托管回调里的输出参数全部写成 `IntPtr`，自己判 `IntPtr.Zero` 再 `Marshal.WriteInt32/64`。** 排查：
    给 callback 加临时 trace，或挂 `AppDomain.FirstChanceException`（上次栈就停在 `ManagedInStream.Seek`）。
79. 托管类实现 COM 接口（CCW）必须 `public` + `[ComVisible(true)]`，接口也要 `[ComVisible(true)]` + `[Guid]` +
    `[InterfaceType(InterfaceIsIUnknown)]`：`internal` 会被 `ComWrappers` 拒掉（`ArgumentException: The specified type
    must be visible from COM.`）；接口写了 `[ComImport]` 却让托管类去实现，CCW 能建出来但
    `Marshal.GetComInterfaceForObject` 直接 NRE。现在：接口一律不写 `[ComImport]`（只有我们**调用**的 `IInArchive`
    保留），实现类全部 `public sealed partial`。
80. 单文件压缩器（gzip/xz/bzip2/zstd/lzma/lz）根本不给条目名：`kpidPath` 是空串（甚至 `VT_EMPTY`）→ `GetEntry` 不能
    把“空路径”当无效条目返回 null（否则 `.tar.gz` 整个列表是空的）。内层文件名按压缩包名派生（`foo.tar.gz`→`foo.tar`、
    `foo.tgz`→`foo.tar`、`log.gz`→`log`），且只在“整包真的只有一个条目”时才派生。
81. `tar -C dir .` 造的包，条目名带 `./` 前缀（`./sub/inner.txt`）：虚拟路径规范化若把 `.` 段当非法段拒掉，整个 tar 一行
    都列不出来（“`.tar.gz` 点进去是空的”）→ `ArchivePath.NormalizeInner` 要**跳过** `.` 段（继续拒绝 `..`），顺便把
    `/` 统一成 `\`、去重复分隔符。
82. 加密 zip：列表不要密码、取文件才要；而且解码器问的是旧版接口。ZipCrypto（以及 AES）的条目名存在明文中央目录里，
    所以 `.zip` 不加密码也能列出条目名；真正读数据时 7-Zip 的 zip 解码器是对**Extract 回调** QI
    `IID_ICryptoGetTextPassword`（v1，只给一个 BSTR），只实现 `ICryptoGetTextPassword2` 会一直报“密码错误”
    （`NOperationResult::kWrongPassword` = 9）→ 两个都实现，且没有密码时返回 `E_ABORT` + 记 `AskedForPassword` 标志，
    才能把“需要密码”和“用户取消”分开。
83. 压缩包的写路径要单独守：`DirectoryExists` / `NormalizeDirectoryPath` 只认真实路径，所以粘贴 / 删除 / 新建 / 拖放
    天然会被拒 —— 但 `NormalizeDirectoryPath("D:\a\b.zip")` 会返回它的**父目录**（那是个真实存在的文件），于是“在包根上
    粘贴”会变成“粘到压缩包所在的目录里” → 所有写操作入口都先在 `FolderTabViewModel.RefuseInArchive()` 里判
    `IsInsideArchive`；视图侧（`DetailsView`）另外把拖拽手势与右键菜单也挡掉。
84. 压缩包临时目录的清理要“看人下菜”：`archive-cache` 里有三类 —— 链式解开的中间 tar 与 `copy` 中转目录（随时可删）、
    以及“给默认程序打开”的副本（用户可能正开着）。→ `CleanupTemp()` 只直接删前两类、第三类只删**一天前**的。一口气
    `Directory.Delete(root, true)` 会让别人正在编辑的文件突然消失（删不掉时也只是静默失败，更难查）。
85. 包内条目取不出来，也不能直接塞进系统剪贴板（只有「压缩包路径 + 包内相对路径」，而 `CF_HDROP` 要真实文件）→
    “包内复制”分两拍，四件事必须一起做：
    * **与系统剪贴板互斥**：复制包内条目要清空系统剪贴板，复制 / 剪切真实文件要清空内存里那份；粘贴时**先看系统剪贴
      板**（包内复制会清空它 → 它非空就一定更新）。否则“包内复制 → 资源管理器复制 → 回 exdir 粘贴”会粘出旧内容。
    * **一次 `Extract` 解多条目**：给 `IInArchive.Extract` 传完整条目号数组 + 一个“条目号 → 落盘路径”的回调
      （`SevenZipInterop.ExtractFiles`）；逐个条目调 `Extract` 在固实包（7z / rar）里会把同一块数据重解 N 遍；回调里
      **每次 `GetStream` 都要先关掉上一份输出流**，否则一次解几百个文件会攒几百个句柄。
    * **目录要自己建、也不能进条目表**：隐式目录在索引里没有 7z 条目号（`ArchiveNode.ArchiveIndex == null`），塞进
      `Extract` 只会白跑；空目录要显式 `Directory.CreateDirectory`，否则“把空目录复制到外面”得到一个不存在的源路径。
    * **每个选中项解到自己的序号子目录**（`<guid>\<序号>\<包内相对路径>`）：跨目录同名条目不会在临时目录互相覆盖，
      而交出去的路径最后一段仍是包内那个名字（`SHFileOperation` 只看最后一段）。
    另：“服务级测试全过、界面测试全挂”往往只是**没重新 build**（本仓库碰过一次）。
86. 想造测试用 ISO 不要去指望 7-Zip（`7z a -tiso` 不支持，7-Zip 只能读 ISO）→ 用 Windows 自带的 **IMAPI2FS** 现造：
    `New-Object -ComObject IMAPI2FS.MsftFileSystemImage` → `FileSystemsToCreate = 3`（ISO9660 | Joliet）→
    `Root.AddTree(<目录>, $false)` → `CreateResultImage()`。两个注意点：
    * **`IStream` 要先在 C# 里 QI**：PowerShell 的 `[ComTypes.IStream]$obj` 会报“无法将 System.__ComObject 转换为
      IStream”；用一小段 `Add-Type`（或 C# 测试代码）写 `(IStream)streamObject` + `stream.Read(buffer, len, IntPtr)`
      分块拷到 `FileStream`（`pcbRead` 用 `Marshal.AllocHGlobal(4)`，读到 0 就是结尾）。
    * **要 Joliet 才有小写名**：`FileSystemsToCreate = 1`（纯 ISO9660）造出来的是大写 8.3（`HELLO.TXT` / `SUB`），
      `= 3` 才是干净的小写名；`= 4` 是**纯 UDF**（没有 ISO9660 那半，`-tIso` 打不开、`-tUdf` 能进），`= 5` 是
      ISO9660 + UDF 混合（ISO9660 那半是大写 8.3），`= 7` 三套都写。
    造出来的映像只有 50～60 KB，正合适；`tools\archive-smoke` 与 `test-archive-copy.ps1` 就是这么现造的（造不出就 SKIP）。

87. `.iso` 上挂着 **Iso 与 Udf 两个处理器**，而一张盘上两套文件系统的**内容可以不一样**：Windows 刻出来的 UDF 盘上，
    ISO9660 那半只有一张写着 “This disc contains a "UDF" file system…” 的 README.TXT，真正的内容全在 UDF 里 →
    只取「第一个能打开的处理器」（格式表里 Iso 在前）的话，用户看到的整张盘就只有一个 README.TXT
    （`7z l` 这种命令行工具反而显示 UDF 内容，很容易对着比出差异）。
    * 修法：`ArchiveService.BuildIndex` 把能打开的处理器都过一遍，**条目多的那个说了算** —— `ItemCount` 当粗筛
      （不比手上这份多就直接跳过，省掉大 ISO 上一整轮 `GetProperty`），真认的是 `ReadNodes` 出来的行数；并列时保留
      格式表顺序（ISO9660 / Joliet 与 UDF 内容一样时不用白读一遍）。
    * 顺带：密码失败也改成「所有处理器都打不开才报」，否则 `.rar` 的 Rar / Rar5 里一个问密码就会拦住另一个。
    * 测试映像只能自己造（第 86 条）：IMAPI2FS 只会把文件**同时**写进两套文件系统，「UDF 说明盘」由
      `archive-smoke` 的 `TryCreateNoticeIso` 拿 `FileSystemsToCreate = 4` 的 UDF 映像打底，在 16～24 号扇区手写一张
      只含 README.TXT 的最小 ISO9660，并把 UDF 卷识别序列挪到 18/19/20 —— 源映像 19 号扇区之后还有 UDF 自己的东西，
      所以只能动最前面几个扇区（整张照搬、只覆写前面那一段），否则 UDF 那半就废了。

88. 「压缩包行就地展开」把「只读」从一个**目录级**属性变成了**行级**属性，三个地方必须跟着改，否则会出现很难
    的一句“什么都没发生”或“掋错地方”：
    * **不要用 `IsDirectory` 当“能不能展开”**：压缩包行是真实文件（`IsDirectory` 必须保持 false，否则排序 /
      状态栏“均为文件夹”/ 拖放落点都跟着变）→ 先加 `FileSystemEntry.IsArchive` / `IsInArchive` 两个标记，再把展开
      收做 `FileItemViewModel.IsExpandable = IsDirectory || IsArchive`（`CanExpand`、`ToggleExpandAsync`、`FindNode`、
      `RestoreExpansionAsync`、方向键全部改用它）。
    * **原来的 `RefuseInArchive()` 看的是当前目录**（`IsInsideArchive`）：在真实目录里就地展开时它是 false，于是
      「删除」会真去删那个不存在的虚拟路径（靠 `FileExists`/`DirectoryExists` 过滤才没出事）、「拖拽」会写出一个
      指向虚拟路径的 `CF_HDROP`。→ 拆成两个守卫：目录级（粘贴 / 新建 / 终端 / 拖入）与**选中项级**
      （`RefuseSelectionInArchive()`：剪切 / 删除 / 属性 / 在资源管理器中显示）；「复制」要按选中项自己解析压缩包
      （`CopyArchiveSelection` 不再读 `ArchiveFile`），混选时明确拒绝而不是默默只复制一半。
    * **拖放落点不能“算不出来就退回当前目录”**：鼠标压在包内目录行上时，`RowAt` 给的是虚拟路径，而旧代码
      （`TryResolveDrop` / `CompleteInternalDropAsync`）拿不到目录行就 `?? viewModel.CurrentPath` → 用户盯着包内
      那个 `sub`，东西却搬进了当前真实目录。→ 压到 `IsInArchive` 的行上**整个拖放不接受（返回 false / 直接 return）**，
      `HandleDropAsync` 的 `_dropTargetDirectory` 也会因为 `DragOver` 的失败分支已经被清空，所以不会补上一次错操作。
    * 另：`DragStarting` 里的取消路径要把 `_draggingPaths` 清掉（否则“上一次拖拽 + 这一次被取消”的组合下，
      `StartRowDragAsync` 收尾的兜底会拿**上一批路径**真做一次移动）。

89. 冒烟工程 `archive-smoke` 里的 **IMAPI2FS 造测试 ISO 用的是 C# 的 `dynamic`**（COM 晚期绑定，Microsoft.CSharp
    的 `IDispatchComObject`）—— 它在**裁剪过**的发布版里会踩出 `AccessViolationException`：
    `IDispatchComObject.EnsureScanDefinedMethods` → `ITypeInfo.ReleaseTypeAttr`，**原生**崩溃，`try/catch` 拦不住
    （退出码 `0xC0000005`，只有事件日志 `.NET Runtime` 里才看得到）。
    `TrimmerRootAssembly Include="Microsoft.CSharp"` 与改用 `Type.InvokeMember`（报 `DISP_E_MEMBERNOTFOUND`）
    都试过，都不行。
    * 症状很有欺骗性：**Debug / 未裁剪的 Release 都正常，只有 `PublishTrimmed=true` 的产物一跑就“无输出猝死”**，
      而且崩在测试代理自己造映像那一步，与被测的压缩包代码无关。
    * 所以裁剪版这样跑：`dotnet publish tools\archive-smoke -c Release -r win-x64 --self-contained
      -p:PublishTrimmed=true -p:TrimMode=partial -o .artifacts\smoke-trimmed` →
      `.artifacts\smoke-trimmed\archive-smoke.exe --no-iso`（ISO / UDF 两段 SKIP）。
    * ISO **浏览**的裁剪版验证靠另一条路：`tools\test-archive-copy.ps1 -Exe dist\win-x64\exdir.exe` 用例 6
      （测试 ISO 由 PowerShell 侧的 IMAPI2FS 现造，宿主进程没被裁剪，不受影响）。

90. `InfoBar` 的 `IsOpen` 绑 `{x:Bind ... Mode=OneWay}` 时，**用关闭按钮关掉它并不会把 VM 里的状态改掉**：控件自己把
    `IsOpen` 置 false（本地值），绑定那边的“上次已知值”还是 true → 下一次消息内容变了也不会重新弹出来（详见
    `DetailsView` 的「解压完成」提示条）。→ 要接 `CloseButtonClick` 把 VM 里的状态清掉（`ClearStatus()`），
    或者把 `IsOpen` 做成 TwoWay。同理：任何“一次性提示”的状态都不该只由控件关闭。

91. 把 `-and` 放在**下一行开头**（哪怕在 `Assert (…)` 这种命令参数的括号里）会被 PowerShell 解析成新的命令/参数，
    报的是“表达式中缺少右括号”这类看不懂的错 → 要么把运算符放在上一行末尾，要么先算到中间变量再断言。
    改完脚本先拿 `[System.Management.Automation.Language.Parser]::ParseFile` 扫一遍（同第 69 条）。

92. 设置里的「浏览…」文件夹选择器直接调外壳的 `IFileOpenDialog`（`FOS_PICKFOLDERS`，见 `Helpers/FolderPicker`），
    不用 WinRT 的 `FolderPicker`：后者只能给一个 `PickerLocationId` 枚举、**指不了具体路径**，而
    `IFileOpenDialog.SetFolder` 可以让对话框从当前配置的目录开始。两个坑：
    * 非打包进程里它照常能用，但必须从有 COM 的 STA 线程（WinUI 的 UI 线程）调，并且要给 `Show` 传宿主窗口的 HWND
      （`WindowNative.GetWindowHandle`）。正文是 `UserControl`、拿不到宿主窗口，所以 `SettingsWindow` 把 `this`
      赋给 `SettingsView.HostWindow`。
    * 它在宿主线程上是**模态**的：对话框开着的时候 app 的 UI 线程不抽消息 → **UIA 探测全线超时**
      （连 `AutomationElement.RootElement.FindAll` 都抛 `Operation timed out (0x80131505)`，`InvokePattern.Invoke`
      也一样）。所以回归脚本只能走 Win32：`EnumWindows` + `GetWindowText` 找那个 `#32770`（标题就是 `SetTitle` 写下的
      「选择文件夹」），再用 `PostMessage(WM_CLOSE)` 关掉（见 `test-settings.ps1` 用例 12）。
93. `SettingsCard` 的内容列是**自然宽度**（不是整宽），里面放一个定宽控件（比如 `Width="280"` 的 TextBox）时，窗口
    一窄（左边 `NavigationView` 还要吃掉 180 DIP）就会把它后面的按钮顶出卡片外 —— 症状是按钮在 UIA 里
    `IsOffscreen=true`、`BoundingRectangle` 为空，点都点不到（截图上像“这行只有一个文本框”）。
    → 一行里有多于一个控件时用 `Grid` 的 `*` + `Auto` 两列（路径框跟着缩、按钮永远占住自己的位置），
    别用横向 `StackPanel` + 固定宽度。另：`*` 列在放不下时只能缩到它的 `MinWidth`（本机 485 DIP 的窗口里就是
    140 DIP）—— 这是能接受的降级，比按钮点不到强。
94. 交互式回归脚本里两条“看着像产品 bug、其实是脚本自己写的”陷阱（`test-compress.ps1` 第一次在真机上跑时两条都踩了）：
    * `Set-Content $f '文本' -Encoding utf8` 会**自动补一个行尾 CRLF**，磁盘上的内容其实是 `"文本\r\n"` → 断言
      `-eq '文本'` 恒定 FAIL。要精确内容就用 `[System.IO.File]::WriteAllText($f, '文本')`（不补换行、无 BOM）；
      C# 侧同理用 `File.WriteAllText`（`archive-smoke` 一直是这么写的，所以只有界面脚本会翻车）。
    * 「文件存在」不等于「活干完了」：`CompressionService` 用 `FileMode.CreateNew` 先把最终文件名建出来、再以
      `FileShare.None` 往里写，而剪贴板是在落盘**之后**才写的 → 拿 `Test-Path` 当等待条件会随机撞上「文件正被
      另一个进程占用」（`ZipFile.OpenRead` 抛异常，整个脚本崩掉）或读到还没写剪贴板的旧内容。等待条件要用
      「能真的 `OpenRead` 出中央目录」/「剪贴板里已出现这个路径」，见脚本里的 `Wait-Zip` / `Wait-ClipboardContains`。
    * 另：桌面被锁时（`GetForegroundWindow()` 为 0、`OpenInputDesktop` 失败、有 `LogonUI.exe`）`SetCursorPos` /
      `mouse_event` / `SendKeys` 全部静默失效 → 真鼠标脚本会把「菜单没弹」报成产品问题。**跑之前先确认屏幕没锁。**

## 7. 非打包模式下的 API 限制

没有 Package Identity，**不要**用 `Windows.Storage.KnownFolders`、`ApplicationData.Current`、`StorageLibrary` 等依赖包
标识的 API。已用的等价 Win32/注册表方案见 `KnownFolderService`：

* 标准目录 → `Environment.GetFolderPath(SpecialFolder.*)`；
* OneDrive → 环境变量 `OneDrive*` + `HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder`；
* 其它云盘 → `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\*\UserSyncRoots`
  （值名是 SID，值是路径；显示名优先取路径最后一段，注册表项名只是内部标识）。

**未实现的功能与后续步骤全部在 `plan.md`。**
