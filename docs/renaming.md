# AppEgress rename — 2026-10-26

AppEgress means application network egress analysis and routing management. The source
repository is [ccut2014/app-egress](https://github.com/ccut2014/app-egress), created by
the maintainer for this fork. Binary releases are separate from source publication.

## Source layout

- `AppEgress.sln`
- `src/AppEgress/AppEgress.csproj`: assembly, root namespace, product and executable name AppEgress.
- `tests/AppEgress.Tests/AppEgress.Tests.csproj`: renamed test assembly and project reference.
- C# namespaces, WPF class/resource namespaces, window/tray branding and all three localization tables updated.
- English, Simplified Chinese and Russian READMEs updated around the current scope and its evidence limits.
- `UPSTREAM.md` preserves TrafikRuler provenance, base revision, original authorship and inherited icon attribution.
- Issue/release links target ccut2014/app-egress. Original upstream release links remain only in historical material.

The existing checkout folder has not been renamed. Historical ignored `bin/obj` output
and older TrafikRuler archives are preserved. Their filenames and contents are not
treated as AppEgress builds.

## Runtime compatibility

New settings/logs use `%APPDATA%\AppEgress`. The loader reads the old TrafikRuler settings
only when the new settings file does not exist. Future saves go to the new directory;
the old settings are not moved or deleted. If a new file is malformed, the loader
does not silently activate old rules.

Legacy mutex/show-event identifiers are shared to prevent two packet engines from
running simultaneously. The existing Task Scheduler slot is reused. Its current
executable path is checked when showing startup status, and the path changes only
when the user explicitly enables AppEgress autostart. No local task or user setting
was changed while editing these sources.

## Packaging

`scripts/package.ps1` defaults to a framework-dependent lightweight Windows x64 package,
with separate corresponding source and checksums in `dist/`. It does not launch the
app or run tests. `-Variant Both` also builds a self-contained full package. Source
archives contain only the committed source. An optional `-RestoreSource` can use an explicit dependency feed.
GitHub's release workflow calls this script after its existing test step and creates
a draft release when a future release tag is pushed.

## Validation status

The source rename and initial source publication used source/reference and format
checks only; those steps did not build or run the application, execute tests, migrate
settings or change scheduled tasks. Later release builds record their validation
scope in the release notes and each package's `BUILD-INFO.json`.
