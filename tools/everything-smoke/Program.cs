using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;
using Exdir.Services.Native;

// 控制台按 UTF-8 输出，否则中文在 936 码页的控制台里是乱码（不影响 PASS / FAIL）
Console.OutputEncoding = System.Text.Encoding.UTF8;

// 「基于 Everything 的快速搜索」的服务级冒烟测试（不需要交互桌面）。
//
// 分四段：
//   1) 查询串拼接（EverythingQuery）—— 纯函数，永远能跑；
//   2) DLL 加载 + 真实查询（EverythingInterop）—— 需要本机 Everything 在运行、且索引了 --root；
//   3) 结果映射 / 隐藏文件过滤（EverythingSearchService）—— 跑通第 2 段才有意义；
//   4) 索引覆盖检查（IsDirectoryIndexedAsync）—— 界面用它区分“索引没覆盖这个目录”与“真的没有匹配”。
//
// 查询串一拼错，表现不是“报错”而是“一个结果都没有”，所以第 1 段的断言写得很死（连转义都断言）。
// 第 2/3 段跑不起来时 SKIP 并打印怎么修（没装 Everything 的机器上也能跑完不报错）。
//
// 跑法：dotnet run -c Debug --project tools\everything-smoke
//       dotnet run -c Debug --project tools\everything-smoke -- --root D:\some\indexed\dir
// --root 指向一个 **Everything 已经索引**的目录（默认用临时目录）。
// 本机没装 / 没运行 Everything、或那个目录没被索引时，真实查询那几段会 SKIP 并打印怎么修。

var failures = 0;
var skipped = 0;

void Assert(bool condition, string message)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
    if (!condition)
    {
        failures++;
    }
}

void Skip(string message)
{
    Console.WriteLine("SKIP " + message);
    skipped++;
}

void Finish()
{
    Console.WriteLine();
    Console.WriteLine(skipped == 0
        ? (failures == 0 ? "全部通过" : $"有 {failures} 项失败")
        : $"有 {failures} 项失败，{skipped} 段 SKIP（未验证）");
}

// 0 = 全过；1 = 有断言失败；2 = 有 SKIP（“服务链路没完整验证”，test-search.ps1 拿它当预检门槛）
int ExitCode() => failures > 0 ? 1 : (skipped > 0 ? 2 : 0);

// ---------------------------------------------------------------- 1) 查询串拼接

Console.WriteLine("=== 1) EverythingQuery ===");

// 结尾必须补反斜杠：不然 path:"D:\a\b" 也会匹配到 D:\a\bc 里的东西（实测 1.4.1.1032）
Assert(EverythingQuery.Build("abc", @"D:\a\b") == "path:\"D:\\a\\b\\\" abc", "当前目录范围：拼出 path:\"<目录>\\\" + 关键字");
Assert(EverythingQuery.Build("abc", null) == "abc", "整机范围：不加任何限定词");
Assert(EverythingQuery.Build("abc", "") == "abc", "空目录 = 整机范围");
Assert(EverythingQuery.Build("abc", @"D:\") == "path:\"D:\\\" abc", "盘根：TrimEnd 之后补回反斜杠仍是 D:\\");
Assert(EverythingQuery.Build("abc", @"D:\a\b\") == "path:\"D:\\a\\b\\\" abc", "目录自带尾反斜杠不会拼成两个");
Assert(EverythingQuery.Build("abc", "\"D:\\a\\b\"") == "path:\"D:\\a\\b\\\" abc", "目录两头带引号会被去掉");
Assert(EverythingQuery.Build("  abc  ", @"D:\a") == "path:\"D:\\a\\\" abc", "关键字两头空白会被去掉");
Assert(EverythingQuery.Build("", @"D:\a") == "path:\"D:\\a\\\"", "空关键字 = 只有范围限定词（列该目录全部后代）");
Assert(EverythingQuery.Build("a\"b", null) == "a\"\"b", "关键字里的双引号用两个双引号转义");
Assert(EverythingQuery.Build("a\"b", @"D:\x") == "path:\"D:\\x\\\" a\"\"b", "转义在范围词之后也生效");

Console.WriteLine("  示例：" + EverythingQuery.Build("report", @"D:\prj\exdir"));

// ---------------------------------------------------------------- 2) DLL 加载 + 真实查询

Console.WriteLine();
Console.WriteLine("=== 2) EverythingInterop ===");

if (!EverythingInterop.EnsureLoaded(out var failure))
{
    Skip("本机用不了 Everything：" + failure);
    Skip("装一份 Everything（https://www.voidtools.com/）并让它保持运行，然后重跑本测试");
    Finish();
    return ExitCode();
}

Assert(true, $"{EverythingLocator.DllName} 加载成功：{EverythingLocator.DllPath}");
Console.WriteLine($"  Everything 主版本：{EverythingInterop.MajorVersion}");

// --root：要在一个 Everything **已经索引**的目录里造测试文件
var root = Path.GetTempPath();
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--root")
    {
        root = args[i + 1];
    }
}

root = Path.GetFullPath(root);
if (!Directory.Exists(root))
{
    Skip($"--root 不存在：{root}");
    Finish();
    return ExitCode();
}

// 造两个**前缀相同**的目录：用来验证范围词结尾必须有反斜杠（否则会把兄弟目录也算进来）
var token = "exdirsmoke" + Guid.NewGuid().ToString("N")[..8];
var fixture = Path.Combine(root, token);
var sibling = Path.Combine(root, token + "2");
const string Needle = "needleneedle";

// 夹具里有 4 个名字含关键字的文件：root / sub / sub\deep / hidden
const int FixtureHitCount = 4;

// 文件夹索引是异步的：等它扫完的上限（次数 × 500 ms = 60 秒）
const int MaxIndexWaitAttempts = 120;

try
{
    Directory.CreateDirectory(Path.Combine(fixture, "sub", "deep"));
    Directory.CreateDirectory(sibling);

    File.WriteAllText(Path.Combine(fixture, $"{Needle}-root.txt"), "root");
    File.WriteAllText(Path.Combine(fixture, "sub", $"{Needle}-sub.txt"), "sub");
    File.WriteAllText(Path.Combine(fixture, "sub", "deep", $"{Needle}-deep.bin"), new string('x', 1234));
    File.WriteAllText(Path.Combine(sibling, $"{Needle}-sibling.txt"), "sibling");

    // 隐藏文件：验证“显示隐藏文件”关掉时会被滤掉
    var hiddenPath = Path.Combine(fixture, $"{Needle}-hidden.txt");
    File.WriteAllText(hiddenPath, "hidden");
    File.SetAttributes(hiddenPath, FileAttributes.Hidden);

    // Everything 的文件夹索引是异步的，而且要等它把整个夹具扫完（扫到一半时结果数会抖动），
    // 所以等的是“连续两次都正好是 4 项”而不是“至少有一项”
    Console.WriteLine($"  夹具目录：{fixture}");
    Console.Write("  等待 Everything 把夹具目录扫完");

    var probe = EverythingQuery.Build(Needle, fixture);
    var lastError = 0u;
    var ready = false;
    var stableHits = 0;

    for (var attempt = 0; attempt < MaxIndexWaitAttempts && !ready; attempt++)
    {
        Thread.Sleep(500);
        Console.Write('.');

        var attemptResult = EverythingInterop.Query(probe, 100);
        lastError = attemptResult.Error;

        stableHits = attemptResult.Hits.Count == FixtureHitCount ? stableHits + 1 : 0;
        ready = stableHits >= 2;
    }

    Console.WriteLine();

    if (lastError == EverythingInterop.ErrorIpc)
    {
        Skip("Everything 客户端没有在运行（SDK 是 IPC 客户端，必须有客户端进程）");
        Skip("启动 Everything 之后重跑本测试");
        Finish();
        return ExitCode();
    }

    if (!ready)
    {
        Skip($"Everything 没有扫到夹具目录（{MaxIndexWaitAttempts / 2} 秒内没稳定在 {FixtureHitCount} 项）："
            + $"把 {root} 加进 Everything 的「工具 → 选项 → 索引 → 文件夹」，或用 --root 指向一个已索引的目录");
        Skip("真实查询 / 结果映射两段整体跳过");
        Finish();
        return ExitCode();
    }

    var raw = EverythingInterop.Query(EverythingQuery.Build(Needle, fixture), 100);

    Assert(raw.Available && raw.Ok, $"查询成功（错误码 {raw.Error}）");
    Assert(raw.Hits.Count == 4, $"范围词结尾的反斜杠挡住了前缀相同的兄弟目录：命中 {raw.Hits.Count} 项（期望 4：root / sub / sub\\deep / hidden 四个文件）");
    Assert(raw.TotalCount == 4, $"Everything 报的匹配总数 = 4（实际 {raw.TotalCount}）");
    Assert(raw.Hits.All(h => h.FullPath.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "所有命中的完整路径都在夹具目录下");

    var deepHit = raw.Hits.FirstOrDefault(h => h.FullPath.EndsWith("deep.bin", StringComparison.OrdinalIgnoreCase));
    Assert(deepHit.FullPath is not null, "递归找到了 sub\\deep 里的文件");
    Assert(deepHit.Size == 1234, $"文件大小读到了（期望 1234，实际 {deepHit.Size}）");
    Assert(deepHit.LastWriteTime != default && Math.Abs((DateTime.UtcNow - deepHit.LastWriteTime).TotalMinutes) < 30, $"修改时间读到了（{deepHit.LastWriteTime:u}）");
    Assert(!deepHit.IsDirectory, "文件没有 Directory 属性位");

    // 只有范围限定词的查询 = 列该目录下全部后代（含目录本身）
    var scopeOnly = EverythingInterop.Query(EverythingQuery.BuildScope(fixture), 100);
    Assert(scopeOnly.Hits.Count >= 6, $"只有范围词的查询列出整棵子树（命中 {scopeOnly.Hits.Count} 项，期望 ≥ 6）");
    Assert(scopeOnly.Hits.Any(h => h.IsDirectory && h.FullPath.EndsWith("sub", StringComparison.OrdinalIgnoreCase)), "结果里的目录带 Directory 属性位");
    Assert(scopeOnly.Hits.All(h => !h.FullPath.Equals(fixture, StringComparison.OrdinalIgnoreCase)), "范围目录自身不算结果（结尾反斜杠的副作用，符合预期）");

    var attributesAvailable = raw.AttributesAvailable;
    Assert(attributesAvailable, "读了文件属性（隐藏文件过滤要用它；为 false 时下面两项会 SKIP）");
    Console.WriteLine($"  属性可用：{attributesAvailable}");

    // ------------------------------------------------------------ 3) EverythingSearchService

    Console.WriteLine();
    Console.WriteLine("=== 3) EverythingSearchService ===");

    var service = new EverythingSearchService(new StubArchiveService());
    Assert(service.IsAvailable, "IsAvailable = true");

    var withHidden = await service.SearchAsync(Needle, fixture, includeHidden: true, EverythingQuery.MaxResults);
    Assert(withHidden.Status == EverythingSearchStatus.Ok, $"有结果时 Status = Ok（实际 {withHidden.Status}）");
    Assert(withHidden.Entries.Count == 4, $"包含隐藏文件时 4 项（实际 {withHidden.Entries.Count}）");
    Assert(withHidden.Entries.All(e => e.Name.Length > 0 && e.FullPath.EndsWith(e.Name, StringComparison.Ordinal)), "每个条目的 Name 与 FullPath 一致");
    Assert(withHidden.Entries.Any(e => e.IsDirectory) == false, "夹具里没有名字含关键字的目录，所以结果全是文件");

    if (attributesAvailable)
    {
        Assert(withHidden.Entries.Any(e => e.IsHidden), "隐藏文件被标成 IsHidden");

        var withoutHidden = await service.SearchAsync(Needle, fixture, includeHidden: false, EverythingQuery.MaxResults);
        Assert(withoutHidden.Entries.Count == 3, $"隐藏文件关掉时 3 项（实际 {withoutHidden.Entries.Count}）");
        Assert(withoutHidden.Entries.All(e => !e.IsHidden), "结果里没有隐藏项");
        Assert(withoutHidden.TotalCount >= (uint)withoutHidden.Entries.Count, "TotalCount 是 Everything 的总数（可以大于过滤后的条数）");
    }
    else
    {
        Skip("Everything 没把文件属性报回来，“显示隐藏文件”的过滤无法验证");
    }

    var missing = await service.SearchAsync("definitelynotpresent" + token, fixture, includeHidden: true, EverythingQuery.MaxResults);
    Assert(missing.Status == EverythingSearchStatus.NoResults, $"查不到时 Status = NoResults（实际 {missing.Status}）");
    Assert(missing.Entries.Count == 0, "查不到时结果为空");

    var machine = await service.SearchAsync(Needle, null, includeHidden: true, EverythingQuery.MaxResults);
    Assert(machine.Status == EverythingSearchStatus.Ok && machine.Entries.Count >= 4, $"整机范围（directory = null）同样能搜到（{machine.Entries.Count} 项）");

    var truncated = await service.SearchAsync(Needle, null, includeHidden: true, maxResults: 2);
    Assert(truncated.Entries.Count == 2, $"maxResults=2 时只取回 2 条（实际 {truncated.Entries.Count}）");
    Assert(truncated.IsTruncated, "取回的比总数少时 IsTruncated = true");

    // ------------------------------------------------------------ 4) 索引覆盖检查
    // 界面靠它把“当前目录一个都没搜到”拆成“索引没覆盖这个目录”与“真的没有匹配”。

    Console.WriteLine();
    Console.WriteLine("=== 4) 索引覆盖检查（IsDirectoryIndexedAsync）===");

    Assert(await service.IsDirectoryIndexedAsync(fixture), "索引里有夹具目录 → true");

    var ghost = Path.Combine(root, "no-such-dir-" + token);
    Assert(!await service.IsDirectoryIndexedAsync(ghost), $"索引里没有这个目录 → false（{ghost}）");
    Assert(!await service.IsDirectoryIndexedAsync(string.Empty), "空路径 → false（不去查）");
}
finally
{
    try
    {
        // 隐藏文件要先清掉属性，否则 Delete 一般也能删（保险起见）
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(fixture, true);
        Directory.Delete(sibling, true);
    }
    catch (Exception ex)
    {
        Console.WriteLine("  清理夹具失败：" + ex.Message);
    }
}

Finish();
return ExitCode();

/// <summary>
/// 冒烟测试只用到 <c>IArchiveService.IsArchiveFile</c>（给搜索结果标“这是压缩包”），
/// 其余成员一律不实现 —— 这样不必把整套压缩包服务（还要带 7z.dll）拖进这个小工程。
/// </summary>
file sealed class StubArchiveService : IArchiveService
{
    public bool IsAvailable => false;

    public string AvailabilityFailure => "冒烟测试里没有压缩包服务";

    public bool IsArchiveFile(string? path) => false;

    public bool TryParse(string? path, out ArchivePath location)
    {
        location = null!;
        return false;
    }

    public bool IsInsideArchive(string? path) => false;

    public Task<IReadOnlyList<FileSystemEntry>> ListAsync(ArchivePath location, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DirectoryExistsAsync(ArchivePath location, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> ExtractToTempAsync(ArchivePath file, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ArchiveExtraction> ExtractForCopyAsync(string archiveFile, IReadOnlyList<string> innerPaths, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ArchiveExtraction> ExtractForDragAsync(string archiveFile, IReadOnlyList<string> innerPaths, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void ReleaseStaging(string stagingDirectory)
    {
    }

    public void ReleaseStagingFor(IReadOnlyList<string> paths)
    {
    }

    public void Invalidate(string? archiveFile)
    {
    }

    public void SetPassword(string? archiveFile, string? password)
    {
    }

    public void CleanupTemp()
    {
    }

    public Task<int> ExtractAllAsync(string archiveFile, string destinationDirectory, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public event EventHandler<string>? Extracted
    {
        add { }
        remove { }
    }
}
