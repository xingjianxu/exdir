# plan.md — exdir 分步实现计划

> 目的：把 Directory Opus 级别的功能拆成**每次只做一步**的任务，确保任何单次会话都不超出大模型上下文窗口。
> 通用约定、命令、坑位记录见 **`AGENTS.md`**；本文件只负责“下一步做什么、怎么算做完”。
> 每完成一步，就把该步的 `[ ]` 改成 `[x]`，并同步更新 `AGENTS.md` 的“当前状态”。

---

## 0. 每一步的标准流程（必须照做）

1. 只读本步骤列出的“涉及文件”+ 必要的相邻文件，不要全仓库通读。
2. 改代码 → `dotnet build exdir.csproj -c Debug -p:Platform=x64 --nologo`，零错误零新增警告。
3. 用 `tools/capture.ps1` 截图 / `tools/inspect-ui.ps1` 查控件坐标，确认界面真的对（不要凭感觉）。
4. 结果不符合预期时，第一件事是 `Get-Content $env:LOCALAPPDATA\exdir\exdir.log`。
5. 如果发现了新的 WinUI 坑或约定，写进 `AGENTS.md` 的“踩过的坑”。
6. 更新本文件的 `[x]` 与“当前状态”。
7. `git add -A && git commit`（提交信息用中文，说明本步做了什么、怎么验证的）。

**每步的规模上限**：新增/修改代码控制在 ~600 行以内、涉及文件不超过 8 个。超了就再拆一步。

---

## 1. 当前状态（已完成，可直接运行）

- [x] **S0 主体框架**（本次完成）
  - 非打包（unpackaged）工程改造：`WindowsPackageType=None` + `WindowsAppSDKSelfContained` + `SelfContained`；
    移除 MSIX 清单与 `EnableMsixTooling`；`Assets\exdir.ico` 由脚本生成并设为应用图标。
  - 外壳布局：`TitleBar` 控件（菜单栏 + 当前目录名 + 原生窗口按钮）/ 工具条（磁盘 + 固定目录 + 快捷菜单）/ 侧边栏文件夹树 / 1~2 个窗格。
  - `PaneSplitter`（WinUI 无 GridSplitter）、Mica 背景、紧凑密度（标题栏 36、列表行 24）。
  - `PaneView`：TabView 多标签，**每个标签页内部自带导航条**（`NavigationBarView`：后退/前进/上一级/刷新 + `PathBreadcrumb` 地址栏）。
  - 列表：详细信息布局（**可展开的树形列表**），列 名称/修改日期/类型/大小，点列头排序、多选、双击进入目录 / 打开文件、空目录与错误提示、选中摘要。
  - 列表交互（S1 部分完成，2026-09）：目录行可就地展开（懒加载子项、增量插行、刷新后恢复展开状态）；
    列宽可拖动 / 双击复位并持久化；自动模式（列宽随窗格自适应）与手动模式（固定列宽 + 横向滚动，列头跟随横向偏移）；
    排序/刷新后按路径恢复原来选中的行。
  - 地址栏（S9 主体完成，2026-09）：路径按目录切分成可点击的面包屑（chevron 分隔），
    点分段跳转、点当前段或右侧空白区切到可编辑输入框（回车前往、Esc/失焦取消），
    编辑入口也有 `Ctrl+L` / `Alt+D`；超长路径滚到最右并在左端提示省略。
  - 菜单栏**「配置 → 设置…」= 统一设置对话框**（2026-09，S17a / S17b）：**左导航 + 右正文**两栏，
    左侧三个分类（文件列表 / 外观 / 布局），右侧每项是“标题 + 说明 + 开关”，
    共八项（过渡动画 / 显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面 / 列宽自适应 /
    工具条 / 侧边栏 / 双窗格）；对话框是一个 `ContentDialog`（`Views/SettingsDialog`），
    底部「保存 / 取消」；点在快照上、点保存才应用并立即落盘（取消什么都不改）；
    回归脚本 `tools/test-settings.ps1`，布局截图 `tools/shot-settings.ps1`。
  - 工具条“固定目录”支持**拖放固定**（2026-09，S7a）：文件列表 / 侧边栏树里的目录拖到工具条右侧即固定，
    松手立即写 `settings.json`；右键固定目录按钮可“取消固定”；
    `AppSettings` 结构版本 2→3（新增 `PinnedFoldersInitialized`，修掉“取消完所有固定目录后重启默认值又回来”）。
  - 工具条“固定目录”支持**拖拽排序**（2026-09，S7b）：按住固定目录按钮横向拖到兄弟按钮上就换位，
    拖动时有插入位置提示条，松手立即写 `settings.json`（也是“工具条上的拖拽是由应用自己识别手势的”首个实现）。
  - 云文件夹的同步状态列（2026-09，S23）：云同步目录（OneDrive / WPS 云盘 / 其它 CFAPI 同步根）
    的列表最前面多出一列状态图标（已同步 / 仅在云端 / 已固定 / 正在同步 / 同步错误 / 未同步），
    可点列头排序；非云目录整列隐藏。
  - 侧边栏：主目录（含桌面/文档/下载/图片/音乐/视频）、云存储（注册表探测同步根）、此电脑（各磁盘）；展开时懒加载子目录。
  - 会话与设置：窗口位置/尺寸/最大化、双窗格、侧边栏宽度、标签页集合、排序偏好、固定目录 → `%LOCALAPPDATA%\exdir\settings.json`。
  - 快捷键：Alt+←/→/↑、F5、Ctrl+T/W、Ctrl+H、Ctrl+B、F6、F10。
  - 工具脚本：`capture.ps1`（截图）、`inspect-ui.ps1`（UIA 控件树 / 点击）、`publish.ps1`（Release 产物）、`make-icon.ps1`。
  - Release 产物：`dist\win-x64\exdir.exe`（自包含，224 MB / 531 文件，已验证可运行）。
  - 已知技术债：见本文件第 5 节。

---

## 2. 待用户确认的决策

这些会影响后续步骤的写法，**动手前先问清楚**（一次问完，不要在多个会话里反复问）：

1. ~~**每个窗格是否保留“导航条 + 路径框”？**~~ **已决定（2026-09）：导航条 + 路径框放在每个标签页内部**，
   即每个 tab 各自拥有后退/前进/上一级/刷新与地址栏（实现见 `Views/NavigationBarView`）。
2. **文件操作的实现路线**：
   - (A) 调用系统 `IFileOperation`（资源管理器同款进度/冲突/撤销对话框，代码最少，行为最“Windows”）；
   - (B) 自研复制/移动引擎（可完全控制 UI、支持队列、可暂停，但工作量大很多）。
   建议先 A，后续需要再逐步替换成 B。
3. **右键菜单**：直接弹**系统真实右键菜单**（`IContextMenu` + `SHBindToParent`，能拿到第三方 shell 扩展，但 UI 风格不统一、难以自动化测试）
   还是**自建菜单**（风格统一、可测试，但只能用自己实现的命令）？建议：自建为主 + “显示系统菜单”兜底项。
4. **视图形态**：目前只有“详细信息列表”。后续要不要 图标/缩略图/紧凑 三种？优先级如何？
5. **快捷菜单**首批要内置哪些命令（当前按需求留空，只保留了数据驱动的框架）。
6. **是否需要单元测试工程**（`exdir.Tests`，xUnit）？如果要，建议在第 3 步之前建立，之后每步补测试。
7. **深色/浅色主题**：跟随系统即可，还是需要手动切换？

---

## 3. 分步实施计划

### Phase 1 — 把“看得见的列表”做扎实（不涉及写文件，风险最低）

- [~] **S1 列表交互与列管理**
  - 目标：列表具备真实文件管理器的基本手感和列控制能力。
  - 涉及：`Views/DetailsView.xaml(.cs)`、`Helpers/ColumnLayout.cs`、`Controls/ColumnResizeHandle.cs`、`ViewModels/FolderTabViewModel.cs`、`ViewModels/FileItemViewModel.cs`、`Themes/ExdirTheme.xaml`、`Models/AppSettings.cs`。
  - 内容：
    - [x] 列宽可拖动：列头右边界自研把手（`Controls/ColumnResizeHandle`，竖直细条 + `SizeWestEast` 光标），双击恢复默认列宽/自动模式；
    - [x] 列宽持久化到设置（`AppSettings.ColumnWidths` + `ColumnAutoFit` + `ColumnAutoFillName`）；
    - [x] 目录行就地展开（树形列表）：`FileItemViewModel` 带 `Depth/IsExpanded/Children`，
          子项懒加载、展开用增量插行（保住滚动位置与选中项）、刷新后按路径恢复展开状态；箭头点击展开，
          `←/→` 方向键展开/折叠，双击仍是“进入目录”；
    - [x] 窗格过窄不再出现“列被挤出可视区”：自动模式各列按余量等比压缩，一旦手动拖过列宽就切到手动模式
          （固定列宽 + `ScrollViewer` 横向滚动，`HeaderContent` 跟随 `HorizontalOffset` 平移）；
    - [ ] 列头排序增加“名称自然排序”（`strings like file2 < file10`，实现 `NaturalStringComparer`）；
    - [ ] 右键列头 → 显示/隐藏列菜单（先只做 UI 骨架，行为在 S1 实现）。
  - 验收：拖动把手列宽变化且重启后保持（已用 `tools/inspect-ui.ps1` 量列边界验证）；把窗格拖到 400 DIP 宽时不出现列被裁掉；
    截图确认列头与数据行仍严格对齐。
  - 预估：~450 行，5 个文件。

- [ ] **S2 键盘与多选导航**
  - 目标：纯键盘可用。
  - 涉及：`Views/DetailsView.xaml(.cs)`、`ViewModels/FolderTabViewModel.cs`、`MainWindow.xaml(.cs)`。
  - 内容：`Ctrl+A` 全选、`Ctrl+Shift+A` 反选、`Enter` 打开、`Backspace` 上一级（仅当焦点在列表时，不要抢占路径框输入）、
    `Home/End/PageUp/PageDown`、输入字母快速定位（type-ahead）、`Esc` 清空选择、
    多选状态下 `Enter` 只打开第一项且目录全部在新标签页打开（可配置）。
  - 验收：`inspect-ui.ps1 -Keys` 发送按键后控件树中选中项数量正确；路径框输入 `Backspace` 不受影响。
  - 预估：~250 行，3 个文件。

- [x] **S3 状态栏与选择统计**（2026-09 完成）
  - 目标：文件列表区底部细状态栏，显示“N 项 / 已选 M 项（合计 X）/ 磁盘可用空间”。
  - 涉及：新建 `Views/StatusBarView.xaml(.cs)`、`ViewModels/StatusBarViewModel.cs`、`MainWindow.xaml(.cs)`、
    `ViewModels/MainViewModel.cs`、`ViewModels/FolderTabViewModel.cs`、`Services/IDriveService.cs`、`Views/DetailsView.xaml`。
  - 已完成：一条状态栏挂在窗格那列的第 1 行（窗格占满其余高度），高度 = `ExRowHeight`（24 DIP）、贴底；
    左侧 `N 项`、中间 `选中 M 项（合计 X）`（只累加文件；目录不递归，全选目录时显示“均为文件夹”）、
    右侧 `D: 可用 120 GB / 共 512 GB`（`IDriveService.GetDriveForPath`，后台读、按卷缓存 3 秒）；
    跟随活动窗格（F6 / 切标签页 / 换目录 / 改选中都会刷新）；
    删掉了原先 `DetailsView` 里每标签页一条的“选中摘要”条。
  - 验收：`tools/test-status-bar.ps1`（16 条断言全过）；状态栏高度实测 24 DIP，贴客户区底边，宽度 = 文件列表区（不跨侧边栏）。
  - 未做：选中项里**目录**的大小（当前只累加文件，需要后台递归求和）；状态栏里的加载 / 异步操作进度。

### Phase 2 — 文件操作基础设施（本项目的核心，务必先建底座）

- [ ] **S4 剪贴板与文件操作服务（只读骨架）**
  - 目标：搭好 `IFileOperationService` / `IClipboardService` 抽象与实现，先只接通“复制路径/复制文件列表到剪贴板（CF_HDROP + Preferred DropEffect）”，不做真正的拷贝。
  - 涉及：新增 `Services/IClipboardService.cs(.cs)`、`Services/IFileOperationService.cs(.cs)`、`Services/Native/ShellInterop.cs`、`App.xaml.cs`（注册）。
  - 内容：
    - Win32 `OleSetClipboard` 或 `DataPackage.SetStorageItems` 两种方案选一（非打包下推荐 Win32，避免包标识问题）；
    - 读剪贴板写成 `ClipboardSnapshot { Operation: Copy|Move, Paths }`；
    - `IFileOperationService` 先只声明方法签名 + 抛 `NotImplementedException` 的实现，便于后续步骤分步填充。
  - 验收：在资源管理器里复制若干文件 → exdir 粘贴按钮能读出版本正确的列表（先用日志/状态栏显示）。
  - 预估：~350 行，5 个文件。

- [ ] **S5 删除 / 重命名 / 新建（第一批真实写操作）**
  - 目标：`Delete`（进回收站）、`Shift+Delete`（永久删除）、`F2` 就地重命名、新建文件夹/文本文档。
  - 涉及：`Services/IFileOperationService.cs` + 实现、`ViewModels/FolderTabViewModel.cs`、`Views/DetailsView.xaml(.cs)`、`Views/RenameBox`（就地编辑用 `TextBox` 覆盖行）。
  - 内容：
    - 用 `SHFileOperation`/`IFileOperation` 完成删除（带回收站）；
    - 就地重命名：列表行切换到编辑态，回车确认、Esc 取消、查重名；
    - 新建后自动进入重命名态并滚动到可见；
    - 所有写操作完成后刷新当前目录并保留选中。
  - 验收：删到回收站能在资源管理器“回收站”里看到；重命名后选中项跟随；无权限时报 `InfoBar` 错误而不是崩溃。
  - 预估：~500 行，5 个文件。

- [ ] **S6 复制 / 移动（含跨窗格与冲突处理）**
  - 目标：剪贴板驱动的复制/剪切/粘贴，以及跨窗格直接复制/移动。
  - 涉及：`Services/IFileOperationService.cs` + 实现、`ViewModels/MainViewModel.cs`、`ViewModels/PanelViewModel.cs`、新增 `Views/OperationProgressDialog.xaml(.cs)`。
  - 内容：优先走系统 `IFileOperation`（获得标准进度/冲突/撤销对话框）；
    源目录与目标目录相同、目标已存在同名等边界处理；操作为异步且可取消；完成后两个窗格各自刷新。
  - 验收：复制 1 GB 目录有进度且可取消；同名冲突弹标准对话框；拖动窗格分隔条期间不阻塞。
  - 预估：~550 行，6 个文件。

- [ ] **S7 拖放**
  - 目标：窗格内/窗格间/与资源管理器互相拖放（`DataPackageOperation.Copy/Move/Link`），拖到目录行上悬停 1 秒进入该目录。
  - 涉及：`Views/DetailsView.xaml(.cs)`、`Views/PaneView.xaml(.cs)`、`Services/IFileOperationService.cs`。
  - 验收：从 exdir 拖到资源管理器能复制；从资源管理器拖进 exdir 能复制；窗格间拖动默认移动（同盘）/复制（跨盘）。
  - [x] **S7a 拖到工具条固定目录**（2026-09）：列表/侧边栏里的目录拖到工具条右侧的固定目录区即固定，
    立即落盘；拖拽时有强调色高亮 + “固定到工具条”提示；右键固定目录按钮可取消固定；外部来源
    （资源管理器，`StorageItems`）拖目录进来也能固定。
    新增 `Helpers/DragDropHelper.cs`、`tools/test-pin-drag.ps1`；改 `Views/DetailsView.xaml(.cs)`、
    `Views/SidebarView.xaml(.cs)`、`Views/DriveBarView.xaml(.cs)`、`ViewModels/MainViewModel.cs`、
    `Models/AppSettings.cs`（结构版本 2→3）、`Services/SettingsService.cs`。
    验收：`tools/test-pin-drag.ps1` 三个用例全通过（列表 `.cargo` → 固定、侧边栏“图片” → 固定、
    右键 Desktop → 取消固定），并检查 `%LOCALAPPDATA%\exdir\settings.json` 的 `PinnedFolders` 真的变了。
  - [x] **S7b 固定目录拖拽排序**（2026-09）：按住工具条上的固定目录按钮横向拖到兄弟按钮上即换位，
    拖动时按钮之间画 2px 强调色插入位置提示条 + “调整固定目录顺序”提示，松手即写 `settings.json`。
    新增格式 `DragDropHelper.PinnedReorderFormat`（`exdir/pinned-reorder`，与代表“新增固定”的
    `exdir/paths` 分开）、`MainViewModel.MovePinnedFolder`（去重定位 + `RemoveAt`/`Insert` + 立即落盘），
    改 `Views/DriveBarView.xaml(.cs)`。
    踩到的坑（已记入 `AGENTS.md` 第 6 节第 25 条）：`Button` 在 WinUI 3 里把左键的
    `PointerPressed`/`PointerMoved` 标成 Handled，`CanDrag` 完全无效，只能在容器上
    `AddHandler(..., handledEventsToo: true)` 自己识别手势，移动超过阈值后调 `StartDragAsync`。
    验收：`tools/test-pin-drag.ps1` 第 4 个用例（把最左边两个固定目录中排在前面的那个拖到另一个的
    右半边 → `settings.json` 的 `PinnedFolders` 前两项互换），拖拽中的截图里能看到插入位置提示条。
  - [ ] 其余（文件本身可拖出到资源管理器、拖到目录行上悬停进入目录）仍未做。

### Phase 3 — 交互增强

- [ ] **S8 右键上下文菜单**
  - 目标：列表空白处 / 选中项 / 列头 / 侧边栏节点各自的菜单。
  - 涉及：新增 `Views/ContextMenus/`、`ViewModels/ContextMenuBuilder.cs`、`Services/IShellService.cs`（增加 `ShowShellContextMenu(paths, hwnd, point)`）。
  - 内容：自建菜单（打开、在新标签页打开、复制/剪切/粘贴、删除、重命名、属性、复制路径、在终端打开、压缩…）；
    末项“显示系统菜单”调用 `IContextMenu` 弹出资源管理器同款菜单（能带第三方扩展）。
  - 验收：不同上下文菜单项正确启用/禁用；系统菜单能弹出（P/Invoke 需 DPI 正确）。
  - 预估：~550 行，6 个文件。 

- [~] **S9 地址面包屑**（2026-09 完成主体，仅“每段右侧下拉同级目录”未做）
  - 目标：导航条上的路径框旁边/替代品：可点击的路径分段，支持 `\\server\share`、`C:\`、WSL 路径。
  - 涉及：新增 `Views/PathBreadcrumb.xaml(.cs)`、`ViewModels/PathSegmentViewModel.cs`、`ViewModels/FolderTabViewModel.cs`、`Views/NavigationBarView.xaml(.cs)`。
  - 内容：
    - [x] 地址栏默认渲染为面包屑：`FolderTabViewModel.PathSegments`（按 `C:\` / `\\server\share` / 目录逐级切分），
          段间用 chevron 字形 `E76C` 分隔，点非当前段 `NavigateToSegmentCommand` 跳转；
    - [x] 点击空白处切回可编辑 `TextBox`（保留回车/Esc 行为）：空白区由 code-behind 按面包屑实际宽度定位的
          `BlankArea` 按钮接管，地址栏其余非分段区域由 `AddressHost.Tapped` 兜底；
    - [x] 编辑态提到 `FolderTabViewModel.IsPathEditing`（而非视图内部），因此新增 Windows 式入口
          `Ctrl+L` / `Alt+D`（`MainViewModel.EditActivePathCommand`）；成功导航/失焦/Esc 都会退出；
    - [x] 超长路径不溢出：`CrumbScroll` 自动滚到最右 + 左端省略号提示；点当前目录段也能进编辑态；
    - [ ] 每段右侧下拉显示同级目录（点击 chevron 弹出兄弟目录菜单）。
  - 验收：已用 UIA（`tools/inspect-ui.ps1`）验证分段名与坐标、点分段跳转、点空白区/当前段进编辑态、
    失焦与外部导航退出编辑态、深层路径滚到尾部且提示省略；本机无交互桌面，真实的鼠标命中/回车无法自动化验证。
  - 预估：~450 行，4 个文件（实际 ~330 行，5 个文件）。

- [ ] **S10 标签页与窗格增强**
  - 目标：拖拽重排标签、复制标签、锁定标签（锁定后导航会新开标签）、标签页组保存/恢复、两窗格同步浏览（同时切换同一子目录）。
  - 涉及：`ViewModels/PanelViewModel.cs`、`ViewModels/FolderTabViewModel.cs`、`Views/PaneView.xaml(.cs)`、`Models/AppSettings.cs`。
  - 验收：10 个标签页拖拽重排后顺序持久化；锁定标签导航时新开标签。
  - 预估：~450 行，4 个文件。

### Phase 4 — 搜索 / 过滤 / 视图

- [ ] **S11 当前目录快速过滤**
  - 目标：导航条右侧输入框，实时（去抖 120 ms）过滤当前列表（支持 `*`/`?` 通配）。
  - 涉及：`Views/DetailsView.xaml(.cs)`、`ViewModels/FolderTabViewModel.cs`。
  - 验收：1 万项目录中输入时无明显卡顿（过滤在后台线程 + 结果整体替换）。
  - 预估：~250 行，2 个文件。

- [ ] **S12 递归搜索**
  - 目标：`Ctrl+F` 打开搜索面板，后台递归枚举（可取消、可暂停），结果为“列表 + 所在目录”列。
  - 涉及：新增 `ViewModels/SearchViewModel.cs`、`Views/SearchPanel.xaml(.cs)`、`Services/IFileSystemService.cs`（加递归枚举）。
  - 验收：搜索 `C:\Users` 时 UI 不卡；能在中途取消；结果双击跳转到文件所在目录并选中。
  - 预估：~550 行，5 个文件。

- [ ] **S13 图标 / 紧凑 / 缩略图视图**
  - 目标：落地已预留的 `ViewLayout` 枚举，导航条加视图切换按钮，每个标签页独立记住布局。
  - 涉及：新增 `Views/IconsView.xaml(.cs)`、`Views/CompactView.xaml(.cs)`；`Views/NavigationBarView.xaml(.cs)`；`Services/IImageCacheService`（缩略图 `IThumbnailProvider`）。
  - 验收：切换布局即时生效并持久化；1000 张图片目录滚动流畅（缩略图异步加载 + 缓存）。
  - 预估：~500 行（缩略图另算一步）。
  - 建议拆分为 S13a（图标/紧凑，无缩略图）与 S13b（缩略图）。

- [x] **S14 真实 Shell 图标**（2026-09 完成，~640 行，11 个文件）
  - 目标：用 `SHGetFileInfo` 取系统图标，替换当前的 Segoe 字形（`Helpers/FileTypeHelper` 里的字形兜底保留）。
  - 做法：新增 `Services/IShellIconService.cs` + `Services/ShellIconService.cs`（两级缓存：
    “图标键 → Lazy<Task>”合并并发，“内容哈希 → 像素”去重）、`Services/Native/ShellIconExtractor.cs`
    （`SHGetFileInfo` → HICON → `GetDIBits` → 预乘 BGRA）、`Models/IconBitmap.cs`、
    `Helpers/IconImageHelper.cs`（UI 线程建 `WriteableBitmap`，按内容哈希共享图像源）；
    `FileItemViewModel` 加 `Icon/HasIcon/IconRequested`，`FolderTabViewModel.EnsureIconAsync` 负责回填，
    `DetailsView` 用 `ContainerContentChanging` 只给真正显示出来的行取图标，
    行模板里 `Image`（名字“程序图标”）与 `FontIcon` 字形固定 16×16 重叠，谁到谁的 `Visibility` 才打开。
  - 验收：`.exe/.lnk/.文件夹` 显示与资源管理器一致（含 `.lnk` 的小箭头覆盖层）；
    图标有缓存、滚动不重复取；工具：`tools/test-shell-icons.ps1`（13 条断言全过：
    可见行全有图标、几个不同程序哈希互不相同、`.lnk` 与目标程序图标不同、3 个 `.txt` 只提取一次、
    滚动到末尾后的行也有图标、日志无异常）。
  - 顺带：图标尺寸用 `SHGFI_SMALLICON`（= `SM_CXSMICON` = 16 DIP 的物理像素，任何缩放下 1:1）。
  - 与文字的对齐（同一轮补上）：量下来图标盒子与文字盒子都在行高里居中，但文字行盒底部有一段
    “降部”空白，字形视觉中心比行盒中心低约 1 DIP，所以图标用 `RenderTransform` 下推 1 DIP；
    新增 `tools/measure-row-align.ps1`（截图 + UIA 量“墨迹中心”偏离断言：中位数 ≤ 3 物理像素、
    平均 ≤ 3.5；去掉那 1 DIP 会恶化到 4.5 / 4.0 并失败）。
  - 坑（已记入 `AGENTS.md` 第 6 节第 30/31/32/33 条）：`SHGetFileInfo` 并发时会偶发“只给索引不给 HICON”
    （串行化 + 重试一次解决）；`calc.exe` 撞上“应用执行别名”、外壳给不出 HICON
    （退回 `ExtractIconEx` 读文件自身的图标资源）；`WriteableBitmap` 要预乘 alpha；
    写测量脚本时 UIA 的矩形是屏幕坐标、截图是窗口相对坐标（第 34/35 条）。
  - 未做：图标还没用到侧边栏树 / 磁盘条（那边仍是字形）；缩略图视图留给 S13b。

### Phase 5 — 压缩、设置、平台

- [ ] **S15 压缩与解压**
  - 目标：`Compress-Archive` 等价功能，自带进度，可解压到当前/新建目录。
  - 涉及：新增 `Services/IArchiveService.cs(.cs)`；`ViewModels/ContextMenuBuilder.cs`。
  - 验收：压缩 1 GB 目录有进度可取消；zip64 大文件可用。
  - 预估：~400 行，3 个文件。

- [ ] **S16 哈希与属性**
  - 目标：SHA256/MD5 计算（后台、可取消、多文件队列）、只读属性对话框、占用空间统计。
  - 涉及：新增 `Services/IHashService.cs(.cs)`、`Views/HashDialog.xaml(.cs)`、`Views/PropertiesDialog.xaml(.cs)`。
  - 预估：~450 行，5 个文件。

- [ ] **S17 设置窗口**
  - 目标：把设置从 JSON 手改变成可编辑 UI：外观（紧凑度/主题/列显示）、标签页与会话行为、固定目录管理、快捷命令编辑器（名称/图标/命令行/是否需要管理员）。
  - 涉及：新增 `Views/SettingsWindow.xaml(.cs)` + `ViewModels/SettingsViewModel.cs`；`Models/AppSettings.cs`。
  - 验收：改完立即生效并落盘；快捷菜单里出现自定义命令且能真正执行。
  - 预估：~600 行，4 个文件（可能需拆成“设置外壳 + 各分页”两步）。
  - [x] **S17a 设置对话框（所有配置项的唯一入口）**（2026-09，~330 行，7 个文件）
    - 内容：菜单栏「配置 → 设置…」弹出 `Views/SettingsDialog`（`ContentDialog`，
      底部 `PrimaryButtonText="保存"` / `CloseButtonText="取消"`，回车=保存、Esc=取消），
      分「文件列表」「界面」两组共 8 项：显示隐藏文件、显示文件扩展名、文件夹排在文件前面、
      过渡动画、列宽自动适应窗格宽度、显示工具条、显示侧边栏、双窗格模式；
      原来的「配置 → 文件列表」子菜单（三个 `ToggleMenuFlyoutItem`）删除，
      「查看」菜单里的工具条 / 侧边栏 / 双窗格保留（与对话框共享同一份设置）。
    - 做法：新增 `ViewModels/SettingsViewModel.cs`（从 `AppSettings` 复制的快照）+
      `Views/SettingsDialog.xaml(.cs)`；`MainViewModel.CreateSettingsSnapshot()` /
      `ApplySettings(snapshot)` 作为唯一应用入口（应用后立刻 `Save()`）；
      `MainWindow.Settings_Click` 接对话框（`XamlRoot = RootGrid.XamlRoot`）。
      两个新坑已记入 `AGENTS.md` 第 6 节第 26/27 条（弹层动画期坐标会变 → 自动化改用 UIA 模式；
      `ContentDialog` 是独立弹出窗口，屏幕外控件要按“对话框后代”而非可见性筛选）。
    - 验收：`tools/test-settings.ps1` 16 项断言全绿（内容齐全、初始值与 settings.json 一致、
      取消不落盘、保存立即落盘、关掉“显示文件扩展名”后列表行名里的 `.xxx` 从 13 行降到 5 行，
      再打开后恢复原样）；跑完自动还原 `settings.json`。
    - 未做（留给 S17 其余部分）：外观（紧凑度/主题）、固定目录管理、快捷命令编辑器。
  - [x] **S17b 设置对话框改成「左导航 + 右正文」**（2026-09，改 4 个文件 / 新增 5 个，~400 行）
    - 内容：改成常见配置对话框的两栏结构——左侧是配置大类列表，右侧只显示当前那一页；
      分类收敛为三个：文件列表（显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面）、
      外观（过渡动画）、布局（列宽自动适应窗格宽度 / 显示工具条 / 显示侧边栏 / 双窗格模式）。
      每一行从“裸 CheckBox”改成“标题 + 灰色说明 + 右侧 ToggleSwitch”。
    - 做法：新增 `Models/SettingsCategory.cs`（枚举）、`ViewModels/SettingsCategoryViewModel.cs`、
      `Views/SettingsToggleRow.xaml(.cs)`（Title/Description/IsOn 三个 DP）；
      `SettingsViewModel` 加 `Categories` / `SelectedCategory` / 三个 `IsXxxPageVisible`；
      `SettingsDialog.xaml` 左侧 `ListView` 绑 `Categories` + `SelectedCategory`（TwoWay），
      右侧三页用 `Visibility` 切；对话框尺寸写死（实测 606×408 DIP：两列都用固定宽度 +
      根 `Grid` 的 `MinHeight` 兜住最矮的页），在 `ContentDialog.Resources` 里覆盖
      `ContentDialogMinWidth/MaxWidth`，并再关一次 `ListViewItemSelectionIndicatorVisualEnabled`。
    - 踩坑（已记入 `AGENTS.md` 第 6 节第 37/38 条）：`ContentDialog` 里的 `FontIcon` 会被算成 ~0 宽
      （字形只画出一条）→ 导航项改成纯文字；负 `Margin` 想“通到对话框边缘”会把左侧内容裁掉；
      这种场景下 UIA 的 `BoundingRectangle` 会给出 `x≈-1118` 这种假坐标，量渲染几何只能扫像素。
    - 验收：`tools/test-settings.ps1` 重写为 4 个用例 26 条断言全绿（三个分类齐全且顺序一致 /
      默认停在「文件列表」/ 每页只看得到本分类的开关 / 初始值与 settings.json 一致 / 取消不落盘 /
      保存立即落盘并作用到文件列表 / 跨分类改「布局」后重新打开能读回）；
      新增 `tools/shot-settings.ps1` 给每个分类截一张图（`.artifacts\settings-<分类名>.png`）。

- [ ] **S18 文件系统监视自动刷新**
  - 目标：`FileSystemWatcher` 监视当前目录，外部变动时增量刷新（去抖），保持选中与滚动位置。
  - 涉及：新增 `Services/IDirectoryWatcher.cs(.cs)`、`ViewModels/FolderTabViewModel.cs`。
  - 验收：在资源管理器里新建文件，exdir 1 秒内出现该文件且不闪烁、不丢选中。
  - 预估：~350 行，3 个文件。

- [ ] **S19 单实例 + 命令行参数 + 管理员**
  - 目标：`exdir.exe <path>` 在当前实例的新标签页打开；已运行时把请求转发给已有实例；“以管理员身份重新启动”。
  - 涉及：`App.xaml.cs`、新增 `Services/IInstanceService.cs(.cs)`（命名管道/互斥体）、`Program.cs`（自定义 `Main` 以便在 `Application.Start` 之前处理参数）。
  - 验收：命令行第二次启动会把路径交给第一个实例；无参数时按会话恢复。
  - 预估：~400 行，4 个文件。

### Phase 6 — 质量

- [ ] **S20 单元测试工程**
  - 目标：`exdir.Tests`（xUnit）覆盖纯逻辑：`FileSystemService` 路径规整、`SizeFormatter`/`FileTypeHelper`、
    排序比较器、`AppSettings` 序列化、`SidebarViewModel` 构建、`FolderTabViewModel` 导航历史。
  - 注意：不要引用 WinUI 类型，把这些逻辑保持在无 UI 依赖的层（必要时抽接口）。
  - 验收：`dotnet test` 全绿；CI 可在无桌面环境下跑。
  - 预估：~500 行，8 个文件。

- [ ] **S21 性能与稳定性收尾**
  - 目标：10 万文件目录的枚举/显示性能（分批填充 + 虚拟化确认）、大目录排序不卡、
    内存占用（图标/缩略图缓存上限）、异常兜底（路径过长、网络盘断开、介质移除）。
  - 验收：写一份 `docs/performance.md` 记录测量方法与结果。
  - 预估：~300 行 + 文档。

- [ ] **S22 可访问性与文档**
  - 目标：补齐 `AutomationProperties.Name`、Tab 键顺序、快捷键提示；更新 `README.md`（截图 + 使用说明）与 `AGENTS.md`。
  - 预估：~200 行 + 文档。

### Phase 7 — 云同步状态（已完成）

- [x] **S23 云文件夹的文件同步状态列**（2026-09）
  - 目标：云同步目录里能一眼看出每个文件/目录的同步状态（像资源管理器的“状态”列 / 图标）。
  - 涉及：新增 `Models/CloudSyncState.cs`、`Helpers/CloudSyncStateHelper.cs`、
    `Services/CloudSyncService.cs`（ICloudSyncService + 实现）、`Services/Native/ShellPropertyStore.cs`；
    修改 `Services/FileSystemService.cs`、`Helpers/ColumnLayout.cs`、`Models/FileSystemEntry.cs`、
    `Models/ViewLayout.cs`、`Models/AppSettings.cs`、`Services/SettingsService.cs`、
    `ViewModels/FileItemViewModel.cs`、`ViewModels/FolderTabViewModel.cs`、
    `Views/DetailsView.xaml(.cs)`、`Converters/CommonConverters.cs`、`App.xaml(.cs)`。
  - 内容：
    - [x] 启动时 `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)`，
          否则云文件的 reparse/offline 位被系统伪装、可用性状态可能恒为“同步挂起”；
    - [x] 目录位于云同步根下时，逐条读 `System.StorageProviderState`（资源管理器同源）+ 
          `System.FilePlaceholderStatus`，映射成 6 种状态；读不到就退回占位符属性位 + 文件属性；
    - [x] 状态存在 `FileSystemEntry.SyncState`，只在云目录里读（并行、1500 ms 预算、可取消），
          普通目录零额外开销；
    - [x] 列表最前面新增“状态”列：云目录里自动出现、非云目录自动隐藏（列定义不动，
          隐藏时算 0 宽，不影响其它列下标与已落盘的列宽），可点列头按状态排序；
    - [x] `AppSettings` 结构版本 1→2：旧的 4 列宽自动在最前面补上状态列宽度。
  - 验收（本机无交互桌面，用 UIA + 真实 WPS 云同步根验证）：
    `tools/inspect-ui.ps1` 可见列头 `按同步状态排序`、每行的 `仅在云端`/`已同步` 文案
    （与 PowerShell 读到的 `System.StorageProviderState` 完全一致，含 2 个 SPS=2 的“已同步”目录）；
    非云目录（`D:\opt`）下状态列头与状态文案均为 0 个；
    用 `InvokePattern` 点列头后行顺序按状态分组（升/降序均正确）；
    云目录 19 项 113 ms / 62 项 247 ms（见 exdir.log 的 `云同步状态：…` 行）。
  - 预估：~700 行，17 个文件（超出了单步 8 个文件的规模线，但功能本身不可再拆）。

---

## 4. 横切关注点

* **不要引入新的 NuGet 依赖**，除非本计划里明确写了。特别是不用 `CommunityToolkit.WinUI.Controls`（会带一堆样式覆盖，和我们的紧凑主题打架）。
* **每个新功能都要能被自动化验证**：控件加 `AutomationProperties.Name`，然后用 `tools/inspect-ui.ps1 -Click/-Keys` 断言。
* **所有耗时操作必须可取消**（`CancellationTokenSource`），并且取消后 UI 状态要能回到一致状态（当前 `FolderTabViewModel._loadCts` 是范例）。
* **UI 线程规则**：`ObservableCollection` 只在 UI 线程改；后台只产出 `IReadOnlyList`。
* **写文件前先确认**：删除/覆盖类操作必须有二次确认或进回收站，避免测试脚本误删用户数据。

---

## 5. 已知技术债 / 需要留意的瑕疵

| 项 | 说明 | 计划处理 |
| --- | --- | --- |
| 列被挤出可视区 | 窗格很窄（< ~410 DIP）时固定列溢出，`大小` 列看不见 | S1（已解决：自动模式等比压缩，手动模式改横向滚动） |
| 图标是字形不是系统图标 | `Helpers/FileTypeHelper` 用 Segoe Fluent 字形兜底 | S14（已解决：列表行首改成真实外壳图标，字形只在取不到时兜底；侧边栏/磁盘条仍是字形） |
| 云状态每项一次属性读取 | 云目录里每个条目约 2~3 ms（并行后），只有云目录才付出这笔开销；超过 1500 ms 预算的条目不再显示状态 | S21（如需优化：两阶段加载，先出列表再补状态） |
| 大目录枚举无分批 | 一次性构建整个 `ObservableCollection`（已用整体替换避免 O(n²)，但内存与首次渲染仍是瓶颈） | S21 |
| 无文件系统监视 | 外部改动需手动 F5 | S18 |
| 快捷菜单为空 | 按需求刻意留空，仅数据驱动 | S17 |
| 设置对话框尺寸写死（606×408 DIP），窗口比它小时边缘会被裁 | 两列固定宽度 + 根 `Grid` 的 `MinHeight`；`ContentDialog` 只会把对话框约束在窗口内，不会自己缩（右侧正文仍有 `ScrollViewer`） | 需要时改成按窗口尺寸自适应 |
| 提权运行后拖放失效 | Windows 不允许高完整性级别（管理员）进程参与拖放：`DragItemsStarting` 会触发，但永远收不到 `DragOver`/`Drop`；以普通权限运行则正常（见 AGENTS.md 第 6 节第 21 条） | 系统限制，无解；必要时在界面上提示 |
| 拖放只做了“目录 → 工具条固定目录”与“工具条固定目录之间排序” | 文件本身不能拖出、不能拖到目录行上悬停进入目录 | S7 其余部分 |
| 固定目录最多 12 个 | 工具条固定目录区不滚动，太多了会把左侧磁盘区挤没（`MainViewModel.MaxPinnedFolders`） | 需要时改成横向滚动 / 溢出菜单 |
| 无右键菜单 | 需求未明确，需先确认路线 | S8 |
| 侧边栏同步是“尽力而为” | 只在已加载节点里查找，深层目录不会自动展开定位 | S10（可加“展开到当前路径”） |
| 单实例未处理 | 多次启动会有多个进程 | S19 |
| 只有 x64 验证过 | x86/ARM64 未测试 | 需要时再验证 |
| 展开的子项不随文件变化刷新 | 已展开目录的子项只在展开时枚举一次，需要 F5 刷新整个标签页 | 将来做文件系统监视时一并处理 |
| 无单元测试 | 纯逻辑可测但尚未建工程 | S20 |
| Release 产物 224 MB | 因为 .NET + WinAppSDK 全自包含。若可接受“要求目标机装 Windows App Runtime”，可改回框架依赖以大幅减小体积 | 按需 |
