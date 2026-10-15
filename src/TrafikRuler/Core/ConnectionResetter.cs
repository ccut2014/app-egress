using TrafikRuler.Native;

namespace TrafikRuler.Core;

/// <summary>
/// Разрывает уже открытые TCP-соединения программы, чтобы она переподключилась через нужный адаптер.
/// IPv4 разрываем сразу через SetTcpEntry(DELETE_TCB). Для IPv6 такого API нет: такие соединения
/// PacketProcessor сбрасывает подставным RST, как только по ним пойдёт следующий пакет.
/// </summary>
internal static class ConnectionResetter
{
    /// <summary>Сбрасывает IPv4-соединения процессов из правила, которые идут не через целевой адаптер.</summary>
    public static int ResetForRule(ActiveRule rule, RoutingSnapshot snapshot, IProcessResolver resolver,
        IReadOnlyList<AdapterInfo> adapters)
    {
        if (rule.State is not (RuleState.Active or RuleState.Blocking))
            return 0;

        UInt128? targetIp = rule.State == RuleState.Active ? rule.Target?.IPv4 : null;
        int count = 0;
        var pathCache = new Dictionary<uint, bool>();

        foreach (var c in IpHelper.GetTcpConnections(ipv6: false))
        {
            if (c.State is not (IpHelper.MIB_TCP_STATE_ESTAB or IpHelper.MIB_TCP_STATE_SYN_SENT))
                continue;
            var local = Addr.FromIPAddress(c.LocalAddress);
            var remote = Addr.FromIPAddress(c.RemoteAddress);
            if (PacketProcessor.IsSpecialDestination(remote) || PacketProcessor.IsSpecialDestination(local))
                continue;
            if (targetIp != null && local == targetIp)
                continue; // уже через нужный адаптер

            if (!pathCache.TryGetValue(c.Pid, out bool matches))
            {
                matches = snapshot.Match(resolver.GetImagePath(c.Pid)) == rule;
                pathCache[c.Pid] = matches;
            }
            if (!matches)
                continue;

            if (rule.State == RuleState.Active && snapshot.ExcludeLocalNetworks)
            {
                var owner = adapters.FirstOrDefault(a => a.IPv4 == local);
                if (owner != null && owner.IsLocalDestination(remote))
                    continue; // локальная сеть остаётся на своём адаптере
            }

            if (IpHelper.ResetTcpConnection(c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort) == 0)
                count++;
        }

        if (count > 0)
            Interlocked.Add(ref rule.Stats.ConnectionsReset, count);
        return count;
    }

    /// <summary>
    /// Сбрасывает IPv4 TCP-соединения, которые шли через NAT, но после смены правил стали недействительными.
    /// Локальный стек знает их по исходному адресу, по нему и удаляем.
    /// </summary>
    public static int ResetNatFlows(IEnumerable<FlowEntry> flows)
    {
        int count = 0;
        foreach (var f in flows)
        {
            if (f.Key.Protocol != Packet.ProtoTcp || f.IsV6 || f.Closing != 0)
                continue;
            uint rc = IpHelper.ResetTcpConnection(
                Addr.ToIPAddress(f.OrigLocal), f.Key.LocalPort, Addr.ToIPAddress(f.Key.Remote), f.Key.RemotePort);
            if (rc == 0)
            {
                count++;
                if (f.Rule != null)
                    Interlocked.Increment(ref f.Rule.Stats.ConnectionsReset);
            }
        }
        return count;
    }
}
