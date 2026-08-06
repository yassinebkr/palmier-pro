# Launches PalmierShell with the native DLLs (Swift runtime, FFmpeg,
# PalmierCoreHost) on PATH. Usage:
#   .\PalmierWin\Shell\run-shell.ps1 [-Build] [args passed to the app...]
# Example:
#   .\PalmierWin\Shell\run-shell.ps1 --add-to-timeline PalmierWin\test_media\testsrc.mp4
param([switch]$Build)

$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$dotnet = "C:\Program Files\dotnet\dotnet.exe"

if ($Build) {
    & $dotnet build "$repo\PalmierWin\Shell\PalmierShell\PalmierShell.csproj"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$env:PATH = @(
    "C:\Users\yassi\AppData\Local\Programs\Swift\Runtimes\6.3.3\usr\bin",
    "$repo\PalmierWin\ThirdParty\ffmpeg\bin",
    "$repo\PalmierWin\.build\x86_64-unknown-windows-msvc\debug",
    $env:PATH
) -join ";"

& "$repo\PalmierWin\Shell\PalmierShell\bin\Debug\net9.0-windows\PalmierShell.exe" --no-update @args
