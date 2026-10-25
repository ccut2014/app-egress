using System.Collections.Concurrent;

namespace AppEgress.Core;

/// <summary>Ключ потока со стороны локального стека: протокол, локальный порт, удалённый адрес и порт.</summary>
internal readonly record struct FlowKey(byte Protocol, ushort LocalPort, UInt128 Remote, ushort RemotePort);

internal readonly record struct FragmentKey(byte Protocol, UInt128 Src, UInt128 Dst, uint Id);

internal enum FlowAction
{
    /// <summary>Пропустить без изменений.</summary>
    Pass,
    /// <summary>Подменить адрес и отправить через целевой адаптер.</summary>
    Nat,
    /// <summary>Заблокировать (для TCP ответить RST).</summary>
    Block,
}

internal sealed class FlowEntry
{
    public required FlowKey Key { get; init; }
    public required FlowAction Action { get; init; }
    public bool RouteUnavailable { get; init; }
    public int Generation;
    public ActiveRule? Rule;
    public bool IsV6 { get; init; }

    // Только для Nat
    public UInt128 OrigLocal { get; init; }
    public uint OrigIfIdx { get; init; }
    public uint OrigSubIfIdx { get; init; }
    public UInt128 NatLocal { get; init; }
    public uint TargetIfIdx { get; init; }
    public long RouteCheckAt;

    public long LastSeen;
    /// <summary>Для Pass без известного PID: после этого момента решение нужно принять заново.</summary>
    public long RecheckAt;
    /// <summary>1, если в потоке встречался FIN или RST.</summary>
    public int Closing;
    /// <summary>1, если поток учтён в RuleStats.ActiveFlows.</summary>
    public int Counted;
}

/// <summary>
/// Таблица потоков: решения по потокам (Pass/Nat/Block), отображение для UDP по локальному порту
/// (для входящих пакетов от новых адресатов) и привязка IP-фрагментов к потокам.
/// </summary>
internal sealed class NatTable
{
    public static readonly TimeSpan TcpIdleTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan TcpClosingTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UdpIdleTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan PassIdleTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan BlockIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<FlowKey, FlowEntry> _flows = new();
    private readonly ConcurrentDictionary<(ushort Port, UInt128 NatLocal), FlowEntry> _udpEndpoints = new();
    private readonly ConcurrentDictionary<FragmentKey, (FlowEntry Entry, long Tick)> _fragments = new();

    public int Count => _flows.Count;

    public bool TryGet(in FlowKey key, out FlowEntry entry) => _flows.TryGetValue(key, out entry!);

    public FlowEntry? FindUdpEndpoint(ushort localPort, UInt128 natLocal) =>
        _udpEndpoints.TryGetValue((localPort, natLocal), out var e) ? e : null;

    /// <summary>
    /// Добавляет запись или заменяет устаревшую. Если другой поток уже принял решение
    /// для этого потока в том же поколении, возвращает его запись, а не новую.
    /// </summary>
    public FlowEntry Put(FlowEntry entry)
    {
        while (true)
        {
            if (_flows.TryGetValue(entry.Key, out var current))
            {
                if (current.Generation == entry.Generation && current.RecheckAt == 0)
                    return current;
                if (_flows.TryUpdate(entry.Key, entry, current))
                {
                    Release(current);
                    break;
                }
            }
            else if (_flows.TryAdd(entry.Key, entry))
            {
                break;
            }
        }

        if (entry.Action == FlowAction.Nat)
        {
            if (entry.Rule != null && Interlocked.Exchange(ref entry.Counted, 1) == 0)
                Interlocked.Increment(ref entry.Rule.Stats.ActiveFlows);
            if (entry.Key.Protocol == Packet.ProtoUdp)
                _udpEndpoints[(entry.Key.LocalPort, entry.NatLocal)] = entry;
        }
        return entry;
    }

    public bool Remove(FlowEntry entry)
    {
        if (!_flows.TryRemove(new KeyValuePair<FlowKey, FlowEntry>(entry.Key, entry)))
            return false;
        Release(entry);
        return true;
    }

    private void Release(FlowEntry entry)
    {
        if (entry.Action != FlowAction.Nat)
            return;
        if (entry.Rule != null && Interlocked.Exchange(ref entry.Counted, 0) == 1)
            Interlocked.Decrement(ref entry.Rule.Stats.ActiveFlows);
        if (entry.Key.Protocol == Packet.ProtoUdp)
            _udpEndpoints.TryRemove(new KeyValuePair<(ushort, UInt128), FlowEntry>((entry.Key.LocalPort, entry.NatLocal), entry));
    }

    public void RememberFragment(in FragmentKey key, FlowEntry entry) => _fragments[key] = (entry, Environment.TickCount64);

    public FlowEntry? FindFragment(in FragmentKey key) =>
        _fragments.TryGetValue(key, out var v) ? v.Entry : null;

    public IEnumerable<FlowEntry> Entries => _flows.Values;

    /// <summary>Удаляет устаревшие записи. Возвращает число удалённых.</summary>
    public int Sweep(long nowTick)
    {
        int removed = 0;
        foreach (var e in _flows.Values)
        {
            long idle = nowTick - Volatile.Read(ref e.LastSeen);
            TimeSpan limit = e.Action switch
            {
                FlowAction.Pass => PassIdleTimeout,
                FlowAction.Block => BlockIdleTimeout,
                _ when e.Key.Protocol == Packet.ProtoUdp => UdpIdleTimeout,
                _ when e.Closing != 0 => TcpClosingTimeout,
                _ => TcpIdleTimeout,
            };
            if (idle > (long)limit.TotalMilliseconds && Remove(e))
                removed++;
        }
        foreach (var kv in _fragments)
            if (nowTick - kv.Value.Tick > (long)FragmentTimeout.TotalMilliseconds)
                _fragments.TryRemove(kv);
        return removed;
    }

    public void Clear()
    {
        foreach (var e in _flows.Values)
            Remove(e);
        _fragments.Clear();
    }
}
