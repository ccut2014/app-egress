using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppEgress.UI.Theme;

/// <summary>
/// Светлая и тёмная палитры в духе Fluent (WinUI 3). Кисти лежат в ресурсах приложения
/// под постоянными ключами, разметка берёт их через DynamicResource, поэтому смена темы
/// Windows подхватывается сразу, без перезапуска.
/// </summary>
internal static class ThemeManager
{
    private static ResourceDictionary? _resources;
    private static bool? _forcedDark;

    /// <summary>Принудительная тема (для тестовых снимков экрана). null — как в Windows.</summary>
    internal static void Force(bool? dark)
    {
        _forcedDark = dark;
        Apply();
    }

    public static bool IsDark { get; private set; }

    /// <summary>Тема или акцентный цвет Windows изменились.</summary>
    public static event Action? Changed;

    public static void Initialize(ResourceDictionary resources)
    {
        _resources = resources;
        resources.MergedDictionaries.Add(new FluentStyles());
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
                or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    private static void Apply()
    {
        if (_resources == null)
            return;
        IsDark = _forcedDark ?? DetectDark();
        var (accentLight, accentDark) = DetectAccent();

        var p = IsDark ? DarkPalette(accentLight) : LightPalette(accentDark);
        foreach (var (key, color) in p)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _resources[key] = brush;
            _resources[key + ".Color"] = color;
        }
        Changed?.Invoke();
    }

    private static List<(string, Color)> LightPalette(Color accent) =>
    [
        ("Window.Fallback", C(0xF3, 0xF3, 0xF3)),
        ("Layer.Bg", C(0x80, 0xFF, 0xFF, 0xFF)),
        ("Card.Bg", C(0xB3, 0xFF, 0xFF, 0xFF)),
        ("Card.BgHover", C(0xD9, 0xFF, 0xFF, 0xFF)),
        ("Card.Stroke", C(0x12, 0x00, 0x00, 0x00)),
        ("Text.Primary", C(0xE4, 0x00, 0x00, 0x00)),
        ("Text.Secondary", C(0x9E, 0x00, 0x00, 0x00)),
        ("Text.Tertiary", C(0x72, 0x00, 0x00, 0x00)),
        ("Text.OnAccent", C(0xFF, 0xFF, 0xFF)),
        ("Control.Bg", C(0xB3, 0xFF, 0xFF, 0xFF)),
        ("Control.BgHover", C(0x80, 0xF9, 0xF9, 0xF9)),
        ("Control.BgPressed", C(0x4D, 0xF9, 0xF9, 0xF9)),
        ("Control.Stroke", C(0x24, 0x00, 0x00, 0x00)),
        ("Subtle.BgHover", C(0x0A, 0x00, 0x00, 0x00)),
        ("Subtle.BgPressed", C(0x06, 0x00, 0x00, 0x00)),
        ("Input.Bg", C(0xB3, 0xFF, 0xFF, 0xFF)),
        ("Input.BgFocus", C(0xFF, 0xFF, 0xFF)),
        ("Toggle.Off", C(0x9E, 0x00, 0x00, 0x00)),
        ("Accent", accent),
        ("Accent.Hover", WithAlpha(accent, 0xE6)),
        ("Accent.Pressed", WithAlpha(accent, 0xCC)),
        ("Accent.Subtle", WithAlpha(accent, 0x1F)),
        ("Accent.Text", accent),
        ("Graph.Down", accent),
        ("Graph.Up", C(0x88, 0x5E, 0xD8)),
        ("State.Ok", C(0x0F, 0x7B, 0x0F)),
        ("State.OkBg", C(0x1F, 0x0F, 0x7B, 0x0F)),
        ("State.Warn", C(0x9D, 0x5D, 0x00)),
        ("State.WarnBg", C(0x24, 0xF7, 0xA4, 0x00)),
        ("State.Error", C(0xC4, 0x2B, 0x1C)),
        ("State.ErrorBg", C(0x1F, 0xC4, 0x2B, 0x1C)),
        ("State.Off", C(0x72, 0x00, 0x00, 0x00)),
        ("State.OffBg", C(0x0F, 0x00, 0x00, 0x00)),
        ("InfoBar.ErrorBg", C(0xFD, 0xE7, 0xE9)),
        ("Divider", C(0x14, 0x00, 0x00, 0x00)),
        ("Popup.Bg", C(0xF9, 0xF9, 0xF9)),
        ("Popup.Stroke", C(0x24, 0x00, 0x00, 0x00)),
        ("Dialog.FooterBg", C(0x0A, 0x00, 0x00, 0x00)),
        ("Scroll.Thumb", C(0x72, 0x00, 0x00, 0x00)),
    ];

    private static List<(string, Color)> DarkPalette(Color accent) =>
    [
        ("Window.Fallback", C(0x20, 0x20, 0x20)),
        ("Layer.Bg", C(0x4C, 0x3A, 0x3A, 0x3A)),
        ("Card.Bg", C(0x0D, 0xFF, 0xFF, 0xFF)),
        ("Card.BgHover", C(0x15, 0xFF, 0xFF, 0xFF)),
        ("Card.Stroke", C(0x1A, 0xFF, 0xFF, 0xFF)),
        ("Text.Primary", C(0xFF, 0xFF, 0xFF)),
        ("Text.Secondary", C(0xC5, 0xFF, 0xFF, 0xFF)),
        ("Text.Tertiary", C(0x87, 0xFF, 0xFF, 0xFF)),
        ("Text.OnAccent", C(0x00, 0x00, 0x00)),
        ("Control.Bg", C(0x0F, 0xFF, 0xFF, 0xFF)),
        ("Control.BgHover", C(0x15, 0xFF, 0xFF, 0xFF)),
        ("Control.BgPressed", C(0x08, 0xFF, 0xFF, 0xFF)),
        ("Control.Stroke", C(0x18, 0xFF, 0xFF, 0xFF)),
        ("Subtle.BgHover", C(0x0F, 0xFF, 0xFF, 0xFF)),
        ("Subtle.BgPressed", C(0x0A, 0xFF, 0xFF, 0xFF)),
        ("Input.Bg", C(0x0F, 0xFF, 0xFF, 0xFF)),
        ("Input.BgFocus", C(0xB3, 0x1E, 0x1E, 0x1E)),
        ("Toggle.Off", C(0xC5, 0xFF, 0xFF, 0xFF)),
        ("Accent", accent),
        ("Accent.Hover", WithAlpha(accent, 0xE6)),
        ("Accent.Pressed", WithAlpha(accent, 0xCC)),
        ("Accent.Subtle", WithAlpha(accent, 0x26)),
        ("Accent.Text", accent),
        ("Graph.Down", accent),
        ("Graph.Up", C(0xC2, 0x9C, 0xFF)),
        ("State.Ok", C(0x6C, 0xCB, 0x5F)),
        ("State.OkBg", C(0x26, 0x6C, 0xCB, 0x5F)),
        ("State.Warn", C(0xFC, 0xE1, 0x00)),
        ("State.WarnBg", C(0x26, 0xFC, 0xE1, 0x00)),
        ("State.Error", C(0xFF, 0x99, 0xA4)),
        ("State.ErrorBg", C(0x26, 0xFF, 0x99, 0xA4)),
        ("State.Off", C(0x87, 0xFF, 0xFF, 0xFF)),
        ("State.OffBg", C(0x12, 0xFF, 0xFF, 0xFF)),
        ("InfoBar.ErrorBg", C(0x44, 0x27, 0x26)),
        ("Divider", C(0x15, 0xFF, 0xFF, 0xFF)),
        ("Popup.Bg", C(0x2C, 0x2C, 0x2C)),
        ("Popup.Stroke", C(0x33, 0x00, 0x00, 0x00)),
        ("Dialog.FooterBg", C(0x40, 0x00, 0x00, 0x00)),
        ("Scroll.Thumb", C(0x8B, 0xFF, 0xFF, 0xFF)),
    ];

    private static bool DetectDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Акцентный цвет Windows. AccentPalette содержит 8 оттенков RGBA, от самого светлого к самому тёмному.
    /// Fluent берёт Light2 для тёмной темы и Dark1 для светлой.
    /// </summary>
    private static (Color Light2, Color Dark1) DetectAccent()
    {
        var fallback = (C(0x60, 0xCD, 0xFF), C(0x00, 0x5F, 0xB8));
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } p)
                return (C(p[4], p[5], p[6]), C(p[16], p[17], p[18]));
        }
        catch (Exception) { }
        return fallback;
    }

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
