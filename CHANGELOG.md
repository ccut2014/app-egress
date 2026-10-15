# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/li4inka/trafik-ruler/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/li4inka/trafik-ruler/releases/tag/v1.0.0
