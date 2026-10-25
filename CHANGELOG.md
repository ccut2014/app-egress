# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### AppEgress fork — 2026-10-26

- Rename the product, executable, solution, projects and namespaces to AppEgress; source repository: ccut2014/app-egress.
- Preserve TrafikRuler attribution, GPL licensing and inherited route/tray artwork.
- Keep connected and referenced VPN interfaces visible independently of forwarding prerequisites.
- Add Windows route preflight checks, monitor-only rules, local peer and related-process route evidence, and explicit system proxy endpoint evidence.
- Add Simplified Chinese and retain English/Russian localization.
- Read legacy TrafikRuler settings when no AppEgress settings file exists; save future settings in the new directory.
- Retain shared instance/autostart identifiers for compatibility; update startup paths only when the user enables autostart.
- Default to lightweight packages with separate corresponding source and checksums.

The renamed source has not yet been built or tested. The versions below describe the upstream TrafikRuler history, not AppEgress releases.

## [1.1.0] - 2026-10-15

### Added

- **English interface.** All windows, the tray menu, statuses, error messages and the log are translated.
- **Language switch** in Settings: *Auto*, English, Русский. Changes apply instantly, without a restart.
- **Automatic language detection** on first start: Russian if it is the Windows display language or the first language in the user's language list, English otherwise.
- Translations live in one JSON file per language (`src/TrafikRuler/Localization`), with correct plural forms; adding a language needs no code changes beyond one line.

### Changed

- Sizes and speeds use the units and decimal separator of the selected language (`1.5 MB/s` / `1,5 МБ/с`).

## [1.0.0] - 2026-10-15

First public release.

### Added

- Per-application routing through a chosen network adapter (Ethernet, Wi‑Fi, VPN, LTE/USB modems) using WinDivert-based NAT.
- Works on already-running applications: existing TCP connections are reset so the app reconnects through the new route.
- Kill switch: block an app's traffic when its adapter is unavailable.
- Local network exclusion, IPv6 support with fallback to IPv4, TCP MSS clamping, IPv4 fragment tracking.
- Windows 11 Fluent UI with Mica, light/dark theme, live per-rule speed graphs and adapter throughput.
- Tray icon with status, pause/resume, and *Start with Windows* via Task Scheduler.
- Single ~0.5 MB executable; .NET 8 Desktop Runtime is installed by the user when missing.

[Unreleased]: https://github.com/ccut2014/app-egress/compare/9868c18b3426f8d5c2d34c0b651eaf0956bed7bd...HEAD
[1.1.0]: https://github.com/li4inka/trafik-ruler/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/li4inka/trafik-ruler/releases/tag/v1.0.0
