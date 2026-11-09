using Chorus.Core.State;
using Xunit;

namespace Chorus.Core.Tests;

public class SystemProxyBackupTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chorus-proxy-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private SystemProxyBackupStore Store() => new(Path.Combine(_dir, "proxy-backup.json"));

    private static SystemProxyBackup Sample() => new()
    {
        CapturedAt = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero),
        Port = 7897,
        ProxyEnable = 0,
        ProxyServer = "corp-proxy.example.com:8080",
        ProxyOverride = "localhost;<local>",
        AutoConfigUrl = "http://pac.example.com/proxy.pac",
    };

    [Fact]
    public void RoundTrip_Preserves_All_Values()
    {
        var store = Store();
        store.Save(Sample());

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(7897, loaded.Port);
        Assert.Equal(0, loaded.ProxyEnable);
        Assert.Equal("corp-proxy.example.com:8080", loaded.ProxyServer);
        Assert.Equal("localhost;<local>", loaded.ProxyOverride);
        Assert.Equal("http://pac.example.com/proxy.pac", loaded.AutoConfigUrl);
        Assert.Equal("127.0.0.1:7897", loaded.OwnServer);
    }

    [Fact]
    public void OwnServer_Follows_The_Port() =>
        Assert.Equal("127.0.0.1:9090", new SystemProxyBackup { Port = 9090 }.OwnServer);

    [Fact]
    public void Clear_Removes_The_File()
    {
        var store = Store();
        store.Save(Sample());
        Assert.True(File.Exists(store.FilePath));

        store.Clear();
        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_Returns_Null_On_Missing_Or_Corrupt_File()
    {
        Assert.Null(Store().Load());

        var store = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "not-json{");
        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_Overwrites_Previous_Snapshot_Atomically()
    {
        var store = Store();
        store.Save(Sample());
        var second = Sample();
        second.Port = 7900;
        second.ProxyServer = null;
        second.AutoConfigUrl = null;
        store.Save(second);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(7900, loaded.Port);
        Assert.Null(loaded.ProxyServer);
        Assert.Null(loaded.AutoConfigUrl);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }
}
