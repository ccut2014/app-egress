using System.IO;
using System.Net;
using System.Net.Sockets;
using AppEgress.Native;

namespace AppEgress.Core;

internal enum PeerEvidence { ReversedTuple, ListenerOnly }

internal sealed record ObservedRoute(uint? InterfaceIndex, string? InterfaceName, AdapterKind? Kind,
    uint? SourceInterfaceIndex, string? SourceInterfaceName)
{
    public static readonly ObservedRoute Unknown = new(null, null, null, null, null);
    public bool SourceMismatch => InterfaceIndex is { } route && SourceInterfaceIndex is { } source && route != source;
}

internal sealed record RelatedConnection(string LocalEndpoint, string RemoteEndpoint, uint State,
    bool IsLocalTarget, bool IsSystemProxyEndpoint, string? ProxyProcess, ObservedRoute Route);

internal sealed record LocalConnectionPeer(uint Pid, string Name, PeerEvidence Evidence,
    IReadOnlyList<RelatedConnection> OtherConnections, int OtherConnectionCount, bool IsSharedProxy)
{
    public string DisplayName => $"{Name} (PID {Pid})";
}

internal sealed record ConnectionPath(string ProcessName, bool HasRemote, bool IsLocalTarget,
    ObservedRoute Route, LocalConnectionPeer? Peer);

/// <summary>
/// A one-snapshot index, not a forwarding graph. Other connections of a peer process are
/// association evidence only: TCP tables cannot correlate its incoming and outgoing payloads.
/// </summary>
internal sealed class ConnectionPathAnalyzer
{
    internal const int MaxRelatedConnections = 8;
    private readonly Dictionary<uint, TcpConnectionInfo[]> _byPid;
    private readonly Dictionary<(IPAddress, ushort, IPAddress, ushort), TcpConnectionInfo[]> _byTuple;
    private readonly Dictionary<ushort, TcpConnectionInfo[]> _listeners;
    private readonly HashSet<IPAddress> _localAddresses;
    private readonly HashSet<uint> _systemProxyOwners;
    private readonly IReadOnlyList<AdapterInfo> _adapters;
    private readonly Func<uint, string?> _imagePath;
    private readonly SystemProxyConfiguration _proxy;
    private readonly IRouteResolver _routes;
    private readonly Dictionary<(IPAddress, IPAddress), ObservedRoute> _routeCache = new();

    public ConnectionPathAnalyzer(IReadOnlyList<TcpConnectionInfo> tcp, IReadOnlyList<UdpEndpointInfo> udp,
        Func<uint, string?> imagePath, IReadOnlyList<AdapterInfo> adapters, SystemProxyConfiguration proxy, IRouteResolver routes)
    {
        _adapters = adapters;
        _imagePath = imagePath;
        _proxy = proxy;
        _routes = routes;
        _byPid = tcp.GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => g.ToArray());
        _byTuple = tcp.Where(HasRemote).GroupBy(c => Tuple(c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort))
            .ToDictionary(g => g.Key, g => g.ToArray());
        _listeners = tcp.Where(c => c.State == 2).GroupBy(c => c.LocalPort).ToDictionary(g => g.Key, g => g.ToArray());
        _localAddresses = tcp.Select(c => Normalize(c.LocalAddress)).Concat(udp.Select(c => Normalize(c.LocalAddress)))
            .Where(a => !IsAny(a)).ToHashSet();
        foreach (var adapter in adapters)
        {
            if (adapter.IPv4 is { } v4) _localAddresses.Add(Addr.ToIPAddress(v4));
            if (adapter.IPv6 is { } v6) _localAddresses.Add(Addr.ToIPAddress(v6));
        }
        _systemProxyOwners = tcp.Where(c => c.State == 2 && proxy.Enabled
            && proxy.Endpoints.Any(e => e.Port == c.LocalPort && IsLocal(e.Address) && ListensOn(c, e.Address)))
            .Select(c => c.Pid).ToHashSet();
    }

    public ConnectionPath Analyze(TcpConnectionInfo connection)
    {
        bool remote = HasRemote(connection);
        bool local = remote && IsLocal(connection.RemoteAddress);
        var route = remote && !local ? RouteFor(connection) : ObservedRoute.Unknown;
        LocalConnectionPeer? peer = null;
        if (local && FindPeer(connection) is { } owner)
        {
            bool sharedProxy = _proxy.Matches(connection.RemoteAddress, connection.RemotePort) || _systemProxyOwners.Contains(owner.Pid);
            // A proxy may multiplex every client's traffic. Never attribute its upstream to this app.
            var other = sharedProxy ? Array.Empty<TcpConnectionInfo>() : _byPid.GetValueOrDefault(owner.Pid, [])
                .Where(c => c.State is (3 or 4 or 5 or 8) && HasRemote(c)
                    && !IsReverseOf(c, connection) && !IsAccepted(c))
                .Distinct().OrderByDescending(c => _proxy.Matches(c.RemoteAddress, c.RemotePort))
                .ThenBy(c => IsLocal(c.RemoteAddress)).ThenBy(c => c.RemoteAddress.ToString(), StringComparer.Ordinal)
                .ThenBy(c => c.RemotePort).ThenBy(c => c.LocalPort).ToArray();
            var related = other.Take(MaxRelatedConnections).Select(c =>
            {
                bool isLocal = IsLocal(c.RemoteAddress);
                bool proxy = _proxy.Matches(c.RemoteAddress, c.RemotePort);
                string? proxyProcess = proxy && isLocal && FindPeer(c) is { } proxyOwner ? Name(proxyOwner.Pid) : null;
                return new RelatedConnection(Endpoint(c.LocalAddress, c.LocalPort), Endpoint(c.RemoteAddress, c.RemotePort),
                    c.State, isLocal, proxy, proxyProcess, isLocal ? ObservedRoute.Unknown : RouteFor(c));
            }).ToArray();
            peer = new(owner.Pid, Name(owner.Pid), owner.Evidence, related, other.Length, sharedProxy);
        }
        return new(Name(connection.Pid), remote, local, route, peer);
    }

    private (uint Pid, PeerEvidence Evidence)? FindPeer(TcpConnectionInfo connection)
    {
        var reverse = Tuple(connection.RemoteAddress, connection.RemotePort, connection.LocalAddress, connection.LocalPort);
        var owners = _byTuple.GetValueOrDefault(reverse, []).Select(c => c.Pid).Distinct().ToArray();
        if (owners.Length > 0)
            return owners.Length == 1 && owners[0] != 0 ? (owners[0], PeerEvidence.ReversedTuple) : null;
        owners = _listeners.GetValueOrDefault(connection.RemotePort, [])
            .Where(c => ListensOn(c, connection.RemoteAddress)).Select(c => c.Pid).Distinct().ToArray();
        return owners.Length == 1 && owners[0] != 0 ? (owners[0], PeerEvidence.ListenerOnly) : null;
    }

    private bool IsAccepted(TcpConnectionInfo connection) => _listeners.GetValueOrDefault(connection.LocalPort, [])
        .Any(c => c.Pid == connection.Pid && ListensOn(c, connection.LocalAddress));

    private static bool ListensOn(TcpConnectionInfo listener, IPAddress address)
    {
        var bind = Normalize(listener.LocalAddress);
        address = Normalize(address);
        return bind.AddressFamily == address.AddressFamily && (IsAny(bind) || bind.Equals(address));
    }

    private ObservedRoute RouteFor(TcpConnectionInfo connection)
    {
        var source = Normalize(connection.LocalAddress);
        var destination = Normalize(connection.RemoteAddress);
        if (_routeCache.TryGetValue((source, destination), out var cached)) return cached;
        var owners = _adapters.Where(a => a.IfIndex != 0 &&
            (a.IPv4 == Addr.FromIPAddress(source) || a.IPv6 == Addr.FromIPAddress(source))).ToArray();
        var sourceAdapter = owners.Length == 1 ? owners[0] : null;
        uint? index = null;
        // The existing native route wrapper cannot pass IPv6 scope IDs.
        if (!IsAny(source) && !Scoped(source) && !Scoped(destination))
        {
            try { index = _routes.FindInterface(Addr.FromIPAddress(source), Addr.FromIPAddress(destination)); }
            catch (Exception) { /* Keep unknown; this is an observation, never a routing action. */ }
        }
        if (index == 0) index = null;
        var adapter = index is { } idx ? _adapters.FirstOrDefault(a => a.IfIndex == idx) : null;
        var result = new ObservedRoute(index, adapter?.Name, adapter?.Kind, sourceAdapter?.IfIndex, sourceAdapter?.Name);
        _routeCache[(source, destination)] = result;
        return result;
    }

    private string Name(uint pid) => _imagePath(pid) is { Length: > 0 } path ? Path.GetFileName(path) : $"PID {pid}";
    private bool IsLocal(IPAddress address) => IPAddress.IsLoopback(Normalize(address)) || _localAddresses.Contains(Normalize(address));
    private static bool Scoped(IPAddress a) => a.AddressFamily == AddressFamily.InterNetworkV6 && (a.IsIPv6LinkLocal || a.ScopeId != 0);
    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
    private static bool IsAny(IPAddress a) => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);
    private static bool HasRemote(TcpConnectionInfo c) => c.State != 2 && c.RemotePort != 0 && !IsAny(c.RemoteAddress);
    private static bool IsReverseOf(TcpConnectionInfo a, TcpConnectionInfo b) =>
        Tuple(a.LocalAddress, a.LocalPort, a.RemoteAddress, a.RemotePort)
            .Equals(Tuple(b.RemoteAddress, b.RemotePort, b.LocalAddress, b.LocalPort));
    private static (IPAddress, ushort, IPAddress, ushort) Tuple(IPAddress local, ushort localPort, IPAddress remote, ushort remotePort)
        => (Normalize(local), localPort, Normalize(remote), remotePort);
    private static string Endpoint(IPAddress address, ushort port) => new IPEndPoint(address, port).ToString();
}
