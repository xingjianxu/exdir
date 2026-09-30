using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Exdir.Helpers;
using Exdir.Models;

namespace Exdir.Services;

/// <summary>应用设置的读写。</summary>
public interface ISettingsService
{
    /// <summary>当前设置对象。修改后调用 <see cref="Save"/> 落盘。</summary>
    AppSettings Current { get; }

    /// <summary>设置文件所在目录（<c>%LOCALAPPDATA%\exdir</c>）。</summary>
    string DataDirectory { get; }

    void Load();

    void Save();
}

/// <inheritdoc cref="ISettingsService" />
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 元数据走源生成（见 SettingsJsonContext）：裁剪过的发布版不能靠反射拿属性
        TypeInfoResolver = SettingsJsonContext.Default,
    };

    /// <summary>
    /// 预先取好的类型元数据。用 <c>JsonSerializer.Serialize(object, JsonTypeInfo)</c> 而不是
    /// 泛型重载，是为了让 IL2026/IL3050（“裁剪/ AOT 下可能坏”的告警）真的消失 ——
    /// 泛型重载的“要求未裁剪代码”标记与 TypeInfoResolver 无关，一律会报。
    /// </summary>
    private static readonly JsonTypeInfo<AppSettings> SettingsTypeInfo =
        (JsonTypeInfo<AppSettings>)SerializerOptions.GetTypeInfo(typeof(AppSettings));

    private readonly object _gate = new();

    public SettingsService()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "exdir");

        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public string DataDirectory { get; }

    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    Current = new AppSettings();
                    return;
                }

                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, SettingsTypeInfo) ?? new AppSettings();
                Migrate(loaded);
                Current = loaded;
            }
            catch (Exception ex)
            {
                // 设置损坏时回退到默认值，不阻塞启动；但要在日志里留一笔，
                // 否则“设置读不出来”会被当成“用户没改过”（裁剪/序列化出问题时就是这种表现）
                Diagnostics.Log.Exception("读取 settings.json", ex);
                Current = new AppSettings();
            }
        }
    }

    /// <summary>把旧版本的设置补成当前结构（只动缺失的部分，不覆盖用户已有设置）。</summary>
    private static void Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion < 2)
        {
            // v1 的列宽只有 名称/修改日期/类型/大小；v2 在最前面多了一个“状态”列。
            // 不补上的话用户拖过的列宽会整体错位（名称的宽度跑到状态列上）。
            if (settings.ColumnWidths.Count == ColumnLayout.ColumnCount - 1)
            {
                settings.ColumnWidths.Insert(0, ColumnLayout.DefaultSyncStateWidth);
            }
            else
            {
                settings.ColumnWidths.Clear();
            }
        }

        if (settings.SchemaVersion < 3)
        {
            // v3 开始把“用户是否自己配过固定目录”单独记账：
            // 老设置里只要有值就算已配置，否则（空列表）当成“从未配置”补默认值。
            settings.PinnedFoldersInitialized = settings.PinnedFolders.Count > 0;
        }

        // v4 新增“系统右键菜单”的两份清单（已知项 / 被关掉的项），默认都是空：
        // 空 = 全部开启，打开设置页时会用样本目标把清单填满，不需要迁移旧数据。

        // v5 新增侧边栏四个分组的显示开关（SidebarShowHome / Favorites / Cloud / Computer）：
        // 默认全部显示；旧设置里没有这些字段，反序列化会保留属性初始值 true，同样不需要迁移。

        // v6 新增「主目录」分组里六个标准文件夹（桌面 / 文档 / 下载 / 图片 / 音乐 / 视频）的显示开关：
        // 默认只开「桌面」与「下载」；旧设置里没有这些字段，反序列化会保留属性初始值（Desktop / Downloads = true），
        // 同样不需要迁移。

        // v7 新增主题（Theme = 跟随系统 / 浅色 / 深色）：
        // 旧设置里没有这个字段，反序列化会保留属性初始值 System（= 现在的行为），同样不需要迁移。

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var json = JsonSerializer.Serialize(Current, SettingsTypeInfo);

                // 先写临时文件再替换，避免写入过程中崩溃导致设置丢失
                var temp = SettingsFilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, SettingsFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // 保存失败不影响使用，但不能静默：设置没落盘/落盘不全是真会丢数据的
                Diagnostics.Log.Exception("写入 settings.json", ex);
            }
        }
    }
}
