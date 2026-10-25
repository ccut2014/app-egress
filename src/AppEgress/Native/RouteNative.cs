using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AppEgress.Native;

// SOCKADDR_INET and MIB_IPFORWARD_ROW2, Windows x64 ABI.
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal unsafe struct SockaddrInet
{
    [FieldOffset(0)] public ushort Family;
    [FieldOffset(4)] private fixed byte _v4[4];
    [FieldOffset(8)] private fixed byte _v6[16];

    public static SockaddrInet From(UInt128 address)
    {
        SockaddrInet result = default;
        if (Core.Addr.IsV4(address))
        {
            result.Family = 2; // AF_INET
            BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(result._v4, 4), (uint)address);
        }
        else
        {
            result.Family = 23; // AF_INET6
            BinaryPrimitives.WriteUInt128BigEndian(new Span<byte>(result._v6, 16), address);
        }
        return result;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 104)]
internal struct IpForwardRow2
{
    [FieldOffset(0)] public ulong InterfaceLuid;
    [FieldOffset(8)] public uint InterfaceIndex;
    [FieldOffset(12)] public SockaddrInet DestinationPrefix;
    [FieldOffset(40)] public byte PrefixLength;
    [FieldOffset(44)] public SockaddrInet NextHop;
    [FieldOffset(84)] public uint Metric;
}

internal static class RouteNative
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex,
        in SockaddrInet source, in SockaddrInet destination, uint addressSortOptions,
        out IpForwardRow2 route, out SockaddrInet bestSource);

    public static uint? FindInterface(UInt128 source, UInt128 destination)
    {
        if (Core.Addr.IsV4(source) != Core.Addr.IsV4(destination))
            return null;
        var src = SockaddrInet.From(source);
        var dst = SockaddrInet.From(destination);
        // Do not constrain InterfaceIndex to the requested adapter: that could hide the
        // route Windows would actually select. WinDivert ignores outbound IfIdx.
        uint error = GetBestRoute2(IntPtr.Zero, 0, src, dst, 0, out var route, out _);
        return error == 0 && route.InterfaceIndex != 0 ? route.InterfaceIndex : null;
    }
}
