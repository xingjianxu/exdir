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

# 5) 重新生成应用图标（Assets\exdir.ico）
pwsh -NoProfile -File tools\make-icon.ps1

# 6) 拖放回归（文件列表 / 侧边栏 → 工具条固定目录，含右键取消固定）
#    需要交互桌面；当前 shell 提权时会自动改用 explorer.exe 以普通权限启动 exdir（见第 6 节第 21 条）
pwsh -NoProfile -File tools\test-pin-drag.ps1
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
├─ Themes/ExdirTheme.xaml     紧凑密度覆盖 + 布局尺寸常量（合并顺序在 XamlControlsResources 之后）
├─ Models/                    POCO：FileSystemEntry / DriveModel / AppSettings / QuickCommand / CloudSyncState / 枚举
├─ Services/                  I/O 与系统交互（接口 + 实现成对出现）
│   ├─ IFileSystemService     目录枚举（异步、跳过无权限项）、路径规整、云目录条目附带同步状态
│   ├─ IDriveService          DriveInfo 枚举
│   ├─ IKnownFolderService    用户标准目录 + 云存储同步根（注册表探测）
│   ├─ ICloudSyncService      云同步根判定 + 单个条目的同步状态（状态列）
│   ├─ ISettingsService       settings.json 读写（含结构版本迁移）
│   ├─ IShellService          默认程序打开 / 终端 / 剪贴板 / 命令行
│   └─ Native/                Win32 互操作（ShellPropertyStore：属性系统 + 占位符兼容模式）
├─ ViewModels/
│   ├─ MainViewModel          磁盘、固定目录、快捷命令、侧边栏、两个窗格、全局命令
│   ├─ PanelViewModel         一个窗格（标签页集合）
│   ├─ FolderTabViewModel     一个标签页（当前目录、条目、选中、历史、排序、地址栏编辑态）
│   ├─ PathSegmentViewModel   地址栏面包屑里的一段路径（显示名 + 完整路径 + 是否当前段）
│   ├─ SidebarViewModel       文件夹树（懒加载）
│   ├─ FileItemViewModel      列表一行（带 Depth/IsExpanded/Children，可展开）
│   └─ PinnedFolderViewModel  title 栏上的固定目录
├─ Views/                     UserControl：SidebarView / DriveBarView / PaneView / NavigationBarView / PathBreadcrumb / DetailsView
├─ Controls/PaneSplitter.cs   自研分隔条（WinUI 没有 GridSplitter）
│           ColumnResizeHandle.cs 列头右边界拖动把手（调列宽 / 双击复位）
├─ Helpers/                   ColumnLayout(列宽：requested/rendered + 自适应) / CloudSyncStateHelper(状态字形+文案) / DpiHelper / FileTypeHelper / SizeFormatter / DragDropHelper(内部拖放格式)
├─ Converters/CommonConverters.cs
├─ Diagnostics/Log.cs
├─ Assets/                    图标等（exdir.ico 由脚本生成）
└─ tools/                     capture / inspect-ui / test-pin-drag / publish / make-icon 脚本
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
* 菜单栏**「配置 → 文件列表」**（2026-09）：过渡动画开关（`AppSettings.EnableListAnimations`，
  关闭后 `DetailsView` 会清空 `ListView` 的 `ItemContainerTransitions`/`Transitions` 并同步已生成的行容器，
  换目录、插行、排序都直接到位）、显示隐藏文件、显示文件扩展名；
  三项都是 `ToggleMenuFlyoutItem` 双向绑定 ViewModel 属性，改动立即生效并随退出落盘；
* **工具条“固定目录”支持拖放固定**（2026-09）：文件列表 / 侧边栏树里的目录可以直接拖到工具条右侧的
  固定目录区（拖拽时强调色高亮 + “固定到工具条”提示，松手即写 `settings.json`），
  右键固定目录按钮可“取消固定”；最多固定 12 个（`MainViewModel.MaxPinnedFolders`）；
  验证脚本 `tools/test-pin-drag.ps1`（列表 → 固定、侧边栏 → 固定、右键 → 取消固定 三个用例）；
* 快捷键、右键菜单尚未实现；
* 文件操作（复制/移动/删除/重命名/新建/压缩/哈希）**完全未实现**。

**未实现的功能与后续步骤全部在 `plan.md`。**
