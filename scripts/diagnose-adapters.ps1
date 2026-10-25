<#
.SYNOPSIS
Read-only interface discovery for AppEgress. No network probes or setting changes.
.DESCRIPTION
Does not print IP addresses, route prefixes, DNS servers or remote endpoints.
Run with both VPN clients connected and compare DotNetInterfaces with WindowsIpInterfaces.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$notes = [System.Collections.Generic.List[string]]::new()
$dotNetInterfaces = @(
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.NetworkInterfaceType -eq 'Loopback') { continue }
        $v4Index = 0
        $v6Index = 0
        $v4Count = 0
        $v6Count = 0
        $gatewayCount = 0
        $readError = $null
        try {
            $properties = $nic.GetIPProperties()
            try { $v4Index = $properties.GetIPv4Properties().Index } catch { }
            try { $v6Index = $properties.GetIPv6Properties().Index } catch { }
            $v4Count = @($properties.UnicastAddresses | Where-Object {
                $_.Address.AddressFamily -eq 'InterNetwork' -and
                ($_.Address.GetAddressBytes()[0..1] -join '.') -ne '169.254'
            }).Count
            $v6Count = @($properties.UnicastAddresses | Where-Object {
                $_.Address.AddressFamily -eq 'InterNetworkV6' -and -not $_.Address.IsIPv6LinkLocal
            }).Count
            $gatewayCount = $properties.GatewayAddresses.Count
        } catch {
            $readError = $_.Exception.GetBaseException().GetType().Name
        }
        [pscustomobject]@{
            Id = $nic.Id
            Name = $nic.Name
            Description = $nic.Description
            Type = $nic.NetworkInterfaceType.ToString()
            Status = $nic.OperationalStatus.ToString()
            IPv4Index = $v4Index
            IPv6Index = $v6Index
            IPv4AddressCount = $v4Count
            NonLinkLocalIPv6AddressCount = $v6Count
            ReportedGatewayCount = $gatewayCount
            ReadyForIPv4RouteCheck = -not $readError -and $nic.OperationalStatus -eq 'Up' -and $v4Index -gt 0 -and $v4Count -gt 0
            PropertyReadError = $readError
        }
    }
)

$windowsIpInterfaces = @()
try {
    $routes = @(Get-NetRoute -PolicyStore ActiveStore)
    $windowsIpInterfaces = @(Get-NetIPInterface | ForEach-Object {
        $ipInterface = $_
        $interfaceRoutes = @($routes | Where-Object {
            $_.InterfaceIndex -eq $ipInterface.InterfaceIndex -and $_.AddressFamily -eq $ipInterface.AddressFamily
        })
        [pscustomobject]@{
            Name = $ipInterface.InterfaceAlias
            Index = $ipInterface.InterfaceIndex
            Family = $ipInterface.AddressFamily.ToString()
            Status = $ipInterface.ConnectionState.ToString()
            DefaultRouteCount = @($interfaceRoutes | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/0', '::/0') }).Count
            OtherRouteCount = @($interfaceRoutes | Where-Object { $_.DestinationPrefix -notin @('0.0.0.0/0', '::/0') }).Count
        }
    })
} catch {
    $notes.Add('Windows IP-interface detail unavailable: ' + $_.Exception.GetBaseException().GetType().Name)
}

[pscustomobject]@{
    Runtime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    DotNetInterfaces = $dotNetInterfaces
    WindowsIpInterfaces = $windowsIpInterfaces
    Notes = $notes.ToArray()
} | ConvertTo-Json -Depth 5
