using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TrafikRuler.Config;
using TrafikRuler.Core;
using TrafikRuler.Localization;
using TrafikRuler.UI;
using TrafikRuler.UI.Theme;
using TrafikRuler.UI.ViewModels;

namespace TrafikRuler.Tests;

/// <summary>
/// Один STA-поток с WPF Application на все UI-тесты: Application в процессе может быть только один.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeManager.Initialize(app.Resources);
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF test UI" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static void Run(Action action) => Instance.Value.Invoke(action);
}

[Collection("Localization")]
public class UiSmokeTests
{
    /// <summary>Если задана переменная окружения, снимки окон сохраняются в эту папку.</summary>
    private static readonly string? PreviewDir = Environment.GetEnvironmentVariable("TRAFIKRULER_PREVIEW_DIR");

    /// <summary>Правило для демо-данных: адаптер берётся из <see cref="FakeAdapters"/> по Id.</summary>
    private static RuleView FakeView(string path, string adapterId, RuleState state, bool enabled = true)
    {
        var adapter = FakeAdapters().First(a => a.Id == adapterId);
        var rule = new Rule { ProgramPath = path, AdapterId = adapter.Id, AdapterName = adapter.Name, Enabled = enabled };
        var stats = new RuleStats { ActiveFlows = state == RuleState.Active ? 35 : 0, BytesIn = 73_400_000, BytesOut = 5_300_000, ConnectionsReset = 6 };
        return new RuleView(rule, state, state == RuleState.Waiting ? null : adapter, stats);
    }

    /// <summary>Демо-адаптеры. Имя Wi-Fi как в Windows на текущем языке интерфейса.</summary>
    private static List<AdapterInfo> FakeAdapters() =>
    [
        new() { Id = "Ethernet", Name = "Ethernet", Description = "Gigabit Ethernet Adapter", Kind = AdapterKind.Ethernet,
                IsUp = true, IfIndex = 10, IPv4 = Addr.FromIPAddress(System.Net.IPAddress.Parse("192.168.1.20")), HasGateway = true },
        new() { Id = "Wi-Fi", Name = Loc.Language == "ru" ? "Беспроводная сеть" : "Wi-Fi", Description = "Wi-Fi 6 Wireless Adapter",
                Kind = AdapterKind.WiFi, IsUp = true, IfIndex = 9, IPv4 = Addr.FromIPAddress(System.Net.IPAddress.Parse("192.168.43.20")),
                HasGateway = true },
        new() { Id = "VPN", Name = "WireGuard", Description = "WireGuard Tunnel", Kind = AdapterKind.Vpn,
                IsUp = true, IfIndex = 26, IPv4 = Addr.FromIPAddress(System.Net.IPAddress.Parse("10.8.0.2")) },
    ];

    private static void Render(FrameworkElement root, double width, double height, string file)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        if (PreviewDir == null)
            return;

        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle((Brush)Application.Current.Resources["Window.Fallback"], null, new Rect(0, 0, width, height));
        // Двойное разрешение: снимки идут в README и должны быть чёткими на HiDPI-экранах
        const int scale = 2;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(width) * scale, (int)Math.Ceiling(height) * scale,
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(bg);
        rtb.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(PreviewDir);
        using var fs = File.Create(Path.Combine(PreviewDir, file));
        png.Save(fs);
    }

    /// <summary>Тема и язык на время теста, потом возврат к английскому и системной теме.</summary>
    private static void WithLook(bool dark, string lang, Action action)
    {
        UiThread.Run(() =>
        {
            ThemeManager.Force(dark);
            Loc.SystemLanguageOverride = lang;
            Loc.SetLanguage(lang);
            try
            {
                action();
            }
            finally
            {
                Loc.SystemLanguageOverride = null;
                Loc.SetLanguage("en");
                ThemeManager.Force(null);
            }
        });
    }

    private static string Name(string window, bool dark, string lang) => $"{window}-{lang}-{(dark ? "dark" : "light")}.png";

    private static MainViewModel DemoViewModel(RuleEngine engine)
    {
        var vm = new MainViewModel(engine);
        var views = new List<RuleView>
        {
            FakeView(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Ethernet", RuleState.Active),
            FakeView(@"C:\Windows\explorer.exe", "Wi-Fi", RuleState.Waiting),
            FakeView("game.exe", "VPN", RuleState.Disabled, enabled: false),
        };
        vm.Sync(views, FakeAdapters(), Loc.T("StatusRunning", 1, 3), hasError: false);

        // Синтетическая история скорости для графика
        var rnd = new Random(3);
        for (int i = 0; i < SpeedHistory.Length; i++)
        {
            Interlocked.Add(ref views[0].Stats.BytesIn, (long)(1_500_000 + 1_200_000 * Math.Sin(i / 6.0) + rnd.Next(400_000)));
            Interlocked.Add(ref views[0].Stats.BytesOut, (long)(150_000 + 90_000 * Math.Cos(i / 4.0) + rnd.Next(60_000)));
            foreach (var r in vm.Rules)
                r.Sample();
        }
        vm.Rules[0].Processes = 25;
        return vm;
    }

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "en")]
    [InlineData(false, "ru")]
    [InlineData(true, "ru")]
    public void MainWindow_RendersWithRules(bool dark, string lang)
    {
        WithLook(dark, lang, () =>
        {
            using var engine = new RuleEngine(new AppSettings());
            using var vm = DemoViewModel(engine);
            var w = new MainWindow(vm);
            Assert.Equal(3, vm.Rules.Count);
            Assert.Equal(3, vm.Adapters.Count);
            Render((FrameworkElement)w.Content, 1080, 720, Name("main", dark, lang));

            // Всплывающая панель настроек
            var popup = (System.Windows.Controls.Primitives.Popup)w.FindName("SettingsPopup");
            var flyout = (FrameworkElement)popup.Child;
            flyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Render(flyout, flyout.DesiredSize.Width, flyout.DesiredSize.Height, Name("settings", dark, lang));
            w.AllowClose = true;
            w.Close();
        });
    }

    [Fact]
    public void MainWindow_SwitchesLanguageLive()
    {
        WithLook(false, "en", () =>
        {
            using var engine = new RuleEngine(new AppSettings());
            using var vm = DemoViewModel(engine);
            var w = new MainWindow(vm);
            Render((FrameworkElement)w.Content, 1080, 720, "unused.png");

            var title = FindText((DependencyObject)w.Content, "Traffic routing");
            Assert.NotNull(title);
            Assert.Equal("Active", vm.Rules[0].StateText);

            Loc.SetLanguage("ru");
            // Карточки пересобираются через BeginInvoke — даём диспетчеру их обработать
            w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.Equal("Маршрутизация трафика", title!.Text);

            // При смене языка модель пересобирает карточки из движка. У тестового движка правил нет,
            // поэтому подаём демо-правила заново — в программе это делает сам движок.
            vm.Sync([FakeView("game.exe", "Ethernet", RuleState.Active)], FakeAdapters(), Loc.T("StatusRunning", 1, 1), false);
            vm.Rules[0].Processes = 25;
            Assert.Equal("Активно", vm.Rules[0].StateText);
            Assert.Contains("25 процессов", vm.Rules[0].StatsText);
            Assert.Contains("1 активное правило", vm.StatusText);
            w.AllowClose = true;
            w.Close();
        });
    }

    private static System.Windows.Controls.TextBlock? FindText(DependencyObject root, string text)
    {
        if (root is System.Windows.Controls.TextBlock tb && tb.Text == text)
            return tb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindText(VisualTreeHelper.GetChild(root, i), text) is { } found)
                return found;
        return null;
    }

    [Fact]
    public void MainWindow_EmptyAndErrorStates()
    {
        WithLook(false, "en", () =>
        {
            using var engine = new RuleEngine(new AppSettings());
            using var vm = new MainViewModel(engine);
            vm.Sync([], FakeAdapters(), Loc.T("ErrAccessDenied"), hasError: true);
            Assert.False(vm.HasRules);
            Assert.True(vm.HasError);
            for (int i = 0; i < 4; i++)
                Logger.Write($"Test log line {i}: " + new string('x', 160));
            vm.IsLogOpen = true;
            var w = new MainWindow(vm);
            // Даём диспетчеру обработать строки журнала, пришедшие через BeginInvoke
            w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Render((FrameworkElement)w.Content, 1080, 720, "main-empty.png");
            w.AllowClose = true;
            w.Close();
        });
    }

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "en")]
    [InlineData(false, "ru")]
    [InlineData(true, "ru")]
    public void RuleWindow_Renders(bool dark, string lang)
    {
        WithLook(dark, lang, () =>
        {
            var rule = new Rule { ProgramPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe", AdapterId = "Ethernet" };
            var w = new RuleWindow(rule, FakeAdapters(), isNew: true);
            var root = (FrameworkElement)w.Content;
            root.Measure(new Size(600, double.PositiveInfinity));
            Render(root, 600, Math.Ceiling(root.DesiredSize.Height), Name("rule", dark, lang));
            w.Close();
        });
    }

    [Fact]
    public void Dialogs_Build()
    {
        UiThread.Run(() =>
        {
            var picker = new ProcessPickerWindow();
            Render((FrameworkElement)picker.Content, 760, 600, "picker.png");
            picker.Close();
        });
    }
    [Fact]
    public void Icons_LoadFromResources()
    {
        foreach (var s in Enum.GetValues<IconState>())
        {
            var tray = AppIcons.Get(s);
            Assert.Equal(System.Windows.Forms.SystemInformation.SmallIconSize.Width, tray.Width);
            UiThread.Run(() =>
            {
                Assert.Equal(16, ((BitmapSource)AppIcons.GetImageSource(s, 16)).PixelWidth);
                Assert.Equal(24, ((BitmapSource)AppIcons.GetImageSource(s, 24)).PixelWidth);
                Assert.Equal(48, ((BitmapSource)AppIcons.GetImageSource(s, 100)).PixelWidth);
            });
        }
    }
}
