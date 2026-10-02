using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Exdir.Models;
using Exdir.Services;

// ArchiveService + 7z.dll 互操作的服务级冒烟测试（不需要交互桌面）。
//
// 直接把 app 里那几个源文件编进来跑真实实现（见 archive-smoke.csproj），覆盖：
// 格式表 / 7z.dll 加载 / 虚拟路径解析（含 .. 与盘符冒号的拒绝、嵌套压缩包）/ 枚举与隐式目录 /
// 目录存在性 / 解到临时目录（含深层目录与链式 tar）/ 加密包（列表不要密码、取文件要密码）/
// 损坏包 / 索引缓存与 Invalidate / 取消 / CleanupTemp。
//
// 跑法：dotnet run -c Debug --project tools\archive-smoke
// 交互部分（双击进包、只读守卫、真鼠标）在 tools\test-archive.ps1 里，那个需要交互桌面。

var failures = 0;

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

        service.SetPassword(encryptedZip, null);
        service.Invalidate(encryptedZip);
    }
    else
    {
        Console.WriteLine("SKIP 没找到可用的 7z.exe，跳过加密压缩包的用例");
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

    Console.WriteLine($"SUMMARY failures={failures}");
    return failures == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(work, true); } catch { }
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
