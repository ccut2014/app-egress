using System.IO;
using System.Net;
using Microsoft.Win32;
using AppEgress.Native;

namespace AppEgress.Core;

internal sealed record ObservedConnection(uint Pid, string Protocol, string LocalEndpoint,
    string? RemoteEndpoint, uint? TcpState, bool IsLocalTarget, uint? RouteInterface,
    string? InterfaceName, bool IsSystemProxyEndpoint, string? PeerProcess)
{
    public ConnectionPath? Path { get; init; }
}

internal sealed record MonitoringSnapshot(DateTimeOffset? CapturedAt, IReadOnlyList<ObservedConnection> Connections,
    int TcpCount, int UdpCount, bool ProxyEnabled, bool PacConfigured, bool ProxyReadable,
    IReadOnlyList<string> ReadErrors)
{
    public static readonly MonitoringSnapshot Empty = new(null, [], 0, 0, false, false, true, []);
    public const int MaxRows = 200;
}

// Only compare literal addresses. Do not resolve proxy hostnames, fetch PAC scripts, or expose credentials.
internal sealed record SystemProxyConfiguration(bool Enabled, bool PacConfigured, bool Readable,
    IReadOnlyList<IPEndPoint> Endpoints)
{
    public static SystemProxyConfiguration Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            return Parse(key?.GetValue("ProxyEnable") is int enabled && enabled != 0,
                key?.GetValue("ProxyServer") as string,
                !string.IsNullOrWhiteSpace(key?.GetValue("AutoConfigURL") as string));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new(false, false, false, []);
        }
    }

    internal static SystemProxyConfiguration Parse(bool enabled, string? server, bool pac)
    {
        var endpoints = new List<IPEndPoint>();
        foreach (string entry in (server ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string value = entry[(entry.IndexOf('=') + 1)..].Trim();
            if (!Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value,
                    UriKind.Absolute, out var uri) || uri.Port is <= 0 or > 65535)
                continue;
            string host = uri.Host.Trim('[', ']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                endpoints.Add(new(IPAddress.Loopback, uri.Port));
                endpoints.Add(new(IPAddress.IPv6Loopback, uri.Port));
            }
            else if (IPAddress.TryParse(host, out var address))
                endpoints.Add(new(address, uri.Port));
        }
        return new(enabled, pac, true, endpoints.Distinct().ToArray());
    }

    public bool Matches(IPAddress address, ushort port) => Enabled
        && Endpoints.Any(e => e.Port == port && Normalize(e.Address).Equals(Normalize(address)));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>Read-only, one-second metadata snapshots. This service never opens a WinDivert handle.</summary>
internal sealed class ConnectionMonitor : IDisposable
{
    private readonly ProcessResolver _resolver;
    private readonly Func<IReadOnlyList<AdapterInfo>> _adapters;
    private readonly IRouteResolver _routes = new SystemRouteResolver();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private Rule[] _rules = [];
    private IReadOnlyDictionary<Guid, MonitoringSnapshot> _snapshots = new Dictionary<Guid, MonitoringSnapshot>();
    private bool _disposed;

    public ConnectionMonitor(ProcessResolver resolver, Func<IReadOnlyList<AdapterInfo>> adapters)
    {
        _resolver = resolver;
        _adapters = adapters;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public MonitoringSnapshot GetSnapshot(Guid id) =>
        Volatile.Read(ref _snapshots).GetValueOrDefault(id) ?? MonitoringSnapshot.Empty;

    public void Configure(IEnumerable<Rule> rules)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _rules = rules.Where(r => r.Mode == RuleMode.Monitor && r.Enabled).Select(r => r.Clone()).ToArray();
            Volatile.Write(ref _snapshots, new Dictionary<Guid, MonitoringSnapshot>());
            _timer.Change(_rules.Length == 0 ? Timeout.Infinite : 0, 1000);
        }
    }

    private void Poll()
    {
        // Avoid queued overlapping polls when process inspection is slow.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed || _rules.Length == 0) return;
            var tcp = IpHelper.GetTcpConnections(false, out uint tcp4);
            tcp.AddRange(IpHelper.GetTcpConnections(true, out uint tcp6));
            var udp = IpHelper.GetUdpEndpoints(false, out uint udp4);
            udp.AddRange(IpHelper.GetUdpEndpoints(true, out uint udp6));
            var errors = new List<string>();
            if (tcp4 != 0) errors.Add($"TCP/IPv4: {tcp4}");
            if (tcp6 != 0) errors.Add($"TCP/IPv6: {tcp6}");
            if (udp4 != 0) errors.Add($"UDP/IPv4: {udp4}");
            if (udp6 != 0) errors.Add($"UDP/IPv6: {udp6}");
            var paths = tcp.Select(c => c.Pid).Concat(udp.Select(c => c.Pid)).Distinct()
                .ToDictionary(pid => pid, pid => _resolver.GetImagePath(pid));
            var proxy = SystemProxyConfiguration.Read();
            var adapters = _adapters();
            var analyzer = new ConnectionPathAnalyzer(tcp, udp, pid => paths.GetValueOrDefault(pid), adapters, proxy, _routes);
            var snapshots = new Dictionary<Guid, MonitoringSnapshot>();
            foreach (var rule in _rules)
                snapshots[rule.Id] = Build(rule, tcp, udp, pid => paths.GetValueOrDefault(pid), adapters, proxy, _routes, errors, analyzer);
            Volatile.Write(ref _snapshots, snapshots);
        }
        catch (Exception)
        {
            // Do not retain stale connections or log endpoint/process data on a read failure.
            var failed = new MonitoringSnapshot(DateTimeOffset.Now, [], 0, 0, false, false, false, ["IP Helper"]);
            Volatile.Write(ref _snapshots, _rules.ToDictionary(r => r.Id, _ => failed));
        }
        finally { Monitor.Exit(_gate); }
    }

    internal static MonitoringSnapshot Build(Rule rule, IReadOnlyList<TcpConnectionInfo> tcp,
        IReadOnlyList<UdpEndpointInfo> udp, Func<uint, string?> imagePath, IReadOnlyList<AdapterInfo> adapters,
        SystemProxyConfiguration proxy, IRouteResolver routes, IReadOnlyList<string> errors, ConnectionPathAnalyzer? analyzer = null)
    {
        var matchingPids = tcp.Select(c => c.Pid).Concat(udp.Select(c => c.Pid)).Distinct()
            .Where(pid => rule.Matches(imagePath(pid))).ToHashSet();
        var appTcp = tcp.Where(c => matchingPids.Contains(c.Pid)).OrderBy(c => c.State == 2)
            .ThenBy(c => c.Pid).ThenBy(c => c.LocalPort).ThenBy(c => c.RemoteAddress.ToString(), StringComparer.Ordinal)
            .ThenBy(c => c.RemotePort).ToArray();
        var appUdp = udp.Where(c => matchingPids.Contains(c.Pid)).OrderBy(c => c.Pid).ThenBy(c => c.LocalPort).ToArray();
        analyzer ??= new ConnectionPathAnalyzer(tcp, udp, imagePath, adapters, proxy, routes);
        var rows = new List<ObservedConnection>();
        foreach (var c in appTcp.Take(MonitoringSnapshot.MaxRows))
        {
            var path = analyzer.Analyze(c);
            rows.Add(new(c.Pid, "TCP", Endpoint(c.LocalAddress, c.LocalPort),
                path.HasRemote ? Endpoint(c.RemoteAddress, c.RemotePort) : null, c.State, path.IsLocalTarget,
                path.Route.InterfaceIndex, path.Route.InterfaceName,
                path.HasRemote && proxy.Matches(c.RemoteAddress, c.RemotePort), path.Peer?.DisplayName) { Path = path });
        }
        foreach (var c in appUdp.Take(MonitoringSnapshot.MaxRows - rows.Count))
            rows.Add(new(c.Pid, "UDP", Endpoint(c.LocalAddress, c.LocalPort), null, null, false, null, null, false, null));
        return new(DateTimeOffset.Now, rows, appTcp.Length, appUdp.Length, proxy.Enabled, proxy.PacConfigured,
            proxy.Readable, errors.ToArray());
    }

    private static string Endpoint(IPAddress address, ushort port) => new IPEndPoint(address, port).ToString();

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _rules = [];
            Volatile.Write(ref _snapshots, new Dictionary<Guid, MonitoringSnapshot>());
        }
    }
}
