using System.Buffers.Binary;
using AppEgress.Core;
using static AppEgress.Tests.TestPackets;

namespace AppEgress.Tests;

public class PacketTests
{
    [Fact]
    public void Ipv4HeaderChecksum_MatchesKnownVector()
    {
        // Пример из RFC/Википедии: 4500 0073 0000 4000 4011 b861 c0a8 0001 c0a8 00c7
        byte[] p = new byte[0x73];
        byte[] hdr = [0x45, 0x00, 0x00, 0x73, 0x00, 0x00, 0x40, 0x00, 0x40, 0x11, 0x00, 0x00, 0xc0, 0xa8, 0x00, 0x01, 0xc0, 0xa8, 0x00, 0xc7];
        hdr.CopyTo(p, 0);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), 0x73 - 20); // длина UDP
        Assert.True(Packet.TryParse(p, out var info));
        Packet.RecalcIpHeaderChecksum(p, info);
        Assert.Equal(0xb8, p[10]);
        Assert.Equal(0x61, p[11]);
    }

    [Theory]
    [InlineData("192.168.1.20", "93.184.216.34")]
    [InlineData("2001:db8::15", "2606:2800:220:1:248:1893:25c8:1946")]
    public void Parse_Tcp(string src, string dst)
    {
        var p = Tcp(src, 50000, dst, 443, TcpFlags.Syn, seq: 12345, mss: 1460, payload: 0);
        Assert.True(Packet.TryParse(p, out var info));
        Assert.True(info.IsTcp);
        Assert.True(info.IsSynOnly);
        Assert.Equal(A(src), info.Src);
        Assert.Equal(A(dst), info.Dst);
        Assert.Equal(50000, info.SrcPort);
        Assert.Equal(443, info.DstPort);
        Assert.Equal(12345u, info.Seq);
        Assert.Equal(24, info.TcpHeaderLength);
        Assert.True(Packet.ChecksumsValid(p, info));
        Assert.True(ReferenceValid(p));
    }

    [Theory]
    [InlineData("192.168.1.20", "93.184.216.34", "192.168.43.20", false)]
    [InlineData("192.168.1.20", "93.184.216.34", "192.168.43.20", true)]
    [InlineData("2001:db8::15", "2606:2800:220:1::1946", "2a00:1450:4010::77", false)]
    [InlineData("2001:db8::15", "2606:2800:220:1::1946", "2a00:1450:4010::77", true)]
    public void RewriteSource_Full_ProducesValidChecksums(string src, string dst, string nat, bool udp)
    {
        var p = udp ? Udp(src, 50000, dst, 53, payload: 33) : Tcp(src, 50000, dst, 443, TcpFlags.Ack | TcpFlags.Psh, payload: 101);
        // Имитируем checksum offload: в исходящем пакете суммы мусорные.
        Assert.True(Packet.TryParse(p, out var info));
        p[info.TransportChecksumOffset] ^= 0x5A;
        if (!info.IsV6) p[10] ^= 0x33;

        Packet.RewriteAddressFull(p, ref info, source: true, A(nat));

        Assert.True(Packet.TryParse(p, out var after));
        Assert.Equal(A(nat), after.Src);
        Assert.Equal(A(dst), after.Dst);
        Assert.True(ReferenceValid(p));
    }

    [Theory]
    [InlineData("93.184.216.34", "192.168.43.20", "192.168.1.20", false)]
    [InlineData("93.184.216.34", "192.168.43.20", "192.168.1.20", true)]
    [InlineData("2606:2800:220:1::1946", "2a00:1450:4010::77", "2001:db8::15", false)]
    [InlineData("2606:2800:220:1::1946", "2a00:1450:4010::77", "2001:db8::15", true)]
    public void RewriteDestination_Incremental_KeepsChecksumsValid(string src, string dst, string orig, bool udp)
    {
        var p = udp ? Udp(src, 53, dst, 50000, payload: 77) : Tcp(src, 443, dst, 50000, TcpFlags.Ack, payload: 1400);
        Assert.True(ReferenceValid(p));
        Assert.True(Packet.TryParse(p, out var info));

        Packet.RewriteAddressIncremental(p, ref info, source: false, A(orig));

        Assert.True(Packet.TryParse(p, out var after));
        Assert.Equal(A(orig), after.Dst);
        Assert.True(ReferenceValid(p));
        // Инкрементальный результат совпадает с полным пересчётом
        var full = (byte[])p.Clone();
        Packet.RecalcChecksums(full, after);
        Assert.Equal(full, p);
    }

    [Fact]
    public void IncrementalRewrite_PreservesCorruption()
    {
        // Повреждённый входящий пакет не должен «исправиться» при NAT.
        var p = Tcp("93.184.216.34", 443, "192.168.43.20", 50000, TcpFlags.Ack, payload: 50);
        p[^1] ^= 0xFF;
        Assert.False(ReferenceValid(p));
        Assert.True(Packet.TryParse(p, out var info));
        Packet.RewriteAddressIncremental(p, ref info, source: false, A("192.168.1.20"));
        Assert.False(ReferenceValid(p));
    }

    [Fact]
    public void UdpV4_ZeroChecksumStaysZero()
    {
        var p = Udp("93.184.216.34", 53, "192.168.43.20", 50000);
        Assert.True(Packet.TryParse(p, out var info));
        p[info.TransportChecksumOffset] = 0;
        p[info.TransportChecksumOffset + 1] = 0;
        Packet.RewriteAddressIncremental(p, ref info, source: false, A("192.168.1.20"));
        Assert.Equal(0, p[info.TransportChecksumOffset]);
        Assert.Equal(0, p[info.TransportChecksumOffset + 1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClampMss_ReducesAndKeepsChecksumValid(bool v6, bool nopBefore)
    {
        var p = v6
            ? Tcp("2001:db8::15", 50000, "2606:2800:220:1::1946", 443, TcpFlags.Syn, mss: 1440, nopBeforeMss: nopBefore)
            : Tcp("192.168.1.20", 50000, "93.184.216.34", 443, TcpFlags.Syn, mss: 1460, nopBeforeMss: nopBefore);
        Assert.True(Packet.TryParse(p, out var info));

        Assert.True(Packet.ClampMss(p, info, 1380));

        Assert.True(ReferenceValid(p));
        int mssOffset = info.TransportOffset + 20 + (nopBefore ? 1 : 0) + 2;
        Assert.Equal(1380, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(mssOffset)));
        // Повторное ограничение ничего не меняет
        Assert.False(Packet.ClampMss(p, info, 1380));
    }

    [Fact]
    public void ClampMss_IgnoresNonSyn()
    {
        var p = Tcp("192.168.1.20", 50000, "93.184.216.34", 443, TcpFlags.Ack, mss: 1460);
        Assert.True(Packet.TryParse(p, out var info));
        Assert.False(Packet.ClampMss(p, info, 1200));
    }

    [Fact]
    public void BuildInboundRst_ForSyn()
    {
        var syn = Tcp("192.168.1.20", 50000, "93.184.216.34", 443, TcpFlags.Syn, seq: 777);
        Assert.True(Packet.TryParse(syn, out var info));
        var buf = new byte[128];
        int n = Packet.BuildInboundRst(buf, info);
        var rst = buf[..n];

        Assert.True(Packet.TryParse(rst, out var r));
        Assert.Equal(A("93.184.216.34"), r.Src);
        Assert.Equal(A("192.168.1.20"), r.Dst);
        Assert.Equal(443, r.SrcPort);
        Assert.Equal(50000, r.DstPort);
        Assert.Equal(TcpFlags.Rst | TcpFlags.Ack, r.TcpFlags);
        Assert.Equal(0u, r.Seq);
        Assert.Equal(778u, r.Ack);
        Assert.True(ReferenceValid(rst));
    }

    [Fact]
    public void BuildInboundRst_ForData_UsesPeerSequence()
    {
        var data = Tcp("2001:db8::15", 50000, "2606:2800:220:1::1946", 443, TcpFlags.Ack | TcpFlags.Psh, seq: 1000, ack: 5000, payload: 10);
        Assert.True(Packet.TryParse(data, out var info));
        var buf = new byte[128];
        int n = Packet.BuildInboundRst(buf, info);
        Assert.True(Packet.TryParse(buf.AsSpan(0, n), out var r));
        Assert.True(r.IsV6);
        Assert.Equal(5000u, r.Seq);
        Assert.Equal(1010u, r.Ack);
        Assert.True(ReferenceValid(buf[..n]));

        n = Packet.BuildOutboundRst(buf, info);
        Assert.True(Packet.TryParse(buf.AsSpan(0, n), out var o));
        Assert.Equal(info.Src, o.Src);
        Assert.Equal(1000u, o.Seq);
        Assert.Equal(TcpFlags.Rst, o.TcpFlags);
        Assert.True(ReferenceValid(buf[..n]));
    }

    [Fact]
    public void Parse_NonFirstFragment_HasNoTransportHeader()
    {
        var p = Udp("192.168.1.20", 50000, "93.184.216.34", 53, payload: 40);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 0x0003); // смещение фрагмента 24 байта
        Assert.True(Packet.TryParse(p, out var info));
        Assert.True(info.IsFragment);
        Assert.False(info.HasTransportHeader);
        Assert.Equal(0x1234u, info.FragmentId);

        Packet.RewriteAddressFull(p, ref info, source: true, A("192.168.43.20"));
        Assert.Equal(0, ReferenceChecksum(p[..20]));
        Assert.Equal(A("192.168.43.20"), info.Src);
    }

    [Fact]
    public void Parse_RejectsGarbage()
    {
        Assert.False(Packet.TryParse(new byte[10], out _));
        Assert.False(Packet.TryParse(new byte[60], out _)); // версия 0
        var icmp = Tcp("192.168.1.20", 1, "1.1.1.1", 2, 0);
        icmp[9] = 1;
        Assert.False(Packet.TryParse(icmp, out _));
    }

    [Fact]
    public void Addr_RoundTrip()
    {
        foreach (var s in new[] { "192.168.1.20", "8.8.8.8", "2001:db8::1", "::1", "fe80::1" })
            Assert.Equal(s, Addr.Format(A(s)));
        Assert.True(Addr.IsV4(A("10.0.0.1")));
        Assert.False(Addr.IsV4(A("2001:db8::1")));
    }

    [Fact]
    public void Subnet_Contains()
    {
        var lan = Subnet.From(A("192.168.1.20"), 24, v4: true);
        Assert.True(lan.Contains(A("192.168.1.1")));
        Assert.True(lan.Contains(A("192.168.1.255")));
        Assert.False(lan.Contains(A("192.168.2.1")));
        Assert.False(lan.Contains(A("2001:db8::1")));

        var v6 = Subnet.From(A("2001:db8:1:2::15"), 64, v4: false);
        Assert.True(v6.Contains(A("2001:db8:1:2::1")));
        Assert.False(v6.Contains(A("2001:db8:1:3::1")));

        var host = Subnet.From(A("10.8.0.2"), 32, v4: true);
        Assert.True(host.Contains(A("10.8.0.2")));
        Assert.False(host.Contains(A("10.8.0.3")));
    }
}
