using Chorus.Core.Utils;
using Xunit;

namespace Chorus.Core.Tests;

public class BytesHumanizeTests
{
    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void Format_Humanizes_Bytes(long bytes, string expected)
    {
        Assert.Equal(expected, BytesHumanize.Format(bytes));
    }
}
