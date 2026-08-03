# Contributing

## How to contribute

The best way to contribute is to open a GitHub issue. Bug reports, feature requests, ideas are welcome.

With AI coding, human reviews are the bottleneck. We don't have the bandwidth to review large unsolicited PRs.

## Getting Started (Windows — the PalmierWin app)

### Prerequisites
- Windows 10/11 x64, Vulkan-capable GPU
- Swift 6.3.3+ toolchain (`x86_64-unknown-windows-msvc`)
- .NET 9 SDK
- MSVC Build Tools

### Build and run
```powershell
git clone https://github.com/yassinebkr/palmier-pro
cd palmier-pro\PalmierWin

.\fetch-deps.ps1       # native deps (Vulkan headers, FFmpeg, glslang, ImGui, miniaudio)
.\build.bat            # Swift media engine + PalmierCoreHost.dll
dotnet build Shell     # C# / Avalonia shell
.\Shell\run-shell.ps1  # launch the editor
```

### Test
```powershell
dotnet test PalmierWin\Shell\PalmierShell.sln   # shell + interop suite
.\build.bat                                     # engine harness: palmierwin-spike
```

## Getting Started (macOS — the upstream Palmier Pro app)

### Prerequisites
- macOS 26+
- Xcode 16+
- Swift 6.2 toolchain

### Develop
```bash
swift build
swift run
```

For a bundled debug build that launches the `.app` and streams OSLog:

```bash
./scripts/dev.sh
```

### Test
```bash
swift test
```

## License

By contributing, you agree your contributions are licensed under [GPLv3](LICENSE).
