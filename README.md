# AppEgress

**Windows application network egress analysis and routing management.**

AppEgress helps you understand which process owns a connection, which local service or proxy it reaches, and which interface Windows currently selects. It also provides separate per-application adapter rules.

[简体中文](README.zh-CN.md) · [Русский](README.ru.md) · [Network evidence](docs/monitoring.md) · [Routing limits](docs/multiple-vpns.md)

Maintained by [ccut2014](https://github.com/ccut2014). Based on [TrafikRuler](https://github.com/li4inka/trafik-ruler) by kulich Software / li4inka. This is a modified fork, not an official TrafikRuler release. See [UPSTREAM.md](UPSTREAM.md) and [LICENSE](LICENSE).

## What you can see

| Feature | Behavior |
|---|---|
| Monitor only | Observe current TCP connections and UDP local bindings without selecting an adapter or changing traffic. |
| Local peer ownership | Match a local connection to its peer process; distinguish exact reverse connections from listener candidates. |
| Network path evidence | Show the application's route queries and related local-process connections separately, including interface/VPN names. |
| System proxy evidence | Identify connections to a configured system proxy endpoint; keep shared upstream attribution and billing unknown. |
| Adapter rules | Select an adapter for a matching executable, subject to Windows routing and VPN compatibility. |
| Native desktop UI | WPF, light/dark themes, tray controls, English, Russian and Simplified Chinese. |

## Read the evidence correctly

- A route query is not a packet capture or proof of successful forwarding through a VPN.
- A local peer's other connections are association evidence; they are not automatically attributed to the original app.
- A proxy endpoint match does not verify its protocol, final exit, selected node or quota consumption.
- UDP rows currently have no remote endpoint or route attribution. Monitor mode does not sample byte totals or speed.
- Counts describe the current snapshot, not command executions, packet counts or connection history.
- Rules match the selected executable path or name. Differently named child/helper processes require their own rules.
- Routing mode retains fail-open cases such as unknown process ownership and stopped interception. It is not a leak-proof firewall.

Details: [monitoring](docs/monitoring.md), [multiple VPNs](docs/multiple-vpns.md).

## Run

Requirements: Windows 10/11 x64 and administrator rights. Choose `win-x64-light.zip` if .NET 8 **Desktop Runtime** x64 is installed, or `win-x64-full.zip` to use the included runtime. WinDivert files are supplied beside the executable for routing.

1. Download an AppEgress archive from [Releases](https://github.com/ccut2014/app-egress/releases) when available, and extract the whole folder.
2. Exit any old TrafikRuler/AppEgress instance through its tray menu.
3. Run `AppEgress.exe`.
4. Choose **Monitor only** to inspect an app, or **Use an adapter** to configure a routing rule. Resume if paused.
5. Expand connection details and select a row to inspect its peer and evidence.

Settings → Language switches languages without restarting.

## Upgrade from TrafikRuler

Settings/logs use `%APPDATA%\AppEgress`. When the new settings file is absent, AppEgress reads the old `%APPDATA%\TrafikRuler\settings.json`; saving writes into the new directory. The old file is preserved.

The old instance guard and autostart task slot are retained intentionally. Re-enable autostart in AppEgress to update that task's executable path. No task is changed merely by renaming the source. See [compatibility details](UPSTREAM.md#compatibility-identifiers).

## Build and package

Install the .NET 8 SDK. From the repository root:

```powershell
./scripts/get-windivert.ps1
dotnet restore AppEgress.sln
dotnet build AppEgress.sln -c Release
```

Create both packages, corresponding source and checksums in `dist/` (omit `-Variant Both` for the default lightweight package). Commit source changes before packaging:

```powershell
./scripts/package.ps1 -Version 1.1.0 -Variant Both
```

The package script does not run tests or launch the app. Run the existing tests separately when requested:

```powershell
dotnet test AppEgress.sln -c Release
```

## License and credits

GPL-3.0-or-later. Original TrafikRuler authorship, route/tray artwork and third-party notices are preserved. See [UPSTREAM.md](UPSTREAM.md), [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
