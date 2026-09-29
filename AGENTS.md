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

# 2) 出 Release（构建 + 镜像到 dist\win-x64）
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

# 5) 重新生成应用图标（Assets\exdir.ico）
pwsh -NoProfile -File tools\make-icon.ps1

# 6) 拖放回归（文件列表 / 侧边栏 → 工具条固定目录，含右键取消固定）
#    需要交互桌面；当前 shell 提权时会自动改用 explorer.exe 以普通权限启动 exdir（见第 6 节第 21 条）
pwsh -NoProfile -File tools\test-pin-drag.ps1

# 7) 设置对话框回归（左导航三个分类 / 每页只显示本分类的开关 / 取消不落盘 / 保存立即落盘并作用到列表 / 跨分类读回）
#    需要交互桌面（真鼠标点击对话框按钮）
pwsh -NoProfile -File tools\test-settings.ps1

# 7b) 设置对话框截图（左侧每个分类各一张，肉眼验证“左导航 + 右正文”的布局）
#     需要交互桌面；输出 .artifacts\settings-<分类名>.png
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
```

### 任务收尾（每个任务都必须做）

**任何代码改动做完后，固定用一条命令收尾：**

```powershell
pwsh -NoProfile -File tools\publish.ps1
```

它会把最新 Release 产物镜像到 **`dist\win-x64`**——这就是交付给用户的版本。

* **只出 win-x64**，不要生成 x86 / ARM64（未验证）。
* `dist\win-x64\exdir.exe` 自包含，双击即可运行（目标机无需预装 .NET / Windows App Runtime）。
* 交付前确认 `dist` 是最新的：看 `dist\win-x64\build-info.txt`（记录源码提交、构建时间、文件数），
  并与 `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\exdir.exe` 的时间戳对照；
  两者不一致说明忘了 publish。
* `publish.ps1` 会自动校验 `exdir.pri` 与每个 `.xaml` 对应的 `.xbf` 都在产物里，缺了就报错。
* **不要用 `dotnet publish`**（见“踩过的坑”第 3 条）。

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
├─ App.xaml(.cs)              DI 容器、全局异常日志、创建主窗口
├─ MainWindow.xaml(.cs)       外壳：顶部菜单栏(TitleBar) / 工具条 / 侧边栏 / 1~2 个窗格
├─ Themes/ExdirTheme.xaml     紧凑密度覆盖 + 布局常量 + 扁平按钮样式 + 强调色悬停色刷（合并顺序在 XamlControlsResources 之后）
├─ Models/                    POCO：FileSystemEntry / DriveModel / AppSettings / QuickCommand / CloudSyncState / IconBitmap / 枚举（含 SettingsCategory）
├─ Services/                  I/O 与系统交互（接口 + 实现成对出现）
│   ├─ IFileSystemService     目录枚举（异步、跳过无权限项）、路径规整、云目录条目附带同步状态
│   ├─ IDriveService          DriveInfo 枚举
│   ├─ IKnownFolderService    用户标准目录 + 云存储同步根（注册表探测）
│   ├─ ICloudSyncService      云同步根判定 + 单个条目的同步状态（状态列）
│   ├─ IShellIconService      系统外壳图标（SHGetFileInfo 提取 + 两级缓存，见第 4 节“名称列图标”）
│   ├─ ISettingsService       settings.json 读写（含结构版本迁移）
│   ├─ IShellService          默认程序打开 / 终端 / 剪贴板 / 命令行
│   └─ Native/                Win32 互操作（ShellPropertyStore：属性系统 + 占位符兼容模式；
│                             ShellIconExtractor：图标提取 / HICON → BGRA 像素）
├─ ViewModels/
│   ├─ MainViewModel          磁盘、固定目录、快捷命令、侧边栏、两个窗格、全局命令
│   ├─ PanelViewModel         一个窗格（标签页集合）
│   ├─ FolderTabViewModel     一个标签页（当前目录、条目、选中、历史、排序、地址栏编辑态）
│   ├─ PathSegmentViewModel   地址栏面包屑里的一段路径（显示名 + 完整路径 + 是否当前段）
│   ├─ SidebarViewModel       文件夹树（懒加载）
│   ├─ FileItemViewModel      列表一行（带 Depth/IsExpanded/Children，可展开）
│   ├─ PinnedFolderViewModel  title 栏上的固定目录
│   ├─ StatusBarViewModel     文件列表区底部状态栏（项数 / 选中摘要 + 合计大小 / 卷容量）
│   ├─ SettingsCategoryViewModel  设置对话框左侧导航的一项（Key + Name）
│   └─ SettingsViewModel      设置对话框的编辑快照（点“保存”才写回 AppSettings）+ 分类与当前选中分类
├─ Views/                     SidebarView / DriveBarView / PaneView / NavigationBarView / PathBreadcrumb / DetailsView
│                             StatusBarView（文件列表区底部一行）
│                             SettingsDialog（ContentDialog：左分类导航 + 右正文 + 底部保存/取消）
│                             SettingsToggleRow（设置对话框里的一行开关：标题 + 说明 + ToggleSwitch）
├─ Controls/PaneSplitter.cs   自研分隔条（WinUI 没有 GridSplitter）
│           ColumnResizeHandle.cs 列头右边界拖动把手（调列宽 / 双击复位）
├─ Helpers/                   ColumnLayout(列宽：requested/rendered + 自适应) / CloudSyncStateHelper(状态字形+文案) / DpiHelper / FileTypeHelper(类型名 + 图标字形兜底) / IconImageHelper(图标像素 → ImageSource + 共享缓存) / SizeFormatter / DragDropHelper(内部拖放格式)
├─ Converters/CommonConverters.cs
├─ Diagnostics/Log.cs
├─ Assets/                    图标等（exdir.ico 由脚本生成）
└─ tools/                     capture / inspect-ui / shot-settings / test-pin-drag / test-settings / test-status-bar / test-shell-icons /
                              measure-row-align / publish / make-icon 脚本
```

## 4. 界面布局约定（改动前务必对齐）

窗口自上而下三行：

```
┌─────────────────────────────────────────────────────────────────┐
│ 行0 TitleBar 控件:  ☰ |MenuBar(文件/编辑/查看/转到/工具/配置/帮助)| … │ 系统窗口按钮 ─┐
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
* 侧边栏分组：`主目录`（可点击，指向 %USERPROFILE%，子项为桌面/文档/下载/图片/音乐/视频）、
  `云存储`（注册表探测到的同步根）、`此电脑`（各磁盘）。分组节点本身可导航当且仅当它有路径
  （`SidebarNodeViewModel.IsNavigable`）。
* 紧凑密度靠 `Themes/ExdirTheme.xaml` 里覆盖 WinUI 数值型资源实现：
  `TitleBarExpandedHeight=36`、`ListViewItemMinHeight=24`、`TreeViewItemMinHeight=24`。
  **只覆盖数值/颜色类资源键**，不要覆盖控件隐式样式（会丢掉默认 ControlTemplate）。
  同一处还关掉了选中行的左侧蓝色竖条：`ListViewItemSelectionIndicatorVisualEnabled=False`（只看整行底色）。
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
    面包屑分段、地址栏右侧空白区、文件列表行内那个 18px 展开箭头，悬停/按下都是普通灰色
    （色刷 `ExSubtleButtonBackgroundPointerOver/Pressed`：深色主题叠白、浅色主题叠黑）。
    地址栏空白区几乎铺满整条地址栏，行内箭头只有 18px，一上强调色就喧宾夺主。
    这两个样式与 `ExToolbar*` 只差悬停/按下的画刷，但 Storyboard 里的 `{ThemeResource}` 键
    是写死在模板里的（`BasedOn` 改不了），所以模板另写了一份，改模板时两处要同步。
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
  * 排序对树的**每一层**生效（`CompareNodes`）；排序/刷新后由 `FolderTabViewModel.PendingSelection`
    （一组路径，视图读，不清空）让视图在新集合里把行选回来；返回上一级用 `selectPath` 选中来源目录。
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
  * 拖动把手 `Controls/ColumnResizeHandle` 由 `DetailsView` code-behind 追加到 `HeaderContent.Children`，
    位置用 `TranslateTransform.X` 推到列边界（**不要用 Canvas**：Canvas 子元素实测高度为 0，命中区会是空的），
    双击把手 = 该列恢复默认宽度 + 回到自动模式；宽度为 0 的列（下面说的“状态”列）会把把手
    `Visibility=Collapsed`，否则它会压在名称列左边界上抢走点击。
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
  * 编辑态由 `FolderTabViewModel.IsPathEditing` 持有（不在视图里），回车 `NavigatePathCommand` 前往、
    `Esc` 或失焦 `CancelPathEdit()` 取消；**任何一次成功导航都会自动退出编辑态**；
  * 路径比地址栏宽时 `CrumbScroll` 滚到最右（当前目录永远可见），左端露省略号提示还有被裁掉的分段。

### 键盘快捷键（定义在 MainWindow.xaml 的 `Grid.KeyboardAccelerators`）

| 快捷键 | 动作 |
| --- | --- |
| `Alt+←` / `Alt+→` / `Alt+↑` | 后退 / 前进 / 上一级 |
| `F5` | 刷新活动窗格 |
| `Ctrl+T` / `Ctrl+W` | 新建 / 关闭标签页 |
| `Ctrl+H` | 显示/隐藏隐藏文件 |
| `Ctrl+B` | 显示/隐藏侧边栏 |
| `F6` | 切换活动窗格 |
| `F10` | 单窗格 / 双窗格切换 |
| `Ctrl+L` / `Alt+D` | 编辑活动窗格的地址栏（等价于点地址栏空白处） |

另外：`文件列表 / 侧边栏文件夹树` 里的**目录**可以直接拖到工具条右侧的“固定目录”区固定下来。

### 设置对话框（所有配置项的唯一入口）

* 菜单栏**「配置 → 设置…」**弹出 `Views/SettingsDialog`（一个 `ContentDialog`），
  **底部是「保存 / 取消」**（`PrimaryButtonText="保存"` + `CloseButtonText="取消"` +
  `DefaultButton="Primary"`，即回车 = 保存、Esc = 取消）。
* **左导航 + 右正文**（2026-09 重构，原来是“一列分组勾选框”）：
  左侧是配置大类列表（`ListView`）+ 右侧只显示当前分类那一页。当前三个分类：

  | 分类 | 配置项 |
  | --- | --- |
  | 文件列表 | 显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面 |
  | 外观 | 过渡动画 |
  | 布局 | 列宽自动适应窗格宽度 / 显示工具条 / 显示侧边栏 / 双窗格模式 |

  * 分类是 `Models/SettingsCategory`（枚举）+ `SettingsViewModel.Categories`
    （列表顺序即导航顺序，`SelectedCategory` 直接绑 `ListView.SelectedItem`，TwoWay）；
    右侧三页的可见性绑 `IsFileListPageVisible` / `IsAppearancePageVisible` / `IsLayoutPageVisible`
    （`SelectedCategory` 的 setter 里一次性通知这三个，省得每页各写一个枚举转换器）。
  * 非当前页是 `Visibility=Collapsed`，**UIA 树里根本没有它们**：所以回归脚本
    “切到某分类后只看得到该分类的开关”本身就是“切页真的生效”的验证。
  * 每一行是 `Views/SettingsToggleRow`（`Title` / `Description` / `IsOn` 三个 DP）：
    左边“标题 + 灰色说明”，右边一个 `ToggleSwitch`（`OnContent`/`OffContent` 留空，
    否则默认的“开 / 关”文字会把开关推歪）。开关的 UIA 名字就是 `Title`（脚本按名字找它）。
    `IsOn` 由对话框 `{x:Bind ViewModel.Xxx, Mode=TwoWay}` 绑到快照上。
  * **对话框尺寸是定死的**（实测约 606×408 DIP，三页完全一致，切分类不跳）：
    两列都用固定 `GridLength`（`156` / `400`）——星号列不参与 DesiredSize 计算，
    不写死的话最长的说明文字会把对话框撑成三种宽度；根 `Grid` 用 `MinHeight`（不是 `Height`）
    兜住最矮的「外观」页，窗口太矮时右侧 `ScrollViewer` 自己出滚动条。
  * `ContentDialog` 默认最宽只有 548 DIP（`ContentDialogMaxWidth`），装不下两栏，
    所以在 `ContentDialog.Resources` 里覆盖成 `520 / 680`；同一个地方还要再关一次
    `ListViewItemSelectionIndicatorVisualEnabled`（见第 6 节第 38 条）。
  * 导航项**只有文字、没有图标**（为什么见第 6 节第 37 条）。
* **编辑的是快照**（`ViewModels/SettingsViewModel`，构造时从 `AppSettings` 复制一份）：
  `ShowAsync()` 返回 `Primary` 才调 `MainViewModel.ApplySettings(snapshot)` 写回并**立即落盘**，
  返回 `Close`（取消）就什么都不做——不需要逐项回滚，也不会误写 `settings.json`。
* `MainViewModel.ApplySettings` 里先把值写进 `AppSettings`、再统一刷新界面：
  隐藏文件 / 扩展名会重新枚举目录，所以这两项是“最后只刷一次”；
  过渡动画只改视图行为（`tab.ApplyAnimationSettings()`），不重载目录。
* 「查看」菜单里的工具条 / 侧边栏 / 双窗格（含快捷键）保留：它们是**命令型**菜单项
  （`MenuFlyoutItem` + `ToggleXxxCommand`，本来就不显示勾选标记），和对话框切的是同一份设置，
  两边不会各说各话（`ApplySettings` 会 `OnPropertyChanged(ShowHiddenFiles/ShowExtensions/…)`，
  对话框里改完再点菜单项，切的就是新状态）。
* **新增配置项要动六个地方**：`AppSettings` 字段 → `SettingsViewModel` 属性（放进对应分类的注释段）→
  `SettingsDialog.xaml` 里**对应分类页**加一行 `SettingsToggleRow` → `MainViewModel.ApplySettings` 应用 →
  `tools/test-settings.ps1` 的 `$KeyMap`（UIA 名字 → 字段名）与 `$CategoryMap`（分类 → 该页的项）→
  需要新分类时再往 `SettingsCategory` / `Categories` 里加一项。
* 回归：`tools/test-settings.ps1`（4 个用例 26 条断言）；
  肉眼看布局用 `tools/shot-settings.ps1`（每个分类截图到 `.artifacts\settings-<分类名>.png`）。
* 对话框比窗口还大时（窗口被缩到 606×408 DIP 以下）边缘会被裁掉：`ContentDialog`
  只会把对话框约束在窗口内，不会自己缩小；正常窗口尺寸（默认 1280×800 DIP）下离边很远。

## 5. 必须遵守的编码约定

* **分层**：`Views` 不直接做 I/O，一律经由 ViewModel → `Services` 接口。
* **服务成对**：新增能力先加 `IXxxService`，再写实现，最后在 `App.ConfigureServices()` 注册。
* **异步**：耗时 I/O 走 `Task.Run`（见 `FileSystemService`），
  从 UI 线程 `await` 时**不要** `ConfigureAwait(false)`，让续体回到 UI 线程后再更新 `ObservableCollection`。
* **UserControl 的 ViewModel 必须是 DependencyProperty**，由父级用 `{x:Bind ...}` 赋值；
  子控件内部用 `{x:Bind ViewModel.X, Mode=OneWay}`。
  （普通 CLR 属性在 `InitializeComponent` 之后赋值时绑定不会生效。）
* **主窗口的 ViewModel 用普通只读属性，且必须在 `InitializeComponent()` 之前赋值**，
  因为 `x:Bind` 在 `InitializeComponent` 期间求值。
  `SettingsDialog`（`ContentDialog` 子类）同理：`ViewModel` 属性在构造函数里先赋值再 `InitializeComponent()`。
* **新增配置项要动六个地方**（详见第 4 节“设置对话框”）：`AppSettings` 字段 → `SettingsViewModel` 属性 →
  `SettingsDialog.xaml` 对应分类页里的 `SettingsToggleRow` → `MainViewModel.ApplySettings` 应用 →
  `tools/test-settings.ps1` 的 `$KeyMap` 与 `$CategoryMap` → 需要时再往 `SettingsCategory` 加分类。
  对话框只负责编辑快照，落盘与应用只在 `MainViewModel.ApplySettings` 一处发生。
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
   `XamlParseException: XAML parsing failed`。
   手动往 `ResolvedFileToPublish` 里补会更糟（整个发布目录被写成同一个文件的内容）。
   **因此发布流程 = `dotnet build` + 镜像输出目录**，见 `tools/publish.ps1`。
4. **`PublishReadyToRun=true` / `PublishTrimmed=true` 都不能开**，同样会导致启动期 XAML 失败。
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
38. **同一个主题资源键，在 `ContentDialog` 里可能读不到 App 级的值**：
    `Themes/ExdirTheme.xaml` 里已经把 `ListViewItemSelectionIndicatorVisualEnabled` 设成 `False`
    （主窗口的文件列表确实没有那条左侧竖条，见第 4 节），但设置对话框里的 `ListView`
    还是画出了强调色竖条；把同一个键再写进该对话框的 `<ContentDialog.Resources>` 就消失了。
    所以“在某个弹层里发现某个主题资源像是没生效”时，先在**这个弹层自己的 `Resources` 里再覆写一遍**，
    而不是去怀疑 `ExdirTheme.xaml` 的合并顺序。

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

* 非打包工程改造、单实例主窗口、Mica 背景、自定义标题栏、图标与窗口位置持久化；
* 磁盘条、固定目录、快捷菜单（按需求留空，仅设置驱动）、侧边栏文件夹树（懒加载）；
* 1/2 窗格 + 自研分隔条、TabView 多标签；
* 详细信息列表：**目录可就地展开的树形列表**（行内箭头 / `←→` 方向键展开、懒加载、刷新后恢复展开）、
  状态/名称/修改日期/类型/大小五列、点列头排序（含树的每一层）、**列宽可拖动+双击复位+持久化**、
  多选、双击进入目录、无选中蓝色竖条、选中行不随排序/刷新丢失；
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
* **所有配置项集中在设置对话框**（2026-09，S17a / S17b）：菜单栏「配置 → 设置…」弹出
  `Views/SettingsDialog`（`ContentDialog`，底部「保存 / 取消」）。**左导航 + 右正文**两栏：
  左侧三个分类（文件列表 / 外观 / 布局，都是纯文字项），右侧只显示当前分类那一页，
  每项是“标题 + 说明 + 右侧开关”（`Views/SettingsToggleRow`）；
  共 8 项：文件列表（显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面）、
  外观（过渡动画）、布局（列宽自动适应窗格宽度 / 显示工具条 / 显示侧边栏 / 双窗格模式）。
  编辑的是 `SettingsViewModel` 快照，**点“取消”什么都不改、点“保存”立即应用并落盘**（不再等退出才写），
  应用入口是 `MainViewModel.ApplySettings`；新增配置项要同时改 `AppSettings`、`SettingsViewModel`、
  `SettingsDialog.xaml`、`ApplySettings` 与 `tools/test-settings.ps1` 的 `$KeyMap` / `$CategoryMap`（见第 4 节）。
  回归：`tools/test-settings.ps1`（4 个用例 26 条断言：分类齐全 / 每页只显示本分类的开关 / 初始值一致 /
  取消不落盘 / 保存立即落盘并作用到文件列表 / 跨分类读回），
  布局截图：`tools/shot-settings.ps1`（.artifacts\settings-<分类名>.png）。
  原先的「配置 → 文件列表」子菜单（三个 `ToggleMenuFlyoutItem`）已移除，
  「查看」菜单里的工具条 / 侧边栏 / 双窗格三项保留（与对话框共享同一份设置）；
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
* **悬停/按下/选中高亮统一改成强调色**（2026-09）：原来是 WinUI 默认的 8% 白（灰底上几乎看不出来），
  现在工具条按钮悬停是强调色 35%（深色）/ 25%（浅色）、按下 60% / 50%；
  文件列表行 / 侧边栏树 / 标签页头 / 菜单项等大表面用低一档的 `ExSurface*`，
  选中态也是强调色、比悬停略重（层次：选中 > 悬停），见第 4 节；
  验证：`tools/inspect-ui.ps1 -Hover Documents` / `-HoverAt "608,191"` 截图对比；
  例外：地址栏（面包屑分段 / 右侧空白区）与文件列表行内的展开箭头保持普通灰色悬停，
  见第 4 节 `ExSubtleButtonStyle`；
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
* 快捷键、右键菜单尚未实现；
* 文件操作（复制/移动/删除/重命名/新建/压缩/哈希）**完全未实现**。

**未实现的功能与后续步骤全部在 `plan.md`。**
