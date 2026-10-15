using System.Text;
using TrafikRuler.Native;
using TrafikRuler.Localization;

namespace TrafikRuler.Core;

/// <summary>
/// Хендл WinDivert на сетевом уровне и рабочие потоки, которые пропускают пакеты через PacketProcessor.
/// Пока ни одно правило ничего не делает с трафиком, хендл закрыт и в работу сети утилита не вмешивается.
/// </summary>
internal sealed class PacketRouter : IDisposable
{
    private const short Priority = 0;

    private readonly PacketProcessor _processor;
    private readonly object _lock = new();
    private WinDivertHandle? _handle;
    private readonly List<Thread> _workers = new();
    private readonly System.Threading.Timer _sweepTimer;

    public event Action<string>? Log;

    public PacketRouter(PacketProcessor processor)
    {
        _processor = processor;
        _sweepTimer = new System.Threading.Timer(_ => _processor.Table.Sweep(Environment.TickCount64), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public bool IsRunning => _handle != null;

    public string? CurrentFilter => _handle?.Filter;

    /// <summary>
    /// Применяет снимок правил и при необходимости пересоздаёт хендл с новым фильтром.
    /// Возвращает NAT-потоки, которые стали недействительными (их нужно сбросить).
    /// </summary>
    public List<FlowEntry> Apply(RoutingSnapshot snapshot)
    {
        lock (_lock)
        {
            var invalid = _processor.ApplySnapshot(snapshot);
            string? filter = BuildFilter(snapshot);
            if (filter == _handle?.Filter)
                return invalid;

            StopHandle();
            if (filter == null)
            {
                _processor.Table.Clear();
                Log?.Invoke(Loc.T("LogCaptureStopped"));
                return invalid;
            }

            var handle = WinDivertHandle.Open(filter, WinDivertLayer.Network, Priority, WinDivertFlags.None);
            handle.SetParam(WinDivertParam.QueueLength, 16384);
            handle.SetParam(WinDivertParam.QueueTime, 2000);
            handle.SetParam(WinDivertParam.QueueSize, 16 * 1024 * 1024);
            _handle = handle;

            int count = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            for (int i = 0; i < count; i++)
            {
                var t = new Thread(() => Worker(handle))
                {
                    IsBackground = true,
                    Name = $"TrafikRuler worker {i}",
                    Priority = ThreadPriority.AboveNormal,
                };
                _workers.Add(t);
                t.Start();
            }
            Log?.Invoke(Loc.T("LogCaptureStarted", count, filter));
            return invalid;
        }
    }

    /// <summary>
    /// Все исходящие TCP/UDP пакеты (кроме loopback) и входящие на адреса целевых адаптеров.
    /// Не первые фрагменты IPv4 ловим отдельно: у них нет TCP/UDP-заголовка.
    /// </summary>
    internal static string? BuildFilter(RoutingSnapshot snapshot)
    {
        if (!snapshot.NeedsInterception)
            return null;

        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in snapshot.Rules)
        {
            if (r.State != RuleState.Active || r.Target == null)
                continue;
            if (r.Target.IPv4 is { } v4)
                targets.Add("ip.DstAddr == " + Addr.Format(v4));
            if (r.Target.IPv6 is { } v6)
                targets.Add("ipv6.DstAddr == " + Addr.Format(v6));
        }

        var sb = new StringBuilder("!loopback and (");
        sb.Append("(outbound and (tcp or udp or (ip and ip.FragOff > 0)))");
        if (targets.Count > 0)
        {
            sb.Append(" or (inbound and (tcp or udp) and (");
            sb.Append(string.Join(" or ", targets));
            sb.Append("))");
        }
        sb.Append(')');
        return sb.ToString();
    }

    private void Worker(WinDivertHandle handle)
    {
        var buffer = new byte[WinDivertHandle.MaxPacketSize];
        var scratch = new byte[128];
        var injector = new HandleInjector(handle);
        while (true)
        {
            int len;
            WinDivertAddress addr;
            try
            {
                len = handle.Recv(buffer, out addr);
            }
            catch (Exception ex)
            {
                Log?.Invoke(Loc.T("LogRecvError", ex.Message));
                return;
            }
            if (len < 0)
                return;
            if (len == 0)
                continue;

            bool send;
            try
            {
                send = _processor.Process(buffer, len, ref addr, injector, scratch);
            }
            catch (Exception ex)
            {
                // Ошибка в обработке не должна ломать сеть: отправляем пакет как есть.
                Log?.Invoke(Loc.T("LogProcessError", ex.Message));
                send = true;
            }
            if (send)
                handle.Send(buffer, len, addr);
        }
    }

    private void StopHandle()
    {
        var h = _handle;
        _handle = null;
        h?.Dispose();
        foreach (var t in _workers)
            t.Join(2000);
        _workers.Clear();
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopHandle();
            _processor.Table.Clear();
        }
    }

    public void Dispose()
    {
        _sweepTimer.Dispose();
        Stop();
    }

    private sealed class HandleInjector(WinDivertHandle handle) : IPacketInjector
    {
        public void Inject(byte[] packet, int length, in WinDivertAddress addr) => handle.Send(packet, length, addr);
    }
}
