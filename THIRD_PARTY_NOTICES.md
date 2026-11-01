# Third-party notices

## TranslucentTB

This LumaBar integration includes the unmodified `ExplorerTAP.dll` from TranslucentTB 2026.2 and uses its taskbar appearance service. TranslucentTB is developed by its authors and contributors, including Ethanhs, Charles Milette (sylveon), MrAksel and denosawr. It is an independent community project.

- Project and copyright information: https://github.com/TranslucentTB/TranslucentTB
- Exact corresponding source: https://github.com/TranslucentTB/TranslucentTB/tree/d4636e439865df0a1a1419db408e055740ce5c74
- Local source: `vendor/TranslucentTB`, pinned git submodule
- License: GNU General Public License, version 3 (`LICENSE`; upstream `vendor/TranslucentTB/LICENSE.md`)
- Binary provenance and checksums: `native/README.md`

Upstream source retains its own copyright and third-party notices. Its build dependencies include Windows Implementation Library, C++/WinRT and Microsoft Detours; consult the pinned source and its dependency manifests for their notices and build requirements.

## Original LumaBar

LumaBar through 0.4 was distributed under the MIT License. The original MIT notice is retained in `LICENSE-LumaBar-MIT`. The combined 0.5+ integration is distributed under GPL-3.0 with the TranslucentTB component. This integration adds managed interop, scene coordination, settings UI and taskbar-region wallpaper sampling; the upstream native binary is unmodified.

No warranty is provided. See `LICENSE` for the complete license terms. A source distribution should include this notice, the managed sources, the pinned native source/dependency instructions and build scripts. Binary distributions should also provide the corresponding source for this exact version under the GPL terms.
