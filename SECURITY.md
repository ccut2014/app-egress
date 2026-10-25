# Security policy

AppEgress runs with administrator rights. Routing mode loads WinDivert and can alter or reset connections. Monitor-only rules use read-only metadata, but the application retains its administrator launch requirement.

## Report privately

Do not publish credentials, VPN subscriptions or sensitive connection records in a public issue.

When private reporting is enabled, use [GitHub private vulnerability reporting](https://github.com/ccut2014/app-egress/security/advisories/new). If that channel is unavailable, contact [ccut2014](https://github.com/ccut2014) through an available private channel before sharing details.

Describe the affected version, Windows version, impact and reproducible steps. Do not test against a server or account without authorization.

## Current limitations

- Windows route queries do not prove packet transmission or end-to-end VPN forwarding.
- Unknown process ownership, unmatched fragments, stopped interception and packet-processing exceptions retain inherited fail-open behavior.
- A local peer's other connections are not verified forwarding paths of the original application.
- Shared proxy upstream attribution, actual proxy billing and UDP remote endpoints remain unknown.
- A rename, build or passing synthetic test does not establish complete dual-VPN compatibility.

There is no AppEgress release or security audit published as part of the source rename.

## Verify releases

Use this repository's [Releases](https://github.com/ccut2014/app-egress/releases) when available, compare the provided SHA-256 checksums, and check the WinDivert driver signature:

```powershell
Get-FileHash .\AppEgress-*-win-x64.zip -Algorithm SHA256
Get-AuthenticodeSignature .\WinDivert64.sys
```

WinDivert DLL/driver hashes are pinned in `scripts/get-windivert.ps1`. The upstream DLL is unsigned; the driver must have a valid signature. AppEgress application signing, if used in a future release, is separate from the driver signature.

WinDivert vulnerabilities should be reported to [WinDivert upstream](https://github.com/basil00/WinDivert). Preserve the attribution and third-party notices when distributing modified builds.
