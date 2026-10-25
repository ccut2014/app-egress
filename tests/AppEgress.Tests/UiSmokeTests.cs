using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppEgress.Config;
using AppEgress.Core;
using AppEgress.Localization;
using AppEgress.UI;
using AppEgress.UI.Theme;
using AppEgress.UI.ViewModels;

namespace AppEgress.Tests;

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
    private static readonly string? PreviewDir = Environment.GetEnvironmentVariable("APPEGRESS_PREVIEW_DIR")
        ?? Environment.GetEnvironmentVariable("TRAFIKRULER_PREVIEW_DIR");

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

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "ru")]
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
    public void MonitorMode_DoesNotRequireAnAdapter_AndPreservesRoutingSelection(bool dark, string lang)
    {
        WithLook(dark, lang, () =>
        {
            var w = new RuleWindow(new Rule { ProgramPath = "app.exe", Mode = RuleMode.Monitor }, [], true);
            Assert.True(((System.Windows.Controls.RadioButton)w.FindName("MonitorMode")).IsChecked);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)w.FindName("RoutingOptions")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)w.FindName("KillSwitchRow")).Visibility);
            Assert.Equal(-1, ((System.Windows.Controls.ListBox)w.FindName("AdapterList")).SelectedIndex);
            var root = (FrameworkElement)w.Content;
            root.Measure(new Size(600, double.PositiveInfinity));
            Render(root, 600, Math.Ceiling(root.DesiredSize.Height), Name("rule-monitor", dark, lang));
            w.Close();

            w = new RuleWindow(new Rule { ProgramPath = "app.exe", AdapterId = "VPN" }, FakeAdapters(), false);
            var selected = ((System.Windows.Controls.ListBox)w.FindName("AdapterList")).SelectedItem;
            ((System.Windows.Controls.RadioButton)w.FindName("MonitorMode")).IsChecked = true;
            ((System.Windows.Controls.RadioButton)w.FindName("RouteMode")).IsChecked = true;
            Assert.Equal(Visibility.Visible, ((FrameworkElement)w.FindName("RoutingOptions")).Visibility);
            Assert.Same(selected, ((System.Windows.Controls.ListBox)w.FindName("AdapterList")).SelectedItem);
            w.Close();
        });
    }

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "ru")]
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
    public void MonitorCard_ShowsMetadataAndClearsItWhenPaused(bool dark, string lang)
    {
        WithLook(dark, lang, () =>
        {
            using var engine = new RuleEngine(new AppSettings());
            using var vm = new MainViewModel(engine);
            var rule = new Rule { ProgramPath = "browser.exe", Mode = RuleMode.Monitor };
            var view = new RuleView(rule, RuleState.Monitoring, null, new RuleStats { BytesIn = 99999 });
            vm.Sync([view], FakeAdapters(), Loc.T("StatusMonitoring", 1), false);
            var card = Assert.Single(vm.Rules);
            Assert.True(card.IsMonitorOnly);
            Assert.Equal(Loc.T("MonitorNoAdapter"), card.AdapterText);
            Assert.Equal("Ok", vm.StatusKind);
            var sample = new MonitoringSnapshot(DateTimeOffset.Now,
                [new(100, "TCP", "127.0.0.1:51000", "127.0.0.1:18080", 5, true, null, null, true,
                    "sample-proxy.exe (PID 200)")], 1, 0, true, false, true, []);
            card.UpdateMonitoring(sample);
            card.Sample();
            Assert.Contains(Loc.T("MonitorProxyMatch"), Assert.Single(card.Connections).Proxy);
            Assert.Equal(0, card.CurrentDown);
            Assert.DoesNotContain("99999", card.StatsText);
            var w = new MainWindow(vm);
            var root = (FrameworkElement)w.Content;
            Render(root, 1080, 720, "unused.png");
            var expander = FindVisual<System.Windows.Controls.Expander>(root);
            Assert.NotNull(expander);
            expander!.IsExpanded = true;
            Render(root, 1080, 720, Name("main-monitor", dark, lang));
            card.Update(new RuleView(rule, RuleState.Disabled, null, view.Stats), paused: true);
            Assert.Empty(card.Connections);
            Assert.Equal(Loc.T("MonitorStopped"), card.MonitorSummary);
            Assert.Equal(Loc.T("StatePaused"), card.StateText);
            w.AllowClose = true;
            w.Close();
        });
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    [Theory]
    [InlineData(false, "zh-CN", 1080)]
    [InlineData(true, "zh-CN", 1080)]
    [InlineData(true, "en", 820)]
    [InlineData(false, "ru", 1080)]
    public void MonitorPathEvidence_SeparatesLocalLinkFromPeerRoute(bool dark, string lang, double width)
    {
        WithLook(dark, lang, () =>
        {
            using var engine = new RuleEngine(new AppSettings());
            using var vm = new MainViewModel(engine);
            var rule = new Rule { ProgramPath = "sample-app.exe", Mode = RuleMode.Monitor };
            var view = new RuleView(rule, RuleState.Monitoring, null, new RuleStats());
            vm.Sync([view], FakeAdapters(), Loc.T("StatusMonitoring", 1), false);
            var route = new ObservedRoute(23, "Example VPN", AdapterKind.Vpn, 23, "Example VPN");
            var peer = new LocalConnectionPeer(200, "sample-helper.exe", PeerEvidence.ReversedTuple,
                [new("192.0.2.10:52000", "203.0.113.20:8080", 5, false, false, null, route)], 1, false);
            var connection = new ObservedConnection(100, "TCP", "127.0.0.1:51016", "127.0.0.1:51014",
                5, true, null, null, false, peer.DisplayName)
                { Path = new("sample-app.exe", true, true, ObservedRoute.Unknown, peer) };
            var snapshot = new MonitoringSnapshot(DateTimeOffset.Now, [connection], 1, 0, true, false, true, []);
            var card = Assert.Single(vm.Rules);
            card.UpdateMonitoring(snapshot);
            Assert.Contains("sample-helper.exe", card.ExitSummary);
            Assert.Contains(Loc.T("PathRelatedRoutes", "Example VPN (VPN)"), card.ExitSummary);
            Assert.DoesNotContain(Loc.T("PathDirectRoutes", "Example VPN (VPN)"), card.ExitSummary);
            Assert.Equal(Loc.T("PathProxyNotObserved"), card.ProxyEvidence);
            Assert.Contains("sample-app.exe → sample-helper.exe", Assert.Single(card.Connections).Network);
            Assert.Contains(Loc.T("PathAssociationOnly"), card.Connections[0].Detail);
            Assert.Contains("203.0.113.20:8080", card.Connections[0].Detail);
            var w = new MainWindow(vm);
            var root = (FrameworkElement)w.Content;
            Render(root, width, 720, "unused.png");
            FindVisual<System.Windows.Controls.Expander>(root)!.IsExpanded = true;
            root.UpdateLayout();
            var grid = FindVisual<System.Windows.Controls.DataGrid>(root)!;
            grid.SelectedIndex = 0;
            Render(root, width, 720, Name("path-" + width, dark, lang));
            Assert.NotNull(FindText(root, Loc.T("PathOverviewTitle")));
            card.Update(new RuleView(rule, RuleState.Disabled, null, view.Stats), true);
            Assert.Empty(card.Connections);
            Assert.Equal(Loc.T("MonitorStopped"), card.ExitSummary);
            Assert.Empty(card.ProxyEvidence);
            w.AllowClose = true;
            w.Close();
        });
    }

    [Fact]
    public void MonitorPathEvidence_MixedProxyAndDirectConnectionsStayDistinct()
    {
        WithLook(false, "zh-CN", () =>
        {
            var proxy = new ObservedConnection(100, "TCP", "127.0.0.1:51000", "127.0.0.1:18080", 5,
                true, null, null, true, "sample-proxy.exe")
                { Path = new("app.exe", true, true, ObservedRoute.Unknown,
                    new(300, "sample-proxy.exe", PeerEvidence.ReversedTuple, [], 0, true)) };
            var direct = new ObservedConnection(100, "TCP", "192.0.2.10:52000", "203.0.113.20:443", 5,
                false, 23, "Example VPN", false, null)
                { Path = new("app.exe", true, false, new(23, "Example VPN", AdapterKind.Vpn, 7, "WLAN"), null) };
            var snapshot = new MonitoringSnapshot(DateTimeOffset.Now, [proxy, direct], 2, 0, true, false, true, []);
            var presentation = MonitoringPresentation.Create(snapshot);
            Assert.Equal(Loc.T("PathMixedEvidence"), presentation.CardText);
            Assert.Contains("sample-proxy.exe", presentation.ProxyEvidence);
            Assert.Contains(Loc.T("PathDirectRoutes", "Example VPN (VPN)"), presentation.ExitSummary);
            Assert.Contains(Loc.T("PathSourceMismatch"), presentation.Notes);
            Assert.Contains(Loc.T("PathSharedProxy"), presentation.Notes);
            Assert.Contains(Loc.T("PathSourceInterface", "WLAN"), presentation.Rows[1].Detail);
        });
    }

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
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
    public void MainWindow_RendersWithRules(bool dark, string lang)
    {
        WithLook(dark, lang, () =>
        {
            using var engine = new RuleEngine(new AppSettings());
            using var vm = DemoViewModel(engine);
            var w = new MainWindow(vm);
            Assert.Equal(3, vm.Rules.Count);
            Assert.Equal(3, vm.Adapters.Count);
            Assert.Contains(vm.LanguageOptions, option => option.Code == "zh-CN" && option.Label == "简体中文");
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

            Loc.SetLanguage("zh-CN");
            w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal("应用网络路由", title.Text);
            vm.Sync([FakeView("game.exe", "Ethernet", RuleState.Active)], FakeAdapters(), Loc.T("StatusRunning", 1, 1), false);
            vm.Rules[0].Processes = 25;
            Assert.Equal("已启用", vm.Rules[0].StateText);
            Assert.Contains("25 个进程", vm.Rules[0].StatsText);
            Assert.Contains("1 条已启用规则", vm.StatusText);
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
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
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
    public void MainWindow_ShowsConnectedVpnWithoutIpv4AndReferencedDisconnectedAdapter()
    {
        WithLook(false, "en", () =>
        {
            var adapters = FakeAdapters();
            adapters.Add(new AdapterInfo { Id = "vpn-b", Name = "VPN B", Description = "IPv6-only private VPN",
                Kind = AdapterKind.Vpn, IsUp = true, IfIndex = 27 });
            adapters.Add(new AdapterInfo { Id = "offline", Name = "Offline VPN", Description = "Disconnected VPN" });
            var rule = new Rule { ProgramPath = "app.exe", AdapterId = "offline" };
            var view = new RuleView(rule, RuleState.Waiting, adapters[^1], new RuleStats());
            using var engine = new RuleEngine(new AppSettings());
            using var vm = new MainViewModel(engine);
            vm.Sync([view], adapters, "", false);
            Assert.Equal(5, vm.Adapters.Count);
            Assert.Contains(vm.Adapters, a => a.Id == "vpn-b");
            Assert.Contains(vm.Adapters, a => a.Id == "offline");
            Assert.All(vm.Adapters, a => Assert.False(a.IsUsed));
        });
    }

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "ru")]
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
    public void RuleWindow_ShowsGatewaylessVpnAndUnavailableReason(bool dark, string lang)
    {
        WithLook(dark, lang, () =>
        {
            var adapters = FakeAdapters();
            var vpn = adapters.Single(a => a.Kind == AdapterKind.Vpn);
            adapters.Add(new AdapterInfo { Id = "vpn-a", Name = "VPN A", Description = "Internet VPN",
                Kind = AdapterKind.Vpn, IsUp = true, IfIndex = 27, HasGateway = true,
                IPv4 = Addr.FromIPAddress(System.Net.IPAddress.Parse("10.10.0.2")) });
            var w = new RuleWindow(new Rule { ProgramPath = "app.exe", AdapterId = vpn.Id }, adapters, true);
            var list = (System.Windows.Controls.ListBox)w.FindName("AdapterList");
            var hint = (System.Windows.Controls.TextBlock)w.FindName("HintText");
            Assert.Equal(4, list.Items.Count);
            Assert.True(vpn.IsUsable);
            Assert.Contains(Loc.T("HintNoGateway"), hint.Text);
            var root = (FrameworkElement)w.Content;
            root.Measure(new Size(600, double.PositiveInfinity));
            Render(root, 600, Math.Ceiling(root.DesiredSize.Height), Name("rule-vpn", dark, lang));
            w.Close();

            var unreadable = new AdapterInfo { Id = "unreadable", Name = "VPN B", Description = "Private VPN",
                Kind = AdapterKind.Vpn, IsUp = true, EnumerationFailed = true };
            var unavailable = new RuleWindow(new Rule { ProgramPath = "app.exe", AdapterId = unreadable.Id }, [vpn, unreadable], true);
            Assert.Equal(2, ((System.Windows.Controls.ListBox)unavailable.FindName("AdapterList")).Items.Count);
            Assert.Equal(Loc.T("HintAdapterReadFailed"), ((System.Windows.Controls.TextBlock)unavailable.FindName("HintText")).Text);
            unavailable.Close();
        });
    }

    [Fact]
    public void RuleCard_ShowsWarningForRecentRouteFailure()
    {
        WithLook(false, "en", () =>
        {
            var view = FakeView("app.exe", "VPN", RuleState.Active);
            view.Stats.RouteFailures = 1;
            view.Stats.LastRouteFailure = Environment.TickCount64;
            var card = new RuleCardViewModel(view, _ => { });
            Assert.Equal(Loc.T("StateRouteIssue"), card.StateText);
            Assert.Equal("Warn", card.StateKind);
            Assert.Contains(Loc.T("StatsRouteFailures", 1), card.StatsText);
            view.Stats.LastRouteFailure = 0;
            card.Sample();
            Assert.Equal(Loc.T("StateActive"), card.StateText);
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
