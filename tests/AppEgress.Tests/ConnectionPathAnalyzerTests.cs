using System.Net;
using AppEgress.Core;
using AppEgress.Native;

namespace AppEgress.Tests;

public class ConnectionPathAnalyzerTests
{
    private static IPAddress Ip(string value) => IPAddress.Parse(value);
    private static readonly Rule App = new() { ProgramPath = @"C:\Apps\sample-app.exe", Mode = RuleMode.Monitor };
    private static readonly AdapterInfo Vpn = new()
    {
        Id = "vpn", Name = "Example VPN", Description = "Test VPN", Kind = AdapterKind.Vpn,
        IfIndex = 23, IPv4 = Addr.FromIPAddress(Ip("192.0.2.10")),
    };
    private static readonly AdapterInfo Wifi = new()
    {
        Id = "wifi", Name = "WLAN", Description = "Test WLAN", Kind = AdapterKind.WiFi,
        IfIndex = 7, IPv4 = Addr.FromIPAddress(Ip("198.51.100.10")),
    };
    private static string? Image(uint pid) => pid switch
    {
        100 => App.ProgramPath, 200 => @"C:\Apps\sample-helper.exe", 300 => @"C:\Proxy\sample-proxy.exe",
        400 => @"C:\Other\other.exe", _ => null,
    };
    private sealed class Routes : IRouteResolver
    {
        public uint? Index { get; set; } = 23;
        public int Calls { get; private set; }
        public uint? FindInterface(UInt128 source, UInt128 destination) { Calls++; return Index; }
    }
    private static TcpConnectionInfo Link => new(Ip("127.0.0.1"), 51016, Ip("127.0.0.1"), 51014, 5, 100);
    private static TcpConnectionInfo Reverse => new(Ip("127.0.0.1"), 51014, Ip("127.0.0.1"), 51016, 5, 200);
    private static TcpConnectionInfo Listener => new(Ip("127.0.0.1"), 51014, IPAddress.Any, 0, 2, 200);
    private static TcpConnectionInfo External(ushort port = 60000) => new(Ip("192.0.2.10"), port, Ip("203.0.113.20"), 8080, 5, 200);
    private static MonitoringSnapshot Build(IReadOnlyList<TcpConnectionInfo> tcp, Routes? routes = null) => ConnectionMonitor.Build(
        App, tcp, [], Image, [Vpn, Wifi], SystemProxyConfiguration.Parse(true, "127.0.0.1:18080", false), routes ?? new(), []);

    [Fact]
    public void LocalLinkHasExactPeer_AndPeerRouteIsSeparateFromAppRoute()
    {
        var snapshot = Build([Link, Reverse, Listener, External()]);
        var connection = Assert.Single(snapshot.Connections);
        Assert.True(connection.IsLocalTarget);
        Assert.Null(connection.RouteInterface);
        var peer = connection.Path!.Peer!;
        Assert.Equal((uint)200, peer.Pid);
        Assert.Equal("sample-helper.exe", peer.Name);
        Assert.Equal(PeerEvidence.ReversedTuple, peer.Evidence);
        var other = Assert.Single(peer.OtherConnections);
        Assert.Equal((uint)23, other.Route.InterfaceIndex);
        Assert.Equal((uint)23, other.Route.SourceInterfaceIndex);
        Assert.Equal(AdapterKind.Vpn, other.Route.Kind);
        Assert.False(connection.IsSystemProxyEndpoint);
        Assert.False(peer.IsSharedProxy);
    }

    [Fact]
    public void SharedSystemProxyDoesNotAttributeItsUpstreamOrQueryItsRoutes()
    {
        var routes = new Routes();
        var snapshot = Build([
            new(Ip("127.0.0.1"), 51000, Ip("127.0.0.1"), 18080, 5, 100),
            new(Ip("127.0.0.1"), 18080, Ip("127.0.0.1"), 51000, 5, 300),
            new(Ip("198.51.100.10"), 52000, Ip("203.0.113.30"), 443, 5, 300),
        ], routes);
        var connection = Assert.Single(snapshot.Connections);
        Assert.True(connection.IsSystemProxyEndpoint);
        Assert.True(connection.Path!.Peer!.IsSharedProxy);
        Assert.Equal("sample-proxy.exe", connection.Path.Peer.Name);
        Assert.Empty(connection.Path.Peer.OtherConnections);
        Assert.Null(connection.Path.Route.InterfaceIndex);
        Assert.Equal(0, routes.Calls);
    }

    [Fact]
    public void ProxyManagementPortDoesNotExposeSharedUpstreamAsAppTraffic()
    {
        var routes = new Routes();
        var connection = Assert.Single(Build([
            new(Ip("127.0.0.1"), 51000, Ip("127.0.0.1"), 9090, 5, 100),
            new(Ip("127.0.0.1"), 9090, Ip("127.0.0.1"), 51000, 5, 300),
            new(Ip("127.0.0.1"), 18080, IPAddress.Any, 0, 2, 300),
            new(Ip("198.51.100.10"), 52000, Ip("203.0.113.30"), 443, 5, 300),
        ], routes).Connections);
        Assert.False(connection.IsSystemProxyEndpoint);
        Assert.True(connection.Path!.Peer!.IsSharedProxy);
        Assert.Empty(connection.Path.Peer.OtherConnections);
        Assert.Equal(0, routes.Calls);
    }

    [Fact]
    public void PeerProxyConnectionIsAnAssociation_NotAnApplicationProxyMatch()
    {
        var snapshot = Build([Link, Reverse, Listener,
            new(Ip("127.0.0.1"), 52000, Ip("127.0.0.1"), 18080, 5, 200),
            new(Ip("127.0.0.1"), 18080, Ip("127.0.0.1"), 52000, 5, 300),
        ]);
        var connection = Assert.Single(snapshot.Connections);
        Assert.False(connection.IsSystemProxyEndpoint);
        var other = Assert.Single(connection.Path!.Peer!.OtherConnections);
        Assert.True(other.IsSystemProxyEndpoint);
        Assert.Equal("sample-proxy.exe", other.ProxyProcess);
        Assert.Null(other.Route.InterfaceIndex);
    }

    [Fact]
    public void AmbiguousReverseTupleDoesNotFallBackToUniqueListener()
    {
        var conflicting = Reverse with { Pid = 400 };
        var connection = Assert.Single(Build([Link, Reverse, conflicting, Listener, External()]).Connections);
        Assert.Null(connection.Path!.Peer);
        Assert.Null(connection.PeerProcess);
    }

    [Fact]
    public void ListenerOnlyEvidenceIsExplicitlyWeaker()
    {
        var connection = Assert.Single(Build([Link, Listener, External()]).Connections);
        Assert.Equal(PeerEvidence.ListenerOnly, connection.Path!.Peer!.Evidence);
        Assert.Single(connection.Path.Peer.OtherConnections);
    }

    [Fact]
    public void PeerOtherClientsAreNotPresentedAsOutboundCandidates()
    {
        var snapshot = Build([Link, Reverse, Listener, External(),
            new(Ip("127.0.0.1"), 51014, Ip("127.0.0.1"), 53000, 5, 200),
            new(Ip("127.0.0.1"), 53000, Ip("127.0.0.1"), 51014, 5, 400),
        ]);
        var peer = Assert.Single(snapshot.Connections).Path!.Peer!;
        Assert.Equal(1, peer.OtherConnectionCount);
        Assert.False(Assert.Single(peer.OtherConnections).IsLocalTarget);
    }

    [Fact]
    public void RelatedLimitIsVisible_AndIdenticalRouteQueriesAreCachedWithinSnapshot()
    {
        var routes = new Routes();
        var tcp = new[] { Link, Reverse, Listener }.Concat(Enumerable.Range(0, 12).Select(i => External((ushort)(60000 + i)))).ToArray();
        var peer = Assert.Single(Build(tcp, routes).Connections).Path!.Peer!;
        Assert.Equal(12, peer.OtherConnectionCount);
        Assert.Equal(ConnectionPathAnalyzer.MaxRelatedConnections, peer.OtherConnections.Count);
        Assert.Equal(1, routes.Calls);
    }

    [Fact]
    public void SourceAddressInterfaceDisagreementRemainsVisible()
    {
        var route = Assert.Single(Build([
            new(Ip("192.0.2.10"), 51000, Ip("203.0.113.20"), 443, 5, 100),
        ], new Routes { Index = 7 }).Connections).Path!.Route;
        Assert.Equal((uint)23, route.SourceInterfaceIndex);
        Assert.Equal((uint)7, route.InterfaceIndex);
        Assert.True(route.SourceMismatch);
    }

    [Fact]
    public void MappedIpv6LoopbackMatchesIpv4PeerByValue()
    {
        var mapped = Link with { LocalAddress = Ip("::ffff:127.0.0.1"), RemoteAddress = Ip("::ffff:127.0.0.1") };
        var peer = Assert.Single(Build([mapped, Reverse, Listener, External()]).Connections).Path!.Peer!;
        Assert.Equal((uint)200, peer.Pid);
        Assert.Equal(PeerEvidence.ReversedTuple, peer.Evidence);
        Assert.Single(peer.OtherConnections);
    }

    [Fact]
    public void MissingRouteIsNotReplacedBySourceAddressInterface()
    {
        var route = Assert.Single(Build([
            new(Ip("192.0.2.10"), 51000, Ip("203.0.113.20"), 443, 5, 100),
        ], new Routes { Index = null }).Connections).Path!.Route;
        Assert.Null(route.InterfaceIndex);
        Assert.Equal((uint)23, route.SourceInterfaceIndex);
        Assert.False(route.SourceMismatch);
    }
}
