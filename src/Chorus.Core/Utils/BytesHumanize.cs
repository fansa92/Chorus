namespace Chorus.Core.Utils;

public static class BytesHumanize
{
    public static string Format(long bytes)
    {
        double value = bytes;
        return value switch
        {
            >= 1024 * 1024 * 1024 => $"{value / 1024 / 1024 / 1024:F2} GB",
            >= 1024 * 1024 => $"{value / 1024 / 1024:F1} MB",
            >= 1024 => $"{value / 1024:F1} KB",
            _ => $"{bytes} B",
        };
    }
}
