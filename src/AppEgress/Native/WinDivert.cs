using System.ComponentModel;
using System.Runtime.InteropServices;
using AppEgress.Localization;

namespace AppEgress.Native;

internal enum WinDivertLayer : int
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4,
}

internal enum WinDivertEvent : byte
{
    NetworkPacket = 0,
    FlowEstablished = 1,
    FlowDeleted = 2,
    SocketBind = 3,
    SocketConnect = 4,
    SocketListen = 5,
    SocketAccept = 6,
    SocketClose = 7,
    ReflectOpen = 8,
    ReflectClose = 9,
}

[Flags]
internal enum WinDivertFlags : ulong
{
    None = 0,
    Sniff = 0x0001,
    Drop = 0x0002,
    RecvOnly = 0x0004,
    SendOnly = 0x0008,
    NoInstall = 0x0010,
    Fragments = 0x0020,
}

internal enum WinDivertParam : int
{
    QueueLength = 0,
    QueueTime = 1,
    QueueSize = 2,
    VersionMajor = 3,
    VersionMinor = 4,
}

internal enum WinDivertShutdownHow : int
{
    Recv = 1,
    Send = 2,
    Both = 3,
}

/// <summary>
/// WINDIVERT_ADDRESS (WinDivert 2.x), 80 байт.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal unsafe struct WinDivertAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] private uint _bits;
    [FieldOffset(12)] private uint _reserved2;

    // union: WINDIVERT_DATA_NETWORK
    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;

    // union: WINDIVERT_DATA_SOCKET
    [FieldOffset(16)] public ulong EndpointId;
    [FieldOffset(24)] public ulong ParentEndpointId;
    [FieldOffset(32)] public uint ProcessId;
    [FieldOffset(36)] public fixed uint LocalAddr[4];
    [FieldOffset(52)] public fixed uint RemoteAddr[4];
    [FieldOffset(68)] public ushort LocalPort;
    [FieldOffset(70)] public ushort RemotePort;
    [FieldOffset(72)] public byte Protocol;

    public WinDivertLayer Layer => (WinDivertLayer)(_bits & 0xFF);
    public WinDivertEvent Event => (WinDivertEvent)((_bits >> 8) & 0xFF);

    public bool Sniffed { get => GetBit(16); set => SetBit(16, value); }
    public bool Outbound { get => GetBit(17); set => SetBit(17, value); }
    public bool Loopback { get => GetBit(18); set => SetBit(18, value); }
    public bool Impostor { get => GetBit(19); set => SetBit(19, value); }
    public bool IPv6 { get => GetBit(20); set => SetBit(20, value); }
    public bool IPChecksum { get => GetBit(21); set => SetBit(21, value); }
    public bool TCPChecksum { get => GetBit(22); set => SetBit(22, value); }
    public bool UDPChecksum { get => GetBit(23); set => SetBit(23, value); }

    private bool GetBit(int bit) => (_bits & (1u << bit)) != 0;

    private void SetBit(int bit, bool value)
    {
        if (value) _bits |= 1u << bit;
        else _bits &= ~(1u << bit);
    }
}

internal static class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern IntPtr WinDivertOpen(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    public static extern unsafe bool WinDivertRecv(IntPtr handle, byte* packet, uint packetLen, out uint recvLen, out WinDivertAddress addr);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    public static extern unsafe bool WinDivertSend(IntPtr handle, byte* packet, uint packetLen, out uint sendLen, in WinDivertAddress addr);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    public static extern bool WinDivertShutdown(IntPtr handle, WinDivertShutdownHow how);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    public static extern bool WinDivertSetParam(IntPtr handle, WinDivertParam param, ulong value);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    public static extern bool WinDivertGetParam(IntPtr handle, WinDivertParam param, out ulong value);

    public static readonly IntPtr InvalidHandle = new(-1);
}

/// <summary>
/// Обёртка над хендлом WinDivert. Методы Recv/Send потокобезопасны: несколько рабочих потоков
/// могут работать с одним хендлом одновременно (так сделано и в примере passthru из WinDivert).
/// </summary>
internal sealed class WinDivertHandle : IDisposable
{
    public const int MaxPacketSize = 0xFFFF;

    private IntPtr _handle;
    private int _closed;

    public string Filter { get; }

    private WinDivertHandle(IntPtr handle, string filter)
    {
        _handle = handle;
        Filter = filter;
    }

    public static WinDivertHandle Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags)
    {
        IntPtr h;
        try
        {
            h = WinDivertNative.WinDivertOpen(filter, layer, priority, flags);
        }
        catch (DllNotFoundException ex)
        {
            throw new WinDivertException(2, Loc.T("ErrDllMissing", ex.Message));
        }

        if (h == WinDivertNative.InvalidHandle || h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new WinDivertException(err, WinDivertException.Describe(err, filter));
        }

        return new WinDivertHandle(h, filter);
    }

    public void SetParam(WinDivertParam param, ulong value) => WinDivertNative.WinDivertSetParam(_handle, param, value);

    public ulong GetParam(WinDivertParam param) =>
        WinDivertNative.WinDivertGetParam(_handle, param, out ulong v) ? v : 0;

    /// <summary>Возвращает длину пакета или -1, если хендл закрыт.</summary>
    public unsafe int Recv(byte[] buffer, out WinDivertAddress addr)
    {
        fixed (byte* p = buffer)
        {
            if (WinDivertNative.WinDivertRecv(_handle, p, (uint)buffer.Length, out uint len, out addr))
                return (int)len;
        }

        int err = Marshal.GetLastWin32Error();
        // ERROR_NO_DATA (232) после Shutdown, ERROR_INVALID_HANDLE (6) / ERROR_OPERATION_ABORTED (995) после Close
        if (err is 232 or 6 or 995 || Volatile.Read(ref _closed) != 0)
            return -1;
        // ERROR_INSUFFICIENT_BUFFER (122): пакет больше буфера. Пропускаем его, ничего не ломая.
        if (err == 122)
            return 0;
        throw new Win32Exception(err);
    }

    public unsafe bool Send(byte[] buffer, int length, in WinDivertAddress addr)
    {
        fixed (byte* p = buffer)
        {
            return WinDivertNative.WinDivertSend(_handle, p, (uint)length, out _, in addr);
        }
    }

    public void Shutdown() => WinDivertNative.WinDivertShutdown(_handle, WinDivertShutdownHow.Both);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        WinDivertNative.WinDivertShutdown(_handle, WinDivertShutdownHow.Both);
        WinDivertNative.WinDivertClose(_handle);
        _handle = WinDivertNative.InvalidHandle;
    }
}

internal sealed class WinDivertException : Exception
{
    public int ErrorCode { get; }

    public WinDivertException(int code, string message) : base(message)
    {
        ErrorCode = code;
    }

    public static string Describe(int err, string filter) => err switch
    {
        2 => Loc.T("ErrDriverMissing"),
        5 => Loc.T("ErrAccessDenied"),
        87 => Loc.T("ErrBadFilter", filter),
        577 => Loc.T("ErrBadSignature"),
        654 => Loc.T("ErrOtherVersion"),
        1060 => Loc.T("ErrServiceMissing"),
        1275 => Loc.T("ErrDriverBlocked"),
        1753 => Loc.T("ErrBfe"),
        _ => Loc.T("ErrWinDivertOther", err, new Win32Exception(err).Message),
    };
}
