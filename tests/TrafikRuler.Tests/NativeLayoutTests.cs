using System.Runtime.InteropServices;
using TrafikRuler.Native;

namespace TrafikRuler.Tests;

public class NativeLayoutTests
{
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
