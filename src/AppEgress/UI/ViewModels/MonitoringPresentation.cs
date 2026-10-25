using AppEgress.Core;
using AppEgress.Localization;

namespace AppEgress.UI.ViewModels;

internal sealed record MonitoringConnectionRow(string Owner, string Endpoints, string Network, string Proxy, string Detail);

internal sealed record MonitoringPresentation(string CardText, string ExitSummary, string ProxyEvidence,
    string Notes, IReadOnlyList<MonitoringConnectionRow> Rows)
{
    public static MonitoringPresentation Create(MonitoringSnapshot snapshot)
    {
        var connections = snapshot.Connections;
        var paths = connections.Where(c => c.Path is { HasRemote: true }).Select(c => c.Path!).ToArray();
        var direct = connections.Where(c => !c.IsSystemProxyEndpoint && c.Path is { HasRemote: true, IsLocalTarget: false })
            .Select(c => c.Path!.Route).Where(r => r.InterfaceIndex != null).Select(RouteName).Distinct().ToArray();
        var peers = paths.Where(p => p.Peer != null).Select(p => p.Peer!).ToArray();
        var related = peers.SelectMany(p => p.OtherConnections).Where(c => !c.IsLocalTarget && c.Route.InterfaceIndex != null)
            .Select(c => RouteName(c.Route)).Distinct().ToArray();
        var proxyEntries = connections.Where(c => c.IsSystemProxyEndpoint)
            .Select(c => c.Path?.Peer?.Name ?? c.PeerProcess ?? c.RemoteEndpoint ?? "?").Distinct().ToArray();
        var peerProxyEntries = peers.SelectMany(p => p.OtherConnections).Where(c => c.IsSystemProxyEndpoint)
            .Select(c => c.ProxyProcess ?? c.RemoteEndpoint).Distinct().ToArray();

        var summaries = new List<string>();
        if (proxyEntries.Length > 0) summaries.Add(Loc.T("PathProxyConnected", Names(proxyEntries)));
        if (direct.Length > 0) summaries.Add(Loc.T("PathDirectRoutes", Names(direct)));
        if (peers.Length > 0) summaries.Add(Loc.T("PathLocalPeers", Names(peers.Select(p => p.DisplayName).Distinct().ToArray())));
        if (related.Length > 0) summaries.Add(Loc.T("PathRelatedRoutes", Names(related)));
        if (summaries.Count == 0) summaries.Add(paths.Length == 0 ? Loc.T("PathNoConnections") : Loc.T("PathUnknown"));

        int evidenceKinds = (proxyEntries.Length > 0 ? 1 : 0) + (direct.Length > 0 ? 1 : 0)
            + (related.Length > 0 ? 1 : 0) + (peerProxyEntries.Length > 0 ? 1 : 0);
        string card = evidenceKinds > 1 ? Loc.T("PathMixedEvidence")
            : proxyEntries.Length > 0 ? Loc.T("PathProxyConnected", Names(proxyEntries))
            : direct.Length > 0 ? Loc.T("PathDirectRoutes", Names(direct))
            : peerProxyEntries.Length > 0 ? Loc.T("PathPeerProxy", Names(peerProxyEntries))
            : related.Length > 0 ? Loc.T("PathRelatedRoutes", Names(related))
            : peers.Length > 0 ? Loc.T("PathLocalPeers", Names(peers.Select(p => p.Name).Distinct().ToArray())) : Loc.T("PathUnknown");
        string proxyEvidence = !snapshot.ProxyReadable ? Loc.T("MonitorProxyReadError")
            : proxyEntries.Length > 0 ? Loc.T("PathProxyConnected", Names(proxyEntries))
            : peerProxyEntries.Length > 0 ? Loc.T("PathPeerProxy", Names(peerProxyEntries))
            : Loc.T("PathProxyNotObserved");
        string notes = Loc.T("PathEvidenceLimits");
        if (snapshot.ReadErrors.Count > 0 || snapshot.TcpCount + snapshot.UdpCount > connections.Count)
            notes += " " + Loc.T("PathPartialScope");
        if (peers.Any(p => p.IsSharedProxy)) notes += " " + Loc.T("PathSharedProxy");
        if (peers.Any(p => p.OtherConnectionCount > p.OtherConnections.Count)) notes += " " + Loc.T("PathRelatedLimited");
        if (paths.Any(p => p.Route.SourceMismatch) || peers.SelectMany(p => p.OtherConnections).Any(c => c.Route.SourceMismatch))
            notes += " " + Loc.T("PathSourceMismatch");
        return new(card, string.Join("\n", summaries), proxyEvidence, notes, connections.Select(Row).ToArray());
    }

    private static MonitoringConnectionRow Row(ObservedConnection c)
    {
        string network = c.IsLocalTarget ? Loc.T("MonitorLocalConnection")
            : c.RouteInterface is { } idx ? Loc.T("MonitorRouteInterface", c.InterfaceName ?? $"#{idx}", idx)
            : Loc.T("MonitorRouteUnknown");
        string proxy = c.IsSystemProxyEndpoint ? Loc.T("MonitorProxyMatch") : Loc.T("MonitorProxyUnconfirmed");
        var details = new List<string>();
        if (c.Path is { } path)
        {
            if (!path.IsLocalTarget && path.HasRemote) network = RouteText(path.Route);
            if (path.Peer is { } peer)
            {
                network = $"{path.ProcessName} → {peer.Name}";
                var routes = peer.OtherConnections.Where(o => !o.IsLocalTarget && o.Route.InterfaceIndex != null)
                    .Select(o => RouteName(o.Route)).Distinct().ToArray();
                network += "\n" + (peer.IsSharedProxy ? Loc.T("PathSharedProxy")
                    : routes.Length > 0 ? Loc.T("PathRelatedRoutes", Names(routes)) : Loc.T("PathUnknown"));
                proxy += "\n" + Loc.T(peer.Evidence == PeerEvidence.ReversedTuple ? "PathPeerExact" : "PathPeerListener", peer.DisplayName);
                details.Add(Loc.T(peer.Evidence == PeerEvidence.ReversedTuple ? "PathPeerExact" : "PathPeerListener", peer.DisplayName));
                if (!peer.IsSharedProxy)
                {
                    proxy += "\n" + Loc.T("PathOtherCount", peer.OtherConnections.Count, peer.OtherConnectionCount);
                    details.Add(Loc.T("PathAssociationOnly"));
                    foreach (var other in peer.OtherConnections)
                    {
                        string evidence = other.IsSystemProxyEndpoint
                            ? Loc.T("PathProxyConnected", other.ProxyProcess ?? other.RemoteEndpoint)
                            : other.IsLocalTarget ? Loc.T("MonitorLocalConnection") : RouteText(other.Route);
                        details.Add($"{peer.Name} · {TcpStateText(other.State)}\n{other.LocalEndpoint} → {other.RemoteEndpoint}\n{evidence}");
                    }
                    if (peer.OtherConnections.Any(o => o.IsSystemProxyEndpoint))
                        proxy += "\n" + Loc.T("PathPeerProxy", Names(peer.OtherConnections.Where(o => o.IsSystemProxyEndpoint)
                            .Select(o => o.ProxyProcess ?? o.RemoteEndpoint).Distinct().ToArray()));
                }
                else details.Add(Loc.T("PathSharedProxy"));
            }
            else if (path.IsLocalTarget && path.HasRemote) details.Add(Loc.T("PathPeerUnknown"));
            if (path.HasRemote && !path.IsLocalTarget) details.Add(RouteText(path.Route));
        }
        else if (c.PeerProcess != null) proxy += "\n" + c.PeerProcess;
        if (details.Count == 0) details.Add(Loc.T("PathUnknown"));
        return new($"{c.Protocol} · PID {c.Pid}" + (c.TcpState is { } state ? "\n" + TcpStateText(state) : ""),
            c.LocalEndpoint + "\n→ " + (c.RemoteEndpoint ?? (c.Protocol == "UDP"
                ? Loc.T("MonitorUdpRemoteUnknown") : Loc.T("MonitorNoRemote"))), network, proxy, string.Join("\n\n", details));
    }

    private static string RouteText(ObservedRoute route)
    {
        string result = route.InterfaceIndex is { } idx
            ? Loc.T("MonitorRouteInterface", RouteName(route), idx) : Loc.T("MonitorRouteUnknown");
        if (route.SourceInterfaceName != null) result += "\n" + Loc.T("PathSourceInterface", route.SourceInterfaceName);
        if (route.SourceMismatch) result += "\n" + Loc.T("PathSourceMismatch");
        return result;
    }

    private static string RouteName(ObservedRoute route)
    {
        string name = route.InterfaceName ?? $"#{route.InterfaceIndex}";
        if (route.Kind == AdapterKind.Vpn) name += " (VPN)";
        return name;
    }

    private static string Names(IReadOnlyList<string> names) => names.Count <= 3
        ? string.Join(" / ", names) : Loc.T("PathMoreNames", string.Join(" / ", names.Take(3)), names.Count - 3);

    internal static string TcpStateText(uint state) => state switch
    {
        1 => "CLOSED", 2 => "LISTEN", 3 => "SYN_SENT", 4 => "SYN_RECEIVED", 5 => "ESTABLISHED",
        6 => "FIN_WAIT_1", 7 => "FIN_WAIT_2", 8 => "CLOSE_WAIT", 9 => "CLOSING", 10 => "LAST_ACK",
        11 => "TIME_WAIT", 12 => "DELETE_TCB", _ => $"TCP {state}",
    };
}
