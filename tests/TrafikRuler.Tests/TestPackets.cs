using System.Buffers.Binary;
using System.Net;
using TrafikRuler.Core;

namespace TrafikRuler.Tests;

/// <summary>
/// Сборка тестовых пакетов. Контрольные суммы считает отдельная эталонная реализация,
/// не связанная с кодом Packet, который мы проверяем.
/// </summary>
internal static class TestPackets
{
    public static UInt128 A(string ip) => Addr.FromIPAddress(IPAddress.Parse(ip));

    public static byte[] Tcp(string src, ushort sport, string dst, ushort dport, byte flags,
        uint seq = 1000, uint ack = 0, int payload = 0, ushort? mss = null, bool nopBeforeMss = false)
    {
        var options = new List<byte>();
        if (mss is { } m)
        {
            if (nopBeforeMss)
                options.Add(1);
            options.AddRange([2, 4, (byte)(m >> 8), (byte)m]);
            while (options.Count % 4 != 0)
                options.Add(1);
        }
        int tcpLen = 20 + options.Count;
        var t = new byte[tcpLen + payload];
        BinaryPrimitives.WriteUInt16BigEndian(t, sport);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), dport);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), ack);
        t[12] = (byte)((tcpLen / 4) << 4);
        t[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(14), 64240);
        options.CopyTo(t, 20);
        for (int i = 0; i < payload; i++)
            t[tcpLen + i] = (byte)(i * 7 + 3);
        return Wrap(src, dst, 6, t, checksumOffset: 16);
    }

    public static byte[] Udp(string src, ushort sport, string dst, ushort dport, int payload = 13)
    {
        var u = new byte[8 + payload];
        BinaryPrimitives.WriteUInt16BigEndian(u, sport);
        BinaryPrimitives.WriteUInt16BigEndian(u.AsSpan(2), dport);
        BinaryPrimitives.WriteUInt16BigEndian(u.AsSpan(4), (ushort)u.Length);
        for (int i = 0; i < payload; i++)
            u[8 + i] = (byte)(i * 11 + 5);
        return Wrap(src, dst, 17, u, checksumOffset: 6);
    }

    private static byte[] Wrap(string src, string dst, byte proto, byte[] transport, int checksumOffset)
    {
        var s = IPAddress.Parse(src);
        var d = IPAddress.Parse(dst);
        bool v6 = s.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        byte[] sb = s.GetAddressBytes(), db = d.GetAddressBytes();

        // Псевдозаголовок для эталонной суммы
        var pseudo = new List<byte>();
        pseudo.AddRange(sb);
        pseudo.AddRange(db);
        if (v6)
        {
            pseudo.AddRange([(byte)(transport.Length >> 24), (byte)(transport.Length >> 16), (byte)(transport.Length >> 8), (byte)transport.Length]);
            pseudo.AddRange([0, 0, 0, proto]);
        }
        else
        {
            pseudo.AddRange([0, proto, (byte)(transport.Length >> 8), (byte)transport.Length]);
        }
        ushort cs = ReferenceChecksum(pseudo.Concat(transport).ToArray());
        if (proto == 17 && cs == 0) cs = 0xFFFF;
        BinaryPrimitives.WriteUInt16BigEndian(transport.AsSpan(checksumOffset), cs);

        byte[] ip;
        if (v6)
        {
            ip = new byte[40];
            ip[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), (ushort)transport.Length);
            ip[6] = proto;
            ip[7] = 64;
            sb.CopyTo(ip, 8);
            db.CopyTo(ip, 24);
        }
        else
        {
            ip = new byte[20];
            ip[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)(20 + transport.Length));
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), 0x1234);
            ip[6] = 0x40;
            ip[8] = 128;
            ip[9] = proto;
            sb.CopyTo(ip, 12);
            db.CopyTo(ip, 16);
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(10), ReferenceChecksum(ip));
        }
        return ip.Concat(transport).ToArray();
    }

    /// <summary>Эталонная интернет-сумма (RFC 1071), написана максимально прямолинейно.</summary>
    public static ushort ReferenceChecksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 2)
        {
            int hi = data[i];
            int lo = i + 1 < data.Length ? data[i + 1] : 0;
            sum += (uint)((hi << 8) | lo);
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    /// <summary>Проверка сумм пакета эталонной реализацией.</summary>
    public static bool ReferenceValid(byte[] packet)
    {
        Assert.True(Packet.TryParse(packet, out var info));
        var copy = (byte[])packet.Clone();
        int csOff = info.TransportChecksumOffset;
        ushort actual = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(csOff));
        copy[csOff] = 0;
        copy[csOff + 1] = 0;
        var transport = copy[info.TransportOffset..info.Length];

        var src = Addr.ToIPAddress(info.Src).GetAddressBytes();
        var dst = Addr.ToIPAddress(info.Dst).GetAddressBytes();
        var pseudo = new List<byte>();
        pseudo.AddRange(src);
        pseudo.AddRange(dst);
        if (info.IsV6)
        {
            pseudo.AddRange([(byte)(transport.Length >> 24), (byte)(transport.Length >> 16), (byte)(transport.Length >> 8), (byte)transport.Length]);
            pseudo.AddRange([0, 0, 0, info.Protocol]);
        }
        else
        {
            pseudo.AddRange([0, info.Protocol, (byte)(transport.Length >> 8), (byte)transport.Length]);
        }
        ushort expected = ReferenceChecksum(pseudo.Concat(transport).ToArray());
        if (info.IsUdp && expected == 0) expected = 0xFFFF;
        if (actual != expected)
            return false;

        if (!info.IsV6)
        {
            var ipHdr = packet[..info.IpHeaderLength];
            if (ReferenceChecksum(ipHdr) != 0)
                return false;
        }
        return true;
    }
}
