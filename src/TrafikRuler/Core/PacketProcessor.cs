using TrafikRuler.Native;

namespace TrafikRuler.Core;

internal interface IPacketInjector
{
    /// <summary>Отправляет в стек дополнительный (собранный нами) пакет, например TCP RST.</summary>
    void Inject(byte[] packet, int length, in WinDivertAddress addr);
}

/// <summary>
/// Решает, что делать с каждым перехваченным пакетом, и при необходимости переписывает его.
/// Исходящие пакеты программ из правил получают source IP целевого адаптера, у входящих ответов
/// адрес возвращается обратно. Без состояния WinDivert, поэтому класс тестируется отдельно.
/// </summary>
internal sealed class PacketProcessor
{
    private readonly IProcessResolver _resolver;
    private RoutingSnapshot _snapshot = RoutingSnapshot.Empty;

    public NatTable Table { get; } = new();

    public PacketProcessor(IProcessResolver resolver)
    {
        _resolver = resolver;
    }

    public RoutingSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Применяет новый снимок правил. NAT-потоки, которые остаются верными, переходят в новое поколение.
    /// Остальные удаляются и возвращаются вызывающему коду для сброса соединений.
    /// </summary>
    public List<FlowEntry> ApplySnapshot(RoutingSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        var invalid = new List<FlowEntry>();
        foreach (var e in Table.Entries)
        {
            if (e.Action != FlowAction.Nat)
                continue;
            if (TryRevalidate(e, snapshot))
                continue;
            if (Table.Remove(e))
                invalid.Add(e);
        }
        return invalid;
    }

    private static bool TryRevalidate(FlowEntry e, RoutingSnapshot snap)
    {
        foreach (var r in snap.Rules)
        {
            if (r.Rule.Id != e.Rule?.Rule.Id || r.State != RuleState.Active || r.Target == null)
                continue;
            UInt128? local = e.IsV6 ? r.Target.IPv6 : r.Target.IPv4;
            if (r.Target.IfIndex != e.TargetIfIdx || local != e.NatLocal)
                return false;
            e.Rule = r;
            Volatile.Write(ref e.Generation, snap.Generation);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Обрабатывает пакет на месте. Возвращает true, если пакет (возможно изменённый) нужно отправить
    /// дальше с адресом <paramref name="addr"/>, и false, если его нужно отбросить.
    /// </summary>
    public bool Process(byte[] buffer, int length, ref WinDivertAddress addr, IPacketInjector injector, byte[] scratch)
    {
        var span = buffer.AsSpan(0, length);
        if (!Packet.TryParse(span, out var info))
            return true;
        return addr.Outbound
            ? ProcessOutbound(span, ref info, ref addr, injector, scratch)
            : ProcessInbound(span, ref info, ref addr);
    }

    private bool ProcessOutbound(Span<byte> span, ref PacketInfo info, ref WinDivertAddress addr,
        IPacketInjector injector, byte[] scratch)
    {
        var snap = Snapshot;
        long now = Environment.TickCount64;

        if (!info.HasTransportHeader)
        {
            // Не первый фрагмент: портов нет, ищем поток по идентификатору фрагмента.
            var frag = Table.FindFragment(new FragmentKey(info.Protocol, info.Src, info.Dst, info.FragmentId));
            if (frag == null || frag.Action != FlowAction.Nat)
                return frag?.Action != FlowAction.Block;
            ApplyNatOutbound(span, ref info, ref addr, frag);
            return true;
        }

        var key = new FlowKey(info.Protocol, info.SrcPort, info.Dst, info.DstPort);
        Table.TryGet(key, out var entry);

        if (entry != null && entry.Generation != snap.Generation && entry.Action == FlowAction.Nat
            && !TryRevalidate(entry, snap))
        {
            // Правило выключили или сменили адаптер, а соединение уже переведено на другой адрес.
            // Сбрасываем его с обеих сторон, приложение переподключится уже по новым правилам.
            if (info.IsTcp && Table.Remove(entry))
            {
                RejectTcp(span, info, addr, injector, scratch, entry, remoteRst: true);
                return false;
            }
            Table.Remove(entry);
            entry = null;
        }

        if (entry == null || entry.Generation != snap.Generation || (entry.RecheckAt != 0 && now >= entry.RecheckAt))
            entry = Decide(info, addr, snap, now);

        Volatile.Write(ref entry.LastSeen, now);
        switch (entry.Action)
        {
            case FlowAction.Nat:
                if (info.IsFragment)
                    Table.RememberFragment(new FragmentKey(info.Protocol, info.Src, info.Dst, info.FragmentId), entry);
                ApplyNatOutbound(span, ref info, ref addr, entry);
                return true;

            case FlowAction.Block:
                if (entry.Rule != null)
                    Interlocked.Increment(ref entry.Rule.Stats.PacketsBlocked);
                if (info.IsTcp)
                    RejectTcp(span, info, addr, injector, scratch, natEntry: null, remoteRst: !info.IsSynOnly);
                return false;

            default:
                return true;
        }
    }

    private FlowEntry Decide(in PacketInfo info, in WinDivertAddress addr, RoutingSnapshot snap, long now)
    {
        var key = new FlowKey(info.Protocol, info.SrcPort, info.Dst, info.DstPort);
        bool isV6 = info.IsV6;

        FlowEntry Pass(long recheckAt = 0, ActiveRule? rule = null) => Table.Put(new FlowEntry
        {
            Key = key, Action = FlowAction.Pass, Generation = snap.Generation, Rule = rule,
            IsV6 = isV6, LastSeen = now, RecheckAt = recheckAt,
        });

        FlowEntry Block(ActiveRule rule) => Table.Put(new FlowEntry
        {
            Key = key, Action = FlowAction.Block, Generation = snap.Generation, Rule = rule,
            IsV6 = isV6, LastSeen = now,
        });

        if (IsSpecialDestination(info.Dst))
            return Pass();

        uint? pid = _resolver.ResolvePid(info.Protocol, info.SrcPort);
        if (pid == null)
        {
            // Владелец порта пока неизвестен (событие сокета ещё не дошло). Пропускаем пакет, а решение
            // принимаем заново через секунду: TCP повторит SYN, UDP пришлёт следующий пакет.
            return Pass(recheckAt: now + 1000);
        }

        var rule = snap.Match(_resolver.GetImagePath(pid.Value));
        if (rule == null)
            return Pass();

        switch (rule.State)
        {
            case RuleState.Blocking:
                return Block(rule);
            case RuleState.Active when rule.Target != null:
                break;
            default:
                return Pass(rule: rule);
        }

        var target = rule.Target!;
        if (addr.IfIdx == target.IfIndex)
            return Pass(rule: rule); // и так идёт через нужный адаптер

        if (snap.ExcludeLocalNetworks && snap.AdaptersByIfIndex.TryGetValue(addr.IfIdx, out var origAdapter)
            && origAdapter.IsLocalDestination(info.Dst))
            return Pass(rule: rule);

        UInt128? natLocal = info.IsV6 ? target.IPv6 : target.IPv4;
        if (natLocal == null)
        {
            // У целевого адаптера нет IPv6: блокируем IPv6, приложение быстро перейдёт на IPv4.
            return Block(rule);
        }

        if (info.IsTcp && !info.IsSynOnly)
        {
            // Соединение открыто до включения правила: перенести его нельзя, сбрасываем.
            Interlocked.Increment(ref rule.Stats.ConnectionsReset);
            return Block(rule);
        }

        return Table.Put(new FlowEntry
        {
            Key = key,
            Action = FlowAction.Nat,
            Generation = snap.Generation,
            Rule = rule,
            IsV6 = info.IsV6,
            OrigLocal = info.Src,
            OrigIfIdx = addr.IfIdx,
            OrigSubIfIdx = addr.SubIfIdx,
            NatLocal = natLocal.Value,
            TargetIfIdx = target.IfIndex,
            LastSeen = now,
        });
    }

    private static void ApplyNatOutbound(Span<byte> span, ref PacketInfo info, ref WinDivertAddress addr, FlowEntry entry)
    {
        if (info.IsSyn && entry.Rule != null)
            Packet.ClampMss(span, info, info.IsV6 ? entry.Rule.MssV6 : entry.Rule.MssV4);
        Packet.RewriteAddressFull(span, ref info, source: true, entry.NatLocal);
        addr.IfIdx = entry.TargetIfIdx;
        addr.SubIfIdx = 0;
        // Суммы посчитаны нами, offload больше не нужен.
        addr.IPChecksum = true;
        if (!info.IsFragment)
        {
            addr.TCPChecksum = info.IsTcp;
            addr.UDPChecksum = info.IsUdp;
        }
        if (info.IsFin || info.IsRst)
            entry.Closing = 1;
        if (entry.Rule != null)
            Interlocked.Add(ref entry.Rule.Stats.BytesOut, info.Length);
    }

    private bool ProcessInbound(Span<byte> span, ref PacketInfo info, ref WinDivertAddress addr)
    {
        if (!info.HasTransportHeader)
            return true;

        var key = new FlowKey(info.Protocol, info.DstPort, info.Src, info.SrcPort);
        FlowEntry? entry = null;
        if (Table.TryGet(key, out var e) && e.Action == FlowAction.Nat && e.NatLocal == info.Dst)
            entry = e;
        else if (info.IsUdp)
            entry = Table.FindUdpEndpoint(info.DstPort, info.Dst);
        if (entry == null)
            return true;

        if (info.IsSyn && entry.Rule != null)
            Packet.ClampMss(span, info, info.IsV6 ? entry.Rule.MssV6 : entry.Rule.MssV4);
        Packet.RewriteAddressIncremental(span, ref info, source: false, entry.OrigLocal);
        addr.IfIdx = entry.OrigIfIdx;
        addr.SubIfIdx = entry.OrigSubIfIdx;

        Volatile.Write(ref entry.LastSeen, Environment.TickCount64);
        if (info.IsFin || info.IsRst)
            entry.Closing = 1;
        if (entry.Rule != null)
            Interlocked.Add(ref entry.Rule.Stats.BytesIn, info.Length);
        return true;
    }

    /// <summary>
    /// Сбрасывает TCP-поток: локальному стеку отправляем RST «от сервера», серверу — RST от нас.
    /// Для потока, который уже шёл через NAT, RST серверу уходит с NAT-адресом через целевой адаптер.
    /// </summary>
    private static void RejectTcp(Span<byte> span, in PacketInfo info, in WinDivertAddress addr,
        IPacketInjector injector, byte[] scratch, FlowEntry? natEntry, bool remoteRst)
    {
        if (info.IsRst)
            return;

        int n = Packet.BuildInboundRst(scratch, info);
        var inAddr = addr;
        inAddr.Outbound = false;
        inAddr.Loopback = false;
        inAddr.Impostor = false;
        inAddr.IPChecksum = true;
        inAddr.TCPChecksum = true;
        inAddr.UDPChecksum = false;
        injector.Inject(scratch, n, inAddr);

        if (!remoteRst)
            return;
        var outInfo = info;
        var outAddr = addr;
        outAddr.IPChecksum = true;
        outAddr.TCPChecksum = true;
        outAddr.UDPChecksum = false;
        if (natEntry != null)
        {
            outInfo.Src = natEntry.NatLocal;
            outAddr.IfIdx = natEntry.TargetIfIdx;
            outAddr.SubIfIdx = 0;
        }
        n = Packet.BuildOutboundRst(scratch, outInfo);
        injector.Inject(scratch, n, outAddr);
    }

    /// <summary>Multicast, broadcast и link-local никогда не перенаправляем.</summary>
    internal static bool IsSpecialDestination(UInt128 dst)
    {
        if (Addr.IsV4(dst))
        {
            uint v4 = (uint)(dst & 0xFFFFFFFF);
            return (v4 >> 28) == 0xE        // 224.0.0.0/4 multicast
                || v4 == 0xFFFFFFFF          // broadcast
                || (v4 >> 16) == 0xA9FE      // 169.254.0.0/16 link-local
                || (v4 >> 24) == 0x7F        // 127.0.0.0/8
                || v4 == 0;
        }
        ushort top = (ushort)(dst >> 112);
        return (top & 0xFF00) == 0xFF00      // ff00::/8 multicast
            || (top & 0xFFC0) == 0xFE80      // fe80::/10 link-local
            || dst == UInt128.One;           // ::1
    }
}
