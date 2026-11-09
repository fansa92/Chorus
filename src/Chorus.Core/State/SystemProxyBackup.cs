using System.Text.Json;

namespace Chorus.Core.State;

/// <summary>
/// 开启系统代理之前，用户原本的 IE/WinINET 代理设置。
/// Enable 会覆盖 ProxyServer / ProxyOverride 并删掉 AutoConfigURL，
/// 关掉时只把 ProxyEnable 置 0 是不够的：用户自己配的公司代理或 PAC 就找不回来了。
/// </summary>
public sealed class SystemProxyBackup
{
    public DateTimeOffset CapturedAt { get; set; }
    public int Port { get; set; }
    public int? ProxyEnable { get; set; }
    public string? ProxyServer { get; set; }
    public string? ProxyOverride { get; set; }
    public string? AutoConfigUrl { get; set; }

    /// <summary>本应用写进去的代理地址，用来判断「现在这套设置还是不是我们设的」。</summary>
    public string OwnServer => $"127.0.0.1:{Port}";
}

/// <summary>代理原值快照的读写。路径可注入，单测用临时文件即可。</summary>
public sealed class SystemProxyBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public SystemProxyBackupStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public void Save(SystemProxyBackup backup)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(backup, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    public SystemProxyBackup? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<SystemProxyBackup>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            // 快照损坏就当没有，退化成「只关开关」
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 删不掉不影响功能，下次 Enable 会覆盖
        }
    }
}
