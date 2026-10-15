# Third-party notices

TrafikRuler is licensed under the GNU General Public License v3.0 or later (see [LICENSE](LICENSE)).
Release archives also ship the following third-party components.

## WinDivert 2.2.2

- Files: `WinDivert.dll`, `WinDivert64.sys` (unmodified, signed by the WinDivert author)
- Author: basil00 and contributors
- Homepage and source code: <https://github.com/basil00/WinDivert>
- Release used: <https://github.com/basil00/WinDivert/releases/tag/v2.2.2>
- License: dual-licensed under the **GNU Lesser General Public License v3** or the
  **GNU General Public License v2**, at the user's choice. TrafikRuler uses it under LGPL-3.0.
  Full license texts are included in the WinDivert source distribution linked above.

TrafikRuler loads `WinDivert.dll` dynamically and does not modify it. You may replace the
WinDivert files next to `TrafikRuler.exe` with any compatible build of WinDivert 2.2.x,
including one you compiled yourself from the source linked above.

## .NET

TrafikRuler runs on the .NET 8 Desktop Runtime, which is installed separately by the user
from <https://dotnet.microsoft.com/download/dotnet/8.0> and is distributed by Microsoft
under the MIT license. The runtime is not bundled with release archives.
