# Contributing to TrafikRuler

Thanks for helping! Every kind of contribution counts: bug reports, testing on unusual network setups, translations, docs and code.

## Ways to help

- **Report a bug** — use the [bug report form](https://github.com/li4inka/trafik-ruler/issues/new?template=bug_report.yml). Your Windows version, adapter types (Ethernet, Wi‑Fi, VPN client, LTE…) and the log from `%AppData%\TrafikRuler\log.txt` make a huge difference.
- **Test with games, VPN clients and anti-cheat** and tell us what works and what doesn't — compatibility reports are some of the most valuable issues.
- **Translate the UI.** English localization is the top roadmap item; other languages will follow the same mechanism.
- **Pick an issue** labeled [`good first issue`](https://github.com/li4inka/trafik-ruler/labels/good%20first%20issue) or [`help wanted`](https://github.com/li4inka/trafik-ruler/labels/help%20wanted).

For bigger changes, please open an issue first so we can agree on the approach before you spend time on it.

## Development setup

1. Windows 10/11 x64 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer).
2. Clone and fetch WinDivert:
   ```powershell
   git clone https://github.com/li4inka/trafik-ruler.git
   cd trafik-ruler
   powershell -ExecutionPolicy Bypass -File scripts\get-windivert.ps1
   ```
   Microsoft Defender may delete the WinDivert files. See [the README](README.md#about-the-microsoft-defender-warning) for how to allow that single detection.
3. Build and test:
   ```powershell
   dotnet build
   dotnet test
   ```
4. Run `src\TrafikRuler\bin\Debug\net8.0-windows\win-x64\TrafikRuler.exe` as administrator.

Read [docs/architecture.md](docs/architecture.md) before touching `Core/` — it explains the packet path, snapshots and threading model.

## Pull requests

- Keep PRs focused: one fix or feature per PR.
- **Tests:** packet and NAT logic must come with unit tests (see `tests/TrafikRuler.Tests/PacketProcessorTests.cs`). Use `TestPackets.ReferenceValid` to check checksums.
- **UI changes:** attach before/after screenshots. You can render them headlessly — set `TRAFIKRULER_PREVIEW_DIR` and run `dotnet test`.
- **Style:** follow `.editorconfig` and the surrounding code. Comments explain *why*, not *what*.
- Make sure `dotnet build` produces no warnings and `dotnet test` passes.
- By submitting a PR you agree that your contribution is licensed under the project's [GPL-3.0 license](LICENSE).

## Commit messages

Short imperative subject line (`Fix IPv6 RST for fragmented SYN`), optional body explaining the reason for the change. Reference issues with `Fixes #123`.

## Releases (maintainers)

Pushing a tag like `v1.2.0` runs [`.github/workflows/release.yml`](.github/workflows/release.yml): tests, publish, download and verify WinDivert, package the zip with SHA256 checksums and create a GitHub release draft. Update [CHANGELOG.md](CHANGELOG.md) before tagging. Notes on code signing and antivirus reputation: [docs/security-audit.md](docs/security-audit.md) (in Russian).
