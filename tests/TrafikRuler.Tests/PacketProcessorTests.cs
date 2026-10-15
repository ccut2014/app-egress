using TrafikRuler.Core;
using TrafikRuler.Native;
using static TrafikRuler.Tests.TestPackets;

namespace TrafikRuler.Tests;

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
        _processor = new PacketProcessor(_resolver);
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
