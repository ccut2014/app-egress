# Origin and attribution

**AppEgress** is a modified fork of [TrafikRuler](https://github.com/li4inka/trafik-ruler),
originally developed by **kulich Software** and published by **li4inka**.
AppEgress is maintained by [ccut2014](https://github.com/ccut2014).

Upstream base revision: `9868c18b3426f8d5c2d34c0b651eaf0956bed7bd`.
The source tree includes subsequent local changes; this revision alone does not identify
an AppEgress release. AppEgress is not an official TrafikRuler release.

## AppEgress changes — 2026-10-25 to 2026-10-26

- Broader adapter visibility, including private-network VPN interfaces without a default gateway.
- Windows route preflight checks around the inherited WinDivert/NAT routing path.
- Independent monitor-only rules using read-only TCP/UDP owner-PID tables.
- Local TCP peer ownership, related-process route evidence and system proxy endpoint matching.
- Explicit unknown, ambiguous, incomplete and shared-proxy attribution states.
- Simplified Chinese localization alongside English and Russian.
- Product, executable, solution, project and namespace rename to AppEgress; documentation and release naming updated.
- Legacy settings fallback and shared instance/autostart identifiers for migration.

## Preserved attribution and licensing

The original authorship remains in assembly metadata, this notice and the license documentation.
The route icon, tray artwork and their generator are inherited from TrafikRuler;
renaming their product labels does not claim original authorship of that artwork.

AppEgress retains the upstream **GPL-3.0-or-later** license. See [LICENSE](LICENSE).
WinDivert notices remain in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Distribute the corresponding AppEgress source with binary releases or make the exact
source archive available alongside them. Preserve the notices in that source.

## Compatibility identifiers

New settings and logs use `%APPDATA%\AppEgress`. If its `settings.json` does not exist,
AppEgress reads `%APPDATA%\TrafikRuler\settings.json` as a fallback. Saving writes to the
new directory. The original settings are not moved or deleted. A malformed new file
does not silently activate an older routing configuration.

The instance mutex and show event intentionally keep their TrafikRuler identifiers,
so the old and renamed packet engines do not run simultaneously. Exit the old app
from its tray menu before starting AppEgress.

Autostart reuses the existing Task Scheduler slot named `TrafikRuler`. It is shown
as enabled only when its executable path matches the currently running AppEgress.
Explicitly enabling autostart in AppEgress updates that slot to `AppEgress.exe`;
the rename itself does not edit or delete a scheduled task.

The historical upstream audit at [docs/security-audit.md](docs/security-audit.md)
is retained for provenance. Its historical branding, file sizes and release advice
are not AppEgress's current release status or an additional license restriction.
