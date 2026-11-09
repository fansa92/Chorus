using Chorus.Core.Interop;
using Xunit;

namespace Chorus.Core.Tests;

#pragma warning disable CA1416 // 测试注入了读写函数，不碰真实注册表
public class AutoStartManagerTests
{
    private const string Exe = @"C:\Apps\Chorus\Chorus.App.exe";

    private static (AutoStartManager Manager, Dictionary<string, string?> Values) Create()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var manager = new AutoStartManager(
            Exe,
            name => values.TryGetValue(name, out var v) ? v : null,
            (name, v) =>
            {
                if (v is null) values.Remove(name);
                else values[name] = v;
            });
        return (manager, values);
    }

    [Fact]
    public void Build_Quotes_Path_And_Appends_Start()
    {
        Assert.Equal("\"C:\\Apps\\Chorus\\Chorus.App.exe\" --start", AutoStartCommand.Build(Exe));
        Assert.Equal("\"C:\\Apps\\Chorus\\Chorus.App.exe\" --start", AutoStartCommand.Build("\"" + Exe + "\""));
        Assert.Equal("", AutoStartCommand.Build(""));
    }

    [Theory]
    [InlineData("\"C:\\Apps\\Chorus\\Chorus.App.exe\" --start", true)]
    [InlineData("\"D:\\old\\Chorus.App.exe\" --start", true)]   // 换了解压目录也认得
    [InlineData("\"C:\\Other\\tool.exe\" --start", false)]
    [InlineData("\"C:\\Apps\\Chorus\\Chorus.App.exe\"", false)] // 缺 --start
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsChorusEntry_Only_Matches_Our_Own_Item(string? value, bool expected) =>
        Assert.Equal(expected, AutoStartCommand.IsChorusEntry(value));

    [Fact]
    public void SetEnabled_Writes_The_Run_Value()
    {
        var (manager, values) = Create();
        Assert.False(manager.IsEnabled());

        manager.SetEnabled(true);

        Assert.True(manager.IsEnabled());
        Assert.Equal("\"C:\\Apps\\Chorus\\Chorus.App.exe\" --start", values["Chorus"]);
    }

    [Fact]
    public void SetEnabled_False_Removes_Our_Item()
    {
        var (manager, values) = Create();
        manager.SetEnabled(true);
        manager.SetEnabled(false);

        Assert.False(manager.IsEnabled());
        Assert.False(values.ContainsKey("Chorus"));
    }

    [Fact]
    public void SetEnabled_False_Does_Not_Touch_Someone_Elses_Item()
    {
        var (manager, values) = Create();
        values["Chorus"] = "\"C:\\Other\\tool.exe\" --silent";

        manager.SetEnabled(false);

        Assert.Equal("\"C:\\Other\\tool.exe\" --silent", values["Chorus"]);
    }

    [Fact]
    public void SetEnabled_True_With_Empty_ExePath_Throws()
    {
        var manager = new AutoStartManager("", _ => null, (_, _) => { });
        Assert.Throws<InvalidOperationException>(() => manager.SetEnabled(true));
    }
}
#pragma warning restore CA1416
