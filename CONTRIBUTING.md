# Contributing to AppEgress

AppEgress is maintained by [ccut2014](https://github.com/ccut2014) and derives from TrafikRuler. Preserve the attribution in [UPSTREAM.md](UPSTREAM.md), the GPL license and third-party notices.

## Report a problem

Use this repository's [issues](https://github.com/ccut2014/app-egress/issues). Include the AppEgress version, Windows version, relevant process names, rule mode and interface status. Redact credentials, subscriptions and private addresses before sharing logs or screenshots. For security issues, see [SECURITY.md](SECURITY.md).

Distinguish an observed TCP peer from a candidate listener, a route query from packet transmission, and missing metadata from a successful negative result.

## Develop

Use Windows and the .NET 8 SDK:

```powershell
./scripts/get-windivert.ps1
dotnet restore AppEgress.sln
dotnet build AppEgress.sln -c Release
```

The source is in `src/AppEgress`; tests are in `tests/AppEgress.Tests`. See [architecture](docs/architecture.md), [monitoring](docs/monitoring.md) and [routing limits](docs/multiple-vpns.md).

Run tests appropriate to a requested change. The existing CI builds and tests pull requests. For UI changes, use the existing WPF smoke-preview workflow; `APPEGRESS_PREVIEW_DIR` sets the output directory, and the old `TRAFIKRULER_PREVIEW_DIR` is accepted as a fallback.

## Translate

Localization files are in `src/AppEgress/Localization`. English is the key reference. Preserve placeholders and plural forms, and register a new language in `Loc.Languages`. English uses one/other, Russian one/few/many and Simplified Chinese other.

## Package

From a Git checkout with Git and the .NET 8 SDK installed:

```powershell
./scripts/package.ps1 -Version 1.1.0
```

The script creates a lightweight Windows x64 package, corresponding source archive and checksums in `dist/`. It does not launch the program or run tests. Keep personal settings and build caches out of source releases.

## Scope of changes

Keep changes focused. Packet processing, process ownership and settings migration need careful review because an incorrect conclusion can affect which network an application uses. Preserve unknown and incomplete states rather than inventing a verified VPN path.

Contribute through [ccut2014/app-egress](https://github.com/ccut2014/app-egress). Binary releases are separate maintainer actions.
