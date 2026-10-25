using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Chorus.App.Services;

/// <summary>
/// 国旗资源（Assets/flags/{code}.png，96×72 圆角）。按国家代码缓存，
/// 省得每次检测都重新解码同一张图；没有对应资源时返回 null，界面退回地球图标。
/// </summary>
public static class Flags
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (Cache.TryGetValue(code, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "flags", $"{code}.png");
            if (File.Exists(path)) image = new BitmapImage(new Uri(path));
        }
        catch
        {
            // 图标读不到不影响检测结果
        }

        Cache[code] = image;
        return image;
    }
}
