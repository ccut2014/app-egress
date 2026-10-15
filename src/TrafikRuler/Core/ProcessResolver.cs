using System.Collections.Concurrent;
using System.Diagnostics;
using TrafikRuler.Native;

namespace TrafikRuler.Core;

internal interface IProcessResolver
{
    /// <summary>PID владельца локального порта, или null, если определить не удалось.</summary>
    uint? ResolvePid(byte protocol, ushort localPort);

    /// <summary>Полный путь к exe процесса (кэшируется с проверкой времени старта процесса).</summary>
    string? GetImagePath(uint pid);
}

/// <summary>
/// Определяет, какому процессу принадлежит локальный порт. Основной источник данных — события
/// WinDivert на уровне сокетов (BIND/CONNECT/ACCEPT/CLOSE), они приходят до первого пакета соединения.
/// Если событие ещё не обработано, берём данные из таблиц IP Helper.
/// </summary>
internal sealed class ProcessResolver : IProcessResolver, IDisposable
{
    private readonly ConcurrentDictionary<int, uint> _portOwners = new();
    private readonly ConcurrentDictionary<uint, (long Created, string? Path)> _images = new();
    private readonly object _snapshotLock = new();
    private long _lastSnapshotTick;
    private WinDivertHandle? _socketHandle;
    private Thread? _thread;

    public event Action<string>? Log;

    private static int Key(byte proto, ushort port) => (proto << 16) | port;

    public void Start()
    {
        if (_socketHandle != null)
            return;
        _socketHandle = WinDivertHandle.Open("true", WinDivertLayer.Socket, 0,
            WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        _thread = new Thread(SocketLoop) { IsBackground = true, Name = "TrafikRuler socket events" };
        _thread.Start();
        // Заполняем кэш сокетами, которые были открыты до запуска утилиты.
        RefreshFromTables(Packet.ProtoTcp);
        RefreshFromTables(Packet.ProtoUdp);
    }

    public void Stop()
    {
        var h = Interlocked.Exchange(ref _socketHandle, null);
        h?.Dispose();
        _thread?.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();

    private void SocketLoop()
    {
        var handle = _socketHandle;
        if (handle == null)
            return;
        var buf = new byte[1];
        while (true)
        {
            int n;
            WinDivertAddress addr;
            try
            {
                n = handle.Recv(buf, out addr);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Ошибка чтения событий сокетов: " + ex.Message);
                return;
            }
            if (n < 0)
                return;
            if (addr.Loopback)
                continue;
            byte proto = addr.Protocol;
            if (proto != Packet.ProtoTcp && proto != Packet.ProtoUdp)
                continue;

            int key = Key(proto, addr.LocalPort);
            switch (addr.Event)
            {
                case WinDivertEvent.SocketBind:
                case WinDivertEvent.SocketConnect:
                case WinDivertEvent.SocketListen:
                case WinDivertEvent.SocketAccept:
                    if (addr.LocalPort != 0)
                        _portOwners[key] = addr.ProcessId;
                    break;
                case WinDivertEvent.SocketClose:
                    // Удаляем запись, только если порт всё ещё числится за этим процессом.
                    _portOwners.TryRemove(new KeyValuePair<int, uint>(key, addr.ProcessId));
                    break;
            }
        }
    }

    public uint? ResolvePid(byte protocol, ushort localPort)
    {
        int key = Key(protocol, localPort);
        if (_portOwners.TryGetValue(key, out uint pid))
            return pid;

        // Событие сокета ещё не дошло. Читаем таблицу IP Helper, но не чаще раза в 20 мс:
        // при всплеске новых соединений одна выборка обслуживает все.
        lock (_snapshotLock)
        {
            if (_portOwners.TryGetValue(key, out pid))
                return pid;
            long now = Environment.TickCount64;
            if (now - _lastSnapshotTick < 20)
                return null;
            _lastSnapshotTick = now;
            RefreshFromTables(protocol);
        }
        return _portOwners.TryGetValue(key, out pid) ? pid : null;
    }

    private void RefreshFromTables(byte protocol)
    {
        try
        {
            if (protocol == Packet.ProtoTcp)
            {
                foreach (bool v6 in new[] { false, true })
                    foreach (var c in IpHelper.GetTcpConnections(v6))
                        if (c.LocalPort != 0)
                            _portOwners[Key(Packet.ProtoTcp, c.LocalPort)] = c.Pid;
            }
            else
            {
                foreach (bool v6 in new[] { false, true })
                    foreach (var u in IpHelper.GetUdpEndpoints(v6))
                        if (u.LocalPort != 0)
                            _portOwners[Key(Packet.ProtoUdp, u.LocalPort)] = u.Pid;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Ошибка чтения таблиц соединений: " + ex.Message);
        }
    }

    public string? GetImagePath(uint pid)
    {
        if (pid is 0 or 4)
            return null;
        bool cached = _images.TryGetValue(pid, out var entry);
        if (!ProcessNative.TryQuery(pid, wantPath: !cached, out long created, out string? path))
        {
            _images.TryRemove(pid, out _);
            return null;
        }
        if (cached && entry.Created == created)
            return entry.Path;
        if (cached)
            ProcessNative.TryQuery(pid, wantPath: true, out created, out path);
        _images[pid] = (created, path);
        return path;
    }

    /// <summary>PID всех процессов, которые подпадают под правило (для UI и сброса соединений).</summary>
    public List<uint> FindProcesses(Func<string?, bool> predicate)
    {
        var result = new List<uint>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                uint pid = (uint)p.Id;
                if (predicate(GetImagePath(pid)))
                    result.Add(pid);
            }
        }
        return result;
    }

    /// <summary>Удаляет из кэша процессы, которые уже завершились.</summary>
    public void Prune()
    {
        foreach (var pid in _images.Keys)
            if (!ProcessNative.TryQuery(pid, false, out _, out _))
                _images.TryRemove(pid, out _);
    }
}
