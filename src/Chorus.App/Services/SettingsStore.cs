using System.Text.Json;
using System.Text.Json.Serialization;
using Chorus.Core.Configuration;
using Chorus.Core.Models;

namespace Chorus.App.Services;

/// <summary>应用设置持久化：%LOCALAPPDATA%\Chorus\settings.json。</summary>
public sealed class SettingsStore
{
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chorus");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SubscriptionEntryConverter() },
    };

    public AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时回退到默认值
        }
        // 手改 settings.json 可能写出非法值（大小写混用、拼错），这里归一到合法集合，
        // 否则 UI 的选中态会全部落空。归一化改过值就顺手落盘，让配置自愈。
        var before = Serialize(settings);
        ProxiesStyleOptions.Sanitize(settings);
        if (Serialize(settings) != before)
        {
            try { Save(settings); }
            catch { /* 落盘失败不致命：内存里已经是合法值，界面照常能用 */ }
        }
        return settings;
    }

    private static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }

    public static string SettingsFilePath => FilePath;
}
