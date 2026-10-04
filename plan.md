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
  - 菜单栏**「配置 → 设置…」= 统一设置窗口**（2026-09，S17a / S17b / S17d / S8b）：
    独立窗口（`Views/SettingsWindow`，默认 860×800），左侧 `NavigationView` 五个分类
    （文件列表 / 外观 / 布局 / 侧边栏 / 右键菜单），右侧一列 Windows 11 风格设置卡片（社区工具包 `SettingsCard`，
    标题 + 说明 + 右侧控件），只显示当前分类那一页；
    前四类共十五项（主题（跟随系统 / 浅色 / 深色，默认跟随系统）/ 过渡动画 / **标签页使用直角（默认开）** / 显示隐藏文件 / 显示文件扩展名 / 文件夹排在文件前面 / 行高 / 列宽自适应 /
    工具条 / 侧边栏 / 双窗格 / 侧边栏四个分组的显示开关），「右键菜单」类是系统菜单项的逐项开关（默认全开，见 S8b）；
    **改任何一项都即时生效并立即落盘**（没有「保存 / 取消」）；
    回归脚本 `tools/test-settings.ps1`（全程 UIA 模式），布局截图 `tools/shot-settings.ps1`。
  - 右键菜单（2026-09，S8a / S8b / S8c）：默认是 exdir 自建的轻量菜单（弹出瞬时），
    可在设置里切回**系统真实菜单**（文件列表条目 / 空白处）+ 逐项开关，见下面的 S8。
  - **单窗口 + 常驻托盘**（2026-09）：点关闭按钮 / `Alt+F4` 只把窗口隐藏到通知区域，进程、窗格、
    标签页与会话全部留着，再打开（左键点托盘图标 / 第二次双击 exe）就是一次 `ShowWindow`（瞬时）；
    托盘右键菜单「显示主窗口 / 退出 exdir」，菜单里的「退出」才是真退出；
    单实例闸门（命名内核事件 `Local\exdir.activate`）跑在 XAML 初始化之前（`Program.cs`），
    第二次双击 exe 只跑几十毫秒，不会出现第二个托盘图标 / 两份会话互相覆盖；
    依赖 `H.NotifyIcon.WinUI 2.3.2`，回归脚本 `tools/test-tray.ps1`，见 AGENTS.md 第 4 节“托盘驻留”。
  - **命令行调用 `exdir [path]`**（2026-09，S19 命令行部分）：已有窗口时把请求转交给它
    （命名事件 `Local\exdir.activate` 判“我是不是第一个实例” + 命名管道 `exdir.activate` 传路径），
    在活动窗格里新开标签页打开（已在同一目录就不新开；`path` 是文件则打开所在目录并选中它；
    路径不存在就在当前标签页显示「无法打开」）；无参数时打开当前工作目录，
    但工作目录是 exe 目录 / `C:\Windows` / `System32` 这类“启动器给的位置”时只唤回窗口；
    回归脚本 `tools/test-command-line.ps1`，见 AGENTS.md 第 4 节“命令行调用”。
  - 磁盘热插拔实时刷新（2026-09，S24）：U 盘 / 光驱换盘 / 网络盘映射变化后，工具条磁盘区与侧边栏
    「此电脑」分组立即更新（窗口藏在托盘里也照样），且只做差量刷新（不重建整棵树、不影响展开状态与收藏夹）；
    回归脚本 `tools/test-drive-hotplug.ps1`，见 AGENTS.md 第 4 节与第 6 节第 56 条。
  - 侧边栏「此电脑」里显示 Windows「网络位置」（2026-09，S27）：读
    `%APPDATA%\Microsoft\Windows\Network Shortcuts` 下的快捷方式（`IShellLinkW` 解析 `target.lnk`），
    以目录名显示、点击进入其目标（UNC 也能走现有导航与面包屑）；离线的照样列出，打开时才报错；
    差量刷新（设备变化）会一并带上它们（不把网络位置算进期望清单就会被刷新误删）；
    回归脚本 `tools/test-network-locations.ps1`，见 AGENTS.md 第 4 节与第 6 节第 63 条。
  - 工具条“固定目录”支持**拖放固定**（2026-09，S7a）：文件列表 / 侧边栏树里的目录拖到工具条右侧即固定，
    松手立即写 `config.json`；右键固定目录按钮可“取消固定”；
    `AppSettings` 结构版本 2→3（新增 `PinnedFoldersInitialized`，修掉“取消完所有固定目录后重启默认值又回来”）。
  - 复制 / 剪切 / 粘贴 + 拖动移动（2026-09，S25）：内置右键菜单里多了剪切 / 复制 / 粘贴
    （快捷键 `Ctrl+X/C/V`），剪贴板用系统标准格式 `CF_HDROP` + `Preferred DropEffect`，
    与资源管理器 / 7-Zip 双向互通；真正的复制 / 移动交给外壳 `SHFileOperation`（有进度对话框与同名冲突询问）；
    文件 / 目录拖到某个目录行、列表空白处或另一个窗格即移动，`Ctrl` 是复制；
    回归脚本 `tools/test-file-ops.ps1`，见 AGENTS.md 第 4 节同名小节与第 6 节第 57—60 条。
  - 删除到回收站 / 永久删除（2026-09，S26）：内置右键菜单里多了「删除」（`Delete`），
    `Shift+Delete` 是永久删除；收归外壳 `SHFileOperation(FO_DELETE)`（`FOF_ALLOWUNDO` 进回收站，
    `FOF_WANTNUKEWARNING` 保证“装不进回收站就只能永久删”时仍警告一次），确认框由外壳弹；
    删除后受影响的标签页刷新，被删目录里开着的标签页退到上一级；
    回归脚本 `tools/test-file-ops.ps1` 用例 H / I，见 AGENTS.md 第 6 节第 61 条。
  - 工具条“固定目录”支持**拖拽排序**（2026-09，S7b）：按住固定目录按钮横向拖到兄弟按钮上就换位，
    拖动时有插入位置提示条，松手立即写 `config.json`（也是“工具条上的拖拽是由应用自己识别手势的”首个实现）。
  - 侧边栏“收藏夹”分组（2026-09，S7c）：`主目录` 下面多一个 `收藏夹` 分组，子项与工具条固定目录
    同序同名；把目录从文件列表 / 侧边栏拖到该分组或其子行上即收藏；右键收藏项可取消收藏。
  - “最新访问”一个入口 + 专用标签页（2026-10，S7d；改版于同日）：侧边栏里只有一个入口（排在最上面，
    不再平铺子项、不带展开箭头），点它在一个**专用标签页**（虚拟路径 `exdir://recent`）里列出最近访问过的
    **目录与文件**，按访问时间倒序；行是真实路径（能打开 / 复制 / 删除 / 拖出去），但“当前目录”不是真目录，
    粘贴 / 新建 / 终端 / 拖入一律拒绝。列表落盘在 `%USERPROFILE%\.local\share\exdir\recents.json`
    （不在 config.json 里，上限 50 条；旧格式只有 `Folders` 时自动迁移），右键节点可「清空最新访问」；
    可在设置「侧边栏」页关掉（`SidebarShowRecent`）。回归 `tools\test-recent.ps1`，见 AGENTS.md 第 4 节
    「「最新访问」虚拟视图」与第 6 节第 107 条。
  - 云文件夹的同步状态列（2026-09，S23）：云同步目录（OneDrive / WPS 云盘 / 其它 CFAPI 同步根）
    的列表最前面多出一列状态图标（已同步 / 仅在云端 / 已固定 / 正在同步 / 同步错误 / 未同步），
    可点列头排序；非云目录整列隐藏。
  - 侧边栏：主目录（含桌面/文档/下载/图片/音乐/视频，**哪几个显示可在设置里逐项开关，默认只开桌面与下载**，见 S28）、云存储（注册表探测同步根）、此电脑（各磁盘 + Windows「网络位置」快捷方式）；展开时懒加载子目录。
  - 深色 / 浅色主题（2026-09，S29）：标题栏左侧（「文件」菜单左边）多了一个太阳 / 月亮图标开关，
    点一下就在浅色 / 深色之间切；设置窗口「外观 → 主题」里还有三态的「跟随系统 / 浅色 / 深色」
    （`AppSettings.Theme`，默认跟随系统 = 保持原有行为）。两个入口共用同一个值，改完当场重画、无需重启，
    并立即落盘。实现只有一处：给根元素设 `RequestedTheme`（菜单栏 / 工具条 / 侧边栏 / 窗格 / 状态栏 / 弹层一起换）；
    系统窗口按钮（最小-最大-关闭）要自己按主题上色；设置窗口是另一个 `Window`，自己接一份主题，
    并顺带修掉了它在浅色主题下左侧导航“黑底黑字”的问题（加 Mica 背板）；
    回归脚本 `tools/test-settings.ps1` 用例 9，见 AGENTS.md 第 4 节“主题”与第 6 节第 68 条。
  - 开机自启 + 预热启动（2026-09，S30）：设置窗口「启动」页可以开关“开机时自动启动 exdir”
    （写 / 删 `HKCU\...\Run` 里的 `"<exe>" --preload`）；带 `--preload` 启动的那一份**不显示主窗口**，
    只恢复上次的目录会话并预取首屏图标，之后双击 exe 几乎是瞬时的；
    回归 `tools/test-autostart.ps1` 与 `tools/test-settings.ps1` 用例 10，
    见 AGENTS.md 第 4 节“开机自启 / 预热启动”与第 6 节第 75 条。
  - 配置文件搬到用户主目录（2026-09）：`settings.json` → `%USERPROFILE%\.config\exdir\config.json`
    （设了 `XDG_CONFIG_HOME` 且为绝对路径时以它为准），首次启动自动把旧 `%LOCALAPPDATA%\exdir\settings.json`
    搬过来；日志仍留在 `%LOCALAPPDATA%\exdir\exdir.log`；见 AGENTS.md 第 6 节第 76 条。
  - 会话与设置：窗口位置/尺寸/最大化、双窗格、侧边栏宽度、标签页集合、排序偏好、固定目录 → `%USERPROFILE%\.config\exdir\config.json`。
  - **远程位置（SFTP / FTP）**（2026-10，S33～S35）：设置窗口「远程」里增删改连接（密码 / 私钥口令经 DPAPI 加密）、
    侧边栏「远程」分组点一下就能浏览（列目录 / 进子目录 / 就地展开 / 排序 / 面包屑），
    双击文件先下到本地再看，右键「下载到…」/「复制」/ 拖出去都能把远程内容弄到本地；**远程一律只读**。
    见 AGENTS.md 第 4 节“远程位置”，回归 `tools\test-remote.ps1` + `tools\remote-smoke` + `tools\remote-test-server`。
  - 快捷键：Alt+←/→/↑、F5、Ctrl+T/W、Ctrl+H、Ctrl+B、Ctrl+F（Everything 搜索）、F6、F10。
  - **Everything 快速搜索**（2026-10，阶段 4 的 S12 落地版）：导航条右侧搜索框（`Ctrl+F` 聚焦），
    **默认搜索范围 = 当前目录及其子目录**，旁边「整机」开关放到全部索引；结果替换当前标签页的列表，
    双击 = 跳到所在目录并选中。靠随包分发的 Everything SDK 客户端 `native\x64\Everything64.dll`
    （要求本机装并运行 Everything）—— 见 AGENTS.md 第 4 节“Everything 快速搜索”，
    回归 `tools\test-search.ps1`（10 用例 47 断言）+ `tools\everything-smoke`。
  - 工具脚本：`capture.ps1`（截图）、`inspect-ui.ps1`（UIA 控件树 / 点击）、`publish.ps1`（Release 产物）、`make-icon.ps1`。
  - Release 产物：`dist\win-x64\exdir.exe`（自包含 + 裁剪，2026-10 含远程位置与 Everything 搜索后是 **196 文件 / 93 MB**，已验证可运行）。
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
   **已决定（2026-09）：两种都做，由设置切换，默认用自建（内置）菜单**：
   - 内置菜单（默认）：`Views/DetailsView` 用 WinUI `MenuFlyout` 现场搭（打开 / 在资源管理器中显示 /
     复制路径 / 属性；背景是 新建文件夹 / 刷新 / 全选 / 复制当前路径 / 在此处打开终端），不建 COM，弹出瞬时；
   - 系统菜单（把「设置 → 右键菜单 → 使用内置的轻量右键菜单」关掉才用）：
     `IShellContextMenuService`，菜单项从系统读出来后在设置页逐项开关（默认全开），
     关掉的项在 `TrackPopupMenu` 之前从 HMENU 里删掉。
   仍未做：压缩 / 重命名这类文件操作（复制粘贴与删除已完成，见 S4–S6 / S25 / S26）。
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
    - [x] 双击的命中范围 = 整条行高亮区（2026-09）：行内任意落点（左边距 / 名称文字右侧空白 /
          类型列与大小列的空白处 / 行内上下留白）双击都进入目录 / 打开文件；
          双击处理器改挂在 `DetailsView` 最外层 Grid 上（行内空白处命中的是 `ListViewItem`，
          挂在行模板 Grid 上收不到），行模板 Grid 补 `Background="Transparent"`；
          回归 `tools/test-row-dblclick.ps1`（改动前的版本上跑会挂 2 个落点）；
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
  - [x] `Ctrl+A` 全选 + 单击列表空白处取消选择（2026-09）：加速器挂在 `DetailsView` 根 Grid 上，
        只对文件列表生效（焦点在地址栏时 `Ctrl+A` 仍是文本框全选）；空白处单击靠 `FindRowItem` 排除行内点击，
        清空后把焦点留在列表上；回归 `tools/test-list-selection.ps1`（5 个用例 12 条断言，真鼠标 + `SendKeys`）。
  - [ ] 其余：`Ctrl+Shift+A` 反选、`Enter` 打开、`Backspace` 上一级、`Home/End/PageUp/PageDown`、type-ahead、`Esc` 清空选择。
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

- [x] **S4 剪贴板与文件操作服务**（2026-09 完成：改用系统标准格式 `CF_HDROP` + `Preferred DropEffect`
  与外壳 `SHFileOperation`，不是原计划里的“自建快照 + 抛 NotImplemented 的骨架”。
  详细交付见本 Phase 末尾的 **S25**；下面保留的是当初的计划文本）
  - 目标：搭好 `IFileOperationService` / `IClipboardService` 抽象与实现，先只接通“复制路径/复制文件列表到剪贴板（CF_HDROP + Preferred DropEffect）”，不做真正的拷贝。
  - 涉及：新增 `Services/IClipboardService.cs(.cs)`、`Services/IFileOperationService.cs(.cs)`、`Services/Native/ShellInterop.cs`、`App.xaml.cs`（注册）。
  - 内容：
    - Win32 `OleSetClipboard` 或 `DataPackage.SetStorageItems` 两种方案选一（非打包下推荐 Win32，避免包标识问题）；
    - 读剪贴板写成 `ClipboardSnapshot { Operation: Copy|Move, Paths }`；
    - `IFileOperationService` 先只声明方法签名 + 抛 `NotImplementedException` 的实现，便于后续步骤分步填充。
  - 验收：在资源管理器里复制若干文件 → exdir 粘贴按钮能读出版本正确的列表（先用日志/状态栏显示）。
  - 预估：~350 行，5 个文件。

- [~] **S5 删除 / 重命名 / 新建（第一批真实写操作）**（2026-09：删除部分已完成，见 **S26**；
  重命名与新建仍未做，下面保留的是当初的计划文本）
  - 目标：`Delete`（进回收站）、`Shift+Delete`（永久删除）、`F2` 就地重命名、新建文件夹/文本文档。
  - 涉及：`Services/IFileOperationService.cs` + 实现、`ViewModels/FolderTabViewModel.cs`、`Views/DetailsView.xaml(.cs)`、`Views/RenameBox`（就地编辑用 `TextBox` 覆盖行）。
  - 内容：
    - 用 `SHFileOperation`/`IFileOperation` 完成删除（带回收站）；
    - 就地重命名：列表行切换到编辑态，回车确认、Esc 取消、查重名；
    - 新建后自动进入重命名态并滚动到可见；
    - 所有写操作完成后刷新当前目录并保留选中。
  - 验收：删到回收站能在资源管理器“回收站”里看到；重命名后选中项跟随；无权限时报 `InfoBar` 错误而不是崩溃。
  - 预估：~500 行，5 个文件。

- [x] **S6 复制 / 移动（含跨窗格与冲突处理）**（2026-09 完成，交付见 **S25**；下面保留的是当初的计划文本）
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
    右键 Desktop → 取消固定），并检查 `%USERPROFILE%\.config\exdir\config.json` 的 `PinnedFolders` 真的变了。
  - [x] **S7b 固定目录拖拽排序**（2026-09）：按住工具条上的固定目录按钮横向拖到兄弟按钮上即换位，
    拖动时按钮之间画 2px 强调色插入位置提示条 + “调整固定目录顺序”提示，松手即写 `config.json`。
    新增格式 `DragDropHelper.PinnedReorderFormat`（`exdir/pinned-reorder`，与代表“新增固定”的
    `exdir/paths` 分开）、`MainViewModel.MovePinnedFolder`（去重定位 + `RemoveAt`/`Insert` + 立即落盘），
    改 `Views/DriveBarView.xaml(.cs)`。
    踩到的坑（已记入 `AGENTS.md` 第 6 节第 25 条）：`Button` 在 WinUI 3 里把左键的
    `PointerPressed`/`PointerMoved` 标成 Handled，`CanDrag` 完全无效，只能在容器上
    `AddHandler(..., handledEventsToo: true)` 自己识别手势，移动超过阈值后调 `StartDragAsync`。
    验收：`tools/test-pin-drag.ps1` 第 4 个用例（把最左边两个固定目录中排在前面的那个拖到另一个的
    右半边 → `config.json` 的 `PinnedFolders` 前两项互换），拖拽中的截图里能看到插入位置提示条。
  - [x] **S7c 侧边栏「收藏夹」分组**（2026-09）：侧边栏 `主目录` 下面多一个 `收藏夹` 分组，子项与
    工具条固定目录同序同名（增删 / 排序即时同步）；把目录从文件列表 / 侧边栏拖到该分组或其子行上
    即收藏（悬停整行强调色高亮）；右键收藏项可「取消收藏」（工具条隐藏时的移除入口）。
    改 `ViewModels/SidebarViewModel.cs`（`SidebarNodeKind.FavoritesGroup/Favorite`、`IsDropTarget`、
    `SyncFavorites`、`PinRequested`/`UnpinRequested`）、`ViewModels/MainViewModel.cs`（订阅
    `PinnedFolders.CollectionChanged`、`UnpinFolderByPath`）、`Views/SidebarView.xaml(.cs)`、
    `Helpers/FileTypeHelper.cs`（`FavoriteGlyph`）、`tools/test-pin-drag.ps1`（用例 0 / 用例 5）。
    踩到的坑（已记入 `AGENTS.md` 第 6 节第 54 条）：拖放事件的 `e.OriginalSource` 永远是带 `AllowDrop`
    的那个元素（挂在 TreeView 上只能拿到 TreeView），落点必须写在行模板根上、用
    `TreeView.ItemFromContainer` 反查；`TreeViewList` 还会把 `AcceptedOperation` 改回 `None`，
    要在 `TreeView` 上用 `handledEventsToo` 再确认一次，否则松手没有 Drop。
    验收：`tools/test-pin-drag.ps1` 用例 0（收藏夹子项与 `config.json` 一致）、
    用例 5（拖 `音乐` 到收藏夹 → `config.json` 新增 `…\Music`，侧边栏同步出现）。
  - [ ] 其余（**真实目录里的**文件可拖出到资源管理器、拖到目录行上悬停进入目录）仍未做。
    （压缩包里的条目已经能拖出去，含拖到资源管理器，见 S32e。）

- [x] **S25 复制 / 剪切 / 粘贴 + 拖动移动**（2026-09，新增 6 个文件 / 改 10 个，~900 行）
  - 目标：内置右键菜单里能剪切 / 复制 / 粘贴（带常用快捷键），并要求“拖文件到目录上即移动”。
  - 涉及：新增 `Services/IClipboardService.cs(.cs)`、`Services/IFileOperationService.cs(.cs)`、
    `Services/Native/ClipboardInterop.cs`、`Services/Native/FileOperationInterop.cs`、
    `tools/test-file-ops.ps1`；改 `App.xaml.cs`、`Helpers/DragDropHelper.cs`、`Helpers/DpiHelper.cs`、
    `ViewModels/FileItemViewModel.cs`、`FolderTabViewModel.cs`、`PanelViewModel.cs`、`MainViewModel.cs`、
    `Views/DetailsView.xaml(.cs)`、`MainWindow.xaml`。
  - 做法：
    * 剪贴板用**系统标准格式**：写 `CF_HDROP` + `Preferred DropEffect`（1 复制 / 2 剪切），
      读先 Win32 `GetClipboardData`、再退 OLE `OleGetClipboard` + `IDataObject`。
      因此与资源管理器 / 7-Zip **双向互通**（内部格式不是必需的）——原来的“自建快照”方案作废。
      注意 `Preferred DropEffect` 必须 `RegisterClipboardFormat`（`0x000C` 是 `CF_WAVE`）。
    * 复制 / 移动走外壳 `SHFileOperation`（`FO_COPY` / `FO_MOVE`）：进度对话框、同名冲突询问、
      自动建目标目录都是资源管理器同款；`SHFileOperation` 要 STA，所以开专用 STA 线程跑，
      完成后用事件按“源目录 + 目标目录”刷新受影响的标签页。
    * 命令：`FolderTabViewModel.CopySelectionCommand` / `CutSelectionCommand` / `PasteCommand`；
      `Ctrl+C/X/V` 是挂在 `DetailsView` 根 Grid 上的 `KeyboardAccelerator`（地址栏里仍是文本框行为）；
      内置菜单的文件行 = 打开 / 在资源管理器中显示 / 剪切 / 复制 / 粘贴 / 复制路径 / 属性，
      空白处 = 粘贴 / 新建文件夹 / 刷新 / 全选 / 复制当前路径 / 在此处打开终端。
    * 拖动移动：文件列表拖到**目录行**（命中行整条强调色高亮）/ 列表空白处 / 另一个窗格 = 移动，
      `Ctrl` = 复制，外部拖入默认复制（`Shift` = 移动）。手势是自己识别的
      （`CanDragItems=False` + `StartDragAsync`），落点只有 `DetailsRoot` 一个（容器 `AllowDrop=False`）、
      鼠标下哪一行用光标位置 + 容器矩形算，并在 `StartDragAsync` 返回后留了一道兜底。
  - 坑（已记入 `AGENTS.md` 第 6 节第 57—60 条）：`CF_PREFERREDDROPEFFECT` 不是 `0x000C`；
    `ListView.CanDragItems` 的拖拽在模拟鼠标下走不完（且 `PointerCaptureLost` 不能当松手、
    行容器要用 `ItemFromContainer` 取数据项）；拖拽期间 `e.GetPosition` 坐标不可靠；
    拖放落点不能用 `e.OriginalSource`，且框架有时不冒泡 `Drop`（所以留了兜底）。
  - 验收：`tools/test-file-ops.ps1` 7 个用例全绿（菜单项 / WinForms 读回 exdir 写的剪贴板且
    `DropEffect=1` / `Ctrl+C`+`Ctrl+V` 复制 / `Ctrl+X`+`Ctrl+V` 移动且 `DropEffect=2` /
    拖到目录行真的移动 / 外部复制的文件能粘进来且源还在 / 外部剪切的粘完源没了）；
    同时重跑了 `test-list-selection` / `test-context-menu` / `test-row-dblclick` / `test-pin-drag`
    （后两个脚本各有 4 / 2 条**改动前就存在**的失败：环境相关，已用 `git stash` 在 HEAD 上复现）。

- [x] **S26 删除（进回收站 / Shift+Delete 永久删除）**（2026-09，新增 0 个文件 / 改 8 个，~200 行）
  - 目标：内置右键菜单与 `Delete` 键能把选中项丢进回收站（可恢复），`Shift+Delete` 才永久删除。
  - 涉及：`Services/Native/FileOperationInterop.cs`（`FO_DELETE`）、
    `Services/IFileOperationService.cs` + `Services/FileOperationService.cs`（`DeleteAsync`，
    事件多了 `IsDelete`、`DestinationDirectory` 变成可空）、`ViewModels/FolderTabViewModel.cs`
    （`DeleteSelectionCommand` / `DeleteSelectionPermanentlyCommand`）、`ViewModels/MainViewModel.cs`
    （转发命令 + 删除后刷新 / 退上一级）、`Views/DetailsView.xaml(.cs)`（`PreviewKeyDown` 里收 `Delete` /
    `Shift+Delete` + 内置菜单的「删除」）、`MainWindow.xaml`（编辑菜单）、`tools/test-file-ops.ps1`。
  - 做法：删除也交给外壳 ——不会再自己写递归删除。`FOF_ALLOWUNDO` 才是“进回收站”，
    再配 `FOF_WANTNUKEWARNING`（超大文件 / 没有回收站的卷只能真删时先警告）；
    `FO_DELETE` 的 `pTo` 传 `null`；确认框（“确实要将其移至回收站吗？”）由外壳弹，
    用户点“否”时 `SHFileOperation` 报 `fAnyOperationsAborted`，界面什么都不做。
  - 坑（已记入 `AGENTS.md` 第 6 节第 61—62 条）：`pTo` 传空串而不是 `null` 会被当成非法参数；
    确认框是**本进程**的 `#32770` 模态窗口，但它不在桌面 UIA 子窗口那一层（只在主窗口的
    `Descendants` 里），用 `Children` 找会“看不到确认框”而让删除卡在 STA 线程；
    回收站要用 `Shell.Application` 的 `NameSpace(10)` 读（别去翻 `$Recycle.Bin`）；
    带 `Shift` 的 `KeyboardAccelerator` 对 `Delete` 根本不触发，删除的键盘入口改用 `PreviewKeyDown` + `GetKeyState`。
  - 验收：`tools/test-file-ops.ps1` 用例 H / I（菜单里有「删除」/ `Delete` 后文件真的进了回收站、
    日志有“文件操作：删除到回收站”/ `Shift+Delete` 后文件既不在磁盘也不在回收站、日志有“永久删除”）。

### Phase 3 — 交互增强

- [x] **S8 右键上下文菜单**
  - 目标：列表空白处 / 选中项 / 列头 / 侧边栏节点各自的菜单。
  - 涉及：新增 `Views/ContextMenus/`、`ViewModels/ContextMenuBuilder.cs`、`Services/IShellService.cs`（增加 `ShowShellContextMenu(paths, hwnd, point)`）。
  - 内容：自建菜单（打开、在新标签页打开、复制/剪切/粘贴、删除、重命名、属性、复制路径、在终端打开、压缩…）；
    末项“显示系统菜单”调用 `IContextMenu` 弹出资源管理器同款菜单（能带第三方扩展）。
  - [x] **S8a 系统右键菜单本体**（2026-09，~900 行，11 个文件）：
    文件列表里行上右键 = 该（批）条目的系统菜单、空白处右键 = 当前目录的背景菜单，
    菜单完全由外壳（`IContextMenu`）生成并执行（exdir 自己一个命令都不实现），
    因此 7-Zip / Git / VS Code / WPS / “打开方式” / “发送到” 这些第三方项与子菜单全在。
    新增 `Services/IShellContextMenuService` + `Services/ShellContextMenuService` +
    `Services/Native/ShellContextMenuInterop`（IShellFolder / IContextMenu(2/3)、`SHBindToObject`、
    `TrackPopupMenuEx`、`InvokeCommand`、`ShellMenuHost` 用 `SetWindowSubclass` 转发
    `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MEASUREITEM`）、`Models/ShellMenuItem`；
    `AppSettings` 结构版本 3→4（`ShellMenuKnownItems` / `ShellMenuDisabledItems`）；
    `DetailsView` 把 `RightTapped` / `ContextRequested` 挂在**最外层 Grid**（不是 ListView）；
    `Helpers/DpiHelper.ToScreenPoint` 做 DIP → 屏幕物理像素（`ClientToScreen`）。
    兼带修两个拦路的既有 bug：`TabView` 被 WinUI 默认样式压成 `VerticalAlignment=Top`
    （文件列表只占“内容那么高”，空白处收不到任何事件）、窗口位置/尺寸存坏
    （DIP 下限当物理像素夹、最小化时存 `-32000` 哨兵值）。
    验收：`tools/test-context-menu.ps1` 12 条断言全过（真鼠标右键 + 截图 + exdir.log；
    Win11 的外壳菜单是自绘的，UIA 里读不到菜单项），另跑了 test-status-bar / test-shell-icons /
    test-settings / measure-row-align 全绿。
  - [x] **S8b 设置页「右键菜单」分类**（2026-09，~180 行，6 个文件）：
    设置页左侧多一个「右键菜单」分类，右侧列出系统右键菜单项（含第三方扩展，默认全部开启）
    逐项开关；关掉的项存进 `ShellMenuDisabledItems`（key 优先用规范动词，例如 `verb:properties`），
    弹出菜单前把这些项从 HMENU 里删掉（顺带清空子菜单、清理多余分隔符）。
    清单来源 = 打开设置页时用样本目标现枚举（样本 .txt / 配置目录 / 配置目录背景）
    ∪ 实际右键过的项（落盘在 `ShellMenuKnownItems`）。新增 `Models/SettingsCategory.ShellMenu`、
    `ViewModels/ShellMenuItemViewModel`；页面本身后来在 S17d 里搬到了 `Views/SettingsView`。
    验收：`tools/test-settings.ps1` 扩成 5 个用例 47 条断言（含“系统菜单项默认全开”、
    “关掉「属性」后落盘 verb:properties”、“重新打开仍为关”、“再拨回来就清空”），
    截图 `tools/shot-settings.ps1`（.artifacts\settings-右键菜单.png）。
  - [x] **S8c 内置（自建）轻量右键菜单 + 风格切换**（2026-09）：
    新增 `AppSettings.UseBuiltInContextMenu`（默认 **true**）与设置页「右键菜单」页的
    「使用内置的轻量右键菜单」开关；`Views/DetailsView` 每次右键时现读该值：
    内置菜单用 WinUI `MenuFlyout` 现场搭（文件行：打开 / 在资源管理器中显示 / 复制路径 / 属性；
    背景：新建文件夹 / 刷新 / 全选 / 复制当前路径 / 在此处打开终端），不建 COM、弹出瞬时；
    `FolderTabViewModel.CreateNewFolderAsync`（exdir 自己建目录并选中）与
    `IShellService.ShowProperties`（`ProcessStartInfo.Verb="properties"`）是新增的两个命令；
    内容与系统菜单**故意不同**，两种菜单不共享清单。
    验收：`tools/test-context-menu.ps1` 扩成 5 个用例（系统菜单 3 个 + 内置菜单 2 个，含
    「内置菜单不是 #32768」「UIA 能按名字读到菜单项」「点「新建文件夹」真的建出目录」）。
  - [ ] 未做：内置菜单里的「在新标签页打开」「压缩…」等仍需要文件操作能力（见 S4–S6）；
    列头与侧边栏节点的菜单（侧边栏 / 固定目录 / 磁盘按钮现在都还没有右键菜单）。
  - 预估：~550 行，6 个文件（实际 ~1080 行，17 个文件——系统菜单这一块比预想的细）。 

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

- [~] **S11 当前目录快速过滤** —— **不做**（2026-10）
  - 原计划：导航条右侧输入框实时（去抖 120 ms）过滤已加载的当前列表（`*` / `?` 通配）。
  - 落地情况：下面的 S12 改成 **Everything 快速搜索** 后，“当前目录”这个范围上给的已经是同一件事
    （而且连子目录一起搜），再做一套“只过滤已加载行”的输入框会多一个入口、多一套状态。
    以后真需要“不递归、只过滤已经在列表里的行”时再单开一步。

- [x] **S12 递归搜索 —— 改成基于 Everything 的快速搜索**（2026-10 完成）
  - 目标（原计划）：`Ctrl+F` 打开搜索面板，后台递归枚举（可取消、可暂停），结果为“列表 + 所在目录”列。
  - **实际做法**：不自研递归枚举，而是查本机 **Everything** 的索引（随包分发 Everything SDK 的
    `Everything64.dll` 当 IPC 客户端）：
    - 导航条右侧一个搜索框（`Ctrl+F` 聚焦）+「整机」范围开关 + 结果计数；输入去抖 180 ms，
      结果**替换当前标签页的列表**，`Esc` / 「×」/ 换目录都退出搜索模式；
    - **默认范围 = 当前目录及其子目录**（`path:"<当前目录>\"`，结尾反斜杠必须有，见 AGENTS.md 第 101 条），
      可切「整机」；结果行在名称右侧用暗色小字标出相对搜索根的目录；
    - 双击结果 = 跳到它所在目录并选中（目录结果直接进去），右键菜单另有「打开所在文件夹」/「打开」；
    - 状态栏「N 项」、列头排序、多选、复制 / 剪切 / 删除 / 压缩 / 属性、右键菜单全都不用改；
    - 只在真实目录里可用（压缩包内 / 远程位置 / 「此电脑」禁用并写明原因）；`F5` 在搜索模式 = 重搜；
    - 找不到 DLL / Everything 没运行时**不动列表**，只在提示条里说明（`EXDIR_EVERYTHING_DLL` 可覆盖 DLL 路径）；
    - **索引没覆盖当前目录时会说明原因**（2026-10 补）：当前目录范围一个都没搜到、而目录里确实有东西、
      且 Everything 里这个目录下一条都没有 → 提示条直说“Everything 的索引里没有这个目录”，并给出办法：
      以管理员身份装一次 Everything 服务（`Everything.exe -install-service`，或「工具 → 选项 → 常规」）
      让整块磁盘被索引，或把这个目录加进「工具 → 选项 → 索引 → 文件夹」。
      没这句提示时，只加了「文件夹索引」的机器上看起来就像“搜索范围跑到了程序自己的工作目录”
      （根因、以及为什么 subst 盘符当不了“索引之外的目录”，见 AGENTS.md 第 106 条）。
  - 涉及（新增）：`Services/IEverythingSearchService.cs`、`Services/EverythingSearchService.cs`、
    `Services/Native/EverythingInterop.cs`、`Helpers/EverythingLocator.cs`、`Helpers/EverythingQuery.cs`、
    `Views/SearchBarView.xaml(.cs)`、`tools/everything-smoke`、`tools/test-search.ps1`。
  - 涉及（改动）：`ViewModels/FolderTabViewModel.cs`（搜索状态 / 去抖 / 扁平结果 / 守卫）、
    `ViewModels/FileItemViewModel.cs`（`SearchPathText`）、`Views/NavigationBarView.xaml`、
    `Views/DetailsView.xaml(.cs)`（相对目录 + 右键菜单 + `EmptyHint` / `ErrorTitle` + 错误提示条可关掉）、
    `ViewModels/MainViewModel.cs`（`FocusActiveSearchCommand`）、`ViewModels/PanelViewModel.cs`、
    `MainWindow.xaml(.cs)`（`Ctrl+F` + 「编辑」菜单项）、`App.xaml.cs`（DI）、`Themes/ExdirTheme.xaml`
    （勾选式开关样式 + 三个主题各一份勾选底色）、`exdir.csproj`、`tools/publish.ps1`、`native/`。
  - 验收：`tools/everything-smoke`（40 断言：查询串拼接 10 + 真实查询 13 + 结果映射 14 + 索引覆盖检查 3）与
    `tools/test-search.ps1`（11 用例 61 断言，UIA，不需交互桌面）全过，**Debug 与 `dist\win-x64\exdir.exe`
    各跑一次**（裁剪版上注册表探测与委托式原生互操作都正常）；既有的 `test-command-line.ps1`（75）、
    `test-settings.ps1`（117）、`test-archive-copy.ps1`（62）仍全过。
  - 未做：本机没有交互桌面，搜索框的**视觉布局**只用 UIA 几何确认过（导航条上 200×30 DIP，
    `tool\capture.ps1` 截图在全黑桌面上拿不到内容，与第 18 条一致），没肉看过；结果行右侧那条相对目录
    在极窄窗格里最多外溢 140 DIP（列宽下限 80 DIP 时），没有做自适应降级。

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
  - [x] **S15a 右键「压缩」：选中项打成一个 zip → 输出目录 → 自动复制到剪贴板**（2026-10，6 个新文件 + 12 个改动）
    - 内容：内置右键菜单在**文件行 / 目录行**上多一项「压缩」（任意真实条目，压缩包内部展开出来的虚拟行没有
      这一项）；把选中项（目录含整棵子树、空目录也保住）打成一个 zip，落到设置里的「压缩输出目录」（留空 = 用户的
      「下载」文件夹），然后把生成的 zip **复制到剪贴板**，并弹一条带「打开目录」的绿色 InfoBar（标题「压缩完成」）。
      包名：单选用条目名（文件去扩展名、目录保留全名）、多选用当前目录名，重名先加 `(2)(3)…`。
    - 做法：新增 `Services/ICompressionService.cs(.cs)`（BCL 的 `System.IO.Compression.ZipArchive`，与「只读浏览
      别人压缩包」那条 7z.dll 的路**互不干扰**）、`Helpers/CompressTargets.cs`（包名 / 输出目录两条纯规则，
      服务级可测）、`Helpers/FolderPicker.cs`（外壳 `IFileOpenDialog` + `FOS_PICKFOLDERS`）；
      `AppSettings.CompressionOutputDirectory`（结构版本 8→9）+ 设置「文件列表 → 压缩输出目录」（文本框 + 「浏览…」）；
      `FolderTabViewModel.CompressSelectionCommand`；`ICompressionService.ArchiveCreated` → `MainViewModel` 按路径刷新标签页；
      `FolderTabViewModel.StatusTitle`（InfoBar 标题从写死的「解压完成」改成按操作给）。
    - 验收：`tools\archive-smoke`（服务级：打包内容 / 空目录 / 同名 (2) / 非法字符 / 失败不留半成品 / 包名与输出目录
      规则；裁剪版 `--no-iso` 0 失败）；`tools\test-settings.ps1` 用例 11 / 12（文本框即时落盘 + 「浏览…」真的弹出
      `#32770`「选择文件夹」，Debug 与裁剪后的 dist 各跑一遍 0 失败）；`tools\test-compress.ps1`（真鼠标右键，
      需交互桌面 —— 本机无桌面，未执行，留存备用）。
    - 未做：压缩进度对话框 / 取消按钮（现在只有文件列表转圈）、zip64 大文件专项验证、压缩级别选项。

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
    - 验收：`tools/test-settings.ps1` 16 项断言全绿（内容齐全、初始值与 config.json 一致、
      取消不落盘、保存立即落盘、关掉“显示文件扩展名”后列表行名里的 `.xxx` 从 13 行降到 5 行，
      再打开后恢复原样）；跑完自动还原 `config.json`。
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
      默认停在「文件列表」/ 每页只看得到本分类的开关 / 初始值与 config.json 一致 / 取消不落盘 /
      保存立即落盘并作用到文件列表 / 跨分类改「布局」后重新打开能读回）；
      新增 `tools/shot-settings.ps1` 给每个分类截一张图（`.artifacts\settings-<分类名>.png`）。
  - [x] **S17c 设置对话框加「行高」滑块**（2026-09，改 8 个文件 / 新增 2 个，~150 行）
    - 内容：文件列表的行高可调（20~48 DIP、步进 2），默认 **28 DIP**（原来固定 24）；
      设置对话框「文件列表」页最后一行是「行高」滑块 + 常驻当前值。
      行高只改变上下留白，图标与名称仍在行内垂直居中；列头保持 26 DIP 不变。
    - 做法：`AppSettings.RowHeight`（默认 `ColumnLayout.DefaultRowHeight`，夹取在 `NormalizeRowHeight`）
      → `SettingsViewModel.RowHeight` → 新增 `Views/SettingsSliderRow.xaml(.cs)`
      （Title/Description/Value/Minimum/Maximum/StepFrequency，与 SettingsToggleRow 同一套行式布局）
      → `DetailsView.xaml` 行模板 `Height="{x:Bind Columns.RowHeight}"`、`ItemContainerStyle.MinHeight` 归零
      → `MainViewModel.ApplySettings` 把值推给所有已存在标签页的 `ColumnLayout.RowHeight`
      （新建标签页在 `FolderTabViewModel` 构造里读设置）。
    - 踩坑（已记入 `AGENTS.md` 第 6 节第 44 条）：`MinHeight` 是**下限**，用它配行高会让“调小”不生效；
      行高放在每标签页共享的 `ColumnLayout` 上，而不是每个 `FileItemViewModel` 上。
    - 验收：`tools/test-settings.ps1` 用例 6（6 条断言：初值一致 / 切分类读不到 / 保存落盘 /
      UIA 量 `ListItem` 高度 = 28 DIP 与 40 DIP）全绿；`tools/measure-row-align.ps1`
      在 28 与 48 两种行高下都通过（中位数偏差 2.5 物理像素）。
  - [x] **S17d 设置对话框 → Windows 11 风格独立设置窗口**（2026-09，删 6 个文件 / 新增 2 个，~450 行）
    - 内容：不再自己写对话框内容与行模板，改用 WinUI 推荐的做法：
      独立 `Window`（默认 860×800、工作区居中、同一时刻只开一个，已开就 `Activate`），
      左侧 `NavigationView` 四个分类，右侧一列 Windows 11 风格设置卡片
      （社区工具包 `CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219 的 `SettingsCard`），
      并改成 **改动即时生效 + 立即落盘**（没有「保存 / 取消」）。
    - 做法：删除 `Views/SettingsDialog.xaml(.cs)`、`Views/SettingsToggleRow.xaml(.cs)`、
      `Views/SettingsSliderRow.xaml(.cs)`；新增 `Views/SettingsWindow.xaml(.cs)`（外壳：标题/尺寸/接线）
      与 `Views/SettingsView.xaml(.cs)`（正文：NavigationView + SettingsCard，ViewModel 是 DP）；
      `SettingsViewModel` 从“快照”改成“当前值 + `Changed` 事件”（每个属性走 `SetAndNotify`，
      `ShellMenuItemViewModel` 改成 `ObservableObject` 以便把开关改动汇报上去）；
      `MainViewModel.CreateSettingsSnapshot()` → `CreateSettingsEditor()`；
      `MainWindow.Settings_Click` 改成开窗口并复用。
    - 踩坑（已记入 `AGENTS.md` 第 6 节第 50—53 条）：
      `Window` 不是 `FrameworkElement` → 写在 Window 根上的 `x:Bind` 用不了 `{StaticResource}` 转换器
      （CS1503），所以正文必须包一层 UserControl；
      `SettingsCard` 在卡片宽 < 476 DIP 时把控件换行到标题下方（`SettingsCardWrapThreshold`）
      → 窗口宽度不能小（600 宽只有 ~372 的卡片，取 860）；
      `NavigationView` 换页要接 `SelectionChanged`，`ItemInvoked` 在键盘 / 程序化选中时不触发；
      自动化脚本找「设置」窗口必须再按进程号过滤（桌面上 `ApplicationFrameHost` 也有同名窗口）。
    - 验收：`tools/test-settings.ps1` 重写为 6 个用例 48 条断言全绿（分类齐全 /
      每页只显示本分类的项 / 初始值一致 / 拨一下立即落盘 / 立刻作用到文件列表 /
      跨分类与关窗重开读回 / 同一时刻只开一个窗口 / 右键菜单项默认全开且能逐项关闭 /
      行高滑块改完列表行真的变高），全程 UIA 模式驱动（不需要前台窗口 / 真鼠标）。

- [ ] **S18 文件系统监视自动刷新**
  - 目标：`FileSystemWatcher` 监视当前目录，外部变动时增量刷新（去抖），保持选中与滚动位置。
  - 涉及：新增 `Services/IDirectoryWatcher.cs(.cs)`、`ViewModels/FolderTabViewModel.cs`。
  - 验收：在资源管理器里新建文件，exdir 1 秒内出现该文件且不闪烁、不丢选中。
  - 预估：~350 行，3 个文件。

- [~] **S19 单实例 + 命令行参数 + 管理员**
  - 目标：`exdir.exe <path>` 在当前实例的新标签页打开；已运行时把请求转发给已有实例；“以管理员身份重新启动”。
  - 涉及：`App.xaml.cs`、新增 `Services/IInstanceService.cs(.cs)`（命名管道/互斥体）、`Program.cs`（自定义 `Main` 以便在 `Application.Start` 之前处理参数）。
  - [x] **命令行参数 + 转发到已有实例**（2026-09，新增 1 个文件 / 改 9 个，~430 行）：
    命令行格式 `exdir [path]`：已在运行时把请求转给已有实例并在**活动窗格里新开标签页**打开
    （已在同一目录就不新开；`path` 是文件则打开所在目录并选中它；路径不存在就在当前标签页显示「无法打开」）；
    无参数时打开**当前工作目录**，但工作目录是 exe 目录 / `C:\Windows` / `System32` 这类
    “启动器给的位置”（双击 exe、点任务栏图标）时只唤回窗口、不动浏览位置；
    exdir 尚未运行则就是普通启动（会话照旧恢复，请求的目录用新标签页打开）。
    实现：`Program.cs` 第一件事 `Helpers/CommandLine.Parse(args)`（拿到本进程的工作目录），
    单实例闸门由“只置位一个命名事件”改成“命名事件 + 命名管道（`exdir.activate`）”，
    `App.OnLaunched` 把 `MainWindow.HandleActivation` 注册给闸门，
    `MainWindow` 把请求排队并在会话恢复完成后交给 `MainViewModel.HandleActivationAsync`。
    踩到的坑（已记入 `AGENTS.md` 第 6 节第 70～74 条）：`Main(string[] args)` 不含 exe 路径；
    裁剪会把纯转发的 `System.IO.Pipes` 从 deps.json 里删掉（裁剪版一启动就无日志陳死，
    `TrimmerRootAssembly` 修）；一份日志会被两个进程同时写（命名互斥体）；
    新建标签页在导航之后才挂上 ViewModel，会错过“按路径恢复选中项”。
    验收：`tools/test-command-line.ps1`（10 个用例，全程 UIA，不需要交互桌面），
    Debug 与 `dist\win-x64\exdir.exe`（裁剪版）上都全绿。
  - [ ] 未做：“以管理员身份重新启动”、多路径参数（多开几个标签页）、`--help` / 选项解析。
  - 预估：~400 行，4 个文件（实际 ~430 行，10 个文件，多出来的主要在文档与回归脚本）。

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

### Phase 8 — 外壳实时性

- [x] **S24 磁盘热插拔实时刷新**（2026-09，新增 3 个文件 / 改 5 个，~260 行）
  - 目标：插上 U 盘后侧边栏「此电脑」与工具条磁盘区**不用手动刷新**就出现新盘，拔掉立刻消失。
  - 涉及：新增 `Services/IDeviceChangeService.cs`、`Services/DeviceChangeService.cs`、
    `Services/Native/VolumeChangeWatcher.cs`；改 `App.xaml.cs`（注册）、`MainWindow.xaml.cs`（挂监听 + 两次延迟刷新）、
    `ViewModels/MainViewModel.cs`（`RefreshDrives` 改差量）、`ViewModels/SidebarViewModel.cs`（新增 `RefreshDrives`）、
    新增 `tools/test-drive-hotplug.ps1`。
  - 做法：主窗口用 `SetWindowSubclass` 接 `WM_DEVICECHANGE`（卷到达 / 移除 + `DBT_DEVNODES_CHANGED`）；
    收到后 600 ms 合并刷新一次、2500 ms 再兜底刷新一次（一条插拔会连发好几条消息，盘符又往往晚几百毫秒才可用）；
    `RefreshDrives` 只按 `DriveInfo` 清单做差量：清单没变一个控件都不动，变了也只增删 / 挪动那一个盘节点
    （没变化的盘复用同一个节点对象，容容器不重建）；「工具 → 重新扫描磁盘」与磁盘变化后重列
    「转到 → 所有位置」都走同一入口。
  - 坑（已记入 `AGENTS.md` 第 6 节第 56 条）：卷事件的 `lParam` 是 `DEV_BROADCAST_HDR*`，
    跨进程 `SendMessage` 会让目标进程去读自己地址空间里的野指针（AV 直接崩进程），所以回归脚本发的是
    没有 lParam 的 `DBT_DEVNODES_CHANGED`，产品代码也把这个事件当“卷可能变了”（顺带兜住就绪延迟）。
  - 验收：`tools/test-drive-hotplug.ps1` 3 个用例 12 条断言全绿（插入前两处都没有该盘符 →
    `subst` + 发消息后两处都出现（并从 exdir.log 确认消息真的被处理）→ `subst /d` + 发消息后两处都消失 →
    侧边栏节点清单与插入前完全一致、收藏夹仍与 config.json 一致）。

- [x] **S28 侧边栏「主目录」里显示哪些标准文件夹可配**（2026-09，改 8 个文件 + 更新回归脚本，~200 行）
  - 目标：设置窗口能把「主目录」分组里的桌面 / 文档 / 下载 / 图片 / 音乐 / 视频逐项关掉，
    **默认只开「桌面」与「下载」**。
  - 涉及：`Models/AppSettings.cs`（6 个 `SidebarHome*` 开关 + 结构版本 6）、
    `Services/IKnownFolderService.cs`（新增 `UserFolderKey` + `SpecialFolderModel.Key`）、
    `Services/KnownFolderService.cs`（给六个标准文件夹打 Key）、
    `ViewModels/SidebarViewModel.cs`（`ApplyHomeFolders` + `SyncHomeFolders`）、
    `ViewModels/SettingsViewModel.cs`、`Views/SettingsView.xaml`（六个开关卡片）、
    `ViewModels/MainViewModel.cs`（`ApplySettings` / `ApplySidebarGroups`）、`tools/test-settings.ps1`。
  - 做法：筛选按 `UserFolderKey`（稳定标识，不受系统语言 / 路径重定向影响）而不是显示名；
    `SyncHomeFolders()` 只增删「主目录」分组的差异子项、复用未变化的节点，
    所以关掉再打开一个文件夹时它已展开的子目录与展开状态都还在（与分组开关同一个理由）。
  - 验收：`tools/test-settings.ps1` 用例 7 追加了相应断言（默认只有桌面 / 下载；
    打开「文档」后树里真的出现；关掉「桌面」后真的消失；用完还原）。

- [x] **S30 开机自启 + 预热启动（`--preload`）**（2026-09，新增 3 个文件 / 改 9 个，~350 行）
  - 目标：设置里能给 exdir 开“开机自启”；登录时起来的那一份**不显示主窗口**，
    只把系统中该预读 / 准备好的东西做掉，让之后双击 exe（或点托盘图标）几乎瞬时就能出窗口。
  - 涉及：新增 `Helpers/AutoStart.cs`、`tools/test-autostart.ps1`；
    改 `Models/AppSettings.cs`（`StartWithWindows` + 结构版本 7→8）、`Models/SettingsCategory.cs`、
    `Helpers/CommandLine.cs`（`--preload` / `@preload` / `IsPreload`）、
    `ViewModels/SettingsViewModel.cs`（`StartWithWindows` + `IsStartupPageVisible`）、
    `Views/SettingsView.xaml`（「启动」页）、`ViewModels/MainViewModel.cs`（自愈写注册表 + 幂等会话恢复 + `PreloadIconsAsync`）、
    `ViewModels/FolderTabViewModel.cs`（`PreloadIconsAsync`）、`MainWindow.xaml.cs`（`StartPreload` / 预热请求不弹窗口）、
    `App.xaml.cs`（`--preload` 时不 `Activate`）、`tools/test-settings.ps1`（用例 10）。
  - 做法：
    * 自启项 = `HKCU\...\CurrentVersion\Run` 里的 `exdir` = `"<exe>" --preload`（非打包应用用不了
      `StartupTask`，写 HKCU 不要管理员权限）；设置一拨就写 / 删，启动时按设置重写一遍（换目录自愈）。
    * 预热进程：不 `Activate()`（窗口从未显示），但把“已隐藏到托盘”的状态记上；
      预热 = 恢复会话（枚举上次打开的目录 + 侧边栏树）+ 预取首屏几十行的外壳图标（提取是串行的）；
      窗口位置恢复 / 菜单构建留给真正显示时的 `Loaded`（那时才有真实 DPI）。
    * 用户之后双击 exe：第二个进程把“唤回窗口”送进管道，预热进程 `ShowWindow` 一下就行；
      `--preload` 的请求载荷是约定值 `@preload`（收到它**不弹窗口**，空串才是只唤窗口）。
  - 坑（已记入 `AGENTS.md` 第 6 节第 75 条）：预热进程必须自己置 `_hiddenToTray`，
    否则 `ShowFromTray` 会走“只是被最小化”那条分支、窗口永远出不来；不能指望 `RootGrid.Loaded`
    触发预热（窗口没显示过，第一帧布局不一定跑），所以会话恢复要从 `Loaded` 里挖出来做成幂等
    （`??=` 一个共享 `Task`，用 bool 会在“第一次还没跑完”时误判成已完成）。
  - 验收：`tools/test-autostart.ps1` 3 个用例 18 条断言全绿（预热进程驻留且**没有可见主窗口**、
    日志有「预热启动：…」「预热完成：… 目录=… 图标=…」（实测 ~600 ms / 37 个图标）、
    第二次启动 ~70 ms 退出并把窗口真的唤出来、带路径的请求照样能转发进来新开标签页）；
    `tools/test-settings.ps1` 10 个用例全绿（含用例 10：拨开关真的写 / 删 HKCU 的 Run 项，用完还原注册表）；
    `tools/test-command-line.ps1` 10 个用例全绿（命令行路径没被 `--preload` 改坏）。

---

## Phase 9 — 压缩包浏览

- [x] **S31a 压缩包引擎与虚拟路径**（2026-09，新增 7 个文件 / 改 8 个，~1100 行）
  - 目标：“双击压缩包 = 以目录形式进入”的底座：能解析“压缩包 + 包内路径”这种虚拟路径，
    能枚举包内目录，能把单个条目解到临时目录。
  - 涉及：新增 `native/`（`x64\7z.dll` + `LICENSE-7z.txt` + `README.md`）、
    `Services/Native/SevenZipInterop.cs`（只读 `IInArchive` + 打开/解压回调）、`Services/IArchiveService.cs`、
    `Services/ArchiveService.cs`、`Models/ArchivePath.cs`、`Helpers/ArchiveFormats.cs`；
    改 `Services/IFileSystemService.cs` + `Services/FileSystemService.cs`（`ResolveDirectoryAsync` /
    `IsInsideArchive` / `EnumerateDirectoryAsync` 认虚拟路径）、`ViewModels/PanelViewModel.cs`、
    `ViewModels/MainViewModel.cs`（会话恢复与命令行）、`App.xaml.cs`（DI）、`exdir.csproj`、`tools/publish.ps1`。
  - 做法：用原生 7z.dll（用户确认的路线）；格式清单收窄到“核心压缩格式”∩ 7z.dll 真认识的；
    索引按“路径 + 大小 + 修改时间”缓存；`.tar.gz`/`.tgz` 透明解开中间那层 tar；
    包内文件双击解到 `%LOCALAPPDATA%\exdir\archive-cache` 再用默认程序打开；
    加密包弹密码框（`IDialogService`）；临时目录启动/退出各清一次（副本只删一天前的）。
  - 坑（已记入 `AGENTS.md` 第 6 节第 77～82 条）：7-Zip 的接口 IID 新布局 + `kClassID` 是“BSTR 装 16 字节 GUID”；
    `Seek` 的 out 指针可以是 NULL；托管 CCW 必须 public + ComVisible；单文件压缩器的条目名是空的；
    tar 的 `./` 前缀；加密 zip 问的是 v1 密码接口。
  - 验收：`tools/archive-smoke`（服务级冒烟，不需要交互桌面）在 Debug 与**裁剪过的自包含产物**上都是 0 失败；
    `tools/test-archive.ps1` 在 Debug 与 `dist\win-x64\exdir.exe` 上的 UIA 部分全绿。

- [x] **S31b 压缩包只读浏览的界面与交互**（2026-09，改 6 个文件 / 新增 1 个脚本，~600 行）
  - 内容：双击进包（`OpenItem`）、`FolderTabViewModel.IsInsideArchive` + 写操作的统一守卫、
    包内右键菜单换成“只读版”且在包内强制用内置菜单、包内拖拽与拖入禁用、
    导航条右侧的「只读」徽标、包内目录的通用文件夹图标、F5 丢掉索引缓存、
    会话与命令行进入包内目录。
  - 验收：`tools/test-archive.ps1`（12 个用例：双击进包 / 上一级 / 面包屑回包根 / 行内展开 / 排序与图标 /
    只读守卫 / 包内文件解到临时目录 / `.tar.gz` 透明解开 / 会话恢复 / 命令行进包 / `.7z`；
    真鼠标双击与右键，需要交互桌面）；另重跑 `tools/test-row-dblclick.ps1` 与 `tools/test-context-menu.ps1`
    确认真实目录没被改坏。

- [ ] **S32 压缩包的更多能力**（后续）
  - [x] **S31c 支持 `.iso`（光盘映像）像其它压缩包一样浏览**（2026-09，改 1 个源文件 + 3 个测试/文档）
    - 需求：把 `.iso` 也当压缩包双击进目录浏览（自然连带“复制到外部目录”与“双击文件解出来打开”）。
    - 涉及：`Helpers/ArchiveFormats.cs`（`Core` 加 `iso`）；`tools/archive-smoke/Program.cs`（ISO 用例 +
      IMAPI2FS 现造测试映像）、`tools/test-archive-copy.ps1`（用例 6：进包 → 复制 → 粘到真实目录）、`AGENTS.md`。
    - 做法：只加扩展名 —— 7z.dll 格式表里 Iso 与 Udf 两个处理器都声明了 `iso`，
      `BuildIndex` 本来就按格式表顺序逐个试（一个打不开就试下一个），所以纯 ISO9660 与 UDF 的映像都能进
      （2026-10 起改成「都试一遍、条目多的说了算」，见 S31d：两套文件系统的内容可以不一样）。
    - 验收：`tools/archive-smoke` 与 `tools/test-archive-copy.ps1` 在 Debug / 裁剪过的 dist 上都 0 失败；
      另拿真实的 9.8 GB Bazzite Live ISO（xorriso 造的 ISO9660 + UDF + El Torito）人工验过列表与取出
      （`[BOOT]` 那种隐式目录也对）。
  - [x] **S31d UDF 混合盘：ISO9660 那半只有说明文件时列出 UDF 的内容**（2026-10，改 1 个源文件 + 1 个测试 + 文档）
    - 问题：`7z.dll` 的 Iso 与 Udf 两个处理器都声明 `iso`，而一张盘上**两套文件系统的内容可以不一样** ——
      Windows 刻出来的 UDF 盘上，ISO9660 那半只有一张写着 “This disc contains a "UDF" file system…” 的
      README.TXT，真正的内容全在 UDF 里；`BuildIndex` 只认第一个能打开的处理器（Iso 在前）→ 用户看到的整张盘
      就只有一个 README.TXT（`7z l` 反而显示 UDF 内容）。
    - 涉及：`Services/ArchiveService.cs`（`BuildIndex`）；`tools/archive-smoke/Program.cs`（UDF-only 映像、
      手写 ISO9660 造的「UDF 说明盘」）；`AGENTS.md`（第 4 节 `.iso` 那条 + 第 87 条）。
    - 做法：能打开的处理器都过一遍，**条目多的那个说了算** —— `ItemCount` 当粗筛（不比手上这份多就直接跳过，
      省掉大 ISO 上一整轮 `GetProperty`），真认的是 `ReadNodes` 出来的行数；并列保留格式表顺序。
      密码失败也改成「所有处理器都打不开才报」（`.rar` 的 Rar / Rar5 会互相干扰）。
    - 验收：`tools/archive-smoke` 0 失败（UDF-only 的列表/取出，UDF 说明盘的列表/取出/复制都对）；
      本机两个真实 ISO（NixOS 3.3 GB、Bazzite 10 GB，都是 ISO9660 + Joliet）行为不变；
      「UDF 说明盘」测试映像由 `TryCreateNoticeIso` 现造（IMAPI2FS 造不出这种盘，见第 86/87 条）。
  - [x] **S32b 压缩包文件在文件列表里像目录一样就地展开**（2026-10，改 5 个源文件 / 改 2 个脚本 + 文档，~260 行）
    - 需求：不用双击进包，列表里的压缩包也能像目录那样点行首箭头**在当前列表里就地展开**，看包内内容。
    - 涉及：`Models/FileSystemEntry.cs`（新增 `IsArchive` / `IsInArchive`）、`Services/FileSystemService.cs`
      （枚举时用 `_archive.IsArchiveFile()` 标出可浏览压缩包）、`Services/ArchiveService.cs`（包内条目 `IsInArchive = true`）、
      `ViewModels/FileItemViewModel.cs`（`IsExpandable = IsDirectory || IsArchive`，`CanExpand` 从它算）、
      `ViewModels/FolderTabViewModel.cs`（展开/折叠、打开、只读守卫按行判断、`CopyArchiveSelection` 按选中项解析压缩包、
      图标、F5 失效展开的包缓存、`EnsureChildrenAsync` 就地弹密码框）、`Views/DetailsView.xaml.cs`（拖拽 / 拖放落点 /
      右键菜单 / 方向键都按行判断）；`tools/test-archive-copy.ps1`（新增用例 7～9）、`tools/test-archive.ps1`
      （补回缺失的 `中文名称.txt` 测试数据 —— 两个预期行清单一直包含它却从未真正断言成功；另加用例 13）、`AGENTS.md`。
    - 做法：展开的唯一判据是 `FileItemViewModel.IsExpandable`（目录，或可浏览的压缩包文件）；子项仍走
      `FileSystemService.EnumerateDirectoryAsync`（包内交给 `ArchiveService.ListAsync`），所以加密包照样就地弹密码框、
      F5 会先把就地展开的那个包 `Invalidate` 掉。只读守卫从“当前目录在包内”拆成“目录级 + **选中项级**”两套
      （`RefuseInArchive` / `RefuseSelectionInArchive`），否则在真实目录里就地展开时「删除 / 拖拽 / 属性」拿到的是
      虚拟路径；拖放落点压在包内目录行上时**整个不接受**，不再退回“当前目录”。
    - 验收：`tools/test-archive-copy.ps1` 9 个用例全绿（含用例 7～9：压缩包行有箭头、就地展开只多两行且不进包
      （无「只读」徽标）、展开出来的行「复制」进内存剪贴板并清空系统剪贴板、「删除」被拦且磁盘无变化、
      折叠后行集合与展开前完全一致）；同一脚本在 `dist\win-x64\exdir.exe`（裁剪过的自包含产物）上也全绿；
      `tools/archive-smoke` 0 失败（新增两条：包内条目都带 `IsInArchive`、包内条目不算是独立的压缩包文件），
      裁剪过的产物加 `--no-iso` 同样 0 失败 —— 那两段 SKIP 是冒烟代理自己用 `dynamic` COM 造 ISO 的问题，
      与被测代码无关（见 AGENTS.md 第 89 条）。`tools/test-archive.ps1` 用例 13（真鼠标）本机无交互桌面，未跑；
      顺手补回那个脚本里一直缺失的 `中文名称.txt` 测试数据（两个预期行清单一直包含它）。
  - [x] **S32a 包内「复制」→ 外部目录粘贴**（2026-09，新增 3 个文件 / 改 9 个，~380 行）
    - 需求：浏览包内文件时，能把包内单个或多个指定文件（含目录的整棵子树）复制、粘贴到外部目录。
    - 涉及：新增 `Services/IArchiveClipboardService.cs` + `ArchiveClipboardService.cs`、`tools/test-archive-copy.ps1`；
      改 `Services/IArchiveService.cs` / `ArchiveService.cs`（`ExtractForCopyAsync` / `ReleaseStaging` / `CleanupTemp`）、
      `Services/Native/SevenZipInterop.cs`（`ExtractFiles`：一次 `Extract` 解多条目）、`App.xaml.cs`（DI）、
      `ViewModels/MainViewModel.cs` / `PanelViewModel.cs` / `FolderTabViewModel.cs`、`Views/DetailsView.xaml.cs`、
      `tools/archive-smoke/Program.cs`、`tools/test-archive.ps1`、`AGENTS.md`。
    - 做法：包内复制只把「压缩包 + 包内相对路径」记在内存（不动磁盘，并清空系统剪贴板）；
      到真实目录粘贴时才解到 `archive-cache\copy` 再交给外壳 `SHFileOperation` 复制，中转副本用完即删；
      与系统剪贴板互斥、粘贴时系统剪贴板优先；每个选中项解到自己的序号子目录（跨目录同名条目不互相覆盖）。
    - 验收：`tools/archive-smoke`（新增“无目录条目的 zip（Compress-Archive 那种）”、多选、空目录、
      跨目录同名条目、加密包、清理等用例）0 失败；`tools/test-archive-copy.ps1`（5 用例 32 断言）
      在 Debug 与**裁剪过的自包含产物**上全绿（全程 UIA，不需交互桌面）；真鼠标那条路在
      `tools/test-archive.ps1` 用例 12（本机当时没有交互桌面，未能跑，已按同一套断言写好）。
  - [x] **S32c 真实压缩包文件行的右键「使用 7-Zip 打开 / 解压到下载文件夹」**（2026-10，新增 2 个文件 / 改 8 个 + 文档）
    - 需求：真实目录里的压缩包文件可以右键交给**系统装的 7-Zip** 打开，也可以一键解压（直接解到 `Downloads\<包名>\`，
      不用先双击进包再复制出来）。
    - 涉及：新增 `Helpers/SevenZipLocator.cs`（找系统的 `7zFM.exe`）、`tools/test-archive-extract.ps1`（真鼠标回归）；
      改 `Services/IShellService.cs` / `ShellService.cs`（`OpenWithProgram`）、`Services/IArchiveService.cs` /
      `ArchiveService.cs`（`ExtractAllAsync` + `Extracted` 事件，`ExtractRoots` 新增“直接铺到目标目录”一档）、
      `ViewModels/FolderTabViewModel.cs`（两个命令 + `StatusMessage` / `OpenStatusTarget` / 目标目录与重名处理）、
      `PanelViewModel.cs` / `MainViewModel.cs`（把 `IKnownFolderService` 传下去、订阅 `Extracted` 刷新相关标签页）、
      `Views/DetailsView.xaml(.cs)`（菜单项 + 绿色 InfoBar 与「打开目录」）、`tools/archive-smoke/Program.cs`、
      `AGENTS.md` / `README.md`。
    - 做法：两个入口只对**真实**压缩包文件行出现（`FileItemViewModel.IsArchive`，可多选；包内只读条目与目录行没有）；
      7-Zip 没装时那一项**置灰且标题写明原因**（`SevenZipLocator` 按注册表 `Path` → 常见安装目录 → `PATH` 找，
      `Lazy` 缓存一次）；解压目标同名目录自动加 `(2)(3)…`，失败 / 取消把刚建出来的半成品目录删掉；完成后绿色 InfoBar
      （带「打开目录」）+ `IArchiveService.Extracted` 事件让宿主刷新正开在目标目录 / 其父目录里的标签页。
    - 验收：`tools/archive-smoke` 新增 15 条断言（整包解压 / 空目录与深层目录 / 中文条目名 / `.tar.gz` / 跨目录同名 /
      目标目录不存在 / 空压缩包 / 加密包 / 取消不留目录 / `Extracted` 事件）在 Debug 与**裁剪过的产物**
      （`.artifacts\smoke-trimmed` + `--no-iso`）上都 0 失败；`tools/test-archive-copy.ps1 -Exe dist\win-x64\exdir.exe`
      9 个用例仍全绿（确认 `ExtractRoots` 的重构没改坏“包内复制 → 粘贴”那条路）；`SevenZipLocator` 另用一次性控制台
      工程直接跑过（本机找到 scoop 的 `7zFM.exe`）；`tools/test-archive-extract.ps1`（真鼠标右键）**本机无交互桌面，
      未跑**（与 S32b 那次同样的限制，脚本已按同一套断言写好）。
  - [x] **S32e 把包内条目拖到别处（永远是复制）**（2026-10，改 8 个源文件 / 改 2 个测试 + 文档，~350 行）
    - 需求：浏览压缩包内容时（进包，或在真实目录里就地展开），把包内的文件 / 目录拖到别的地方 —— 另一个窗格 /
      目录行 / 列表空白处 / 资源管理器 / 桌面 —— 复制出来。
    - 涉及：`Services/IArchiveService.cs` + `ArchiveService.cs`（`ExtractForDragAsync` / `ReleaseStagingFor`，
      `CleanupTemp` 对 `drag` 分类用“只删一天前”）、`Helpers/DragDropHelper.cs`（`ArchiveDragFormat` /
      `SetArchiveDrag` / `MayContainFolder` 拒绝临时副本）、`Models/DragPayload.cs`（新增）、
      `ViewModels/FolderTabViewModel.cs`（`BuildDragPayloadAsync` + 忙碌提示条）、`ViewModels/MainViewModel.cs`
      （复制完成回收临时副本）、`Views/DetailsView.xaml(.cs)`（手势里预解包 + `SetStorageItems` + 落点一律复制 +
      兜底守卫）、`AGENTS.md`、`tools/archive-smoke`、`tools/test-archive.ps1`（改写用例 6 + 新增用例 14）。
    - 做法：包内条目是虚拟路径、交不出 `CF_HDROP`；WinUI 3 的“延迟提供 `StorageItems`”（`SetDataProvider`）有已知
      bug（microsoft-ui-xaml#9629，拖到资源管理器会被直接拒）→ 只能在**手势开始前**把选中条目解到
      `archive-cache\drag\<guid>`，再用 `SetStorageItems` 交给系统（于是资源管理器 / 桌面也能收）。数据包额外带一个
      标记格式 `exdir/archive-drag`：工具条固定目录区与侧边栏收藏夹据此拒绝（不把缓存里的临时目录固定 / 收藏起来），
      文件列表据此一律按复制（`Ctrl` / `Shift` 都不改语义 —— 包是只读的，交出去的也只是临时副本）。临时副本在内部
      复制完成后由 `ReleaseStagingFor(源路径)` 回收；拖到外部 / 被 Esc 取消 / 落在包内时留给 `CleanupTemp()` 按时间扫。
    - 验收：`tools/archive-smoke` 新增 8 条断言（`drag` 分类落点 / 内容与名字 / `ReleaseStagingFor` 只认 `drag`、
      不碰 `copy` 与真实路径 / 传子路径也能反查到根 / `CleanupTemp` 扫掉一天前的、留住刚解出来的）在 Debug 与
      **裁剪过的产物**（`.artifacts\smoke-trimmed --no-iso`）上都 0 失败；`tools/test-archive-copy.ps1` 在 Debug 与
      `dist\win-x64\exdir.exe` 上都 9 个用例全绿（确认没改坏“包内复制 → 粘贴”与就地展开那条路）。
    - **真鼠标用例本轮未跑**：跑脚本的那个会话当时没有可用输入桌面（`GetForegroundWindow()` 为 0、
      `SetCursorPos` / `SendKeys` / `CopyFromScreen` 全部失败，`test-archive.ps1` 连用例 1 的双击都点不动）——
      环境问题，与被改的代码无关。要在**有交互桌面的会话**里补跑：`pwsh -NoProfile -File tools\test-archive.ps1`
      （用例 6 / 14 是新增的）、`tools\test-file-ops.ps1`、`tools\test-pin-drag.ps1`（固定目录受
      `MayContainFolder` 改动影响）。
  - [ ] 其余都还没做，按需再排：**包内条目**右键「解压到当前文件夹 / 解压到 <同名> 文件夹」（带进度对话框；
    **真实压缩包文件行**已经有「解压到下载文件夹」了，见 S32c）、包内新建/删除/重命名条目
    （只 zip/7z）。

---

## Phase 10 — 远程位置（SFTP / FTP，只读浏览 + 下载到本地）

一段一次做完的（用户直接提的需求：“支持在侧边栏中添加 sftp 与 ftp 位置，并支持点击后浏览”）。
已确认的决策：范围 = **浏览 + 复制到本地**（不做上传 / 删除 / 重命名 / 新建）；客户端库 =
**SSH.NET + FluentFTP**；凭据 = **DPAPI 加密后存 `config.json`**；SFTP 认证 = **密码 + 私钥文件**。

- [x] **S33 远程位置的数据模型 / 路径解析 / 凭据加密**（2026-10，新增 4 个源文件 + 改 2 个，~500 行）
  - 内容：`Models/RemoteLocation.cs`（协议 / 登录方式 / 主机 / 端口 / 起始目录 / 被动模式 / 允许无效证书）、
    `Models/RemotePath.cs`（`sftp://` `ftp://` `ftps://` 的解析 / 拼接 / 规整 / 父目录 / 面包屑分段）、
    `Services/Native/DpapiInterop.cs` + `Helpers/SecretProtector.cs`（`CryptProtectData` 两个 P/Invoke，不引 NuGet）、
    `AppSettings` 新增 `RemoteLocations` + `SidebarShowRemote`（结构版本 9→10）。
  - 验收：`tools/remote-smoke` 里的路径与凭据用例（35 断言，Debug 与裁剪产物都绿）。

- [x] **S34 SFTP / FTP 会话与远程文件服务**（2026-10，新增 7 个源文件 + 改 2 个，~700 行）
  - 内容：`Services/Remote/`（`RemoteSession` 抽象 + `SftpSession` + `FtpSession` + `RemoteSessionPool`）、
    `IRemoteFileService` + `RemoteFileService`（列目录 / 存在性 / 递归下载）、`Helpers/RemoteCache.cs`（中转目录）、
    `FileSystemService` 的远程分派、`exdir.csproj` 加两个包。
  - 验收：`tools/remote-test-server`（node：ssh2 实现的最小 SFTP 服务 + ftp-srv）+ `tools/remote-smoke`
    （135 断言：列目录 / 隐藏项 / 递归下载 / 重名 (2) / 错密码 / 连不上；FTP 与 SFTP 密码与私钥三种连接，
    在 Debug 与裁剪过的产物上都 0 失败）。

- [x] **S35 侧边栏「远程」分组 + 只读浏览 + 下载到本地 + 设置页**（2026-10，改 14 个源文件 / 新增 2 个，~1100 行）
  - 内容：侧边栏「远程」分组（`SidebarNodeKind.Remote` + `ApplyRemoteLocations` 差量更新）；
    标签页的远程只读守卫（`RefuseInRemote`）与远程只读版右键菜单；双击文件先下到 `remote-cache\open` 再打开；
    右键「下载到…」（文件夹选择器 + 递归下载 + 绿色 InfoBar）；「复制」下到 `remote-cache\copy` 后进系统剪贴板；
    拖出去（复用压缩包的临时副本拖拽那条路）；面包屑 / 标签页标题识别远程路径；
    设置窗口新增「远程」分类（列表 + 添加 / 编辑 / 删除对话框）；`文件 → 打开`（顺带补上）。
  - 顺带修掉一个**老 bug**：`WindowX/WindowY` 还是 `double.NaN` 时 `Save()` 会抛（JSON 没有 NaN 字面量）→
    任何“窗口还没定位就改设置”都“界面生效但 `config.json` 没变”（AGENTS.md 第 96 条）。
  - 验收：`tools/test-remote.ps1`（9 用例 43 断言，全程 UIA，Debug 与 `dist\win-x64\exdir.exe` 都 0 失败）；
    `tools/test-settings.ps1`（改为 7 个分类 118 断言，Debug 与 dist 都 0 失败）；
    `test-drive-hotplug` / `test-network-locations` / `test-command-line` 重跑无回归。
  - **未跑的部分**：真鼠标与拖拽（双击下载打开、拖到资源管理器、右键菜单）需要交互桌面，
    跑脚本的会话当时屏幕是锁的（`GetForegroundWindow()` 为 0、有 `LogonUI.exe`），按第 18/94 条只能 UIA。
    补跑方式：在**未锁屏**的会话里 `pwsh -NoProfile -File tools\test-remote.ps1` + 手动点一遍双击 / 拖拽。

- [ ] **S36 远程位置后续（按需）**：上传（拖进去 / 粘贴 / 新建目录 / 删除 / 重命名）、
  FTPS 隐式 TLS、known_hosts 校验、多窗口同时浏览同一个远程位置（目前每条连接串行）。

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
| 设置窗口宽度有下限 | `SettingsCard` 在卡片宽 < 476 DIP 时把控件换行到标题下方，所以窗口默认 860 DIP 宽；屏幕比 860 DIP 还窄时会被系统夹小，卡片就换成竖排 | 需要时把左导航收成 compact（48 DIP）或调小 `SettingsCardWrapThreshold` |
| 提权运行后拖放失效 | Windows 不允许高完整性级别（管理员）进程参与拖放：`DragItemsStarting` 会触发，但永远收不到 `DragOver`/`Drop`；以普通权限运行则正常（见 AGENTS.md 第 6 节第 21 条） | 系统限制，无解；必要时在界面上提示 |
| 拖放的目标还很有限 | 已做：目录 → 工具条固定目录、工具条固定目录之间排序、目录 → 侧边栏收藏夹、文件 / 目录 → 目录行 / 列表空白处 / 另一个窗格、**压缩包里的条目 → 上面任何地方 + 资源管理器 / 桌面（永远是复制，见 S32e）** | 真实目录里的文件拖到资源管理器（数据包还只有自定义格式与纯文本，没放 `StorageItems`）、拖到目录行上悬停 1 秒进入目录 | S7 其余部分 |
| 固定目录最多 12 个 | 工具条固定目录区不滚动，太多了会把左侧磁盘区挤没（`MainViewModel.MaxPinnedFolders`） | 需要时改成横向滚动 / 溢出菜单 |
| 无右键菜单 | 需求未明确，需先确认路线 | S8（已解决 S8a/S8b：文件列表条目 + 空白处弹系统真菜单，设置页可逐项关闭；侧边栏/列头与自建命令项仍未做） |
| 文件列表只有“内容那么高” | `TabView` 默认样式是 `VerticalAlignment=Top`，列上只有两三个文件时列表下面一大片空白既点不到也没有右键 | 已修（S8a 兼带：`PaneView` 的 TabView 显式 `Stretch`，见 AGENTS.md 第 6 节第 39 条） |
| 窗口位置/尺寸可能存坏 | DIP 下限被当成物理像素夹（高分屏下窗口只有下限的一半宽）；最小化时存下 `-32000` 哨兵值 | 已修（S8a 兼带，见 AGENTS.md 第 6 节第 41 条） |
| 侧边栏同步是“尽力而为” | 只在已加载节点里查找，深层目录不会自动展开定位 | S10（可加“展开到当前路径”） |
| 单实例 | 已解决（2026-09，托盘驻留）：命名事件 `Local\exdir.activate` 做闸门，闸门跑在 XAML 初始化之前，第二次双击 exe 只唤回已有窗口；命令行参数（`exdir <path>`）走同一条路（加一个命名管道传载荷，见 S19）；不再是“多个进程” | — |
| 只有 x64 验证过 | x86/ARM64 未测试 | 需要时再验证 |
| 展开的子项不随文件变化刷新 | 已展开目录的子项只在展开时枚举一次，需要 F5 刷新整个标签页 | 将来做文件系统监视时一并处理 |
| 无单元测试 | 纯逻辑可测但尚未建工程 | S20 |
| Release 产物 224 MB | 因为 .NET + WinAppSDK 全自包含。若可接受“要求目标机装 Windows App Runtime”，可改回框架依赖以大幅减小体积 | 按需 |
