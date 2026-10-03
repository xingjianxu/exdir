using System.Text;
using Exdir.Helpers;
using Exdir.Models;
using Exdir.Services;

// 远程位置（SFTP / FTP）的服务级冒烟测试（不需要交互桌面）：
// 把 app 里那几个源文件编进来跑真实实现，协议那一头是本机的 tools\remote-test-server（node）。
//
// 覆盖：路径解析 / 规整 / 父目录 / 面包屑分段、DPAPI 凭据往返、FTP 与 SFTP（密码 / 私钥）的
// 连接 + 列目录 + 存在性判断 + 递归下载 + 错误路径、中转目录的回收。
//
// 跑法（先起服务器，见 tools\remote-test-server\server.js）：
//   dotnet run -c Debug --project tools\remote-smoke -- --ftp-port 2121 --sftp-port 2222 --root <目录> --key <客户端私钥>

var failures = 0;

void Assert(bool condition, string message)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
    if (!condition)
    {
        failures++;
    }
}

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        continue;
    }

    options[args[i][2..]] = args[++i];
}

// 小工具（回归脚本要用）：把一段明文字符串加密后打出来，好手写进测试用的 config.json。
// DPAPI 是“按用户”的，所以只能在目标用户的会话里现场生成。
if (options.TryGetValue("protect", out var toProtect))
{
    Console.WriteLine(SecretProtector.Protect(toProtect));
    return 0;
}

var ftpPort = options.TryGetValue("ftp-port", out var fp) ? int.Parse(fp) : 2121;
var sftpPort = options.TryGetValue("sftp-port", out var sp) ? int.Parse(sp) : 2222;
var user = options.TryGetValue("user", out var u) ? u : "testuser";
var password = options.TryGetValue("password", out var p) ? p : "testpass";
var privateKey = options.TryGetValue("key", out var k) ? k : string.Empty;

// ---------------------------------------------------------------- 1. 路径解析（纯字符串）

Console.WriteLine("== 路径解析 ==");

Assert(RemotePath.LooksRemote("sftp://me@host:22/a/b"), "sftp:// 被认成远程路径");
Assert(RemotePath.LooksRemote("ftp://host/pub"), "ftp:// 被认成远程路径");
Assert(!RemotePath.LooksRemote(@"C:\Users\me"), @"C:\ 不是远程路径");
Assert(!RemotePath.LooksRemote(@"\\server\share"), @"UNC 不是远程路径");
Assert(!RemotePath.LooksRemote(@"D:\dl\a.zip\inner"), "压缩包虚拟路径不是远程路径");
Assert(!RemotePath.LooksRemote(null), "null 不是远程路径");

Assert(RemotePath.TryParse("sftp://me@example.com/home/me/docs", out var parsed), "解析 sftp 路径");
Assert(parsed.Protocol == RemoteProtocol.Sftp, "协议 = SFTP");
Assert(parsed.UserName == "me", "用户名 = me");
Assert(parsed.Host == "example.com", "主机 = example.com");
Assert(parsed.Port == 22, "没写端口时用 22");

Assert(RemotePath.TryParse("ftps://bob@ftp.example.com:2121/pub/data/", out var ftps), "解析 ftps 路径");
Assert(ftps.Protocol == RemoteProtocol.Ftps, "协议 = FTPS");
Assert(ftps.Port == 2121, "端口 = 2121");
Assert(ftps.Path == "/pub/data", "末尾斜杠被去掉");

Assert(RemotePath.TryParse("sftp://[::1]/x", out var v6), "解析 IPv6 主机");
Assert(v6.Host == "::1", "IPv6 主机 = ::1");
Assert(RemotePath.TryParse("sftp://[::1]:2222/x", out var v6Port), "解析带端口的 IPv6 主机");
Assert(v6Port.Port == 2222, "IPv6 端口 = 2222");

Assert(!RemotePath.TryParse("sftp://host:0/x", out _), "端口 0 被拒绝");
Assert(!RemotePath.TryParse("sftp://host:99999/x", out _), "端口越界被拒绝");
Assert(!RemotePath.TryParse("http://host/x", out _), "不认识的协议被拒绝");
Assert(!RemotePath.TryParse("sftp:///x", out _), "没有主机名被拒绝");

Assert(RemotePath.NormalizePath("/a//b/./c/../d/") == "/a/b/d", "规整路径：去空段 / 点段 / 消解 ..");
Assert(RemotePath.NormalizePath("/../../x") == "/x", ".. 不会退到根之上");
Assert(RemotePath.NormalizePath(@"\a\b\") == "/a/b", "反斜杠统一成 /");

var built = RemotePath.Build(RemoteProtocol.Sftp, "me", "host", 22, "/a/b");
Assert(built == "sftp://me@host/a/b", $"默认端口不写进路径：{built}");
var builtPort = RemotePath.Build(RemoteProtocol.Ftp, "bob", "host", 2121, "/pub");
Assert(builtPort == "ftp://bob@host:2121/pub", $"非默认端口写进路径：{builtPort}");

Assert(RemotePath.GetParent("/a/b/c") == "/a/b", "父目录 = /a/b");
Assert(RemotePath.GetParent("/a") == "/", "单级目录的父目录是根");
Assert(RemotePath.GetParent("/") is null, "根目录没有父目录");

var segments = RemotePath.Segments(new RemotePathInfo(RemoteProtocol.Sftp, "me", "host", 22, "/home/me"));
Assert(segments.Count == 3, $"面包屑三段（user@host + home + me），实际 {segments.Count}");
Assert(segments[0].Display == "me@host", $"首段显示 user@host，实际 {segments[0].Display}");
Assert(segments[1].FullPath == "sftp://me@host/home", $"第二段完整路径，实际 {segments[1].FullPath}");
Assert(segments[2].FullPath == "sftp://me@host/home/me", "第三段完整路径");

// ---------------------------------------------------------------- 2. 凭据加密（DPAPI）

Console.WriteLine("== 凭据加密 ==");

var secret = "p@ssw0rd 中文 密码";
var protectedSecret = SecretProtector.Protect(secret);
Assert(protectedSecret.Length > 0, "加密结果非空");
Assert(!protectedSecret.Contains(secret, StringComparison.Ordinal), "密文里看不到明文");
Assert(SecretProtector.Unprotect(protectedSecret) == secret, "解密还原原样");
Assert(SecretProtector.Unprotect(string.Empty) == string.Empty, "空串 = 没保存密码");
Assert(SecretProtector.Unprotect("bm90LWEtZHBhcGktYmxvYg==") is null, "坏密文返回 null（要提示重新填写）");
Assert(SecretProtector.Protect(null) == string.Empty, "空密码不加密");

// ---------------------------------------------------------------- 3. 真实协议：FTP / SFTP

var root = options.TryGetValue("root", out var r) ? r : string.Empty;
if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
{
    Console.WriteLine("SKIP 没有 --root（跳过真实协议测试）");
}
else
{
    var ftpLocation = new RemoteLocation
    {
        Name = "本机 FTP",
        Protocol = RemoteProtocol.Ftp,
        Host = "127.0.0.1",
        Port = ftpPort,
        UserName = user,
        Auth = RemoteAuthMethod.Password,
        ProtectedPassword = SecretProtector.Protect(password),
        StartPath = "/",
    };

    var sftpLocation = new RemoteLocation
    {
        Name = "本机 SFTP",
        Protocol = RemoteProtocol.Sftp,
        Host = "127.0.0.1",
        Port = sftpPort,
        UserName = user,
        Auth = RemoteAuthMethod.Password,
        ProtectedPassword = SecretProtector.Protect(password),
        StartPath = "/",
    };

    var sftpKeyLocation = new RemoteLocation
    {
        Name = "本机 SFTP（私钥）",
        Protocol = RemoteProtocol.Sftp,
        Host = "127.0.0.1",
        Port = sftpPort,
        UserName = user,
        Auth = RemoteAuthMethod.PrivateKey,
        PrivateKeyPath = privateKey,
        StartPath = "/",
    };

    var source = new FixedLocationSource([ftpLocation, sftpLocation, sftpKeyLocation]);
    var service = new RemoteFileService(source);

    await ExerciseAsync("FTP", ftpLocation);
    await ExerciseAsync("SFTP", sftpLocation);

    if (string.IsNullOrEmpty(privateKey) || !File.Exists(privateKey))
    {
        Console.WriteLine("SKIP 没有 --key（跳过 SFTP 私钥登录）");
    }
    else
    {
        await ExerciseAsync("SFTP（私钥）", sftpKeyLocation);
    }

    // 没配置过的位置：明确报错，而不是猜一个连过去
    var unknown = new RemoteFileService(new FixedLocationSource([]));
    try
    {
        await unknown.ListAsync("sftp://nobody@nohost/", includeHidden: false);
        Assert(false, "没配置的远程位置应当报错");
    }
    catch (Exception ex)
    {
        Assert(ex.Message.Contains("没有这个远程位置的配置", StringComparison.Ordinal), $"没配置的位置报错清楚：{ex.Message}");
    }

    async Task ExerciseAsync(string label, RemoteLocation location)
    {
        Console.WriteLine($"== {label} ==");

        var rootPath = location.RootPath;

        // 列根目录
        var entries = await service.ListAsync(rootPath, includeHidden: true);
        var names = entries.Select(e => e.Name).ToList();
        Assert(names.Contains("hello.txt"), $"{label}: 列出 hello.txt（实际 {string.Join(",", names)}）");
        Assert(names.Contains("sub"), $"{label}: 列出目录 sub");
        Assert(names.Contains("目录 with space"), $"{label}: 列出带空格的中文目录名");
        Assert(entries.All(e => e.FullPath.StartsWith(location.DisplayTarget, StringComparison.Ordinal)), $"{label}: 条目路径都带协议头");
        Assert(entries.First(e => e.Name == "sub").IsDirectory, $"{label}: sub 被认成目录");
        Assert(!entries.First(e => e.Name == "hello.txt").IsDirectory, $"{label}: hello.txt 被认成文件");
        Assert(entries.First(e => e.Name == "hello.txt").Size > 0, $"{label}: hello.txt 有大小");
        Assert(entries.First(e => e.Name == "hello.txt").TypeName == "文本文档", $"{label}: 类型列按扩展名");

        // 隐藏文件：加一个点开头的文件（两侧都按 Unix 惯例当隐藏）
        var dotted = Path.Combine(root, ".hidden-test");
        File.WriteAllText(dotted, "hidden");
        var visible = await service.ListAsync(rootPath, includeHidden: false);
        var withHidden = await service.ListAsync(rootPath, includeHidden: true);
        Assert(!visible.Any(e => e.Name == ".hidden-test"), $"{label}: 不显示隐藏时不列 .hidden-test");
        Assert(withHidden.Any(e => e.Name == ".hidden-test"), $"{label}: 显示隐藏时列出 .hidden-test");
        File.Delete(dotted);

        // 存在性判断
        Assert(await service.ResolveDirectoryAsync(rootPath) == rootPath, $"{label}: 根目录存在");
        Assert(await service.ResolveDirectoryAsync(rootPath.TrimEnd('/') + "/sub") is not null, $"{label}: 子目录存在");
        Assert(await service.ResolveDirectoryAsync(rootPath.TrimEnd('/') + "/nope") is null, $"{label}: 不存在的目录返回 null");
        Assert(
            await service.ResolveDirectoryAsync(rootPath.TrimEnd('/') + "/hello.txt") == rootPath,
            $"{label}: 给文件路径返回它所在目录");

        // 父目录
        Assert(service.GetParent(rootPath.TrimEnd('/') + "/sub") == rootPath, $"{label}: 子目录的父目录是根");
        Assert(service.GetParent(rootPath) is null, $"{label}: 远程根没有父目录");

        // 只列子目录（侧边栏懒加载那条路）
        var directories = await service.ListDirectoriesAsync(rootPath, 100);
        Assert(directories.All(d => d.IsDirectory), $"{label}: ListDirectoriesAsync 只给目录");
        Assert(directories.Any(d => d.Name == "sub"), $"{label}: ListDirectoriesAsync 里有 sub");

        // 下载单个文件
        var downloadRoot = Path.Combine(Path.GetTempPath(), "exdir-remote-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(downloadRoot);

        try
        {
            var written = await service.DownloadAsync([rootPath.TrimEnd('/') + "/hello.txt"], downloadRoot);
            Assert(written.Count == 1, $"{label}: 下载一个文件");
            Assert(File.Exists(written[0]), $"{label}: 下载的文件真的落盘");
            Assert(File.ReadAllText(written[0]).StartsWith("hello", StringComparison.Ordinal), $"{label}: 下载的内容对");

            // 再下一次：同名加 (2)，不覆盖
            var again = await service.DownloadAsync([rootPath.TrimEnd('/') + "/hello.txt"], downloadRoot);
            Assert(Path.GetFileName(again[0]) == "hello (2).txt", $"{label}: 重名自动加 (2)（实际 {Path.GetFileName(again[0])}）");

            // 下载目录（递归，含子目录与空目录）
            var directory = await service.DownloadAsync([rootPath.TrimEnd('/') + "/sub"], downloadRoot);
            Assert(File.Exists(Path.Combine(directory[0], "inner.txt")), $"{label}: 目录递归下载到 inner.txt");
            Assert(File.Exists(Path.Combine(directory[0], "deep", "deep.txt")), $"{label}: 目录递归下载到 deep/deep.txt");

            var empty = await service.DownloadAsync([rootPath.TrimEnd('/') + "/empty"], downloadRoot);
            Assert(Directory.Exists(empty[0]), $"{label}: 空目录也被建出来");

            // 带空格的中文目录名
            var spaced = await service.DownloadAsync([rootPath.TrimEnd('/') + "/目录 with space"], downloadRoot);
            Assert(Directory.Exists(spaced[0]), $"{label}: 中文带空格的目录也下得下来");

            // 下载单个文件到指定路径（双击打开 / 拖拽中转那条路）+ 大小一致时复用
            var target = Path.Combine(downloadRoot, "single", "hello.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var info = new FileInfo(Path.Combine(root, "hello.txt"));

            await service.DownloadFileToAsync(rootPath.TrimEnd('/') + "/hello.txt", target, info.Length);
            Assert(File.Exists(target), $"{label}: DownloadFileToAsync 落盘");
            var stamp = File.GetLastWriteTimeUtc(target);
            await service.DownloadFileToAsync(rootPath.TrimEnd('/') + "/hello.txt", target, info.Length);
            Assert(File.GetLastWriteTimeUtc(target) == stamp, $"{label}: 大小一致时不重复下载");

            // 中转目录回收：交出去的路径在 drag\ 下时整棵删掉
            var staging = RemoteCache.NewStaging(RemoteCache.DragCategory);
            var staged = Path.Combine(staging, "x.txt");
            File.WriteAllText(staged, "x");
            service.ReleaseStagingFor([staged]);
            Assert(!Directory.Exists(staging), $"{label}: ReleaseStagingFor 回收了拖拽中转目录");

            service.ReleaseStaging(RemoteCache.NewStaging(RemoteCache.CopyCategory));
        }
        finally
        {
            try
            {
                Directory.Delete(downloadRoot, recursive: true);
            }
            catch (Exception)
            {
                // 清理不掉不影响结论
            }
        }

        // 密码错了要给出“登录失败”而不是一句没头没脑的异常
        var wrong = new RemoteLocation
        {
            Protocol = location.Protocol,
            Host = location.Host,
            Port = location.Port,
            UserName = location.UserName,
            Auth = RemoteAuthMethod.Password,
            ProtectedPassword = SecretProtector.Protect("definitely-wrong"),
            StartPath = "/",
        };

        var wrongService = new RemoteFileService(new FixedLocationSource([wrong]));
        try
        {
            await wrongService.ListAsync(wrong.RootPath, includeHidden: false);
            Assert(false, $"{label}: 错密码应当失败");
        }
        catch (Exception ex)
        {
            Assert(
                ex.Message.Contains("登录失败", StringComparison.Ordinal) || ex.Message.Contains("密码", StringComparison.Ordinal),
                $"{label}: 错密码的提示能看懂：{ex.Message}");
        }

        // 连不上的端口：提示里要有主机与端口
        var unreachable = new RemoteLocation
        {
            Protocol = location.Protocol,
            Host = "127.0.0.1",
            Port = 1,
            UserName = location.UserName,
            Auth = RemoteAuthMethod.Password,
            ProtectedPassword = SecretProtector.Protect(password),
        };

        var unreachableService = new RemoteFileService(new FixedLocationSource([unreachable]));
        try
        {
            await unreachableService.ListAsync(unreachable.RootPath, includeHidden: false);
            Assert(false, $"{label}: 连不上应当失败");
        }
        catch (Exception ex)
        {
            Assert(ex.Message.Contains("127.0.0.1", StringComparison.Ordinal), $"{label}: 连不上的提示带主机：{ex.Message}");
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过" : $"失败 {failures} 项");
return failures == 0 ? 0 : 1;

/// <summary>测试里用的固定连接清单。</summary>
internal sealed class FixedLocationSource(IReadOnlyList<RemoteLocation> locations) : IRemoteLocationSource
{
    public IReadOnlyList<RemoteLocation> Locations { get; } = locations;

    public RemoteLocation? Find(string connectionKey)
        => Locations.FirstOrDefault(l => string.Equals(l.ConnectionKey, connectionKey, StringComparison.Ordinal));
}
