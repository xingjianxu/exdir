# native/ —— 随程序分发的原生组件

两个：

* `x64/7z.dll` —— 压缩包只读浏览（双击压缩包 = 以目录形式进入）；
* `x64/Everything64.dll` —— 基于 Everything 的快速搜索（IPC 客户端，需本机装并运行 Everything）。

两者都以**独立文件**放在 exe 旁边，不静态链接；许可原文在同目录的 `LICENSE-*.txt`。

## `x64/7z.dll`

exdir 的「双击压缩包 = 以目录形式进入」靠它实现：这是 **7-Zip 官方发布的原生引擎 DLL**
（不是第三方重打包），只读浏览压缩包用，不做压缩 / 写入。

| 项 | 值 |
| --- | --- |
| 版本 | 7-Zip 26.03（x64） |
| 大小 | 1,906,688 字节 |
| SHA256 | `65E4C1F855F9EF6E8F0F5DF8E3F27D9EB5F07311408639DA0A1CA0B8F4871B0D` |
| 来源（本机） | `C:\Users\xingjian\scoop\apps\7zip\26.03\7z.dll`（scoop 装的 7-Zip） |
| 来源（官方） | <https://www.7-zip.org/download.html>，取 `7-Zip Extra` 或安装包里的 `x64/7z.dll` |
| 许可 | 见同目录 `LICENSE-7z.txt`：主体 LGPL-2.1，RAR 相关代码带 unRAR restriction，另有部分 BSD 3-clause |

做法：把 `7z.dll` 以**独立文件**的形式放在 exe 旁边（`exdir.csproj` 里用 `Content` + `Link="7z.dll"`），
不是静态链接进去 —— 这样用户可以直接替换这一个 DLL（LGPL 的 relinking 要求），
而且不参与 .NET 的裁剪（它本来就是原生文件）。

## 升级步骤

1. 从上面的官方来源取得新版的 x64 `7z.dll`（**不要**取 x86 的）；
2. 覆盖 `native/x64/7z.dll`，并同步替换 `native/LICENSE-7z.txt`；
3. 更新本文件里的版本 / 大小 / SHA256 / 来源；
4. `pwsh -NoProfile -File tools\publish.ps1`（脚本第 3 步会校验 `dist\win-x64\7z.dll` 存在）；
5. 跑一遍 `pwsh -NoProfile -File tools\test-archive.ps1`（Debug 与 `dist\win-x64\exdir.exe` 各一次）。

## `x64/Everything64.dll`

exdir 的「基于 Everything 的快速搜索」靠它实现：这是 **voidtools 官方发布的 Everything SDK**
里的 IPC 客户端 DLL（不是 Everything 本体，里面没有索引引擎），用来向本机正在运行的
Everything 客户端发起查询（`Everything_SetSearchW` / `Everything_QueryW` / `Everything_GetResult*`）。

| 项 | 值 |
| --- | --- |
| 大小 | 91,304 字节 |
| SHA256 | `81B5BE18126ACD2C2B913F8F4A821E476B18393CDD3DEBD03387C50AFD8DB88F` |
| 来源（官方） | <https://www.voidtools.com/Everything-SDK.zip>，取包内 `dll/Everything64.dll` |
| 许可 | MIT（见同目录 `LICENSE-Everything-SDK.txt`，内含完整声明与升级步骤） |

做法：与 `7z.dll` 一样以**独立文件**放在 exe 旁边（`exdir.csproj` 里用 `Content` + `Link`），
不静态链接、不参与 .NET 裁剪，用户可以自己替换。
运行期查找顺序见 `Helpers/EverythingLocator.cs`：
**exe 旁边 → 注册表 `InstallLocation` → `%ProgramFiles%\Everything` 等常见目录**，
并可用环境变量 `EXDIR_EVERYTHING_DLL` 指定具体路径（回归脚本用它验证“检测不到 Everything”的降级路径）。

> **用户体验上的约束**：SDK 要求 **Everything 客户端进程正在运行**（它只是 IPC 客户端）。
> 找不到 DLL 时整个搜索框显示「未检测到 Everything」；找到 DLL 但客户端没起来时显示
> 「Everything 没有在运行」，两种情况都只是提示、不影响其它功能。

## 为什么不用 NuGet 包装包

exdir 的压缩包浏览是**只读**的（打开 / 列举 / 取单个文件），需要的接口只有
`IInArchive` + 打开/解压回调那一小块，`Services/Native/SevenZipInterop.cs` 自己写一层更可控：
没有额外的托管依赖要跟版本、也不用担心包装包的许可与裁剪问题（7z.dll 的许可还是要一起分发）。
