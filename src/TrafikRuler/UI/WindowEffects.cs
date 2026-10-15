using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using TrafikRuler.UI.Theme;

namespace TrafikRuler.UI;

internal enum Backdrop
{
    /// <summary>Mica: фон главного окна.</summary>
    Mica = 2,
    /// <summary>Акрил: фон диалогов и временных окон.</summary>
    Acrylic = 3,
}

/// <summary>
/// Фон Mica/Acrylic и заголовок окна в стиле Windows 11. Работает на Windows 11 22H2 и новее.
/// На Windows 10 окно остаётся со стандартным заголовком и сплошным фоном.
/// </summary>
internal static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>Windows 11 22H2+ (сборка 22621): системный фон DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public static bool SupportsBackdrop { get; } = Environment.OSVersion.Version.Build >= 22621;

    public const double TitleBarHeight = 32;

    /// <summary>
    /// Готовит окно до показа: на Windows 11 содержимое заходит под заголовок,
    /// кнопки свернуть/закрыть рисует система. Вызывается из конструктора окна.
    /// </summary>
    public static void Prepare(Window window, Backdrop backdrop, FrameworkElement? titleBar)
    {
        if (SupportsBackdrop)
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = TitleBarHeight,
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = true,
                CornerRadius = new CornerRadius(0),
            });
            window.Background = Brushes.Transparent;
            window.SourceInitialized += (_, _) => Apply(window, backdrop);
            // При развороте на весь экран окно с WindowChrome вылезает за край на ширину рамки.
            window.StateChanged += (_, _) =>
            {
                if (window.Content is FrameworkElement root)
                    root.Margin = window.WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
            };
        }
        else
        {
            window.SetResourceReference(Window.BackgroundProperty, "Window.Fallback");
            if (titleBar != null)
                titleBar.Visibility = Visibility.Collapsed;
            window.SourceInitialized += (_, _) => SetDarkMode(window);
        }

        void OnTheme() => window.Dispatcher.BeginInvoke(() => SetDarkMode(window));
        ThemeManager.Changed += OnTheme;
        window.Closed += (_, _) => ThemeManager.Changed -= OnTheme;
    }

    private static void Apply(Window window, Backdrop backdrop)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        SetDarkMode(window);
        int value = (int)backdrop;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
    }

    private static void SetDarkMode(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int dark = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
