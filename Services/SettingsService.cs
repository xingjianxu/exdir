using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    };

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
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
                Migrate(loaded);
                Current = loaded;
            }
            catch (Exception)
            {
                // 设置损坏时回退到默认值，不阻塞启动
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

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var json = JsonSerializer.Serialize(Current, SerializerOptions);

                // 先写临时文件再替换，避免写入过程中崩溃导致设置丢失
                var temp = SettingsFilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, SettingsFilePath, overwrite: true);
            }
            catch (Exception)
            {
                // 保存失败不影响使用
            }
        }
    }
}
