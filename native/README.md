# Taskbar material component

LumaBar 0.5 and later integrated versions use the unmodified x64 `ExplorerTAP.dll` from the official TranslucentTB 2026.2 portable release. The TranslucentTB application, ExplorerHooks.dll and Xaml.dll are not required or started.

- Release: https://github.com/TranslucentTB/TranslucentTB/releases/tag/2026.2
- Corresponding source commit: `d4636e439865df0a1a1419db408e055740ce5c74`
- Source submodule: `../vendor/TranslucentTB` (run `git submodule update --init --recursive`)
- Component source: `ExplorerTAP/`, `Common/`, and upstream project dependencies. Follow the upstream README build instructions to rebuild the native project. The bundled binary is upstream's official build, not a locally rebuilt binary.
- IPC contract: `ExplorerTAP/ITaskbarAppearanceService.idl`, API version 4. All methods are invoked on LumaBar's dedicated STA worker.
- License: GNU GPL version 3; see `../LICENSE` and `../THIRD_PARTY_NOTICES.md`.

Archive SHA256: `0dbe8e0255c20e131cde536dcd0ae490d45989a7d360e26d0150dff1922ac420`

DLL SHA256: `c48f1a06168acd7839269dde0d15ccf5d09c702512b94dd6ba9a05df1b153dc2`

`./native/fetch.ps1` retrieves and verifies the pinned official binary if missing. `build.ps1` verifies the binary before compiling.

The component loads into Explorer using TranslucentTB's XAML diagnostics mechanism. It watches LumaBar's process and restores original brushes if LumaBar exits unexpectedly. Normal pause/exit explicitly restores brushes first. The DLL remains loaded in Explorer until Explorer restarts or Windows signs out; do not delete/move its directory while loaded. Changing the native component version requires an Explorer restart or sign-out. LumaBar does not restart Explorer automatically.

This first integration targets Windows 11 x64 with the native XAML taskbar. ARM64, classic replacement taskbars and third-party taskbar customizers are not supported in this build. Only taskbar backgrounds/borders are controlled by this component; Start/Search/Quick Settings retain native materials and follow the system light/dark mode. All taskbars share the selected scene and system glyph tone. From LumaBar 0.6 onward, backgrounds have per-monitor appearance overrides and independent wallpaper-based contrast tint. Material failures fall back to native brushes and require manual retry or an Explorer/taskbar topology change; failed restoration is reported explicitly.
