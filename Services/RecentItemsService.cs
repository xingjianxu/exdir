using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>
/// 侧边栏「最新访问」列表的读写（最近访问过的目录与文件，最前面是最新的一次）。
///
/// 单独一个文件（<c>%USERPROFILE%\.local\share\exdir\recents.json</c>）而不是塞进 config.json：
/// 它是使用痕迹、导航 / 开文件一次就可能变一次，和用户显式配置分开互不干扰，用户想清空直接删文件也行。
/// 界面上的入口是侧边栏那一个节点（点开一个虚拟标签页按顺序列出这些条目）。
/// </summary>
public interface IRecentItemsService
{
    /// <summary>最近访问过的目录与文件（最前面的是最新的一次），只读快照。</summary>
    IReadOnlyList<RecentEntry> Entries { get; }

    /// <summary>列表最多保留的条数，超出后挤掉最旧的一条。</summary>
    int MaxEntries { get; }

    /// <summary>
    /// 记一次访问：已存在就提到最前（大小写不敏感，类型以这次为准），不存在的插到最前。
    /// </summary>
    /// <param name="path">访问到的路径。</param>
    /// <param name="isDirectory">它是目录（true）还是文件（false）。</param>
    void Add(string path, bool isDirectory);

    /// <summary>清空整个列表（侧边栏右键「清空最新访问」）。</summary>
    void Clear();

    /// <summary>列表内容有变化时触发（打开着的「最新访问」标签页据此重读一遍）。</summary>
    event EventHandler? Changed;
}

/// <inheritdoc cref="IRecentItemsService" />
public sealed class RecentItemsService : IRecentItemsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // 元数据走源生成（见 RecentItemsJsonContext）：裁剪过的发布版不能靠反射拿属性
        TypeInfoResolver = RecentItemsJsonContext.Default,
    };

    /// <summary>
    /// 预先取好的类型元数据。用 <c>JsonSerializer.Serialize(object, JsonTypeInfo)</c> 而不是泛型重载，
    /// 让 IL2026/IL3050（“裁剪 / AOT 下可能坏”）的告警真的消失（同 <see cref="SettingsService" />）。
    /// </summary>
    private static readonly JsonTypeInfo<RecentItems> TypeInfo =
        (JsonTypeInfo<RecentItems>)SerializerOptions.GetTypeInfo(typeof(RecentItems));

    /// <summary>
    /// 列表最多保留多少条。列表本身不再挤在侧边栏里，而是单独一个标签页，所以可以放宽一些
    /// （翻找“刚才那个文件”时几十条刚好够用）。
    /// </summary>
    public const int DefaultMaxEntries = 50;

    /// <summary>落盘文件名。</summary>
    public const string FileName = "recents.json";

    private readonly object _gate = new();
    private readonly List<RecentEntry> _entries = new();

    public RecentItemsService()
    {
        DataDirectory = ResolveDataDirectory();
        FilePath = Path.Combine(DataDirectory, FileName);

        Load();
    }

    public IReadOnlyList<RecentEntry> Entries => _entries;

    public int MaxEntries => DefaultMaxEntries;

    /// <summary>数据文件所在目录（<c>%USERPROFILE%\.local\share\exdir</c>；设了 <c>XDG_DATA_HOME</c> 时以它为准）。</summary>
    public string DataDirectory { get; }

    /// <summary>数据文件完整路径（<c>recents.json</c>）。</summary>
    public string FilePath { get; }

    public event EventHandler? Changed;

    public void Add(string path, bool isDirectory)
    {
        path = path?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var at = IndexOf(path);

            // 已经在最前面、类型也没变：什么都不用做（否则每导航一次就写一次盘）
            if (at == 0 && _entries[0].IsDirectory == isDirectory)
            {
                return;
            }

            if (at > 0)
            {
                _entries.RemoveAt(at);
            }

            var entry = new RecentEntry { Path = path, IsDirectory = isDirectory };

            if (at == 0)
            {
                _entries[0] = entry;
            }
            else
            {
                _entries.Insert(0, entry);
            }

            if (_entries.Count > DefaultMaxEntries)
            {
                _entries.RemoveRange(DefaultMaxEntries, _entries.Count - DefaultMaxEntries);
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private int IndexOf(string path)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (EqualsPath(_entries[i].Path, path))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>路径比较：忽略大小写与末尾分隔符（<c>C:\</c> 与 <c>C:</c> 是同一个位置）。</summary>
    private static bool EqualsPath(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 数据目录：默认放用户主目录下的 <c>.local\share\exdir</c>；设了 <c>XDG_DATA_HOME</c>（且是绝对路径）时用它
    /// —— 那个环境变量的语义就是“用户数据根目录”，与 <see cref="SettingsService" /> 用 <c>XDG_CONFIG_HOME</c> 对配置目录同理。
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");

        return Path.Combine(root, "exdir");
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize(json, TypeInfo) ?? new RecentItems();

                _entries.Clear();

                // 旧格式（只有目录）：按原顺序（最前面最新）迁成条目
                foreach (var path in loaded.Folders ?? new List<string>())
                {
                    AddLoaded(path, isDirectory: true);
                }

                foreach (var entry in loaded.Entries)
                {
                    AddLoaded(entry.Path, entry.IsDirectory);
                }
            }
            catch (Exception ex)
            {
                // 列表读不出来不影响使用（最多是「最新访问」空着），但不能静默：它多半是文件被写坏了
                Diagnostics.Log.Exception("读取 recents.json", ex);
                _entries.Clear();
            }
        }
    }

    /// <summary>读盘时补一条（去重、截断）；必须在 <see cref="_gate" /> 里调用。</summary>
    private void AddLoaded(string? path, bool isDirectory)
    {
        if (_entries.Count >= DefaultMaxEntries)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path) || IndexOf(path.Trim()) >= 0)
        {
            return;
        }

        _entries.Add(new RecentEntry { Path = path.Trim(), IsDirectory = isDirectory });
    }

    /// <summary>必须在 <see cref="_gate" /> 里调用。</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);

            var json = JsonSerializer.Serialize(new RecentItems { Entries = _entries.ToList() }, TypeInfo);

            // 先写临时文件再替换，避免写入过程中崩溃把列表弄坏（同 SettingsService）
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 保存失败不影响使用，但不能静默
            Diagnostics.Log.Exception("写入 recents.json", ex);
        }
    }
}
