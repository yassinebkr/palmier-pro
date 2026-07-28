# PalmierWin

Windows-only Swift package for the Palmier Pro Windows media engine: Swift bindings to the Windows media/render APIs (Media Foundation, Direct3D, Direct2D, DirectWrite) and the future `WinFrameRenderer`. This package is intentionally kept out of the repo-root `Package.swift` (which builds the macOS-only `PalmierPro` app and its Apple-only dependencies — Sparkle/Sentry/MLX/etc.), so macOS CI never sees it and Windows CI builds it explicitly.

## Why a separate package

The repo-root `Package.swift` resolves Apple-only SwiftPM dependencies that don't exist on Windows. A Windows-only package lets the Windows media work build in isolation against just the portable `PalmierCore` + the Windows SDK, mirroring the isolated-package technique used throughout the port (see `docs/windows-port-proposal.md`).

## Layout

```
PalmierWin/
  Package.swift              Windows-only manifest; PalmierCore is a path target
  CMediaFoundation/          SwiftPM systemLibrary: filtered C wrapper + module map
  Sources/
    PalmierCore/             (gitignored — symlinked in CI, copied in for local builds)
    PalmierWin/              Swift overlay (MF lifecycle now; WinFrameRenderer later)
```

## PalmierCore dependency

`PalmierCore` is consumed as a **path target**, not a SwiftPM dependency — because depending on the repo-root package would pull in the Apple-only deps. The `Sources/PalmierCore/` directory is gitignored:

- **CI** (`.github/workflows/ci-windows.yml`): symlinks `Sources/PalmierCore` → `../../Sources/PalmierCore` before `swift build`. Windows runners have symlink permission.
- **Local builds** without Developer Mode (can't symlink): copy instead — `cp -r ../Sources/PalmierCore Sources/PalmierCore`. Keep the copy in sync with the core.

## Binding pattern (critical)

Each `C*` systemLibrary target uses a **filtered C wrapper**, NOT an umbrella `#include` of the SDK header. Reason: the Windows SDK's COM-heavy headers (`mediaobj.h`, etc.) use MSVC-specific macros (`DECLSPEC_XFGVIRT`, STDMETHOD vtable declarations) that do not parse under Swift's Clang importer without full MSVC compatibility. Empirically, `#include <mfapi.h>` fails with `expected ')'` in `mediaobj.h:460`.

The pattern (see `CMediaFoundation/CMediaFoundation.h`):

1. Declare a minimal C header with only the symbols actually called — typedefs for `HRESULT`/`ULONG`, the function prototypes, and macro values as constants.
2. Module map: `header "Foo.h"` + `link "ImportLib"` (e.g. `Mfplat`).
3. Swift overlay casts at the boundary where a `#define` imports as `Int32` but the C function takes `UInt32` (`MF_VERSION`, `MFSTARTUP_LITE`).
4. The linker resolves symbols from the Windows SDK import lib (`Mfplat.lib`, `d3d11.lib`, …) — no extra config needed when the MSVC environment is sourced.

Verified end-to-end on Swift 6.3.3 / `x86_64-unknown-windows-msvc`: `CMediaFoundation` compiles, links against `Mfplat.lib`, and `MFStartup` returns `S_OK` (`0x0`) at runtime.

## Building locally (Windows host)

The MSVC environment must be sourced first (Swift's linker needs the SDK):

```bat
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "SWIFT_ROOT=%LOCALAPPDATA%\Programs\Swift"
set "TC=%SWIFT_ROOT%\Toolchains\6.3.3+Asserts\usr"
set "PATH=%TC%\bin;%SWIFT_ROOT%\Runtimes\6.3.3\usr\bin;%PATH%"
set "SDKROOT=%SWIFT_ROOT%\Platforms\6.3.3\Windows.platform\Developer\SDKs\Windows.sdk"
set "SWIFT_DRIVER_WINDOWS_SDK=Windows.sdk"
cd PalmierWin
xcopy /E /I ..\Sources\PalmierCore Sources\PalmierCore   :: if you can't symlink
swift build
```
