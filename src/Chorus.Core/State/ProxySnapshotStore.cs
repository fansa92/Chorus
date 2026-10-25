using System.Text.Json;

namespace Chorus.Core.State;

/// <summary>
/// 上一次成功从 Clash API 读到的策略组快照。
/// 核心没在运行时用它填满策略组页（Bettbox 的核心随应用启动、配置常驻内存，
/// 所以未「启动代理」也能列组；Chorus 的核心是独立进程，只能靠落盘快照达到同样效果）。
/// </summary>
public sealed class ProxySnapshot
{
    public DateTimeOffset SavedAt { get; set; }
    /// <summary>快照来自哪份配置（订阅名或「全部订阅合并」）。</summary>
    public string Source { get; set; } = "";
    public int ProxyCount { get; set; }
    public List<ProxySnapshotGroup> Groups { get; set; } = [];
}

public sealed class ProxySnapshotGroup
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Now { get; set; } = "";
    public List<ProxySnapshotMember> Members { get; set; } = [];
}

public sealed class ProxySnapshotMember
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>最近一次测速延迟（毫秒）；null = 未测速，-1 = 失败。</summary>
    public int? DelayMs { get; set; }
    public bool Selected { get; set; }
}

/// <summary>策略组快照的读写（默认落到 %LOCALAPPDATA%\Chorus\proxies-cache.json）。</summary>
public sealed class ProxySnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public ProxySnapshotStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public void Save(ProxySnapshot snapshot)
    {
        if (snapshot.Groups.Count == 0) return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 先写临时文件再替换：写到一半被强杀也不会把上一份好快照写坏
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // 快照只是锦上添花，写失败不影响主流程
        }
    }

    public ProxySnapshot? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var snapshot = JsonSerializer.Deserialize<ProxySnapshot>(
                File.ReadAllText(FilePath), JsonOptions);
            return snapshot?.Groups.Count > 0 ? snapshot : null;
        }
        catch
        {
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
            // 删除失败无妨，下次读取时会被新快照覆盖
        }
    }
}
