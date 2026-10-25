using System.Net.NetworkInformation;
using AppEgress.Core;

namespace AppEgress.Tests;

public class AdapterServiceTests
{
    private sealed class UnreadableVpn : NetworkInterface
    {
        public override string Id => "vpn-b";
        public override string Name => "VPN B";
        public override string Description => "Private VPN";
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Ppp;
        public override OperationalStatus OperationalStatus => OperationalStatus.Up;
        public override long Speed => 0;
        public override bool IsReceiveOnly => false;
        public override bool SupportsMulticast => false;
        public override IPInterfaceProperties GetIPProperties() => throw new NetworkInformationException(1168);
        public override IPv4InterfaceStatistics GetIPv4Statistics() => throw new NotSupportedException();
        public override PhysicalAddress GetPhysicalAddress() => PhysicalAddress.None;
        public override bool Supports(NetworkInterfaceComponent component) => false;
    }

    [Fact]
    public void PropertyReadFailure_KeepsVpnVisibleButUnavailable()
    {
        var adapter = AdapterService.DescribeOrUnavailable(new UnreadableVpn());
        Assert.Equal("vpn-b", adapter.Id);
        Assert.Equal("VPN B", adapter.Name);
        Assert.Equal(AdapterKind.Vpn, adapter.Kind);
        Assert.True(adapter.IsUp);
        Assert.True(adapter.EnumerationFailed);
        Assert.False(adapter.IsUsable);
    }
}
