using System.Net;
using System.Runtime.InteropServices;

namespace TrafikRuler.Native;

internal readonly record struct TcpConnectionInfo(
    IPAddress LocalAddress, ushort LocalPort, IPAddress RemoteAddress, ushort RemotePort, uint State, uint Pid);

internal readonly record struct UdpEndpointInfo(IPAddress LocalAddress, ushort LocalPort, uint Pid);

/// <summary>
/// Таблицы TCP/UDP из IP Helper с PID владельца, а также сброс TCP-соединений.
/// </summary>
internal static class IpHelper
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint MIB_TCP_STATE_SYN_SENT = 3;
    public const uint MIB_TCP_STATE_ESTAB = 5;
    public const uint MIB_TCP_STATE_DELETE_TCB = 12;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint SetTcpEntry(ref MIB_TCPROW pTcpRow);

    public static List<TcpConnectionInfo> GetTcpConnections(bool ipv6)
    {
        var result = new List<TcpConnectionInfo>();
        IntPtr buf = ReadTable(ipv6, tcp: true);
        if (buf == IntPtr.Zero)
            return result;
        try
        {
            int count = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            if (!ipv6)
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                for (int i = 0; i < count; i++, row += 24)
                {
                    result.Add(new TcpConnectionInfo(
                        new IPAddress((uint)Marshal.ReadInt32(row + 4)),
                        PortFromDword((uint)Marshal.ReadInt32(row + 8)),
                        new IPAddress((uint)Marshal.ReadInt32(row + 12)),
                        PortFromDword((uint)Marshal.ReadInt32(row + 16)),
                        (uint)Marshal.ReadInt32(row),
                        (uint)Marshal.ReadInt32(row + 20)));
                }
            }
            else
            {
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                for (int i = 0; i < count; i++, row += 56)
                {
                    result.Add(new TcpConnectionInfo(
                        ReadIPv6(row),
                        PortFromDword((uint)Marshal.ReadInt32(row + 20)),
                        ReadIPv6(row + 24),
                        PortFromDword((uint)Marshal.ReadInt32(row + 44)),
                        (uint)Marshal.ReadInt32(row + 48),
                        (uint)Marshal.ReadInt32(row + 52)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return result;
    }

    public static List<UdpEndpointInfo> GetUdpEndpoints(bool ipv6)
    {
        var result = new List<UdpEndpointInfo>();
        IntPtr buf = ReadTable(ipv6, tcp: false);
        if (buf == IntPtr.Zero)
            return result;
        try
        {
            int count = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            if (!ipv6)
            {
                // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid
                for (int i = 0; i < count; i++, row += 12)
                {
                    result.Add(new UdpEndpointInfo(
                        new IPAddress((uint)Marshal.ReadInt32(row)),
                        PortFromDword((uint)Marshal.ReadInt32(row + 4)),
                        (uint)Marshal.ReadInt32(row + 8)));
                }
            }
            else
            {
                // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScope, localPort, pid
                for (int i = 0; i < count; i++, row += 28)
                {
                    result.Add(new UdpEndpointInfo(
                        ReadIPv6(row),
                        PortFromDword((uint)Marshal.ReadInt32(row + 20)),
                        (uint)Marshal.ReadInt32(row + 24)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return result;
    }

    /// <summary>
    /// Принудительно разрывает IPv4 TCP-соединение (DELETE_TCB). Возвращает код ошибки Win32 (0, если успешно).
    /// </summary>
    public static uint ResetTcpConnection(IPAddress local, ushort localPort, IPAddress remote, ushort remotePort)
    {
        var row = new MIB_TCPROW
        {
            dwState = MIB_TCP_STATE_DELETE_TCB,
            dwLocalAddr = AddrToDword(local),
            dwLocalPort = PortToDword(localPort),
            dwRemoteAddr = AddrToDword(remote),
            dwRemotePort = PortToDword(remotePort),
        };
        return SetTcpEntry(ref row);
    }

    private static IntPtr ReadTable(bool ipv6, bool tcp)
    {
        int af = ipv6 ? AF_INET6 : AF_INET;
        int size = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr buf = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
            uint rc = tcp
                ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
            if (rc == 0 && buf != IntPtr.Zero)
                return buf;
            if (buf != IntPtr.Zero)
                Marshal.FreeHGlobal(buf);
            if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0)
                return IntPtr.Zero;
            // Таблица могла вырасти между вызовами, берём размер с запасом.
            size += 4096;
        }
        return IntPtr.Zero;
    }

    private static IPAddress ReadIPv6(IntPtr p)
    {
        var bytes = new byte[16];
        Marshal.Copy(p, bytes, 0, 16);
        return new IPAddress(bytes);
    }

    // Порт хранится в младших 16 битах DWORD в сетевом порядке байт.
    private static ushort PortFromDword(uint dw) => (ushort)(((dw & 0xFF) << 8) | ((dw >> 8) & 0xFF));

    private static uint PortToDword(ushort port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    private static uint AddrToDword(IPAddress a) => BitConverter.ToUInt32(a.GetAddressBytes(), 0);
}
