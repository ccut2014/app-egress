using System.Runtime.InteropServices;
using AppEgress.Core;
using AppEgress.Native;

namespace AppEgress.Tests;

public class NativeLayoutTests
{
    [Fact]
    public void RouteStructures_MatchWindowsX64Layout()
    {
        Assert.Equal(28, Marshal.SizeOf<SockaddrInet>());
        Assert.Equal(104, Marshal.SizeOf<IpForwardRow2>());
        Assert.Equal(8, (int)Marshal.OffsetOf<IpForwardRow2>(nameof(IpForwardRow2.InterfaceIndex)));
        Assert.Equal(40, (int)Marshal.OffsetOf<IpForwardRow2>(nameof(IpForwardRow2.PrefixLength)));
        Assert.Equal(44, (int)Marshal.OffsetOf<IpForwardRow2>(nameof(IpForwardRow2.NextHop)));
        Assert.Equal(84, (int)Marshal.OffsetOf<IpForwardRow2>(nameof(IpForwardRow2.Metric)));
    }

    [Theory]
    [InlineData("192.0.2.7", 2, 4, new byte[] { 192, 0, 2, 7 })]
    [InlineData("2001:db8::7", 23, 8, new byte[] { 0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7 })]
    public unsafe void RouteAddresses_UseNetworkByteOrder(string text, int family, int offset, byte[] expected)
    {
        var address = SockaddrInet.From(Addr.FromIPAddress(System.Net.IPAddress.Parse(text)));
        Assert.Equal(family, address.Family);
        var bytes = new ReadOnlySpan<byte>(&address, 28);
        Assert.Equal(expected, bytes.Slice(offset, expected.Length).ToArray());
    }

    [Fact]
    public void GetBestRoute2_LoopbackLookupNeedsNoDriverOrRemoteTraffic()
    {
        var local = Addr.FromIPAddress(System.Net.IPAddress.Loopback);
        uint? index = RouteNative.FindInterface(local, local);
        Assert.True(index is > 0);
    }

    [Fact]
    public void WinDivertAddress_MatchesNativeLayout()
    {
        // sizeof(WINDIVERT_ADDRESS) == 80 в WinDivert 2.x
        Assert.Equal(80, Marshal.SizeOf<WinDivertAddress>());
        Assert.Equal(16, (int)Marshal.OffsetOf<WinDivertAddress>(nameof(WinDivertAddress.IfIdx)));
        Assert.Equal(20, (int)Marshal.OffsetOf<WinDivertAddress>(nameof(WinDivertAddress.SubIfIdx)));
        Assert.Equal(32, (int)Marshal.OffsetOf<WinDivertAddress>(nameof(WinDivertAddress.ProcessId)));
        Assert.Equal(68, (int)Marshal.OffsetOf<WinDivertAddress>(nameof(WinDivertAddress.LocalPort)));
        Assert.Equal(72, (int)Marshal.OffsetOf<WinDivertAddress>(nameof(WinDivertAddress.Protocol)));
    }

    [Fact]
    public unsafe void WinDivertAddress_Bitfields()
    {
        var a = default(WinDivertAddress);
        a.Outbound = true;
        a.IPv6 = true;
        a.TCPChecksum = true;
        uint bits = *(uint*)((byte*)&a + 8);
        Assert.Equal((1u << 17) | (1u << 20) | (1u << 22), bits);
        a.Outbound = false;
        Assert.False(a.Outbound);
        Assert.True(a.IPv6);

        *(uint*)((byte*)&a + 8) = 3 | (7 << 8); // Layer = SOCKET, Event = SOCKET_CLOSE
        Assert.Equal(WinDivertLayer.Socket, a.Layer);
        Assert.Equal(WinDivertEvent.SocketClose, a.Event);
    }
}
