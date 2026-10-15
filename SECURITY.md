# Security Policy

TrafikRuler runs with administrator rights and loads a kernel-mode packet filter driver, so we take security reports seriously.

## Supported versions

Only the [latest release](https://github.com/li4inka/trafik-ruler/releases/latest) receives security fixes.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately via GitHub: **[Report a vulnerability](https://github.com/li4inka/trafik-ruler/security/advisories/new)**. Include:

- the TrafikRuler version and Windows version;
- what an attacker can achieve (for example, privilege escalation, traffic leaking past a kill switch, crashes triggered by crafted packets);
- steps to reproduce or a proof of concept.

You can expect an acknowledgment within a few days. We'll keep you updated, agree on a disclosure date with you, and credit you in the release notes unless you prefer otherwise.

## In scope

- Code in this repository (packet parsing and rewriting, NAT, rule handling, settings, autostart).
- Release packaging and the GitHub Actions workflows.

## Out of scope

- Vulnerabilities in [WinDivert](https://github.com/basil00/WinDivert) itself — please report those upstream.
- Microsoft Defender classifying WinDivert as `HackTool:Win64/WinDivert` — this is a known, documented classification, see the [README](README.md#about-the-microsoft-defender-warning).

## Verifying downloads

Every release lists SHA256 checksums for its files. Check them before running:

```powershell
Get-FileHash .\TrafikRuler-*-win-x64.zip -Algorithm SHA256
```

`WinDivert64.sys` must have a valid Authenticode signature:

```powershell
Get-AuthenticodeSignature .\WinDivert64.sys
```

Only download TrafikRuler from [this repository's Releases page](https://github.com/li4inka/trafik-ruler/releases).
