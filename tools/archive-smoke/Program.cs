using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;

// ArchiveService + 7z.dll 互操作的服务级冒烟测试（不需要交互桌面）。
//
// 直接把 app 里那几个源文件编进来跑真实实现（见 archive-smoke.csproj），覆盖：
// 格式表 / 7z.dll 加载 / 虚拟路径解析（含 .. 与盘符冒号的拒绝、嵌套压缩包）/ 枚举与隐式目录 /
// 目录存在性 / 解到临时目录（含深层目录与链式 tar）/ “包内复制 → 外部目录”中转（ExtractForCopyAsync）/ 
// ISO（光盘映像）/ UDF（同一张盘上的两套文件系统，含“ISO9660 那半只有一张 UDF 说明文件”的混合盘）——
// 测试映像用系统 IMAPI2FS 现造，IMAPI 造不出的那种混合盘由 TryCreateNoticeIso 手写 ISO9660 那半；
// 加密包（列表不要密码、取文件要密码）/ 无目录条目的 zip /
// 损坏包 / 索引缓存与 Invalidate / 取消 / CleanupTemp。
//
// 跑法：dotnet run -c Debug --project tools\archive-smoke
//   裁剪过的发布版要额外加 --no-iso（造测试 ISO 用的 dynamic COM 在裁剪后原生崩溃，见下面 skipIso 处的说明）
// 交互部分（双击进包、只读守卫、真鼠标）在 tools\test-archive.ps1 里，那个需要交互桌面。

var failures = 0;

// 造测试 ISO 用的是 C# 的 dynamic COM 晚期绑定（Microsoft.CSharp 的 IDispatchComObject）；
// **裁剪过的**自包含发布版里，它会在 ITypeInfo.ReleaseTypeAttr 上踩出 AccessViolationException
// （原生崩溃，catch 不住，整个进程就没了）→ 裁剪版跑冒烟时加 --no-iso 跳过 ISO / UDF 那几段
// （ISO 的浏览仍由 test-archive-copy.ps1 用例 6 在 dist 产物上验证；见 AGENTS.md 第 89 条）。
var skipIso = Array.IndexOf(args, "--no-iso") >= 0;
const string SkipIsoReason = "--no-iso（裁剪过的发布版里 dynamic COM 会原生崩溃）";

void Assert(bool condition, string message)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
    if (!condition)
    {
        failures++;
    }
}

var work = Path.Combine(Path.GetTempPath(), "iztest-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);

try
{
    // ---------------------------------------------------------------- 测试数据
    var staging = Path.Combine(work, "staging");
    Directory.CreateDirectory(Path.Combine(staging, "sub", "deep"));
    Directory.CreateDirectory(Path.Combine(staging, "空目录"));
    File.WriteAllText(Path.Combine(staging, "hello.txt"), "hello 世界");
    File.WriteAllText(Path.Combine(staging, "中文名称.txt"), "中文");
    File.WriteAllText(Path.Combine(staging, "sub", "inner.txt"), "inner");
    File.WriteAllText(Path.Combine(staging, "sub", "deep", "deep.txt"), new string('x', 5000));

    var zipPath = Path.Combine(work, "sample.zip");
    ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

    var tarGzPath = Path.Combine(work, "sample.tar.gz");
    Run("tar.exe", $"-czf \"{tarGzPath}\" -C \"{staging}\" .");

    Run("tar.exe", $"-cf \"{Path.Combine(work, "sample.tar")}\" -C \"{staging}\" .");

    var plainTxt = Path.Combine(work, "notes.txt");
    File.WriteAllText(plainTxt, "not an archive");
    var docx = Path.Combine(work, "report.docx");
    ZipFile.CreateFromDirectory(staging, docx, CompressionLevel.Optimal, includeBaseDirectory: false);

    // 嵌套压缩包：zip 里再放一个 zip
    var outerDir = Path.Combine(work, "outer");
    Directory.CreateDirectory(outerDir);
    File.Copy(zipPath, Path.Combine(outerDir, "inner.zip"));
    File.WriteAllText(Path.Combine(outerDir, "readme.txt"), "outer");
    var outerZip = Path.Combine(work, "outer.zip");
    ZipFile.CreateFromDirectory(outerDir, outerZip, CompressionLevel.Optimal, includeBaseDirectory: false);

    // 不同目录下的同名条目（验证“包内复制”的中转目录不会互相覆盖）
    var dupDir = Path.Combine(work, "dup");
    Directory.CreateDirectory(Path.Combine(dupDir, "a"));
    Directory.CreateDirectory(Path.Combine(dupDir, "b"));
    File.WriteAllText(Path.Combine(dupDir, "a", "same.txt"), "AAA");
    File.WriteAllText(Path.Combine(dupDir, "b", "same.txt"), "BBB");
    var dupZip = Path.Combine(work, "dup.zip");
    ZipFile.CreateFromDirectory(dupDir, dupZip, CompressionLevel.Optimal, includeBaseDirectory: false);

    // 损坏的压缩包
    var brokenZip = Path.Combine(work, "broken.zip");
    File.WriteAllBytes(brokenZip, Encoding.ASCII.GetBytes("this is definitely not a zip file at all................"));

    // 加密 zip（要 7z.exe）
    var sevenZip = FindSevenZip();
    string? encryptedZip = null;
    if (sevenZip is not null)
    {
        encryptedZip = Path.Combine(work, "secret.zip");
        var result = Run(sevenZip, $"a -tzip -psecret123 -mem=ZipCrypto \"{encryptedZip}\" \"{staging}\\hello.txt\"");
        if (result.ExitCode != 0 || !File.Exists(encryptedZip))
        {
            encryptedZip = null;
        }
    }

    // ---------------------------------------------------------------- 格式表与识别
    var service = new ArchiveService();
    Console.WriteLine($"--- 7z.dll 可用 = {service.IsAvailable}（{service.AvailabilityFailure}）");

    Assert(service.IsAvailable, "7z.dll 可用");

    Assert(service.IsArchiveFile(zipPath), "sample.zip 认成压缩包");
    Assert(service.IsArchiveFile(tarGzPath), "sample.tar.gz 认成压缩包");
    Assert(service.IsArchiveFile(Path.Combine(work, "sample.tar")), "sample.tar 认成压缩包");
    Assert(!service.IsArchiveFile(plainTxt), "notes.txt 不是压缩包");
    Assert(!service.IsArchiveFile(docx), "report.docx 不是压缩包（核心清单里没有它）");
    Assert(!service.IsArchiveFile(work), "目录不是压缩包");
    Assert(!service.IsArchiveFile(@"D:\不存在的\a.zip"), "不存在的 .zip 不是压缩包");
    Assert(service.IsArchiveFile(brokenZip), "损坏的 broken.zip 仍然按扩展名认成压缩包（打开时才报错）");

    // ---------------------------------------------------------------- 路径解析
    Assert(service.TryParse(zipPath, out var root) && root.IsRoot && root.FullPath == zipPath, "zip 自身解析成压缩包根");
    Assert(service.TryParse(zipPath + @"\sub", out var sub) && sub.InnerPath == "sub" && sub.Name == "sub", "包内目录解析正确");
    Assert(service.TryParse(zipPath + @"\sub\inner.txt", out var innerFile) && innerFile.InnerPath == @"sub\inner.txt", "包内文件解析正确");
    Assert(service.TryParse(zipPath + @"\sub\deep", out var deep) && deep.InnerPath == @"sub\deep", "包内两层目录解析正确");
    Assert(!service.TryParse(work, out _), "真实目录不是压缩包路径");
    Assert(!service.TryParse(plainTxt, out _), "真实文件不是压缩包路径");
    Assert(!service.TryParse(zipPath + @"\..\..\Windows", out _), "含 .. 的包内路径被拒绝");
    Assert(!service.TryParse(zipPath + @"\C:\Windows", out _), "含盘符冒号的包内路径被拒绝");
    Assert(service.TryParse(outerZip + @"\inner.zip\sub", out var nested)
           && nested.ArchiveFile == outerZip
           && nested.InnerPath == @"inner.zip\sub", "嵌套压缩包的路径仍然挂在最外层压缩包上");
    Assert(service.IsInsideArchive(zipPath) && service.IsInsideArchive(zipPath + @"\sub"), "IsInsideArchive 对根与包内目录都成立");
    Assert(!service.IsInsideArchive(work), "IsInsideArchive 对真实目录不成立");

    // ---------------------------------------------------------------- 枚举
    var rootItems = await service.ListAsync(root);
    Console.WriteLine($"  zip 根: {string.Join(", ", rootItems.Select(i => $"{i.Name}{(i.IsDirectory ? "/" : $"({i.Size})")}"))}");
    Assert(rootItems.Any(i => i.Name == "hello.txt" && !i.IsDirectory && i.Size == Encoding.UTF8.GetByteCount("hello 世界")), "hello.txt 的大小正确");
    Assert(rootItems.Any(i => i.Name == "中文名称.txt"), "中文名条目正确（编码没问题）");
    Assert(rootItems.Any(i => i.Name == "sub" && i.IsDirectory), "隐式目录 sub 被补出来了");
    Assert(rootItems.All(i => i.FullPath.StartsWith(zipPath, StringComparison.OrdinalIgnoreCase)), "子项 FullPath 都是压缩包虚拟路径");
    Assert(rootItems.Count == 4 && rootItems.Any(i => i.Name == "空目录" && i.IsDirectory), "comp-archive 里的空目录也在（共 4 项）");
    Assert(rootItems.All(i => i.IsInArchive), "包内条目都带 IsInArchive（文件列表里的写操作按它拒绝）");
    Assert(rootItems.All(i => !i.IsArchive), "包内条目不算是独立的压缩包文件（嵌套包也一样，只有最外层能进）");

    var subItems = await service.ListAsync(sub);
    Console.WriteLine($"  zip sub: {string.Join(", ", subItems.Select(i => i.Name))}");
    Assert(subItems.Count == 2 && subItems.Any(i => i.Name == "deep" && i.IsDirectory) && subItems.Any(i => i.Name == "inner.txt"), "sub 里是 deep / inner.txt");

    var deepItems = await service.ListAsync(deep);
    Assert(deepItems.Count == 1 && deepItems[0].Name == "deep.txt" && deepItems[0].Size == 5000, "deep 里是 deep.txt（5000 字节）");

    Assert(await service.DirectoryExistsAsync(root), "压缩包根算目录");
    Assert(await service.DirectoryExistsAsync(sub), "包内 sub 算目录");
    Assert(await service.DirectoryExistsAsync(deep), "包内 sub\\deep 算目录");
    Assert(!await service.DirectoryExistsAsync(ArchivePath.Create(zipPath, "nope")), "包内不存在的目录不算目录");
    Assert(!await service.DirectoryExistsAsync(innerFile), "包内文件不算目录");

    // ---------------------------------------------------------------- tar.gz 链式解开
    Assert(service.TryParse(tarGzPath, out var tarGzRoot) && tarGzRoot.IsRoot, "tar.gz 解析成压缩包根");
    var tarGzItems = await service.ListAsync(tarGzRoot);
    Console.WriteLine($"  tar.gz 根: {string.Join(", ", tarGzItems.Select(i => $"{i.Name}{(i.IsDirectory ? "/" : string.Empty)}"))}");
    Assert(tarGzItems.Count == 4, "tar.gz 直接列出 tar 里的 4 项（不是只有一行 sample.tar）");
    Assert(tarGzItems.Any(i => i.Name == "空目录" && i.IsDirectory), "tar 里的空目录也在");
    Assert(tarGzItems.Any(i => i.Name == "sub" && i.IsDirectory), "tar 里的 sub 是目录");
    Assert(tarGzItems.All(i => i.Name != "sample.tar"), "看不到中间那层 tar");

    // ---------------------------------------------------------------- 解出单个文件
    var tempFile = await service.ExtractToTempAsync(innerFile);
    Console.WriteLine($"  extract → {tempFile}");
    Assert(File.Exists(tempFile) && File.ReadAllText(tempFile) == "inner", "包内文件解到临时目录且内容正确");
    Assert(await service.ExtractToTempAsync(innerFile) == tempFile, "重复解同一个文件命中同一份");

    var deepFile = ArchivePath.Create(zipPath, @"sub\deep\deep.txt");
    var tempDeep = await service.ExtractToTempAsync(deepFile);
    Assert(File.Exists(tempDeep) && new FileInfo(tempDeep).Length == 5000, "包内深层文件能解出来（目录自动创建）");

    var tarGzHello = ArchivePath.Create(tarGzPath, "hello.txt");
    var tempTarHello = await service.ExtractToTempAsync(tarGzHello);
    Assert(File.Exists(tempTarHello) && File.ReadAllText(tempTarHello) == "hello 世界", "链式解开后的 tar 里的文件也能解出来");

    // ---------------------------------------------------------------- 解出一批条目（“包内复制 → 真实目录粘贴”的中转）
    var archiveCache = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "exdir", "archive-cache");

    var copyOne = await service.ExtractForCopyAsync(zipPath, new[] { @"sub\inner.txt" });
    Console.WriteLine($"  copy 1 项 → {string.Join(", ", copyOne.Paths)}");
    Assert(copyOne.Paths.Count == 1
           && Path.GetFileName(copyOne.Paths[0]) == "inner.txt"
           && File.Exists(copyOne.Paths[0])
           && File.ReadAllText(copyOne.Paths[0]) == "inner", "包内单个文件解到中转目录（名字不变、内容正确）");
    Assert(copyOne.Paths[0].StartsWith(Path.Combine(archiveCache, "copy"), StringComparison.OrdinalIgnoreCase), "中转副本落在 archive-cache\\copy 下");
    service.ReleaseStaging(copyOne.StagingDirectory);
    Assert(!Directory.Exists(copyOne.StagingDirectory), "ReleaseStaging 删掉了中转目录");

    var copyMany = await service.ExtractForCopyAsync(zipPath, new[] { "hello.txt", @"sub\inner.txt" });
    Assert(copyMany.Paths.Count == 2
           && copyMany.Paths.Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(new[] { "hello.txt", "inner.txt" })
           && copyMany.Paths.All(File.Exists), "包内多个文件一次解出来（来自不同子目录）");
    service.ReleaseStaging(copyMany.StagingDirectory);

    // PowerShell 的 Compress-Archive 造的 zip：**没有显式目录条目**，条目顺序也不同（子目录先、根级文件最后）。
    // 这两种包在 7z.dll 里的条目号不一样，解单个根级文件时曾经拿不到输出流。
    var caZip = Path.Combine(work, "comp-archive.zip");
    using (var zip = ZipFile.Open(caZip, ZipArchiveMode.Create))
    {
        zip.CreateEntryFromFile(Path.Combine(staging, "sub", "inner.txt"), "sub/inner.txt");
        zip.CreateEntryFromFile(Path.Combine(staging, "sub", "deep", "deep.txt"), "sub/deep/deep.txt");
        zip.CreateEntryFromFile(Path.Combine(staging, "hello.txt"), "hello.txt");
    }

    var caRootItems = await service.ListAsync(ArchivePath.Create(caZip, string.Empty));
    Console.WriteLine($"  comp-archive.zip 根: {string.Join(", ", caRootItems.Select(i => i.Name))}");
    Assert(caRootItems.Count == 2 && caRootItems.Any(i => i.Name == "hello.txt"), "没有目录条目的 zip 也能列出根级文件");

    var caSingle = await service.ExtractForCopyAsync(caZip, new[] { "hello.txt" });
    Assert(caSingle.Paths.Count == 1 && File.Exists(caSingle.Paths[0])
           && File.ReadAllText(caSingle.Paths[0]) == "hello 世界", "没有目录条目的 zip：单个根级文件也能解出来");
    service.ReleaseStaging(caSingle.StagingDirectory);

    var caMany = await service.ExtractForCopyAsync(caZip, new[] { "hello.txt", "sub" });
    Assert(caMany.Paths.Count == 2
           && File.Exists(Path.Combine(caMany.Paths.Single(p => Path.GetFileName(p) == "sub"), "deep", "deep.txt")), "没有目录条目的 zip：多选（文件 + 隐式目录）也解得出来");
    service.ReleaseStaging(caMany.StagingDirectory);

    var copyDir = await service.ExtractForCopyAsync(zipPath, new[] { "sub" });
    Assert(copyDir.Paths.Count == 1 && Path.GetFileName(copyDir.Paths[0]) == "sub" && Directory.Exists(copyDir.Paths[0]), "包内目录按整棵子树解出来");
    Assert(File.Exists(Path.Combine(copyDir.Paths[0], "inner.txt")) && File.Exists(Path.Combine(copyDir.Paths[0], "deep", "deep.txt")), "子树里的文件都在");
    service.ReleaseStaging(copyDir.StagingDirectory);

    var copyEmpty = await service.ExtractForCopyAsync(zipPath, new[] { "空目录" });
    Assert(copyEmpty.Paths.Count == 1
           && Directory.Exists(copyEmpty.Paths[0])
           && !Directory.EnumerateFileSystemEntries(copyEmpty.Paths[0]).Any(), "空目录也照样解出来（不然复制过去会丢目录）");
    service.ReleaseStaging(copyEmpty.StagingDirectory);

    var copyNested = await service.ExtractForCopyAsync(zipPath, new[] { @"sub\inner.txt", "sub" });
    Assert(copyNested.Paths.Count == 1 && Path.GetFileName(copyNested.Paths[0]) == "sub", "选中项里的子项被父目录覆盖（只解一份）");
    service.ReleaseStaging(copyNested.StagingDirectory);

    var copyTar = await service.ExtractForCopyAsync(tarGzPath, new[] { "sub" });
    Assert(copyTar.Paths.Count == 1 && File.Exists(Path.Combine(copyTar.Paths[0], "inner.txt")), "链式解开的 tar.gz 里的目录也能解出来");
    service.ReleaseStaging(copyTar.StagingDirectory);

    var copyDup = await service.ExtractForCopyAsync(dupZip, new[] { @"a\same.txt", @"b\same.txt" });
    Assert(copyDup.Paths.Count == 2 && copyDup.Paths.All(File.Exists), "不同目录下的同名条目都能解出来");
    Assert(copyDup.Paths.Select(Path.GetFileName).All(n => n == "same.txt"), "同名条目交出去的名字仍是包内那个名字");
    Assert(copyDup.Paths.Select(File.ReadAllText).OrderBy(t => t).SequenceEqual(new[] { "AAA", "BBB" }), "同名条目的内容没有互相覆盖");
    service.ReleaseStaging(copyDup.StagingDirectory);

    try
    {
        await service.ExtractForCopyAsync(zipPath, new[] { "nope/nothing" });
        Assert(false, "不存在的包内路径应当抛 ArchiveOpenException");
    }
    catch (ArchiveOpenException)
    {
        Assert(true, "不存在的包内路径抛 ArchiveOpenException");
    }

    // 缓存：第二次枚举读的是缓存（大小写不敏感地比对，内容与第一次一致）
    var second = await service.ListAsync(root);
    Assert(second.Count == rootItems.Count && second.Any(i => i.Name == "hello.txt"), "第二次枚举命中索引缓存");
    service.Invalidate(zipPath);
    var rebuilt = await service.ListAsync(root);
    Assert(rebuilt.Count == rootItems.Count && rebuilt.Any(i => i.Name == "sub"), "Invalidate 之后能重建索引");

    // ---------------------------------------------------------------- 失败路径
    var brokenLocation = ArchivePath.Create(brokenZip, string.Empty);
    try
    {
        await service.ListAsync(brokenLocation);
        Assert(false, "损坏的压缩包应当抛 ArchiveOpenException");
    }
    catch (ArchiveOpenException ex)
    {
        Assert(true, $"损坏的压缩包抛 ArchiveOpenException（{ex.Message}）");
    }

    if (encryptedZip is not null)
    {
        var secret = ArchivePath.Create(encryptedZip, string.Empty);
        Assert(service.IsArchiveFile(encryptedZip), "加密 zip 认成压缩包");

        // ZipCrypto 的条目名是明文，所以“只列表”不需要密码；要拿文件内容才需要
        var secretItems = await service.ListAsync(secret);
        Assert(secretItems.Count == 1 && secretItems[0].Name == "hello.txt", "加密 zip 不加密码也能列出条目名（ZipCrypto 的特性）");

        var secretFile = ArchivePath.Create(encryptedZip, "hello.txt");
        try
        {
            await service.ExtractForCopyAsync(encryptedZip, new[] { "hello.txt" });
            Assert(false, "加密 zip 没给密码就“复制”应当抛 ArchivePasswordRequiredException");
        }
        catch (ArchivePasswordRequiredException)
        {
            Assert(true, "加密 zip 没给密码就“复制”会抛 ArchivePasswordRequiredException");
        }

        try
        {
            await service.ExtractToTempAsync(secretFile);
            Assert(false, "加密 zip 没给密码就取文件应当抛 ArchivePasswordRequiredException");
        }
        catch (ArchivePasswordRequiredException)
        {
            Assert(true, "加密 zip 没给密码就取文件会抛 ArchivePasswordRequiredException");
        }

        service.SetPassword(encryptedZip, "secret123");
        try
        {
            var temp = await service.ExtractToTempAsync(secretFile);
            Assert(File.Exists(temp) && File.ReadAllText(temp) == "hello 世界", "给了正确密码后能把加密 zip 里的文件解出来");
        }
        catch (Exception ex)
        {
            Assert(false, $"给了正确密码后能把加密 zip 里的文件解出来（实际抛了 {ex.GetType().Name}: {ex.Message}）");
        }

        try
        {
            var copied = await service.ExtractForCopyAsync(encryptedZip, new[] { "hello.txt" });
            Assert(copied.Paths.Count == 1 && File.ReadAllText(copied.Paths[0]) == "hello 世界", "给了正确密码后包内“复制”也能解出来");
            service.ReleaseStaging(copied.StagingDirectory);
        }
        catch (Exception ex)
        {
            Assert(false, $"给了正确密码后包内“复制”也能解出来（实际抛了 {ex.GetType().Name}: {ex.Message}）");
        }

        service.SetPassword(encryptedZip, null);
        service.Invalidate(encryptedZip);
    }
    else
    {
        Console.WriteLine("SKIP 没找到可用的 7z.exe，跳过加密压缩包的用例");
    }

    // ---------------------------------------------------------------- 整包解压到目录（右键「解压到下载文件夹」）

    var extractedEvents = new List<string>();
    service.Extracted += (_, directory) => extractedEvents.Add(directory);

    var unpackDir = Path.Combine(work, "unpack");
    var unpackedFiles = await service.ExtractAllAsync(zipPath, unpackDir);

    Assert(unpackedFiles == 4, $"整包解压写出的文件数与包内一致（实际 {unpackedFiles}）");
    Assert(File.ReadAllText(Path.Combine(unpackDir, "hello.txt")) == "hello 世界", "整包解压出来的文件内容正确");
    Assert(File.ReadAllText(Path.Combine(unpackDir, "中文名称.txt")) == "中文", "中文条目名解到磁盘上不乱码");
    Assert(File.ReadAllText(Path.Combine(unpackDir, "sub", "deep", "deep.txt")).Length == 5000, "深层目录里的文件也解出来了");
    Assert(Directory.Exists(Path.Combine(unpackDir, "空目录")), "空目录照原样建出来");
    Assert(extractedEvents.Count == 1 && extractedEvents[0] == unpackDir, "解压完成后触发 Extracted 事件（宿主据此刷新标签页）");

    var unpackTarDir = Path.Combine(work, "unpack-tar");
    await service.ExtractAllAsync(tarGzPath, unpackTarDir);
    Assert(File.Exists(Path.Combine(unpackTarDir, "sub", "inner.txt")), "tar.gz 也能整包解到目录（解的是链式解开的那个 tar）");

    var unpackDupDir = Path.Combine(work, "unpack-dup");
    await service.ExtractAllAsync(dupZip, unpackDupDir);
    Assert(File.ReadAllText(Path.Combine(unpackDupDir, "a", "same.txt")) == "AAA"
           && File.ReadAllText(Path.Combine(unpackDupDir, "b", "same.txt")) == "BBB",
        "不同目录下的同名条目各自落盘，没有互相覆盖");

    // 目标目录不存在时自己建（「解压到下载文件夹」给的就是一个还没建的 Downloads\<包名>\）
    var unpackNewDir = Path.Combine(work, "unpack-new", "sub-dir");
    Assert(!Directory.Exists(unpackNewDir), "解压前目标目录不存在");
    await service.ExtractAllAsync(zipPath, unpackNewDir);
    Assert(File.Exists(Path.Combine(unpackNewDir, "hello.txt")), "目标目录不存在时自动建出来");

    // 空压缩包：没有可解的内容，明确报错（而不是静默建一个空目录）
    try
    {
        var emptySource = Path.Combine(work, "empty-src");
        Directory.CreateDirectory(emptySource);
        var emptyZip = Path.Combine(work, "empty.zip");
        ZipFile.CreateFromDirectory(emptySource, emptyZip);
        Console.WriteLine($"  empty.zip = {new FileInfo(emptyZip).Length:N0} 字节");

        try
        {
            await service.ExtractAllAsync(emptyZip, Path.Combine(work, "unpack-empty"));
            Assert(false, "空压缩包应当抛 ArchiveOpenException");
        }
        catch (ArchiveOpenException)
        {
            Assert(true, "空压缩包抛 ArchiveOpenException（没有可解压的内容）");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SKIP 造不出空压缩包（{ex.GetType().Name}: {ex.Message}）");
    }

    if (encryptedZip is not null)
    {
        var unpackSecretDir = Path.Combine(work, "unpack-secret");

        try
        {
            await service.ExtractAllAsync(encryptedZip, unpackSecretDir);
            Assert(false, "加密 zip 没给密码就整包解压应当抛 ArchivePasswordRequiredException");
        }
        catch (ArchivePasswordRequiredException)
        {
            Assert(true, "加密 zip 没给密码就整包解压会抛 ArchivePasswordRequiredException");
        }

        service.SetPassword(encryptedZip, "secret123");
        var secretFiles = await service.ExtractAllAsync(encryptedZip, unpackSecretDir);
        Assert(secretFiles == 1 && File.ReadAllText(Path.Combine(unpackSecretDir, "hello.txt")) == "hello 世界",
            "给了正确密码后整包解压能把加密 zip 解出来");

        service.SetPassword(encryptedZip, null);
        service.Invalidate(encryptedZip);
    }

    // 取消：token 已取消时一个文件都不写
    using (var extractCts = new CancellationTokenSource())
    {
        extractCts.Cancel();
        var cancelledDir = Path.Combine(work, "unpack-cancelled");

        try
        {
            await service.ExtractAllAsync(zipPath, cancelledDir, extractCts.Token);
            Assert(false, "已取消的 token 在整包解压时应当抛 OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            Assert(true, "已取消的 token 在整包解压时抛 OperationCanceledException");
        }

        Assert(!Directory.Exists(cancelledDir), "取消掉的解压没有在磁盘上留下目录");
    }

    // ---------------------------------------------------------------- 7z / 取消 / 清理
    if (sevenZip is not null)
    {
        var sevenZipPath = Path.Combine(work, "sample.7z");
        if (Run(sevenZip, $"a -t7z \"{sevenZipPath}\" \"{staging}\\*\" -r").ExitCode == 0 && File.Exists(sevenZipPath))
        {
            Assert(service.IsArchiveFile(sevenZipPath), "sample.7z 认成压缩包");
            var sevenZipItems = await service.ListAsync(ArchivePath.Create(sevenZipPath, string.Empty));
            Assert(sevenZipItems.Any(i => i.Name == "hello.txt") && sevenZipItems.Any(i => i.Name == "sub"), "7z 也能列出条目");
        }
        else
        {
            Console.WriteLine("SKIP sample.7z 造不出来");
        }
    }

    // ---------------------------------------------------------------- ISO / UDF（光盘映像）
    var isoSource = Path.Combine(work, "iso-src");
    Directory.CreateDirectory(Path.Combine(isoSource, "sub"));
    File.WriteAllText(Path.Combine(isoSource, "hello.txt"), "hello 世界");
    File.WriteAllText(Path.Combine(isoSource, "sub", "inner.txt"), "inner");

    var isoPath = Path.Combine(work, "sample.iso");
    var isoFailure = skipIso ? SkipIsoReason : TryCreateIso(isoSource, isoPath, 3);   // ISO9660 | Joliet（Joliet 才有小写名）

    if (isoFailure is null)
    {
        Console.WriteLine($"  sample.iso = {new FileInfo(isoPath).Length:N0} 字节");
        Assert(service.IsArchiveFile(isoPath), "sample.iso 认成压缩包（7z.dll 的 Iso / Udf 处理器）");

        var isoRoot = ArchivePath.Create(isoPath, string.Empty);
        var isoItems = await service.ListAsync(isoRoot);
        Console.WriteLine($"  iso 根: {string.Join(", ", isoItems.Select(i => $"{i.Name}{(i.IsDirectory ? "/" : string.Empty)}"))}");
        Assert(isoItems.Count == 2 && isoItems.Any(i => i.Name == "hello.txt") && isoItems.Any(i => i.Name == "sub" && i.IsDirectory), "iso 根能列出 hello.txt 与 sub");
        Assert(isoItems.Any(i => i.Name == "hello.txt" && i.Size == Encoding.UTF8.GetByteCount("hello 世界")), "iso 里的文件大小正确");
        Assert(await service.DirectoryExistsAsync(ArchivePath.Create(isoPath, "sub")), "iso 里的目录算目录");
        Assert(!await service.DirectoryExistsAsync(ArchivePath.Create(isoPath, "nope")), "iso 里不存在的目录不算目录");

        var isoFile = ArchivePath.Create(isoPath, @"sub\inner.txt");
        var tempIsoFile = await service.ExtractToTempAsync(isoFile);
        Assert(File.Exists(tempIsoFile) && File.ReadAllText(tempIsoFile) == "inner", "iso 里的文件能解到临时目录（双击打开那条路）");

        var isoCopy = await service.ExtractForCopyAsync(isoPath, new[] { "hello.txt", "sub" });
        Assert(isoCopy.Paths.Count == 2 && isoCopy.Paths.Any(p => Path.GetFileName(p) == "hello.txt" && File.Exists(p)), "iso 里的条目也能“复制 → 外部目录”");
        Assert(File.Exists(Path.Combine(isoCopy.Paths.Single(p => Path.GetFileName(p) == "sub"), "inner.txt")), "iso 里的目录按整棵子树解出来");
        service.ReleaseStaging(isoCopy.StagingDirectory);

        service.Invalidate(isoPath);
        service.SetPassword(isoPath, null);
        Assert((await service.ListAsync(isoRoot)).Count == isoItems.Count, "iso 的索引缓存失效后能重建");
    }
    else
    {
        Console.WriteLine($"SKIP 造不出测试 ISO（{isoFailure}），跳过 iso 的用例");
    }

    // UDF：同一个 .iso 扩展名上还挂着 7z.dll 的 Udf 处理器，而 ISO9660 与 UDF 是**同一张盘上的两套文件系统**，
    // 内容可能不一样 —— Windows 刻出来的 UDF 盘上，ISO9660 那半只有一张写着「本盘使用 UDF」的 README.TXT，
    // 真正的内容全在 UDF 里。只认第一个能打开的处理器，用户就只会看到一个 README.TXT。
    var udfPath = Path.Combine(work, "sample-udf.iso");
    var udfFailure = skipIso ? SkipIsoReason : TryCreateIso(isoSource, udfPath, 4);   // 只有 UDF

    if (udfFailure is null)
    {
        var udfRoot = ArchivePath.Create(udfPath, string.Empty);
        var udfItems = await service.ListAsync(udfRoot);
        Console.WriteLine($"  sample-udf.iso = {new FileInfo(udfPath).Length:N0} 字节，根: {string.Join(", ", udfItems.Select(i => i.Name))}");
        Assert(service.IsArchiveFile(udfPath), "UDF 光盘映像认成压缩包");
        Assert(udfItems.Count == 2 && udfItems.Any(i => i.Name == "hello.txt") && udfItems.Any(i => i.Name == "sub" && i.IsDirectory),
            "只有 UDF 的映像能列出 hello.txt 与 sub（Iso 处理器打不开，靠 Udf）");
        Assert(File.ReadAllText(await service.ExtractToTempAsync(ArchivePath.Create(udfPath, @"sub\inner.txt"))) == "inner",
            "UDF 映像里的文件能解出来");

        var noticePath = Path.Combine(work, "udf-notice.iso");
        var noticeFailure = TryCreateNoticeIso(udfPath, noticePath);

        if (noticeFailure is null)
        {
            var noticeRoot = ArchivePath.Create(noticePath, string.Empty);
            var noticeItems = await service.ListAsync(noticeRoot);
            Console.WriteLine($"  udf-notice.iso 根: {string.Join(", ", noticeItems.Select(i => i.Name))}");
            Assert(noticeItems.Count == 2 && noticeItems.Any(i => i.Name == "hello.txt") && noticeItems.Any(i => i.Name == "sub" && i.IsDirectory),
                "ISO9660 那半只有 UDF 说明文件的盘：列的是 UDF 里的内容");
            Assert(noticeItems.All(i => i.Name != "README.TXT"), "没有把 ISO9660 那半的 README.TXT 当成整张盘的内容");

            var noticeTemp = await service.ExtractToTempAsync(ArchivePath.Create(noticePath, @"sub\inner.txt"));
            Assert(File.Exists(noticeTemp) && File.ReadAllText(noticeTemp) == "inner",
                "这种盘里的文件能解出来（解压用的也是选中的那个处理器）");

            var noticeCopy = await service.ExtractForCopyAsync(noticePath, new[] { "hello.txt" });
            Assert(noticeCopy.Paths.Count == 1 && File.ReadAllText(noticeCopy.Paths[0]) == "hello 世界",
                "这种盘里的条目也能“复制 → 外部目录”");
            service.ReleaseStaging(noticeCopy.StagingDirectory);
        }
        else
        {
            Console.WriteLine($"SKIP 造不出 UDF 说明盘（{noticeFailure}）");
        }
    }
    else
    {
        Console.WriteLine($"SKIP 造不出 UDF 映像（{udfFailure}）");
    }

    using (var cts = new CancellationTokenSource())
    {
        cts.Cancel();
        service.Invalidate(zipPath);
        try
        {
            await service.ListAsync(ArchivePath.Create(zipPath, "sub"), cts.Token);
            Assert(false, "已取消的 token 在需要重建索引时应当抛 OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            Assert(true, "已取消的 token 在需要重建索引时抛 OperationCanceledException");
        }
    }

    service.CleanupTemp();
    var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "exdir", "archive-cache");
    Assert(!Directory.Exists(Path.Combine(cache, "tar")), "CleanupTemp 清掉了链式解开的中间 tar 目录");
    Assert(!Directory.Exists(Path.Combine(cache, "copy")), "CleanupTemp 清掉了中转副本目录");

    // ---------------------------------------------------------------- 右键「压缩」：把选中项打成一个 zip
    // CompressionService 走 BCL 的 System.IO.Compression（与上面那条“只读浏览别人压缩包”的路互不干扰）；
    // 界面侧只负责决定“打包哪几项 / 放到哪个目录 / baseName 叫什么”，所以这里验证服务本身。
    var compression = new CompressionService();
    string? createdZip = null;
    compression.ArchiveCreated += (_, path) => createdZip = path;

    var compressOut = Path.Combine(work, "compress-out");
    var multiZip = await compression.CompressAsync(
        new[] { Path.Combine(staging, "hello.txt"), Path.Combine(staging, "sub"), Path.Combine(staging, "空目录") },
        compressOut,
        "合集");

    Assert(File.Exists(multiZip), "压缩：写出的 zip 真的落盘");
    Assert(createdZip == multiZip, "压缩：ArchiveCreated 事件报的就是写出的那个 zip");
    Assert(Path.GetDirectoryName(multiZip) == compressOut && Path.GetFileName(multiZip) == "合集.zip",
        "压缩：zip 落在调用方给的输出目录、名字由 baseName 派生");

    using (var zip = ZipFile.OpenRead(multiZip))
    {
        var names = zip.Entries.Select(e => e.FullName).ToArray();
        Console.WriteLine($"  压缩产物条目: {string.Join(", ", names)}");
        Assert(names.Contains("hello.txt"), "压缩：选中的文件在包里（顶层不带目录前缀）");
        Assert(names.Contains("sub/"), "压缩：选中的目录自己也写了条目（空目录才还原得出来）");
        Assert(names.Contains("sub/inner.txt") && names.Contains("sub/deep/deep.txt"), "压缩：选中的目录整棵子树都进去了");
        Assert(names.Contains("空目录/"), "压缩：选中的空目录也写了条目");

        using var reader = new StreamReader(zip.Entries.Single(e => e.FullName == "hello.txt").Open());
        Assert(reader.ReadToEnd() == "hello 世界", "压缩：包里的内容与源文件一致");
    }

    var secondZip = await compression.CompressAsync(new[] { Path.Combine(staging, "hello.txt") }, compressOut, "合集");
    Assert(Path.GetFileName(secondZip) == "合集 (2).zip", "压缩：同名 zip 已存在时落到「合集 (2).zip」而不是覆盖");
    Assert(File.Exists(multiZip), "压缩：第二次压缩没有动第一次写好的包");

    var overlapZip = await compression.CompressAsync(
        new[] { Path.Combine(staging, "sub"), Path.Combine(staging, "sub", "inner.txt") },
        compressOut,
        "重叠");

    using (var zip = ZipFile.OpenRead(overlapZip))
    {
        Assert(zip.Entries.Count(e => e.FullName == "sub/inner.txt") == 1,
            "压缩：同时选中目录与它下面的条目时不会打进两份");
    }

    var sanitizedZip = await compression.CompressAsync(new[] { Path.Combine(staging, "hello.txt") }, compressOut, "a:b*c");
    Assert(Path.GetFileName(sanitizedZip) == "a_b_c.zip", "压缩：baseName 里的非法字符会被换掉");

    var failureOut = Path.Combine(work, "compress-fail");
    var failed = false;
    try
    {
        await compression.CompressAsync(new[] { Path.Combine(staging, "no-such-file.txt") }, failureOut, "坏的");
    }
    catch (Exception)
    {
        failed = true;
    }

    Assert(failed, "压缩：源不存在时抛异常（调用方据此显示「压缩失败：…」）");
    Assert(!Directory.Exists(failureOut) || !Directory.EnumerateFiles(failureOut, "*.zip").Any(),
        "压缩：失败时不留下写了一半的 zip");

    // 界面上“包叫什么 / 放哪里”的两条规则（见 Helpers/CompressTargets.cs）也在这里盯一下：
    // 这两条是用户直接看得到的约定，而 tools\test-compress.ps1 需要交互桌面才能跑。
    var helloPath = Path.Combine(staging, "hello.txt");
    var subPath = Path.Combine(staging, "sub");
    var dottedDir = Path.Combine(staging, "my.folder");
    Directory.CreateDirectory(dottedDir);

    Assert(CompressTargets.BaseName(new[] { helloPath }, staging) == "hello",
        "压缩包名：单个文件用条目名（去掉扩展名）");
    Assert(CompressTargets.BaseName(new[] { subPath }, staging) == "sub",
        "压缩包名：单个目录用目录全名");
    Assert(CompressTargets.BaseName(new[] { dottedDir }, staging) == "my.folder",
        "压缩包名：目录名里的点不是扩展名（my.folder → my.folder.zip）");
    Assert(CompressTargets.BaseName(new[] { subPath + "\\" }, staging) == "sub",
        "压缩包名：末尾的分隔符不算名字的一部分");
    Assert(CompressTargets.BaseName(new[] { helloPath, subPath }, staging) == Path.GetFileName(staging),
        "压缩包名：多选时用当前目录名");
    var driveRoot = Path.GetPathRoot(work)!;
    Assert(CompressTargets.BaseName(new[] { driveRoot }, driveRoot) == "压缩包",
        "压缩包名：盘根这种取不到名字的情况用「压缩包」");

    Assert(CompressTargets.ResolveOutputDirectory(null, @"C:\dl") == @"C:\dl",
        "压缩输出目录：没配置就用「下载」文件夹");
    Assert(CompressTargets.ResolveOutputDirectory("   ", @"C:\dl") == @"C:\dl",
        "压缩输出目录：只填了空白也算没配置");
    Assert(CompressTargets.ResolveOutputDirectory(@" D:\zips ", @"C:\dl") == @"D:\zips",
        "压缩输出目录：配置了就用配置的（去掉首尾空白）");

    Console.WriteLine($"SUMMARY failures={failures}");
    return failures == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

/// <summary>
/// 用 Windows 自带的 IMAPI2FS 现造一个测试用的 ISO（7-Zip 不会写 ISO，只能借系统组件）。
/// <paramref name="fileSystems" /> 是 IMAPI 的 FsiFileSystems 位组合：1 = ISO9660、2 = Joliet、4 = UDF。
/// 造不出来（精简过的系统 / 没有这个组件）时返回原因，调用方 SKIP。
/// </summary>
static string? TryCreateIso(string sourceDirectory, string isoPath, int fileSystems)
{
    try
    {
        var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage");
        if (type is null)
        {
            return "没有 IMAPI2FS.MsftFileSystemImage";
        }

        dynamic image = Activator.CreateInstance(type)!;
        image.FileSystemsToCreate = fileSystems;
        image.VolumeName = "EXDIRTEST";
        image.Root.AddTree(sourceDirectory, false);

        dynamic result = image.CreateResultImage();
        var stream = (IStream)result.ImageStream;

        using (var file = File.Create(isoPath))
        {
            var buffer = new byte[64 * 1024];
            var read = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, read);
                    var count = Marshal.ReadInt32(read);
                    if (count <= 0)
                    {
                        break;
                    }

                    file.Write(buffer, 0, count);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(read);
            }
        }

        return null;
    }
    catch (Exception ex)
    {
        var inner = ex.InnerException;
        return inner is null
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message} → {inner.GetType().Name}: {inner.Message}";
    }
}

/// <summary>
/// 造一张“Windows 刻出来的那种 UDF 混合盘”：ISO9660 那半只有一张写着
/// “This disc contains a "UDF" file system…” 的 README.TXT，真正的内容全在 UDF 里。
///
/// IMAPI2FS 只会把文件同时写进两套文件系统，造不出这种盘 → 拿它造的 UDF-only 映像打底，
/// 在 UDF 用不到的 16～24 号扇区里手写一张最小的 ISO9660（主卷描述符 + 卷描述符终止符 +
/// 路径表 + 只含一个 README.TXT 的根目录），再把源映像的 UDF 卷识别序列（BEA01 / NSR02 /
/// TEA01）挪到 ISO9660 描述符后面 —— 与真混合盘的布局一致（源映像 256 号扇区之后是 UDF 的
/// 锚点与描述符序列，原样保留，绝对地址不变）。造不出来返回原因，调用方 SKIP。
/// </summary>
static string? TryCreateNoticeIso(string udfImage, string output)
{
    const int SectorSize = 2048;
    const int PathTableSector = 21;
    const int MirrorPathTableSector = 22;
    const int RootDirectorySector = 23;
    const int ReadmeSector = 24;
    const int VolumeSectors = 25;

    try
    {
        var udf = File.ReadAllBytes(udfImage);
        if (udf.Length < 512 * SectorSize)
        {
            return "UDF 源映像太小";
        }

        var readme = Encoding.ASCII.GetBytes(
            "This disc contains a \"UDF\" file system and requires an operating system\r\n" +
            "that supports the ISO-13346 \"UDF\" file system specification.\r\n");

        var now = DateTime.Now;
        var image = (byte[])udf.Clone();

        // 源映像 16/17/18 扇区是 UDF 的卷识别序列 → 挪到 ISO9660 描述符之后（19 扇区往后还有 UDF 自己的东西，不动）
        Array.Copy(udf, 16 * SectorSize, image, 18 * SectorSize, 3 * SectorSize);

        // 根目录（"." / ".." / README.TXT;1）：先写记录，再回头把目录长度回填到 "." 与 ".." 上
        var root = new byte[SectorSize];
        var dotLength = WriteIsoDirectoryRecord(root, 0, RootDirectorySector, 0, isDirectory: true, new byte[] { 0 }, now);
        var cursor = WriteIsoDirectoryRecord(root, dotLength, RootDirectorySector, 0, isDirectory: true, new byte[] { 1 }, now);
        cursor = WriteIsoDirectoryRecord(root, cursor, ReadmeSector, (uint)readme.Length, isDirectory: false, Encoding.ASCII.GetBytes("README.TXT;1"), now);
        var rootLength = (uint)cursor;
        PutBothEndian32(root, 10, rootLength);
        PutBothEndian32(root, dotLength + 10, rootLength);

        // 路径表（只有根一层）。ECMA-119 要求 L / M 两张互为字节序镜像，7-Zip 会比对。
        var pathTable = new byte[SectorSize];
        pathTable[0] = 1;
        PutLe32(pathTable, 2, RootDirectorySector);
        PutLe16(pathTable, 6, 1);

        var mirrorPathTable = new byte[SectorSize];
        mirrorPathTable[0] = 1;
        PutBe32(mirrorPathTable, 2, RootDirectorySector);
        PutBe16(mirrorPathTable, 6, 1);

        // 主卷描述符（ECMA-119 §8.4 的字段偏移；“卷空间大小”故意只算到 ISO9660 那半，跟真盘一样）
        var pvd = new byte[SectorSize];
        pvd[0] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1);
        pvd[6] = 1;
        FillBytes(pvd, 8, 32, (byte)' ');
        FillBytes(pvd, 40, 32, (byte)' ');
        Encoding.ASCII.GetBytes("UDFNOTICE").CopyTo(pvd, 40);
        PutBothEndian32(pvd, 80, VolumeSectors);
        PutBothEndian16(pvd, 120, 1);
        PutBothEndian16(pvd, 124, 1);
        PutBothEndian16(pvd, 128, SectorSize);
        PutBothEndian32(pvd, 132, 10);
        PutLe32(pvd, 140, PathTableSector);
        PutBe32(pvd, 148, MirrorPathTableSector);
        WriteIsoDirectoryRecord(pvd, 156, RootDirectorySector, rootLength, isDirectory: true, new byte[] { 0 }, now);
        FillBytes(pvd, 190, 128, (byte)' ');
        FillBytes(pvd, 318, 128, (byte)' ');
        FillBytes(pvd, 446, 128, (byte)' ');
        FillBytes(pvd, 574, 128, (byte)' ');
        FillBytes(pvd, 702, 37, (byte)' ');
        FillBytes(pvd, 739, 37, (byte)' ');
        FillBytes(pvd, 776, 37, (byte)' ');
        PutIsoVolumeDate(pvd, 813, now);
        PutIsoVolumeDate(pvd, 830, now);
        pvd[881] = 1;
        var terminator = new byte[SectorSize];
        terminator[0] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(terminator, 1);
        terminator[6] = 1;

        Array.Copy(pvd, 0, image, 16 * SectorSize, SectorSize);
        Array.Copy(terminator, 0, image, 17 * SectorSize, SectorSize);
        Array.Copy(pathTable, 0, image, PathTableSector * SectorSize, SectorSize);
        Array.Copy(mirrorPathTable, 0, image, MirrorPathTableSector * SectorSize, SectorSize);
        Array.Copy(root, 0, image, RootDirectorySector * SectorSize, SectorSize);
        Array.Copy(readme, 0, image, ReadmeSector * SectorSize, readme.Length);

        File.WriteAllBytes(output, image);
        return null;
    }
    catch (Exception ex)
    {
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}

/// <summary>写一条 ECMA-119 目录记录（固定部分 33 字节 + 文件名，总长必须是偶数）。</summary>
static int WriteIsoDirectoryRecord(byte[] target, int offset, uint extent, uint size, bool isDirectory, byte[] identifier, DateTime now)
{
    var length = 33 + identifier.Length;
    if (length % 2 != 0)
    {
        length++;
    }

    target[offset] = (byte)length;
    PutBothEndian32(target, offset + 2, extent);
    PutBothEndian32(target, offset + 10, size);
    target[offset + 18] = (byte)(now.Year - 1900);
    target[offset + 19] = (byte)now.Month;
    target[offset + 20] = (byte)now.Day;
    target[offset + 21] = (byte)now.Hour;
    target[offset + 22] = (byte)now.Minute;
    target[offset + 23] = (byte)now.Second;
    target[offset + 25] = (byte)(isDirectory ? 2 : 0);
    PutBothEndian16(target, offset + 28, 1);
    target[offset + 32] = (byte)identifier.Length;
    Array.Copy(identifier, 0, target, offset + 33, identifier.Length);
    return offset + length;
}

static void FillBytes(byte[] target, int offset, int count, byte value)
{
    for (var i = 0; i < count; i++)
    {
        target[offset + i] = value;
    }
}

static void PutIsoVolumeDate(byte[] target, int offset, DateTime now)
{
    Encoding.ASCII.GetBytes(now.ToString("yyyyMMddHHmmss")).CopyTo(target, offset);
    target[offset + 14] = (byte)'0';
    target[offset + 15] = (byte)'0';
    target[offset + 16] = 0;
}

static void PutLe16(byte[] target, int offset, int value)
{
    target[offset] = (byte)(value & 0xFF);
    target[offset + 1] = (byte)((value >> 8) & 0xFF);
}

static void PutBe16(byte[] target, int offset, int value)
{
    target[offset] = (byte)((value >> 8) & 0xFF);
    target[offset + 1] = (byte)(value & 0xFF);
}

static void PutBothEndian16(byte[] target, int offset, int value)
{
    PutLe16(target, offset, value);
    PutBe16(target, offset + 2, value);
}

static void PutLe32(byte[] target, int offset, long value)
{
    target[offset] = (byte)(value & 0xFF);
    target[offset + 1] = (byte)((value >> 8) & 0xFF);
    target[offset + 2] = (byte)((value >> 16) & 0xFF);
    target[offset + 3] = (byte)((value >> 24) & 0xFF);
}

static void PutBe32(byte[] target, int offset, long value)
{
    target[offset] = (byte)((value >> 24) & 0xFF);
    target[offset + 1] = (byte)((value >> 16) & 0xFF);
    target[offset + 2] = (byte)((value >> 8) & 0xFF);
    target[offset + 3] = (byte)(value & 0xFF);
}

static void PutBothEndian32(byte[] target, int offset, long value)
{
    PutLe32(target, offset, value);
    PutBe32(target, offset + 4, value);
}

static string? FindSevenZip()
{
    foreach (var candidate in new[]
    {
        @"C:\Users\xingjian\scoop\shims\7z.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
    })
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

static (int ExitCode, string Output) Run(string exe, string arguments)
{
    var process = Process.Start(new ProcessStartInfo(exe, arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    })!;

    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output);
}
