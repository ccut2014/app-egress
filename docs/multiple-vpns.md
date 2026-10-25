# Multiple VPNs: one application, one adapter

Applies to AppEgress. References to TrafikRuler 1.1.0 describe the inherited upstream behavior; see [UPSTREAM.md](../UPSTREAM.md).

Use one rule per executable: an internet application targets VPN A, and a separate internal application targets VPN B. A rule does not split one application's destinations across several VPNs. Prefer full executable paths over filename-only matching. Child processes, service helpers and VPN transport executables are separate processes; avoid routing a VPN's own transport back into its tunnel.

## Discovery and readiness

The 1.1.0 adapter picker enumerates every non-loopback .NET network interface; there is no single-VPN limit or gateway filter. Its main-window summary previously required `IsUp`, a non-APIPA IPv4 address and a nonzero interface index. It could therefore hide a connected VPN that failed those prerequisites. An exception while reading IP properties could also remove an adapter from the picker entirely. A VPN implemented only as a proxy/WFP filter may expose no bindable IP adapter.

This change keeps connected interfaces visible in the summary and keeps disconnected interfaces referenced by rules. The picker retains adapters whose IP properties could not be read, with an unavailable reason. Visibility never marks those adapters as ready. The current engine still requires IPv4 to activate a rule; IPv6-only adapters are visible but unavailable. IPv6 NAT retains the existing global-address selection and single-interface-index limitations; ULA-only IPv6 support is outside this change.

`HasGateway` comes from the adapter's gateway properties, not the forwarding table. It neither proves an internet route nor determines whether a private route works. VPN B can be usable with only its internal prefixes or on-link routes. VPN A may use two `/1` routes rather than `/0`.

## What forwarding can establish

[WinDivert ignores `IfIdx` and `SubIfIdx` for outbound injection](https://github.com/basil00/WinDivert/blob/master/doc/windivert.html#L1604-L1608). Rewriting a source address and setting those fields is not an interface-binding guarantee.

The new route guard asks [GetBestRoute2](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getbestroute2) which interface Windows selects for the translated source and the actual destination, without constraining the lookup to the requested interface. If that lookup fails or selects a different interface, TCP/UDP is blocked and the failure is counted. A route failure sends a reset only to the local TCP application, never through an unintended remote interface. Known blocked first fragments also block their following fragments. Failed decisions retry after one second; successful NAT route checks also expire after one second. Snapshot changes invalidate the successful-check cache.

The rule card shows a warning for ten seconds after a failed route check and retains a cumulative failure count. "Active" describes an enabled rule, not verified connectivity.

This is a preflight check. It does not change the system route table, set socket options in another process, or bypass VPN WFP policies. Successful NAT unit tests and a matching route do not prove physical transmission or receipt. Some route arrangements may remain unsupported and be blocked conservatively. There is a race between a check and injection, including up to one second of cached routing information. An immediate, enforceable per-application boundary would require a separate WFP/socket-binding design or supported VPN-client controls.

## DNS and safety boundaries

- Windows DNS Client service requests are attributed to that service, not the original application. Do not route all of `svchost.exe` to a VPN to compensate. Corporate-name handling depends on the VPN's DNS/NRPT policy; see [Microsoft's VPN name-resolution documentation](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/vpn/vpn-name-resolution). Application-owned DNS/DoH remains subject to its own process and connection rules.
- Disable **Exclude local networks** when testing fixed per-app adapter selection; otherwise connected local subnets intentionally bypass the selected target. Special multicast, broadcast, loopback and link-local destinations retain their existing bypass behavior.
- Enable each rule's kill switch to block while its adapter is unavailable. With the kill switch off, a waiting rule still uses normal Windows routing. An active rule with a failed route check blocks regardless of that setting.
- This is not a leak-proof firewall. Existing behavior passes traffic when the process cannot be identified, passes unmatched/orphan fragments, and returns to normal routing when interception stops or the program exits. Packet-processing exceptions also retain the original fail-open policy.
- Process attribution still keys on protocol and local port. Shared ports or identical tuples across local addresses can be ambiguous, especially for UDP; this change does not repair the process/NAT key design.
- A VPN's kill switch, route interception, overlapping prefixes, MTU, tunnel address rules, NAT restrictions and other WFP filters can reject traffic even after a route check passes. Existing counters measure processed bytes, not packets confirmed on the target interface. No two-VPN compatibility or throughput benchmark has been run for this patch.

## Local verification

`scripts/diagnose-adapters.ps1` lists .NET interfaces and Windows IP interfaces using read-only APIs. It reports address and route counts, without printing addresses, route prefixes, DNS servers or remote endpoints. It makes no connectivity probes and changes no network settings. Run it on the machine showing the problem to distinguish an omitted interface from one visible but unavailable.

Compare the reported runtime as well: [.NET 8 uses gateway/WINS enumeration flags](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Net.NetworkInformation/src/System/Net/NetworkInformation/SystemNetworkInterface.cs#L80-L82), whereas [.NET 10 also requests all interfaces](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.NetworkInformation/src/System/Net/NetworkInformation/SystemNetworkInterface.cs#L79-L82). PowerShell on .NET 10 can therefore list hardware/filter components that TrafikRuler on .NET 8 does not enumerate. A component reported as NotPresent with no IP address or IP-interface index cannot be made routable by simply displaying it or upgrading the runtime.

Automated tests use synthetic packets and fake process/route resolvers. The native route test only looks up loopback locally and sends no packets. WPF previews use synthetic adapters. Live validation still needs the actual VPN clients: confirm both adapters in the picker, use two separate test applications, inspect the intended interface's capture/counters, verify DNS separately, and test disconnect/reconnect plus IPv6. Use authorized test destinations and avoid production sessions because rule changes can reset existing TCP connections.
