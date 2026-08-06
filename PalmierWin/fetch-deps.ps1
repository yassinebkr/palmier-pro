# Reproducibly fetches the Windows media-engine native deps into PalmierWin/ThirdParty:
#   - Vulkan-Headers (Khronos, MIT) — the flat-C headers CVulkan binds.
#   - vulkan-1.lib — import lib generated from the system loader (vulkan-1.dll).
#   - FFmpeg shared dev build (BtbN, GPL) — libavformat/libavcodec/libavutil headers + libs + DLLs.
#   - glslangValidator (KhronosGroup/glslang main-tot) — compiles Shaders/*.vert|.frag
#     to .spv; the compiled bytecode is committed as Swift byte arrays in
#     Sources/PalmierWin/Shaders.swift, so this is only needed when editing shaders.
#
# Run from the PalmierWin/ directory (or pass -Root). Idempotent: skips a step
# if its output already exists. Used by both CI (.github/workflows/ci-windows.yml)
# and local builds — see README.md for the manual MSVC-env sourcing step.
#
# Why these sources: both are flat-C (no COM), the binding pattern that makes
# CVulkan/CFFmpeg work. Vulkan-Headers + the system loader cover the render API;
# BtbN FFmpeg covers decode/export. Alternatives (Media Foundation, Direct3D)
# are COM and unbindable from Swift today — see docs/windows-media-engine-design.md.
[CmdletBinding()]
param(
    [string]$Root = (Get-Location)
)

$ErrorActionPreference = "Stop"
$ThirdParty = Join-Path $Root "ThirdParty"
New-Item -ItemType Directory -Force -Path $ThirdParty | Out-Null

# Helper: run a native EXE, streaming its stdout+stderr to the host and failing
# on a non-zero exit. Uses Start-Process to bypass PowerShell's NativeCommandError
# behavior (under ErrorActionPreference=Stop, any native stderr line aborts
# before 2>&1 can redirect it — a notorious PS gotcha that killed the fetch).
function Invoke-Native([string]$Exe, [string[]]$CmdArgs) {
    $outFile = Join-Path $env:TEMP "nat-out-$(Get-Random).txt"
    $errFile = Join-Path $env:TEMP "nat-err-$(Get-Random).txt"
    Write-Host "[Invoke-Native] $Exe $($CmdArgs -join ' ')"
    $proc = Start-Process -FilePath $Exe -ArgumentList $CmdArgs -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    Write-Host "[Invoke-Native] exit code: $($proc.ExitCode)"
    if (Test-Path $outFile) { Write-Host "--- stdout ---"; Get-Content $outFile | ForEach-Object { Write-Host $_ } }
    if (Test-Path $errFile) { Write-Host "--- stderr ---"; Get-Content $errFile | ForEach-Object { Write-Host $_ } }
    if ($proc.ExitCode -ne 0) { throw "$Exe exited with code $($proc.ExitCode)" }
}

# --- 1. Vulkan headers (flat-C, Khronos) -------------------------------------
$VkHeaders = Join-Path $ThirdParty "Vulkan-Headers"
if (-not (Test-Path (Join-Path $VkHeaders "include/vulkan/vulkan_core.h"))) {
    Write-Host "==> fetching Vulkan-Headers"
    # Clone directly into ThirdParty (same volume as the workspace) — cloning to
    # $env:TEMP then Move-Item across volumes (C: -> D: on GH runners) fails on
    # the read-only files inside .git. Remove a stale checkout first.
    if (Test-Path $VkHeaders) { Remove-Item -Recurse -Force $VkHeaders }
    Invoke-Native "git" @("clone", "--depth", "1", "https://github.com/KhronosGroup/Vulkan-Headers.git", $VkHeaders)
} else { Write-Host "==> Vulkan-Headers present, skipping" }

# --- 2. vulkan-1.lib (generated from the system loader) ----------------------
$VkLib = Join-Path $ThirdParty "vulkan-1.lib"
if (-not (Test-Path $VkLib)) {
    Write-Host "==> generating vulkan-1.lib from C:\Windows\System32\vulkan-1.dll"
    $work = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP "vk-lib-$(Get-Random)")
    $raw = Join-Path $work "raw.txt"
    # dumpbin -> raw.txt. Use cmd.exe redirection to capture stdout to a file
    # (simpler than ProcessStartInfo across PS versions).
    cmd /c "dumpbin /exports C:\Windows\System32\vulkan-1.dll > `"$raw`" 2>&1"
    $names = Select-String -Path $raw -Pattern '^\s+\d+\s+\w+\s+\w+\s+(vk\w+)' |
        ForEach-Object { $_.Matches.Groups[1].Value }
    "LIBRARY VULKAN-1.DLL", "EXPORTS", ($names -join "`n") | Set-Content (Join-Path $work "vulkan-1.def")
    Invoke-Native "lib" @("/DEF:$(Join-Path $work 'vulkan-1.def')", "/OUT:$VkLib", "/MACHINE:x64")
} else { Write-Host "==> vulkan-1.lib present, skipping" }

# --- 3. FFmpeg shared dev build (BtbN, GPL) ----------------------------------
# Pinned autobuild, never "latest": the tag moves daily and silently diverged
# dev (avcodec-62) from CI (avcodec-63) — same source, different decode stack.
# Verified byte-identical to the v0.1.3 install. Bump deliberately.
$FFmpegZip = "https://github.com/BtbN/FFmpeg-Builds/releases/download/" +
             "autobuild-2026-08-05-15-18/ffmpeg-N-125972-ge13b2e00e8-win64-gpl-shared.zip"
$FFRoot = Join-Path $ThirdParty "ffmpeg"
if (-not (Test-Path (Join-Path $FFRoot "include/libavformat"))) {
    Write-Host "==> fetching BtbN FFmpeg shared build"
    $zip = Join-Path $ThirdParty "ffmpeg-shared.zip"
    Invoke-Native "curl.exe" @("-sL", "-o", $zip, $FFmpegZip)
    # The zip's top dir is ffmpeg-master-latest-win64-gpl-shared. Extract into
    # ThirdParty, then move that single child to ThirdParty/ffmpeg (same-volume
    # move — metadata-only, works across runner volumes unlike TEMP->workspace).
    if (Test-Path $FFRoot) { Remove-Item -Recurse -Force $FFRoot }
    Expand-Archive -Path $zip -DestinationPath $ThirdParty -Force
    $extracted = Get-ChildItem -Directory $ThirdParty | Where-Object Name -like "ffmpeg-*" | Select-Object -First 1
    Move-Item $extracted.FullName $FFRoot
    Remove-Item $zip
} else { Write-Host "==> ffmpeg present, skipping" }

# --- 4. glslang (shader compiler, KhronosGroup/glslang main-tot) -------------
# Only needed when editing Shaders/*.vert|.frag — the compiled .spv bytecode is
# committed as Swift byte arrays (Sources/PalmierWin/Shaders.swift), so a fresh
# clone builds without this. Fetched on demand by build-shaders.ps1.
# Recent glslang releases renamed the tool glslangValidator -> glslang and the
# asset to glslang-main-windows-x86_64-release.zip; accept either binary name.
$Glslang = Join-Path $ThirdParty "glslang/bin/glslang.exe"
$GlslangLegacy = Join-Path $ThirdParty "glslang/bin/glslangValidator.exe"
if (-not (Test-Path $Glslang) -and -not (Test-Path $GlslangLegacy)) {
    Write-Host "==> fetching glslang (KhronosGroup/glslang main-tot)"
    $work = Join-Path $ThirdParty "glslang"
    $zip = Join-Path $ThirdParty "glslang-windows-Release.zip"
    Invoke-Native "curl.exe" @("-fsSL", "-o", $zip, "https://github.com/KhronosGroup/glslang/releases/download/main-tot/glslang-main-windows-x86_64-release.zip")
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    Expand-Archive -Path $zip -DestinationPath $work -Force
    Remove-Item $zip
} else { Write-Host "==> glslang present, skipping" }

# --- 5. Dear ImGui (ocornut/imgui, MIT) -------------------------------------
# CImGui wraps ImGui's C++ API in extern "C" for Swift. The wrapper is committed
# (CImGui/src/CImGui.cpp + include/CImGui.h); the upstream ImGui source is not.
$ImguiDir = Join-Path $Root "CImGui/deps/imgui"
if (-not (Test-Path (Join-Path $ImguiDir "imgui.h"))) {
    Write-Host "==> fetching Dear ImGui"
    $ImguiDeps = Join-Path $Root "CImGui/deps"
    if (-not (Test-Path $ImguiDeps)) { New-Item -ItemType Directory -Force -Path $ImguiDeps | Out-Null }
    Invoke-Native "git" @("clone", "--depth", "1", "https://github.com/ocornut/imgui.git", $ImguiDir)
} else { Write-Host "==> Dear ImGui present, skipping" }

Write-Host "==> deps ready under $ThirdParty"
Write-Host "    Vulkan headers: $(Join-Path $VkHeaders 'include/vulkan/vulkan_core.h')"
Write-Host "    vulkan-1.lib:    $VkLib"
Write-Host "    ffmpeg include:  $(Join-Path $FFRoot 'include/libavformat')"
