using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TrafikRuler.Core;

internal enum AdapterKind
{
    Ethernet,
    WiFi,
    Vpn,
    Other,
}

internal readonly record struct Subnet(UInt128 Network, int PrefixBits)
{
    public bool Contains(UInt128 addr) => PrefixBits == 0 || (addr >> (128 - PrefixBits)) == (Network >> (128 - PrefixBits));

    public static Subnet From(UInt128 addr, int prefix, bool v4)
    {
        int bits = v4 ? 96 + prefix : prefix;
        UInt128 mask = bits == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - bits);
        return new Subnet(addr & mask, bits);
    }
}

internal sealed class AdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public AdapterKind Kind { get; init; }
    public bool IsUp { get; init; }
    public uint IfIndex { get; init; }
    public UInt128? IPv4 { get; init; }
    public UInt128? IPv6 { get; init; }
    public int Mtu { get; init; } = 1500;
    public bool HasGateway { get; init; }
    public IReadOnlyList<Subnet> Subnets { get; init; } = Array.Empty<Subnet>();

    /// <summary>Через адаптер можно отправлять IPv4-трафик.</summary>
    public bool IsUsable => IsUp && IPv4 != null && IfIndex != 0;

    public string KindLabel => Kind switch
    {
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Vpn => "VPN",
        _ => "Другое",
    };

    public string DisplayText
    {
        get
        {
            string ip = IPv4 is { } a ? Addr.Format(a) : "нет IPv4";
            string state = IsUp ? ip : "отключён";
            return $"{KindLabel}: {Name} ({state})";
        }
    }

    public bool IsLocalDestination(UInt128 dst)
    {
        foreach (var s in Subnets)
            if (s.Contains(dst))
                return true;
        return false;
    }
}

internal static class AdapterService
{
    private static readonly string[] VpnMarkers =
        ["vpn", "tap-windows", "wireguard", "wintun", "tunnel", "openvpn", "tailscale", "zerotier", "hamachi", "radmin"];

    public static List<AdapterInfo> GetAll()
    {
        var result = new List<AdapterInfo>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            try
            {
                result.Add(Describe(nic));
            }
            catch (NetworkInformationException)
            {
                // Адаптер мог исчезнуть во время перечисления
            }
        }

        return result
            .OrderByDescending(a => a.IsUsable)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static AdapterInfo Describe(NetworkInterface nic)
    {
        var props = nic.GetIPProperties();
        uint ifIndex = 0;
        int mtu = 1500;
        try
        {
            var v4 = props.GetIPv4Properties();
            if (v4 != null)
            {
                ifIndex = (uint)v4.Index;
                if (v4.Mtu > 0) mtu = v4.Mtu;
            }
        }
        catch (NetworkInformationException) { }
        if (ifIndex == 0)
        {
            try
            {
                var v6 = props.GetIPv6Properties();
                if (v6 != null) ifIndex = (uint)v6.Index;
            }
            catch (NetworkInformationException) { }
        }

        UInt128? ipv4 = null;
        UInt128? ipv6 = null;
        var subnets = new List<Subnet>();
        foreach (var ua in props.UnicastAddresses)
        {
            var ip = ua.Address;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var a = Addr.FromIPAddress(ip);
                int prefix = ua.PrefixLength is > 0 and <= 32 ? ua.PrefixLength : 32;
                subnets.Add(Subnet.From(a, prefix, v4: true));
                bool apipa = ip.GetAddressBytes() is [169, 254, ..];
                if (ipv4 == null && !apipa)
                    ipv4 = a;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal)
                    continue;
                var a = Addr.FromIPAddress(ip);
                int prefix = ua.PrefixLength is > 0 and <= 128 ? ua.PrefixLength : 128;
                subnets.Add(Subnet.From(a, prefix, v4: false));
                byte first = ip.GetAddressBytes()[0];
                bool global = (first & 0xE0) == 0x20; // 2000::/3
                if (ipv6 == null && global && ua.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                    ipv6 = a;
            }
        }

        bool hasGateway = props.GatewayAddresses.Any(g =>
            !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));

        return new AdapterInfo
        {
            Id = nic.Id,
            Name = nic.Name,
            Description = nic.Description,
            Kind = Classify(nic),
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            IfIndex = ifIndex,
            IPv4 = ipv4,
            IPv6 = ipv6,
            Mtu = mtu,
            HasGateway = hasGateway,
            Subnets = subnets,
        };
    }

    private static AdapterKind Classify(NetworkInterface nic)
    {
        string d = (nic.Description + " " + nic.Name).ToLowerInvariant();
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
            || VpnMarkers.Any(d.Contains))
            return AdapterKind.Vpn;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            return AdapterKind.WiFi;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Ethernet3Megabit)
            return AdapterKind.Ethernet;
        return AdapterKind.Other;
    }
}
