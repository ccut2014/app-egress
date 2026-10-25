using System.Net;
using System.Text.Json;
using AppEgress.Core;
using AppEgress.Native;

namespace AppEgress.Tests;

public class ConnectionMonitorTests
{
    private static IPAddress Ip(string text) => IPAddress.Parse(text);
    private static readonly Rule MonitorRule = new() { ProgramPath = @"C:\Apps\app.exe", Mode = RuleMode.Monitor };
    private static string? Image(uint pid) => pid switch
    {
        100 => @"C:\Apps\app.exe",
        200 => @"C:\Proxy\sample-proxy.exe",
        300 => @"C:\Other\app.exe",
        _ => null,
    };

    private sealed class Routes : IRouteResolver
    {
        public uint? Index { get; set; } = 23;
        public int Calls { get; private set; }
        public uint? FindInterface(UInt128 source, UInt128 destination) { Calls++; return Index; }
    }

    private static MonitoringSnapshot Build(IReadOnlyList<TcpConnectionInfo> tcp,
        IReadOnlyList<UdpEndpointInfo>? udp = null, SystemProxyConfiguration? proxy = null, Routes? routes = null,
        Rule? rule = null, IReadOnlyList<string>? errors = null) => ConnectionMonitor.Build(rule ?? MonitorRule,
        tcp, udp ?? [], Image, [new AdapterInfo { Id = "vpn", Name = "Example VPN", Description = "VPN", IfIndex = 23 }],
        proxy ?? SystemProxyConfiguration.Parse(true, "127.0.0.1:18080", false), routes ?? new(), errors ?? []);

    [Fact]
    public void OldSettingsDefaultToRouting_AndModeRoundTrips()
    {
        Assert.Equal(RuleMode.Route, JsonSerializer.Deserialize<Rule>("{\"ProgramPath\":\"app.exe\"}")!.Mode);
        var restored = JsonSerializer.Deserialize<Rule>(JsonSerializer.Serialize(MonitorRule))!;
        Assert.Equal(RuleMode.Monitor, restored.Mode);
        Assert.Empty(restored.AdapterId);
    }

    [Fact]
    public void MonitorNeverMatchesRoutingOrStartsCapture_EvenWithInconsistentState()
    {
        var monitor = new ActiveRule { Rule = MonitorRule, State = RuleState.Active, Stats = new() };
        var snapshot = new RoutingSnapshot([monitor], new Dictionary<uint, AdapterInfo>(), true, 1);
        Assert.False(snapshot.NeedsInterception);
        Assert.Null(snapshot.Match(MonitorRule.ProgramPath));
        Assert.Null(PacketRouter.BuildFilter(snapshot));

        var route = new ActiveRule { Rule = new Rule { ProgramPath = MonitorRule.ProgramPath }, State = RuleState.Waiting, Stats = new() };
        var mixed = new RoutingSnapshot([monitor, route], new Dictionary<uint, AdapterInfo>(), true, 2);
        Assert.Same(route, mixed.Match(MonitorRule.ProgramPath));
    }

    [Fact]
    public void SystemProxyEnabledDoesNotMeanApplicationUsesIt()
    {
        var s = Build([new(Ip("192.0.2.10"), 51000, Ip("198.51.100.20"), 443, 5, 100)]);
        Assert.True(s.ProxyEnabled);
        var row = Assert.Single(s.Connections);
        Assert.False(row.IsSystemProxyEndpoint);
        Assert.Equal((uint)23, row.RouteInterface);
        Assert.Equal("Example VPN", row.InterfaceName);
    }

    [Fact]
    public void ProxyEndpointEvidenceHasLocalPeer_ButNoInventedUpstreamInterface()
    {
        var routes = new Routes();
        var s = Build([
            new(Ip("127.0.0.1"), 51000, Ip("127.0.0.1"), 18080, 5, 100),
            new(Ip("127.0.0.1"), 18080, Ip("127.0.0.1"), 51000, 5, 200),
            new(Ip("192.0.2.10"), 51001, Ip("203.0.113.20"), 443, 5, 200),
        ], routes: routes);
        var row = Assert.Single(s.Connections);
        Assert.True(row.IsSystemProxyEndpoint);
        Assert.True(row.IsLocalTarget);
        Assert.Contains("sample-proxy.exe", row.PeerProcess);
        Assert.Null(row.RouteInterface);
        Assert.Equal(0, routes.Calls);
    }

    [Fact]
    public void LocalPeerUsesFullTuple_NotJustPortNumber()
    {
        var s = Build([
            new(Ip("127.0.0.1"), 51000, Ip("127.0.0.1"), 18080, 5, 100),
            new(Ip("127.0.0.1"), 18080, Ip("127.0.0.1"), 52000, 5, 300),
            new(Ip("127.0.0.1"), 18080, Ip("127.0.0.1"), 51000, 5, 200),
        ]);
        Assert.Contains("sample-proxy.exe", Assert.Single(s.Connections).PeerProcess);
    }

    [Fact]
    public void UdpOnlyHasLocalBinding_AndUnknownRouteIsNotReportedAsDirect()
    {
        var routes = new Routes { Index = null };
        var s = Build([new(Ip("192.0.2.10"), 51000, Ip("198.51.100.20"), 443, 5, 100)],
            [new(IPAddress.Any, 53000, 100)], routes: routes);
        Assert.Equal(1, s.UdpCount);
        Assert.Null(s.Connections[0].RouteInterface);
        var udp = s.Connections.Single(c => c.Protocol == "UDP");
        Assert.Null(udp.RemoteEndpoint);
        Assert.Null(udp.RouteInterface);
        Assert.False(udp.IsSystemProxyEndpoint);
        Assert.Equal(1, routes.Calls);
    }

    [Fact]
    public void PathRuleDoesNotIncludeSameNameInAnotherFolder_OrUnreadablePid()
    {
        TcpConnectionInfo[] rows = [
            new(Ip("192.0.2.10"), 1, Ip("198.51.100.20"), 443, 5, 100),
            new(Ip("192.0.2.10"), 2, Ip("198.51.100.20"), 443, 5, 300),
            new(Ip("192.0.2.10"), 3, Ip("198.51.100.20"), 443, 5, 999),
        ];
        Assert.Equal((uint)100, Assert.Single(Build(rows).Connections).Pid);
        var named = MonitorRule.Clone();
        named.ProgramPath = "app.exe";
        Assert.Equal(2, Build(rows, rule: named).Connections.Count);
    }

    [Fact]
    public void SnapshotCapDoesNotHideTotal_AndNativeErrorsRemainVisible()
    {
        var rows = Enumerable.Range(1, 240)
            .Select(i => new TcpConnectionInfo(Ip("192.0.2.10"), (ushort)(50000 + i), Ip("198.51.100.20"), 443, 5, 100)).ToArray();
        var s = Build(rows, errors: ["UDP/IPv6: 5"]);
        Assert.Equal(240, s.TcpCount);
        Assert.Equal(MonitoringSnapshot.MaxRows, s.Connections.Count);
        Assert.Single(s.ReadErrors);
    }

    [Theory]
    [InlineData("127.0.0.1:18080", "127.0.0.1", 18080)]
    [InlineData("http=127.0.0.1:18080;https=[::1]:18081", "::1", 18081)]
    [InlineData("socks=localhost:18080", "::1", 18080)]
    [InlineData("127.0.0.1:18080", "::ffff:127.0.0.1", 18080)]
    public void ProxyParserSupportsWindowsFormsWithoutHostnameResolution(string config, string address, int port)
    {
        var proxy = SystemProxyConfiguration.Parse(true, config, false);
        Assert.True(proxy.Matches(Ip(address), (ushort)port));
        Assert.False(proxy.Matches(Ip(address), (ushort)(port + 1)));
        Assert.False(SystemProxyConfiguration.Parse(false, config, true).Matches(Ip(address), (ushort)port));
    }

    [Fact]
    public void HostnameProxyAndPacRemainUnconfirmed()
    {
        var proxy = SystemProxyConfiguration.Parse(true, "proxy.example:8080;bad-value", true);
        Assert.Empty(proxy.Endpoints);
        Assert.True(proxy.PacConfigured);
    }

    [Fact]
    public void ScopedIpv6IsDisplayedWithoutGuessingAnInterface()
    {
        var routes = new Routes();
        var s = Build([new(Ip("fe80::1%23"), 50000, Ip("fe80::2%23"), 443, 5, 100)], routes: routes);
        Assert.Contains("%23", Assert.Single(s.Connections).LocalEndpoint);
        Assert.Null(s.Connections[0].RouteInterface);
        Assert.Equal(0, routes.Calls);
    }
}
