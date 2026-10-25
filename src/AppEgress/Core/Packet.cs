using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AppEgress.Core;

/// <summary>
/// IP-адрес как 128-битное число: IPv6 хранится как есть, IPv4 в виде ::ffff:a.b.c.d.
/// Быстрый и неаллоцирующий ключ для словарей.
/// </summary>
internal static class Addr
{
    private static readonly UInt128 V4MappedPrefix = (UInt128)0xFFFF << 32;
    private static readonly UInt128 V4Mask = ((UInt128)1 << 32) - 1;

    public static UInt128 FromV4(ReadOnlySpan<byte> b) => V4MappedPrefix | BinaryPrimitives.ReadUInt32BigEndian(b);

    public static UInt128 FromV6(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt128BigEndian(b);

    public static bool IsV4(UInt128 a) => (a & ~V4Mask) == V4MappedPrefix;

    public static UInt128 FromIPAddress(IPAddress ip)
    {
        Span<byte> b = stackalloc byte[16];
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            ip.TryWriteBytes(b, out _);
            return FromV4(b);
        }
        if (ip.IsIPv4MappedToIPv6)
            return FromIPAddress(ip.MapToIPv4());
        ip.TryWriteBytes(b, out _);
        return FromV6(b);
    }

    public static IPAddress ToIPAddress(UInt128 a)
    {
        if (IsV4(a))
        {
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b4, (uint)(a & V4Mask));
            return new IPAddress(b4);
        }
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(b, a);
        return new IPAddress(b);
    }

    public static void WriteV4(Span<byte> dst, UInt128 a) => BinaryPrimitives.WriteUInt32BigEndian(dst, (uint)(a & V4Mask));

    public static void WriteV6(Span<byte> dst, UInt128 a) => BinaryPrimitives.WriteUInt128BigEndian(dst, a);

    public static string Format(UInt128 a) => ToIPAddress(a).ToString();
}

internal static class TcpFlags
{
    public const byte Fin = 0x01;
    public const byte Syn = 0x02;
    public const byte Rst = 0x04;
    public const byte Psh = 0x08;
    public const byte Ack = 0x10;
}

internal struct PacketInfo
{
    public bool IsV6;
    public byte Protocol;          // 6 = TCP, 17 = UDP
    public int Length;
    public int IpHeaderLength;     // для IPv6 включает разобранные заголовки расширений
    public int TransportOffset;
    public bool HasTransportHeader; // false для не первых фрагментов
    public bool IsFragment;
    public uint FragmentId;
    public UInt128 Src;
    public UInt128 Dst;
    public ushort SrcPort;
    public ushort DstPort;
    public byte TcpFlags;
    public uint Seq;
    public uint Ack;
    public int TcpHeaderLength;
    public int PayloadLength;

    public readonly bool IsTcp => Protocol == 6;
    public readonly bool IsUdp => Protocol == 17;
    public readonly bool IsSyn => IsTcp && (TcpFlags & Core.TcpFlags.Syn) != 0;
    public readonly bool IsSynOnly => IsSyn && (TcpFlags & Core.TcpFlags.Ack) == 0;
    public readonly bool IsRst => IsTcp && (TcpFlags & Core.TcpFlags.Rst) != 0;
    public readonly bool IsFin => IsTcp && (TcpFlags & Core.TcpFlags.Fin) != 0;
    public readonly int TransportChecksumOffset => TransportOffset + (IsTcp ? 16 : 6);
}

/// <summary>
/// Разбор и перезапись IPv4/IPv6 пакетов с TCP/UDP: замена адресов, пересчёт контрольных сумм,
/// ограничение MSS и сборка TCP RST.
/// </summary>
internal static class Packet
{
    public const byte ProtoTcp = 6;
    public const byte ProtoUdp = 17;

    public static bool TryParse(ReadOnlySpan<byte> p, out PacketInfo info)
    {
        info = default;
        if (p.Length < 20)
            return false;

        int version = p[0] >> 4;
        int offset;
        byte proto;
        if (version == 4)
        {
            int ihl = (p[0] & 0x0F) * 4;
            int total = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
            if (ihl < 20 || total < ihl || total > p.Length)
                return false;
            info.Length = total;
            info.IpHeaderLength = ihl;
            proto = p[9];
            ushort frag = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
            bool moreFragments = (frag & 0x2000) != 0;
            int fragOffset = frag & 0x1FFF;
            info.IsFragment = moreFragments || fragOffset != 0;
            info.HasTransportHeader = fragOffset == 0;
            info.FragmentId = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
            info.Src = Addr.FromV4(p[12..]);
            info.Dst = Addr.FromV4(p[16..]);
            offset = ihl;
        }
        else if (version == 6)
        {
            if (p.Length < 40)
                return false;
            info.IsV6 = true;
            int total = 40 + BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
            if (total > p.Length)
                return false;
            info.Length = total;
            info.Src = Addr.FromV6(p[8..]);
            info.Dst = Addr.FromV6(p[24..]);
            proto = p[6];
            offset = 40;
            info.HasTransportHeader = true;
            // Пропускаем типичные заголовки расширений: Hop-by-Hop, Routing, Destination Options.
            for (int guard = 0; guard < 8 && proto is 0 or 43 or 60; guard++)
            {
                if (offset + 8 > total)
                    return false;
                byte next = p[offset];
                offset += (p[offset + 1] + 1) * 8;
                proto = next;
            }
            if (proto == 44) // Fragment
            {
                if (offset + 8 > total)
                    return false;
                byte next = p[offset];
                ushort fo = BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 2)..]);
                info.IsFragment = true;
                info.HasTransportHeader = (fo & 0xFFF8) == 0;
                info.FragmentId = BinaryPrimitives.ReadUInt32BigEndian(p[(offset + 4)..]);
                offset += 8;
                proto = next;
            }
            info.IpHeaderLength = offset;
        }
        else
        {
            return false;
        }

        if (proto != ProtoTcp && proto != ProtoUdp)
            return false;
        info.Protocol = proto;
        info.TransportOffset = offset;

        if (!info.HasTransportHeader)
            return true;

        var t = p[offset..info.Length];
        if (proto == ProtoTcp)
        {
            if (t.Length < 20)
                return false;
            info.TcpHeaderLength = (t[12] >> 4) * 4;
            if (info.TcpHeaderLength < 20 || info.TcpHeaderLength > t.Length)
                return false;
            info.SrcPort = BinaryPrimitives.ReadUInt16BigEndian(t);
            info.DstPort = BinaryPrimitives.ReadUInt16BigEndian(t[2..]);
            info.Seq = BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
            info.Ack = BinaryPrimitives.ReadUInt32BigEndian(t[8..]);
            info.TcpFlags = t[13];
            info.PayloadLength = t.Length - info.TcpHeaderLength;
        }
        else
        {
            if (t.Length < 8)
                return false;
            info.SrcPort = BinaryPrimitives.ReadUInt16BigEndian(t);
            info.DstPort = BinaryPrimitives.ReadUInt16BigEndian(t[2..]);
            info.PayloadLength = t.Length - 8;
        }
        return true;
    }

    /// <summary>
    /// Заменяет адрес источника или назначения с инкрементальной правкой контрольных сумм (RFC 1624).
    /// Используется для входящих пакетов и для фрагментов, где весь датаграм недоступен.
    /// </summary>
    public static void RewriteAddressIncremental(Span<byte> p, ref PacketInfo info, bool source, UInt128 newAddr)
    {
        int addrOffset;
        int addrLen = info.IsV6 ? 16 : 4;
        if (info.IsV6) addrOffset = source ? 8 : 24;
        else addrOffset = source ? 12 : 16;

        Span<byte> oldBytes = stackalloc byte[16];
        p.Slice(addrOffset, addrLen).CopyTo(oldBytes);
        Span<byte> newBytes = stackalloc byte[16];
        if (info.IsV6) Addr.WriteV6(newBytes, newAddr);
        else Addr.WriteV4(newBytes, newAddr);

        var oldA = oldBytes[..addrLen];
        var newA = newBytes[..addrLen];
        newA.CopyTo(p.Slice(addrOffset, addrLen));

        if (!info.IsV6)
            AdjustChecksumField(p, 10, oldA, newA, udpZeroMeansNone: false);

        // Транспортная сумма включает псевдозаголовок с адресами, но заголовок есть только в первом фрагменте.
        if (info.HasTransportHeader)
            AdjustChecksumField(p, info.TransportChecksumOffset, oldA, newA, udpZeroMeansNone: info.IsUdp && !info.IsV6);

        if (source) info.Src = newAddr;
        else info.Dst = newAddr;
    }

    /// <summary>
    /// Заменяет адрес и полностью пересчитывает контрольные суммы. Нужно для исходящих пакетов:
    /// при checksum offload в их полях может лежать недосчитанная сумма.
    /// </summary>
    public static void RewriteAddressFull(Span<byte> p, ref PacketInfo info, bool source, UInt128 newAddr)
    {
        if (info.IsFragment)
        {
            // Транспортную сумму фрагментированного датаграма стек считает сам (offload тут не работает),
            // её правим инкрементально. Сумму IP-заголовка пересчитываем полностью.
            RewriteAddressIncremental(p, ref info, source, newAddr);
            RecalcIpHeaderChecksum(p, info);
            return;
        }
        if (info.IsV6)
            Addr.WriteV6(p[(source ? 8 : 24)..], newAddr);
        else
            Addr.WriteV4(p[(source ? 12 : 16)..], newAddr);
        if (source) info.Src = newAddr;
        else info.Dst = newAddr;
        RecalcChecksums(p, info);
    }

    public static void RecalcIpHeaderChecksum(Span<byte> p, in PacketInfo info)
    {
        if (info.IsV6)
            return;
        p[10] = 0;
        p[11] = 0;
        ushort ipSum = Fold(Sum(p[..info.IpHeaderLength], 0));
        BinaryPrimitives.WriteUInt16BigEndian(p[10..], (ushort)~ipSum);
    }

    public static void RecalcChecksums(Span<byte> p, in PacketInfo info)
    {
        RecalcIpHeaderChecksum(p, info);

        if (!info.HasTransportHeader || info.IsFragment)
            return;

        var t = p[info.TransportOffset..info.Length];
        int csOff = info.IsTcp ? 16 : 6;
        t[csOff] = 0;
        t[csOff + 1] = 0;
        ulong sum = PseudoHeaderSum(p, info, (uint)t.Length);
        sum = Sum(t, sum);
        ushort cs = (ushort)~Fold(sum);
        if (info.IsUdp && cs == 0)
            cs = 0xFFFF;
        BinaryPrimitives.WriteUInt16BigEndian(t[csOff..], cs);
    }

    /// <summary>Проверяет контрольные суммы (для тестов и отладки).</summary>
    public static bool ChecksumsValid(ReadOnlySpan<byte> p, in PacketInfo info)
    {
        if (!info.IsV6 && Fold(Sum(p[..info.IpHeaderLength], 0)) != 0xFFFF)
            return false;
        if (!info.HasTransportHeader || info.IsFragment)
            return true;
        var t = p[info.TransportOffset..info.Length];
        if (info.IsUdp && !info.IsV6 && BinaryPrimitives.ReadUInt16BigEndian(t[6..]) == 0)
            return true;
        ulong sum = PseudoHeaderSum(p, info, (uint)t.Length);
        sum = Sum(t, sum);
        return Fold(sum) == 0xFFFF;
    }

    /// <summary>
    /// Если в SYN/SYN-ACK указан MSS больше <paramref name="maxMss"/>, уменьшает его.
    /// Контрольная сумма TCP правится инкрементально. Возвращает true, если пакет изменён.
    /// </summary>
    public static bool ClampMss(Span<byte> p, in PacketInfo info, ushort maxMss)
    {
        if (!info.IsSyn || !info.HasTransportHeader || info.TcpHeaderLength <= 20)
            return false;
        int start = info.TransportOffset + 20;
        int end = info.TransportOffset + info.TcpHeaderLength;
        int i = start;
        while (i < end)
        {
            byte kind = p[i];
            if (kind == 0)
                break;
            if (kind == 1)
            {
                i++;
                continue;
            }
            if (i + 1 >= end)
                break;
            int len = p[i + 1];
            if (len < 2 || i + len > end)
                break;
            if (kind == 2 && len == 4)
            {
                ushort mss = BinaryPrimitives.ReadUInt16BigEndian(p[(i + 2)..]);
                if (mss <= maxMss)
                    return false;
                Span<byte> oldB = stackalloc byte[2];
                Span<byte> newB = stackalloc byte[2];
                p.Slice(i + 2, 2).CopyTo(oldB);
                BinaryPrimitives.WriteUInt16BigEndian(newB, maxMss);
                newB.CopyTo(p.Slice(i + 2, 2));
                // Смещение опции чётное относительно начала TCP-заголовка, если перед ней нет NOP с нечётным выравниванием.
                // Для общего случая правим сумму по 16-битным словам, в которые попадает поле.
                AdjustChecksumUnaligned(p, info.TransportChecksumOffset, info.TransportOffset, i + 2, oldB, newB);
                return true;
            }
            i += len;
        }
        return false;
    }

    /// <summary>
    /// Собирает TCP RST «от удалённой стороны» для локального стека в ответ на исходящий пакет <paramref name="orig"/>.
    /// Возвращает длину пакета в <paramref name="dst"/>.
    /// </summary>
    public static int BuildInboundRst(Span<byte> dst, in PacketInfo orig)
    {
        uint advance = (uint)orig.PayloadLength
                       + ((orig.TcpFlags & TcpFlags.Syn) != 0 ? 1u : 0u)
                       + ((orig.TcpFlags & TcpFlags.Fin) != 0 ? 1u : 0u);
        uint seq = (orig.TcpFlags & TcpFlags.Ack) != 0 ? orig.Ack : 0;
        uint ack = orig.Seq + advance;
        return BuildTcp(dst, orig.IsV6, orig.Dst, orig.Src, orig.DstPort, orig.SrcPort, seq, ack,
            (byte)(TcpFlags.Rst | TcpFlags.Ack));
    }

    /// <summary>Собирает TCP RST от локальной стороны удалённой (в продолжение исходящего пакета).</summary>
    public static int BuildOutboundRst(Span<byte> dst, in PacketInfo orig) =>
        BuildTcp(dst, orig.IsV6, orig.Src, orig.Dst, orig.SrcPort, orig.DstPort, orig.Seq, 0, TcpFlags.Rst);

    public static int BuildTcp(Span<byte> dst, bool v6, UInt128 src, UInt128 dstAddr, ushort sport, ushort dport,
        uint seq, uint ack, byte flags)
    {
        int ipLen = v6 ? 40 : 20;
        int total = ipLen + 20;
        dst[..total].Clear();
        if (v6)
        {
            dst[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(dst[4..], 20);
            dst[6] = ProtoTcp;
            dst[7] = 64;
            Addr.WriteV6(dst[8..], src);
            Addr.WriteV6(dst[24..], dstAddr);
        }
        else
        {
            dst[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)total);
            dst[6] = 0x40; // DF
            dst[8] = 64;
            dst[9] = ProtoTcp;
            Addr.WriteV4(dst[12..], src);
            Addr.WriteV4(dst[16..], dstAddr);
        }
        var t = dst[ipLen..];
        BinaryPrimitives.WriteUInt16BigEndian(t, sport);
        BinaryPrimitives.WriteUInt16BigEndian(t[2..], dport);
        BinaryPrimitives.WriteUInt32BigEndian(t[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(t[8..], ack);
        t[12] = 5 << 4;
        t[13] = flags;

        TryParse(dst[..total], out var info);
        RecalcChecksums(dst, info);
        return total;
    }

    private static ulong PseudoHeaderSum(ReadOnlySpan<byte> p, in PacketInfo info, uint transportLength)
    {
        ulong sum = 0;
        if (info.IsV6)
        {
            sum = Sum(p.Slice(8, 32), sum);
            sum += transportLength >> 16;
            sum += transportLength & 0xFFFF;
            sum += info.Protocol;
        }
        else
        {
            sum = Sum(p.Slice(12, 8), sum);
            sum += info.Protocol;
            sum += transportLength;
        }
        return sum;
    }

    private static ulong Sum(ReadOnlySpan<byte> data, ulong sum)
    {
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
            sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length)
            sum += (uint)(data[i] << 8);
        return sum;
    }

    private static ushort Fold(ulong sum)
    {
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)sum;
    }

    /// <summary>HC' = ~(~HC + ~m + m') по RFC 1624 для выровненных 16-битных слов.</summary>
    private static void AdjustChecksumField(Span<byte> p, int csOffset, ReadOnlySpan<byte> oldBytes,
        ReadOnlySpan<byte> newBytes, bool udpZeroMeansNone)
    {
        ushort hc = BinaryPrimitives.ReadUInt16BigEndian(p[csOffset..]);
        if (udpZeroMeansNone && hc == 0)
            return;
        ulong sum = (ushort)~hc;
        for (int i = 0; i + 1 < oldBytes.Length; i += 2)
        {
            sum += (ushort)~((oldBytes[i] << 8) | oldBytes[i + 1]);
            sum += (uint)((newBytes[i] << 8) | newBytes[i + 1]);
        }
        ushort result = (ushort)~Fold(sum);
        if (udpZeroMeansNone && result == 0)
            result = 0xFFFF;
        BinaryPrimitives.WriteUInt16BigEndian(p[csOffset..], result);
    }

    private static void AdjustChecksumUnaligned(Span<byte> p, int csOffset, int baseOffset, int fieldOffset,
        ReadOnlySpan<byte> oldBytes, ReadOnlySpan<byte> newBytes)
    {
        if (((fieldOffset - baseOffset) & 1) == 0)
        {
            AdjustChecksumField(p, csOffset, oldBytes, newBytes, udpZeroMeansNone: false);
            return;
        }
        // Поле начинается с нечётного байта: оно попадает в младший байт одного слова и старший байт следующего.
        // Сумма с перестановкой байтов (byte swap) эквивалентна, поэтому суммируем слова с обменом байтов.
        Span<byte> o = stackalloc byte[2] { oldBytes[1], oldBytes[0] };
        Span<byte> n = stackalloc byte[2] { newBytes[1], newBytes[0] };
        AdjustChecksumField(p, csOffset, o, n, udpZeroMeansNone: false);
    }
}
