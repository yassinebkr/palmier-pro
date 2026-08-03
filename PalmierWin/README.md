# PalmierWin

The Windows side of the repo: the Swift media engine, the C ABI host, and the C#/Avalonia shell. Everything is flat-C bindable — FFmpeg + Vulkan + miniaudio + stb_truetype + Dear ImGui (harness only) — **no COM, no WinUI 3**. `CMediaFoundation` is kept as a flat-C reference (MFStartup works) but Media Foundation's COM decode surface is unbindable from Swift; the engine uses FFmpeg/Vulkan.

This package is intentionally kept out of the repo-root `Package.swift` (which builds the macOS-only `PalmierPro` app and its Apple-only dependencies), so macOS CI never sees it and Windows CI builds it explicitly.

## Layout

```
PalmierWin/
  Package.swift              Windows-only manifest; PalmierCore is a path target
  fetch-deps.ps1             Fetches Vulkan-Headers, FFmpeg, glslang, ImGui into ThirdParty/
  build.bat                  Sources MSVC env, then swift build
  build-shaders.ps1          Compiles Shaders/*.{vert,frag} -> .spv + Shaders.swift
  make-test-media.ps1        Generates test_media/ fixtures
  CMediaFoundation/          systemLibrary: filtered C wrapper (reference)
  CVulkan/                   systemLibrary: umbrella include of vulkan.h
  CFFmpeg/                   systemLibrary: libav* umbrella include (+ swresample)
  CMiniaudio/                Vendored miniaudio (stb-style single header) for WASAPI audio
  CSTBTrueType/              stb_truetype C target (text layers)
  CImGui/                    Dear ImGui flat-C++ wrapper (engine dev harness UI)
  Shaders/                   GLSL sources + compiled .spv (layer quad, effect dispatch)
  Sources/
    PalmierCore/             (gitignored — symlinked in CI, copied in for local builds)
    PalmierWin/              Engine: decoders, Vulkan objects, compositor, audio, caches
    PalmierCoreHost/         @_cdecl C ABI over the engine + PalmierCore (the shell's backend)
    palmierwin-spike/        Engine dev harness (console exe: decode/render/effects/export checks)
  Shell/
    PalmierShell/            The Avalonia editor app
    PalmierShell.Tests/      xunit interop/geometry/generation suites
    Spike/                   Interop proof (C# -> Swift DLL, HWND rendering)
    run-shell.ps1            Builds + runs the editor with the right PATH
```

## Build

Requires the Swift 6.3.3+ Windows toolchain, MSVC Build Tools, and .NET 9 SDK.

```powershell
.\fetch-deps.ps1     # once (or when deps change)
.\build.bat          # Swift: engine + PalmierCoreHost.dll + spike
dotnet build Shell   # C# shell
```

`build.bat` sources the MSVC environment itself. `PalmierCore` is a path target:
CI symlinks `Sources/PalmierCore`; locally without Developer Mode, copy it:
`cp -r ../Sources/PalmierCore Sources/PalmierCore`.

## The C ABI boundary (PalmierCoreHost)

The shell calls the Swift core in-proc via `PalmierCoreHost.dll`. Rules (learned the hard way — see git history):

- Opaque handles for anything stateful; every create has a matching destroy.
- Value types and JSON across the boundary; events are polled, not callbacks.
- Teardown is dependency order (swapchain → device → instance).
- A Vulkan object's Swift owner must outlive every in-flight command buffer that references it.

## Binding pattern (critical)

Each `C*` systemLibrary target uses a **filtered C wrapper**, NOT an umbrella `#include` of the SDK header. The Windows SDK's COM-heavy headers use MSVC-specific macros that do not parse under Swift's Clang importer. The pattern (see `CMediaFoundation/CMediaFoundation.h`): declare only the symbols actually called, `link` the import lib in the module map, cast at the boundary where a `#define` imports as `Int32` but the C function takes `UInt32`.

## Verification

- `dotnet test Shell/PalmierShell.sln` — the shell + interop suite (300+ tests).
- `palmierwin-spike` — engine harness: decode/playback/export/effect-kernel readback checks. Needs `test_media/` (`make-test-media.ps1`) and a GPU.
