using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppEgress.UI;

internal enum IconState
{
    Active,
    Idle,
    Error,
}

/// <summary>
/// Иконки состояния из ресурсов сборки (Assets/tray-*.ico). Сами файлы рисует генератор tools/IconGen;
/// в каждом .ico есть кадры 16–48 px, нужный размер выбирается под DPI.
/// </summary>
internal static class AppIcons
{
    private static readonly Dictionary<IconState, Icon> TrayCache = new();
    private static readonly Dictionary<(IconState, int), ImageSource> ImageCache = new();

    private static Stream Open(IconState state)
    {
        string name = state switch
        {
            IconState.Active => "tray-active.ico",
            IconState.Error => "tray-error.ico",
            _ => "tray-idle.ico",
        };
        return typeof(AppIcons).Assembly.GetManifestResourceStream(name)
               ?? throw new InvalidOperationException("Missing icon resource " + name);
    }

    /// <summary>Иконка для значка в трее, размером под текущий DPI.</summary>
    public static Icon Get(IconState state)
    {
        lock (TrayCache)
        {
            if (!TrayCache.TryGetValue(state, out var icon))
            {
                using var s = Open(state);
                icon = new Icon(s, SystemInformation.SmallIconSize);
                TrayCache[state] = icon;
            }
            return icon;
        }
    }

    /// <summary>Кадр иконки для WPF: ближайший не меньше <paramref name="pixelSize"/>.</summary>
    public static ImageSource GetImageSource(IconState state, int pixelSize)
    {
        lock (ImageCache)
        {
            if (ImageCache.TryGetValue((state, pixelSize), out var cached))
                return cached;
            using var s = Open(state);
            var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frames = decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
            var frame = frames.FirstOrDefault(f => f.PixelWidth >= pixelSize) ?? frames[^1];
            frame.Freeze();
            ImageCache[(state, pixelSize)] = frame;
            return frame;
        }
    }
}
