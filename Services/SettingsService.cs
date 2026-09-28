using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
                Current.SchemaVersion = AppSettings.CurrentSchemaVersion;
            }
            catch (Exception)
            {
                // 设置损坏时回退到默认值，不阻塞启动
                Current = new AppSettings();
            }
        }
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
