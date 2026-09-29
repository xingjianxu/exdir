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
├─ Models/                    POCO：FileSystemEntry / DriveModel / AppSettings / QuickCommand / 枚举
├─ Services/                  I/O 与系统交互（接口 + 实现成对出现）
│   ├─ IFileSystemService     目录枚举（异步、跳过无权限项）、路径规整
│   ├─ IDriveService          DriveInfo 枚举
│   ├─ IKnownFolderService    用户标准目录 + 云存储同步根（注册表探测）
│   ├─ ISettingsService       settings.json 读写
│   └─ IShellService          默认程序打开 / 终端 / 剪贴板 / 命令行
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
├─ Helpers/                   ColumnLayout(列宽：requested/rendered + 自适应) / DpiHelper / FileTypeHelper / SizeFormatter
├─ Converters/CommonConverters.cs
├─ Diagnostics/Log.cs
├─ Assets/                    图标等（exdir.ico 由脚本生成）
└─ tools/                     capture / inspect-ui / publish / make-icon 脚本
```

## 4. 界面布局约定（改动前务必对齐）

窗口自上而下三行：

```
┌─────────────────────────────────────────────────────────────────┐
│ 行0 TitleBar 控件:  ☰ |MenuBar(文件/编辑/查看/转到/工具/配置/帮助)| … │ 系统窗口按钮 ─┐
│                              中间: 当前目录名                     │  (AppWindow)  │
├─────────────────────────────────────────────────────────────────┤
│ 行1 工具条:  左=磁盘/网络盘/可移动盘      右=固定目录 + 快捷菜单⋯  │
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
    任何窗格尺寸下都能看到全部列；
  * 手动模式：用户拖过任意列边界就进入（`SetRequestedWidth` 会关掉 `AutoFit`），列宽就是拖出来的值，
    装不下就横向滚动（`ScrollViewer.HorizontalScrollMode=Enabled`，行 Grid 的
    `MinWidth={x:Bind Columns.RowMinWidth}` 提供滚动范围）；
  * 手动模式下列头靠 `TranslateTransform` 跟随 `ScrollViewer.HorizontalOffset`，
    `HeaderRow.Clip` 负责裁掉平移出去的部分；
  * 拖动把手 `Controls/ColumnResizeHandle` 由 `DetailsView` code-behind 追加到 `HeaderContent.Children`，
    位置用 `TranslateTransform.X` 推到列边界（**不要用 Canvas**：Canvas 子元素实测高度为 0，命中区会是空的），
    双击把手 = 该列恢复默认宽度 + 回到自动模式。
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
  名称/修改日期/类型/大小四列、点列头排序（含树的每一层）、**列宽可拖动+双击复位+持久化**、
  多选、双击进入目录、无选中蓝色竖条、选中行不随排序/刷新丢失；
* 导航条（← → ↑ ⟳ + **面包屑地址栏**）在**每个标签页内部**（`Views/NavigationBarView` +
  `Views/PathBreadcrumb`），标签页之间历史与编辑态互不影响；
  路径按目录分段显示（chevron 分隔）、点分段跳转、点当前目录段或右侧空白区就地编辑，回车跳转、Esc 取消；
  超长路径自动滚到最右（当前目录永远可见）并在左端提示省略，编辑入口也有 `Ctrl+L` / `Alt+D`；
* 前进/后退/上一级历史、路径框回车跳转、显示隐藏文件、显示扩展名、会话恢复；
* 菜单栏**「配置 → 文件列表」**（2026-09）：过渡动画开关（`AppSettings.EnableListAnimations`，
  关闭后 `DetailsView` 会清空 `ListView` 的 `ItemContainerTransitions`/`Transitions` 并同步已生成的行容器，
  换目录、插行、排序都直接到位）、显示隐藏文件、显示文件扩展名；
  三项都是 `ToggleMenuFlyoutItem` 双向绑定 ViewModel 属性，改动立即生效并随退出落盘；
* 快捷键、右键菜单尚未实现；
* 文件操作（复制/移动/删除/重命名/新建/压缩/哈希）**完全未实现**。

**未实现的功能与后续步骤全部在 `plan.md`。**
