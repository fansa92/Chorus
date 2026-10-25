using Chorus.Core.State;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>策略组快照：核心停止后策略组页靠它兜底，所以读写必须可靠、坏文件不能炸。</summary>
public sealed class ProxySnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chorus-snap-" + Guid.NewGuid().ToString("N"));

    private ProxySnapshotStore Store() => new(Path.Combine(_dir, "proxies-cache.json"));

    private static ProxySnapshot Sample() => new()
    {
        SavedAt = new DateTimeOffset(2026, 10, 25, 15, 30, 0, TimeSpan.FromHours(8)),
        Source = "配置「我的机场」",
        ProxyCount = 63,
        Groups =
        [
            new ProxySnapshotGroup
            {
                Name = "🚀 节点选择",
                Type = "Selector",
                Now = "JP-1",
                Members =
                [
                    new ProxySnapshotMember { Name = "JP-1", Type = "VLESS", DelayMs = 88, Selected = true },
                    new ProxySnapshotMember { Name = "US-1", Type = "Hysteria2", DelayMs = null, Selected = false },
                ],
            },
        ],
    };

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var store = Store();
        store.Save(Sample());

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("配置「我的机场」", loaded!.Source);
        Assert.Equal(63, loaded.ProxyCount);
        var group = Assert.Single(loaded.Groups);
        Assert.Equal("🚀 节点选择", group.Name);
        Assert.Equal("JP-1", group.Now);
        Assert.Equal(2, group.Members.Count);
        Assert.True(group.Members[0].Selected);
        Assert.Equal(88, group.Members[0].DelayMs);
        Assert.Null(group.Members[1].DelayMs);
    }

    [Fact]
    public void Empty_Snapshot_Is_Not_Written()
    {
        var store = Store();
        store.Save(new ProxySnapshot { Groups = [] });

        Assert.Null(store.Load());
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Corrupt_File_Returns_Null_Instead_Of_Throwing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Store().FilePath, "{ not json");

        Assert.Null(Store().Load());
    }

    [Fact]
    public void Clear_Removes_File()
    {
        var store = Store();
        store.Save(Sample());
        Assert.True(File.Exists(store.FilePath));

        store.Clear();

        Assert.False(File.Exists(store.FilePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
