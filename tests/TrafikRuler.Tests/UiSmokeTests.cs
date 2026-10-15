using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TrafikRuler.Config;
using TrafikRuler.Core;
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

public class UiSmokeTests
{
    /// <summary>Если задана переменная окружения, снимки окон сохраняются в эту папку.</summary>
    private static readonly string? PreviewDir = Environment.GetEnvironmentVariable("TRAFIKRULER_PREVIEW_DIR");

    /// <summary>Правило для демо-данных: адаптер берётся из <see cref="FakeAdapters"/> по имени.</summary>
    private static RuleView FakeView(string path, string adapterName, RuleState state, bool enabled = true)
    {
        var adapter = FakeAdapters().First(a => a.Name == adapterName);
        var rule = new Rule { ProgramPath = path, AdapterId = adapter.Id, AdapterName = adapter.Name, Enabled = enabled };
        var stats = new RuleStats { ActiveFlows = state == RuleState.Active ? 35 : 0, BytesIn = 73_400_000, BytesOut = 5_300_000, ConnectionsReset = 6 };
        return new RuleView(rule, state, state == RuleState.Waiting ? null : adapter, stats);
    }

    private static List<AdapterInfo> FakeAdapters() =>
    [
        new() { Id = "Ethernet", Name = "Ethernet", Description = "Gigabit Ethernet Adapter", Kind = AdapterKind.Ethernet,
                IsUp = true, IfIndex = 10, IPv4 = Addr.FromIPAddress(System.Net.IPAddress.Parse("192.168.1.20")), HasGateway = true },
        new() { Id = "Wi-Fi", Name = "Беспроводная сеть", Description = "Wi-Fi 6 Wireless Adapter", Kind = AdapterKind.WiFi,
                IsUp = true, IfIndex = 9, IPv4 = Addr.FromIPAddress(System.Net.IPAddress.Parse("192.168.43.20")), HasGateway = true },
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainWindow_RendersWithRules(bool dark)
    {
        UiThread.Run(() =>
        {
            ThemeManager.Force(dark);
            using var engine = new RuleEngine(new AppSettings());
            using var vm = new MainViewModel(engine);
            var views = new List<RuleView>
            {
                FakeView(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Ethernet", RuleState.Active),
                FakeView(@"C:\Windows\explorer.exe", "Беспроводная сеть", RuleState.Waiting),
                FakeView("game.exe", "WireGuard", RuleState.Disabled, enabled: false),
            };
            vm.Sync(views, FakeAdapters(), "Работает", hasError: false);

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

            var w = new MainWindow(vm);
            Assert.Equal(3, vm.Rules.Count);
            Assert.Equal(3, vm.Adapters.Count);
            Render((FrameworkElement)w.Content, 1080, 720, dark ? "main-dark.png" : "main-light.png");

            // Всплывающая панель настроек
            var popup = (System.Windows.Controls.Primitives.Popup)w.FindName("SettingsPopup");
            var flyout = (FrameworkElement)popup.Child;
            flyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Render(flyout, flyout.DesiredSize.Width, flyout.DesiredSize.Height, dark ? "settings-dark.png" : "settings-light.png");
            w.AllowClose = true;
            w.Close();
            ThemeManager.Force(null);
        });
    }

    [Fact]
    public void MainWindow_EmptyAndErrorStates()
    {
        UiThread.Run(() =>
        {
            ThemeManager.Force(false);
            using var engine = new RuleEngine(new AppSettings());
            using var vm = new MainViewModel(engine);
            vm.Sync([], FakeAdapters(), "Нет прав: TrafikRuler нужно запускать от имени администратора (ERROR_ACCESS_DENIED).", hasError: true);
            Assert.False(vm.HasRules);
            Assert.True(vm.HasError);
            for (int i = 0; i < 4; i++)
                Logger.Write($"Тестовая строка журнала {i}: " + new string('x', 160));
            vm.IsLogOpen = true;
            var w = new MainWindow(vm);
            // Даём диспетчеру обработать строки журнала, пришедшие через BeginInvoke
            w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Render((FrameworkElement)w.Content, 1080, 720, "main-empty.png");
            w.AllowClose = true;
            w.Close();
            ThemeManager.Force(null);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuleWindow_Renders(bool dark)
    {
        UiThread.Run(() =>
        {
            ThemeManager.Force(dark);
            var rule = new Rule { ProgramPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe", AdapterId = "Ethernet" };
            var w = new RuleWindow(rule, FakeAdapters(), isNew: true);
            var root = (FrameworkElement)w.Content;
            root.Measure(new Size(600, double.PositiveInfinity));
            Render(root, 600, Math.Ceiling(root.DesiredSize.Height), dark ? "rule-dark.png" : "rule-light.png");
            w.Close();
            ThemeManager.Force(null);
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
