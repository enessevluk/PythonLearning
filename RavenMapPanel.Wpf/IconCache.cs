using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RavenMapPanel;

internal static class IconCache
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource Get(string file)
    {
        if (Cache.TryGetValue(file, out var cached)) return cached;
        var custom = AssetStore.CustomIconPath(file);
        var uri = custom is null ? new Uri($"pack://application:,,,/Assets/icons/{file}", UriKind.Absolute) : new Uri(custom, UriKind.Absolute);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = uri; bitmap.EndInit(); bitmap.Freeze();
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var stride = converted.PixelWidth * 4; var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        var minX = converted.PixelWidth; var minY = converted.PixelHeight; var maxX = -1; var maxY = -1;
        for (var y = 0; y < converted.PixelHeight; y++) for (var x = 0; x < converted.PixelWidth; x++) if (pixels[y * stride + x * 4 + 3] > 20) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        ImageSource result = bitmap;
        if (maxX >= minX && maxY >= minY) result = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        if (result.CanFreeze) result.Freeze(); Cache[file] = result; return result;
    }
    public static void Remove(string file) => Cache.Remove(file);
}
