# Reproducibly fetches the Windows media-engine native deps into PalmierWin/ThirdParty:
#   - Vulkan-Headers (Khronos, MIT) — the flat-C headers CVulkan binds.
#   - vulkan-1.lib — import lib generated from the system loader (vulkan-1.dll).
#   - FFmpeg shared dev build (BtbN, GPL) — libavformat/libavcodec/libavutil headers + libs + DLLs.
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

# Helper: run a native command, treating its (stderr) output as text rather
# than letting PowerShell's NativeCommandError abort under ErrorActionPreference.
function Invoke-Native([scriptblock]$Block) {
    $out = & $Block 2>&1
    $out | ForEach-Object { Write-Host $_ }
}

# --- 1. Vulkan headers (flat-C, Khronos) -------------------------------------
$VkHeaders = Join-Path $ThirdParty "Vulkan-Headers"
if (-not (Test-Path (Join-Path $VkHeaders "include/vulkan/vulkan_core.h"))) {
    Write-Host "==> fetching Vulkan-Headers"
    $tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP "vk-hdrs-$(Get-Random)")
    Invoke-Native { git clone --depth 1 --quiet https://github.com/KhronosGroup/Vulkan-Headers.git $tmp.FullName }
    if (Test-Path $VkHeaders) { Remove-Item -Recurse -Force $VkHeaders }
    Move-Item $tmp.FullName $VkHeaders
} else { Write-Host "==> Vulkan-Headers present, skipping" }

# --- 2. vulkan-1.lib (generated from the system loader) ----------------------
$VkLib = Join-Path $ThirdParty "vulkan-1.lib"
if (-not (Test-Path $VkLib)) {
    Write-Host "==> generating vulkan-1.lib from C:\Windows\System32\vulkan-1.dll"
    $work = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP "vk-lib-$(Get-Random)")
    Invoke-Native { dumpbin /exports "C:\Windows\System32\vulkan-1.dll" } | Out-File (Join-Path $work "raw.txt")
    $names = Select-String -Path (Join-Path $work "raw.txt") -Pattern '^\s+\d+\s+\w+\s+\w+\s+(vk\w+)' |
        ForEach-Object { $_.Matches.Groups[1].Value }
    "LIBRARY VULKAN-1.DLL", "EXPORTS", ($names -join "`n") | Set-Content (Join-Path $work "vulkan-1.def")
    Invoke-Native { lib /DEF:"$(Join-Path $work 'vulkan-1.def')" /OUT:$VkLib /MACHINE:x64 }
} else { Write-Host "==> vulkan-1.lib present, skipping" }

# --- 3. FFmpeg shared dev build (BtbN, GPL) ----------------------------------
$FFRoot = Join-Path $ThirdParty "ffmpeg"
if (-not (Test-Path (Join-Path $FFRoot "include/libavformat"))) {
    Write-Host "==> fetching BtbN FFmpeg shared build"
    $zip = Join-Path $env:TEMP "ffmpeg-shared.zip"
    Invoke-Native { curl.exe -sL -o $zip "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip" }
    $tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP "ffmpeg-ex-$(Get-Random)")
    Expand-Archive -Path $zip -DestinationPath $tmp.FullName -Force
    $extracted = Get-ChildItem -Directory $tmp.FullName | Where-Object Name -like "ffmpeg-*" | Select-Object -First 1
    if (Test-Path $FFRoot) { Remove-Item -Recurse -Force $FFRoot }
    Move-Item $extracted.FullName $FFRoot
} else { Write-Host "==> ffmpeg present, skipping" }

Write-Host "==> deps ready under $ThirdParty"
Write-Host "    Vulkan headers: $(Join-Path $VkHeaders 'include/vulkan/vulkan_core.h')"
Write-Host "    vulkan-1.lib:    $VkLib"
Write-Host "    ffmpeg include:  $(Join-Path $FFRoot 'include/libavformat')"
