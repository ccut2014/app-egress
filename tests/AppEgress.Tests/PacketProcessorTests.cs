using AppEgress.Core;
using AppEgress.Native;
using static AppEgress.Tests.TestPackets;

namespace AppEgress.Tests;

public class PacketProcessorTests
{
    private const uint EthIf = 10;
    private const uint WifiIf = 9;
    private const uint AppPid = 1234;
    private const uint OtherPid = 999;
    private const string EthIp = "192.168.1.20";
    private const string WifiIp = "192.168.43.20";
    private const string Server = "93.184.216.34";

    private sealed class FakeResolver : IProcessResolver
    {
        public Dictionary<(byte, ushort), uint> Ports { get; } = new();

        public uint? ResolvePid(byte protocol, ushort localPort) =>
            Ports.TryGetValue((protocol, localPort), out uint pid) ? pid : null;

        public string? GetImagePath(uint pid) => pid switch
        {
            AppPid => @"C:\Games\app.exe",
            OtherPid => @"C:\Windows\other.exe",
            _ => null,
        };
    }

    private sealed class FakeInjector : IPacketInjector
    {
        public List<(byte[] Packet, WinDivertAddress Addr)> Sent { get; } = new();

        public void Inject(byte[] packet, int length, in WinDivertAddress addr) => Sent.Add((packet[..length], addr));
    }

    private readonly FakeResolver _resolver = new();
    private sealed class FakeRoutes : IRouteResolver
    {
        public uint? Interface { get; set; } = WifiIf;
        public Dictionary<UInt128, uint> InterfacesBySource { get; } = new();
        public List<(UInt128 Source, UInt128 Destination)> Calls { get; } = new();
        public uint? FindInterface(UInt128 source, UInt128 destination)
        {
            Calls.Add((source, destination));
            return InterfacesBySource.TryGetValue(source, out uint index) ? index : Interface;
        }
    }

    private readonly FakeRoutes _routes = new();
    private readonly FakeInjector _injector = new();
    private readonly PacketProcessor _processor;
    private readonly Rule _rule = new() { ProgramPath = @"C:\Games\app.exe", AdapterId = "wifi", AdapterName = "Wi-Fi" };
    private readonly RuleStats _stats = new();
    private int _generation;

    private static AdapterInfo Ethernet => new()
    {
        Id = "eth", Name = "Ethernet", Description = "Gigabit Ethernet Adapter", Kind = AdapterKind.Ethernet, IsUp = true,
        IfIndex = EthIf, IPv4 = A(EthIp), HasGateway = true, Mtu = 1500,
        Subnets = [Subnet.From(A(EthIp), 24, v4: true)],
    };

    private static AdapterInfo Wifi(bool withV6 = false) => new()
    {
        Id = "wifi", Name = "Wi-Fi", Description = "Wi-Fi 6 Wireless Adapter", Kind = AdapterKind.WiFi, IsUp = true,
        IfIndex = WifiIf, IPv4 = A(WifiIp), IPv6 = withV6 ? A("2a00:1450:4010::77") : null, HasGateway = true, Mtu = 1400,
        Subnets = [Subnet.From(A(WifiIp), 24, v4: true)],
    };

    public PacketProcessorTests()
    {
        _processor = new PacketProcessor(_resolver, _routes);
        _resolver.Ports[(6, 50000)] = AppPid;
        _resolver.Ports[(17, 50001)] = AppPid;
        _resolver.Ports[(6, 50100)] = OtherPid;
        _resolver.Ports[(6, 50002)] = AppPid;
        Apply(RuleState.Active, Wifi());
    }

    private List<FlowEntry> Apply(RuleState state, AdapterInfo? target, bool includeRule = true)
    {
        var eth = Ethernet;
        var rules = includeRule
            ? new List<ActiveRule> { new() { Rule = _rule, State = state, Target = target, Stats = _stats } }
            : new List<ActiveRule>();
        var byIndex = new Dictionary<uint, AdapterInfo> { [EthIf] = eth };
        if (target != null)
            byIndex[target.IfIndex] = target;
        return _processor.ApplySnapshot(new RoutingSnapshot(rules, byIndex, true, ++_generation));
    }

    private bool Out(byte[] p, out WinDivertAddress addr, uint ifIdx = EthIf)
    {
        addr = default;
        addr.Outbound = true;
        addr.IfIdx = ifIdx;
        addr.SubIfIdx = 3;
        return _processor.Process(p, p.Length, ref addr, _injector, new byte[128]);
    }

    private bool In(byte[] p, out WinDivertAddress addr, uint ifIdx = WifiIf)
    {
        addr = default;
        addr.Outbound = false;
        addr.IfIdx = ifIdx;
        return _processor.Process(p, p.Length, ref addr, _injector, new byte[128]);
    }

    private static PacketInfo Parse(byte[] p)
    {
        Assert.True(Packet.TryParse(p, out var info));
        return info;
    }

    [Fact]
    public void NewTcpConnection_IsTranslatedBothWays()
    {
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn, mss: 1460);
        Assert.True(Out(syn, out var a));
        var s = Parse(syn);
        Assert.Equal(A(WifiIp), s.Src);
        Assert.Equal(WifiIf, a.IfIdx);
        Assert.True(a.TCPChecksum);
        Assert.True(ReferenceValid(syn));
        Assert.Equal(1, _stats.ActiveFlows);

        // MSS ограничен MTU Wi-Fi (1400 - 40)
        Assert.Equal(1360, (syn[s.TransportOffset + 22] << 8) | syn[s.TransportOffset + 23]);

        var synAck = Tcp(Server, 443, WifiIp, 50000, TcpFlags.Syn | TcpFlags.Ack, mss: 1460);
        Assert.True(In(synAck, out var b));
        var r = Parse(synAck);
        Assert.Equal(A(EthIp), r.Dst);
        Assert.Equal(EthIf, b.IfIdx);
        Assert.Equal(3u, b.SubIfIdx);
        Assert.True(ReferenceValid(synAck));

        var data = Tcp(EthIp, 50000, Server, 443, TcpFlags.Ack | TcpFlags.Psh, payload: 500);
        Assert.True(Out(data, out _));
        Assert.Equal(A(WifiIp), Parse(data).Src);
        Assert.True(_stats.BytesOut > 0);
        Assert.True(_stats.BytesIn > 0);
        Assert.Empty(_injector.Sent);
    }

    [Fact]
    public void OtherProcess_IsNotTouched()
    {
        var syn = Tcp(EthIp, 50100, Server, 443, TcpFlags.Syn);
        var copy = (byte[])syn.Clone();
        Assert.True(Out(syn, out var a));
        Assert.Equal(copy, syn);
        Assert.Equal(EthIf, a.IfIdx);
    }

    [Fact]
    public void UnrelatedInbound_IsNotTouched()
    {
        var p = Tcp(Server, 443, WifiIp, 40000, TcpFlags.Ack, payload: 10);
        var copy = (byte[])p.Clone();
        Assert.True(In(p, out _));
        Assert.Equal(copy, p);
    }

    [Fact]
    public void ExistingConnection_IsReset()
    {
        // Пакет середины соединения без NAT-записи: соединение открыто до правила.
        var data = Tcp(EthIp, 50000, Server, 443, TcpFlags.Ack | TcpFlags.Psh, seq: 100, ack: 900, payload: 20);
        Assert.False(Out(data, out _));
        Assert.Equal(2, _injector.Sent.Count);

        var (inRst, inAddr) = _injector.Sent[0];
        Assert.False(inAddr.Outbound);
        var ri = Parse(inRst);
        Assert.Equal(A(EthIp), ri.Dst);
        Assert.Equal(900u, ri.Seq);
        Assert.True(ri.IsRst);

        var (outRst, outAddr) = _injector.Sent[1];
        Assert.True(outAddr.Outbound);
        Assert.Equal(A(EthIp), Parse(outRst).Src);
        Assert.Equal(1, _stats.ConnectionsReset);
    }

    [Fact]
    public void LocalNetwork_IsExcluded()
    {
        var syn = Tcp(EthIp, 50000, "192.168.1.50", 445, TcpFlags.Syn);
        var copy = (byte[])syn.Clone();
        Assert.True(Out(syn, out _));
        Assert.Equal(copy, syn);
    }

    [Fact]
    public void AlreadyOnTargetAdapter_IsNotTouched()
    {
        var syn = Tcp(WifiIp, 50000, Server, 443, TcpFlags.Syn);
        var copy = (byte[])syn.Clone();
        Assert.True(Out(syn, out _, ifIdx: WifiIf));
        Assert.Equal(copy, syn);
    }

    [Fact]
    public void VpnWithoutGateway_UsesDestinationRouteAndReverseNat()
    {
        var vpn = new AdapterInfo
        {
            Id = "vpn-b", Name = "VPN B", Description = "Private VPN", Kind = AdapterKind.Vpn,
            IsUp = true, IfIndex = WifiIf, IPv4 = A("10.20.0.2"), HasGateway = false,
        };
        Apply(RuleState.Active, vpn);
        var syn = Tcp(EthIp, 50000, "10.50.0.10", 443, TcpFlags.Syn);
        Assert.True(Out(syn, out _));
        Assert.Equal(A("10.20.0.2"), Parse(syn).Src);
        Assert.Equal((A("10.20.0.2"), A("10.50.0.10")), Assert.Single(_routes.Calls));
        Assert.True(ReferenceValid(syn));
        var reply = Tcp("10.50.0.10", 443, "10.20.0.2", 50000, TcpFlags.Syn | TcpFlags.Ack);
        Assert.True(In(reply, out _));
        Assert.Equal(A(EthIp), Parse(reply).Dst);
        Assert.True(ReferenceValid(reply));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EthIf)]
    public void MissingOrWrongRoute_BlocksWithoutFallback(uint? routeInterface)
    {
        _routes.Interface = routeInterface;
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        var copy = syn.ToArray();
        Assert.False(Out(syn, out _));
        Assert.Equal(copy, syn);
        var (rst, addr) = Assert.Single(_injector.Sent);
        Assert.False(addr.Outbound);
        Assert.True(ReferenceValid(rst));
        Assert.Equal(1, _stats.RouteFailures);
        Assert.False(_rule.KillSwitch); // An active rule does not silently use another VPN.
        var udp = Udp(EthIp, 50001, Server, 443);
        Assert.False(Out(udp, out _));
        Assert.Equal(2, _stats.PacketsBlocked);
        Assert.Single(_injector.Sent);
    }

    [Fact]
    public void ExistingNatFlow_RechecksRouteAndStopsOnMismatch()
    {
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.True(Out(syn, out _));
        _routes.Interface = EthIf;
        var flow = Assert.Single(_processor.Table.Entries);
        flow.RouteCheckAt = 0; // Expire the one-second route cache deterministically.
        var data = Tcp(EthIp, 50000, Server, 443, TcpFlags.Ack, payload: 10);
        Assert.False(Out(data, out _));
        Assert.Equal(A(EthIp), Parse(data).Src);
        Assert.Equal(0, _stats.ActiveFlows);
        Assert.False(Assert.Single(_injector.Sent).Addr.Outbound);
    }

    [Fact]
    public void BlockedRoute_RecoversWhenRouteAppears()
    {
        _routes.Interface = null;
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.False(Out(syn, out _));
        Assert.Single(_processor.Table.Entries).RecheckAt = 1;
        _routes.Interface = WifiIf;
        var retry = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.True(Out(retry, out _));
        Assert.Equal(A(WifiIp), Parse(retry).Src);
        Assert.Equal(1, _stats.ActiveFlows);
        Assert.True(ReferenceValid(retry));
    }

    [Fact]
    public void CachedPassOnTarget_DoesNotAllowPacketOnAnotherInterface()
    {
        var syn = Tcp(WifiIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.True(Out(syn, out _, WifiIf));
        _routes.Interface = EthIf;
        var retry = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.False(Out(retry, out _, EthIf));
    }

    [Fact]
    public void RouteFailure_DoesNotSendRemoteResetOnWrongInterface()
    {
        _routes.Interface = EthIf;
        var data = Tcp(EthIp, 50000, Server, 443, TcpFlags.Ack, payload: 10);
        Assert.False(Out(data, out _));
        Assert.False(Assert.Single(_injector.Sent).Addr.Outbound);
    }

    [Fact]
    public void TwoApps_KeepIndependentVpnTargets()
    {
        var vpnA = new AdapterInfo { Id = "vpn-a", Name = "VPN A", Description = "Internet VPN",
            Kind = AdapterKind.Vpn, IsUp = true, IfIndex = 30, IPv4 = A("10.10.0.2"), HasGateway = true };
        var vpnB = new AdapterInfo { Id = "vpn-b", Name = "VPN B", Description = "Private VPN",
            Kind = AdapterKind.Vpn, IsUp = true, IfIndex = 31, IPv4 = A("10.20.0.2"), HasGateway = false };
        var ruleA = new Rule { ProgramPath = @"C:\Windows\other.exe", AdapterId = vpnA.Id };
        _routes.InterfacesBySource[vpnA.IPv4!.Value] = vpnA.IfIndex;
        _routes.InterfacesBySource[vpnB.IPv4!.Value] = vpnB.IfIndex;
        _processor.ApplySnapshot(new RoutingSnapshot(
            [new ActiveRule { Rule = ruleA, Target = vpnA, State = RuleState.Active, Stats = new RuleStats() },
             new ActiveRule { Rule = _rule, Target = vpnB, State = RuleState.Active, Stats = _stats }],
            new Dictionary<uint, AdapterInfo> { [EthIf] = Ethernet, [30] = vpnA, [31] = vpnB }, false, ++_generation));

        var publicSyn = Tcp(EthIp, 50100, Server, 443, TcpFlags.Syn);
        var privateSyn = Tcp(EthIp, 50000, "10.50.0.10", 443, TcpFlags.Syn);
        Assert.True(Out(publicSyn, out _));
        Assert.True(Out(privateSyn, out _));
        Assert.Equal(vpnA.IPv4, Parse(publicSyn).Src);
        Assert.Equal(vpnB.IPv4, Parse(privateSyn).Src);
        Assert.True(ReferenceValid(publicSyn));
        Assert.True(ReferenceValid(privateSyn));
        Assert.Equal(2, _processor.Table.Count);
        var replyA = Tcp(Server, 443, "10.10.0.2", 50100, TcpFlags.Syn | TcpFlags.Ack);
        var replyB = Tcp("10.50.0.10", 443, "10.20.0.2", 50000, TcpFlags.Syn | TcpFlags.Ack);
        Assert.True(In(replyA, out _, 30));
        Assert.True(In(replyB, out _, 31));
        Assert.Equal(A(EthIp), Parse(replyA).Dst);
        Assert.Equal(A(EthIp), Parse(replyB).Dst);
        Assert.True(ReferenceValid(replyA));
        Assert.True(ReferenceValid(replyB));
    }

    [Fact]
    public void BlockedFirstFragment_AlsoBlocksFollowingFragments()
    {
        _routes.Interface = null;
        var first = Udp(EthIp, 50001, Server, 443, payload: 64);
        first[6] |= 0x20;
        Packet.TryParse(first, out var fi);
        Packet.RecalcIpHeaderChecksum(first, fi);
        Assert.False(Out(first, out _));

        var second = Udp(EthIp, 50001, Server, 443, payload: 64);
        second[7] = 9;
        Packet.TryParse(second, out var si);
        Packet.RecalcIpHeaderChecksum(second, si);
        Assert.False(Out(second, out _));
        Assert.Equal(A(EthIp), Parse(second).Src);
    }

    [Fact]
    public void CachedFragmentNat_RechecksRoute()
    {
        var first = Udp(EthIp, 50001, Server, 443, payload: 64);
        first[6] |= 0x20;
        Packet.TryParse(first, out var fi);
        Packet.RecalcIpHeaderChecksum(first, fi);
        Assert.True(Out(first, out _));
        Assert.Single(_processor.Table.Entries).RouteCheckAt = 0;
        _routes.Interface = EthIf;
        var second = Udp(EthIp, 50001, Server, 443, payload: 64);
        second[7] = 9;
        Packet.TryParse(second, out var si);
        Packet.RecalcIpHeaderChecksum(second, si);
        Assert.False(Out(second, out _));
        Assert.Equal(A(EthIp), Parse(second).Src);
    }

    [Fact]
    public void Multicast_IsNotTouched()
    {
        var p = Udp(EthIp, 50001, "224.0.0.251", 5353);
        var copy = (byte[])p.Clone();
        Assert.True(Out(p, out _));
        Assert.Equal(copy, p);
    }

    [Fact]
    public void Ipv6_WithoutTargetV6_IsRejected()
    {
        var syn = Tcp("2001:db8::15", 50000, "2606:2800:220:1::1946", 443, TcpFlags.Syn);
        Assert.False(Out(syn, out _));
        var (rst, addr) = Assert.Single(_injector.Sent);
        Assert.False(addr.Outbound);
        Assert.True(Parse(rst).IsRst);
    }

    [Fact]
    public void Ipv6_WithTargetV6_IsTranslated()
    {
        Apply(RuleState.Active, Wifi(withV6: true));
        var syn = Tcp("2001:db8::15", 50000, "2606:2800:220:1::1946", 443, TcpFlags.Syn, mss: 1440);
        Assert.True(Out(syn, out var a));
        Assert.Equal(A("2a00:1450:4010::77"), Parse(syn).Src);
        Assert.Equal(WifiIf, a.IfIdx);
        Assert.True(ReferenceValid(syn));
    }

    [Fact]
    public void Udp_IsTranslated_AndRepliesFromNewPeersAccepted()
    {
        var q = Udp(EthIp, 50001, "8.8.8.8", 53);
        Assert.True(Out(q, out _));
        Assert.Equal(A(WifiIp), Parse(q).Src);

        var reply = Udp("8.8.8.8", 53, WifiIp, 50001);
        Assert.True(In(reply, out _));
        Assert.Equal(A(EthIp), Parse(reply).Dst);
        Assert.True(ReferenceValid(reply));

        // Пакет от другого адресата на тот же NAT-порт (P2P, STUN) тоже возвращается программе.
        var other = Udp("1.1.1.1", 3478, WifiIp, 50001);
        Assert.True(In(other, out _));
        Assert.Equal(A(EthIp), Parse(other).Dst);
    }

    [Fact]
    public void KillSwitch_BlocksTraffic()
    {
        Apply(RuleState.Blocking, null);
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.False(Out(syn, out _));
        Assert.Single(_injector.Sent); // только RST приложению, серверу слать нечего
        var udp = Udp(EthIp, 50001, "8.8.8.8", 53);
        Assert.False(Out(udp, out _));
        Assert.Equal(2, _stats.PacketsBlocked);
    }

    [Fact]
    public void Waiting_PassesTraffic()
    {
        Apply(RuleState.Waiting, null);
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        var copy = (byte[])syn.Clone();
        Assert.True(Out(syn, out _));
        Assert.Equal(copy, syn);
    }

    [Fact]
    public void UnknownPid_PassesAndRechecksLater()
    {
        var syn = Tcp(EthIp, 51111, Server, 443, TcpFlags.Syn);
        var copy = (byte[])syn.Clone();
        Assert.True(Out(syn, out _));
        Assert.Equal(copy, syn);
        Assert.True(_processor.Table.TryGet(new FlowKey(6, 51111, A(Server), 443), out var e));
        Assert.Equal(FlowAction.Pass, e.Action);
        Assert.NotEqual(0, e.RecheckAt);
    }

    [Fact]
    public void DisablingRule_InvalidatesNatFlows()
    {
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.True(Out(syn, out _));
        Assert.Equal(1, _stats.ActiveFlows);

        var invalid = Apply(RuleState.Active, null, includeRule: false);
        var flow = Assert.Single(invalid);
        Assert.Equal(A(EthIp), flow.OrigLocal);
        Assert.Equal(0, _stats.ActiveFlows);

        // Следующий пакет того же соединения идёт обычным маршрутом
        var data = Tcp(EthIp, 50000, Server, 443, TcpFlags.Ack, payload: 5);
        var copy = (byte[])data.Clone();
        Assert.True(Out(data, out _));
        Assert.Equal(copy, data);
    }

    [Fact]
    public void ReapplyingSameRule_KeepsNatFlows()
    {
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.True(Out(syn, out _));
        Assert.Empty(Apply(RuleState.Active, Wifi()));

        var data = Tcp(EthIp, 50000, Server, 443, TcpFlags.Ack, payload: 5);
        Assert.True(Out(data, out _));
        Assert.Equal(A(WifiIp), Parse(data).Src);
        Assert.Empty(_injector.Sent);
    }

    [Fact]
    public void TargetIpChange_InvalidatesNatFlows()
    {
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.True(Out(syn, out _));
        var changed = new AdapterInfo
        {
            Id = "wifi", Name = "Wi-Fi", Description = "Wi-Fi 6 Wireless Adapter", Kind = AdapterKind.WiFi, IsUp = true,
            IfIndex = WifiIf, IPv4 = A("10.0.0.5"), Mtu = 1500,
        };
        Assert.Single(Apply(RuleState.Active, changed));
    }

    [Fact]
    public void Fragments_FollowTheirFlow()
    {
        var first = Udp(EthIp, 50001, "8.8.8.8", 4000, payload: 64);
        first[6] |= 0x20; // MF
        Packet.TryParse(first, out var fi);
        Packet.RecalcIpHeaderChecksum(first, fi);
        Assert.True(Out(first, out _));
        Assert.Equal(A(WifiIp), Parse(first).Src);

        var second = Udp(EthIp, 50001, "8.8.8.8", 4000, payload: 64);
        second[6] = 0x00;
        second[7] = 0x09; // смещение 72 байта, тот же Id 0x1234
        Packet.TryParse(second, out var si);
        Packet.RecalcIpHeaderChecksum(second, si);
        Assert.True(Out(second, out var a));
        Assert.Equal(A(WifiIp), Parse(second).Src);
        Assert.Equal(WifiIf, a.IfIdx);
        Assert.Equal(0, ReferenceChecksum(second[..20]));
    }

    [Fact]
    public void Sweep_RemovesIdleFlows()
    {
        var syn = Tcp(EthIp, 50000, Server, 443, TcpFlags.Syn);
        Assert.True(Out(syn, out _));
        var fin = Tcp(EthIp, 50000, Server, 443, TcpFlags.Fin | TcpFlags.Ack);
        Assert.True(Out(fin, out _));
        long later = Environment.TickCount64 + (long)NatTable.TcpClosingTimeout.TotalMilliseconds + 1000;
        Assert.Equal(1, _processor.Table.Sweep(later));
        Assert.Equal(0, _stats.ActiveFlows);
    }

    [Fact]
    public void BuildFilter_IncludesTargets()
    {
        var snap = new RoutingSnapshot(
            [new ActiveRule { Rule = _rule, State = RuleState.Active, Target = Wifi(withV6: true), Stats = _stats }],
            new Dictionary<uint, AdapterInfo>(), true, 1);
        string? f = PacketRouter.BuildFilter(snap);
        Assert.NotNull(f);
        Assert.Contains("ip.DstAddr == 192.168.43.20", f);
        Assert.Contains("ipv6.DstAddr == 2a00:1450:4010::77", f);
        Assert.StartsWith("!loopback and (", f);

        Assert.Null(PacketRouter.BuildFilter(RoutingSnapshot.Empty));
        var waiting = new RoutingSnapshot(
            [new ActiveRule { Rule = _rule, State = RuleState.Waiting, Target = null, Stats = _stats }],
            new Dictionary<uint, AdapterInfo>(), true, 1);
        Assert.Null(PacketRouter.BuildFilter(waiting));
    }

    [Fact]
    public void RuleMatching()
    {
        var byPath = new Rule { ProgramPath = @"C:\Games\app.exe" };
        Assert.True(byPath.Matches(@"c:\games\APP.exe"));
        Assert.False(byPath.Matches(@"D:\Other\app.exe"));
        var byName = new Rule { ProgramPath = "app.exe" };
        Assert.True(byName.MatchesByNameOnly);
        Assert.True(byName.Matches(@"D:\Other\app.exe"));
        Assert.False(byName.Matches(@"D:\Other\app2.exe"));
        Assert.False(byName.Matches(null));
    }
}
