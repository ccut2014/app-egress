using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppEgress.UI;

/// <summary>Иконки exe как ImageSource для WPF (с кэшем по пути).</summary>
internal static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\\', '/']) < 0)
            return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;
        }

        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
        }
        catch (Exception) { }

        lock (Cache)
            Cache[path] = image;
        return image;
    }
}
