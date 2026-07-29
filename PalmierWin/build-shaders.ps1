# Compiles Shaders/*.vert|.frag to .spv via glslangValidator and regenerates
# Sources/PalmierWin/Shaders.swift with the bytecode as Swift byte arrays.
# Run this after editing any shader source. The compiled bytecode ships inside
# the binary (no runtime .spv loading), so a fresh clone builds without running
# this — glslangValidator is only a dev-time tool.
#
# Requires glslangValidator at ThirdParty/glslang/bin/glslangValidator.exe.
# If absent, run ./fetch-deps.ps1 first (it downloads the KhronosGroup/glslang
# main-tot Windows Release build).
[CmdletBinding()]
param(
    [string]$Root = (Get-Location)
)
$ErrorActionPreference = "Stop"
$Glslang = Join-Path $Root "ThirdParty/glslang/bin/glslangValidator.exe"
if (-not (Test-Path $Glslang)) { throw "$Glslang not found — run ./fetch-deps.ps1 first" }

$ShadersDir = Join-Path $Root "Shaders"
$OutDir = Join-Path $env:TEMP "palmier-spv-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# (stem) pairs to compile. The --vn <name> emits a C header with the bytecode
# as `const uint32_t <name>[]`, which we reformat to Swift below.
$pairs = @(
    @{ stem = "textured_quad_vert"; arr = "textured_quad_vertSPIRV"; size = "textured_quad_vertSize" },
    @{ stem = "textured_quad_frag"; arr = "textured_quad_fragSPIRV"; size = "textured_quad_fragSize" },
    @{ stem = "layer_quad_vert";    arr = "layer_quad_vertSPIRV";    size = "layer_quad_vertSize" },
    @{ stem = "layer_quad_frag";    arr = "layer_quad_fragSPIRV";    size = "layer_quad_fragSize" }
)

# 1) Compile each shader to .spv + a C header.
foreach ($p in $pairs) {
    $src = Join-Path $ShadersDir "$($p.stem).glsl"
    # The shader sources use .vert/.frag extensions (GLSL convention).
    $src = Join-Path $ShadersDir "$($p.stem -replace '_vert$','.vert' -replace '_frag$','.frag')"
    $spv = Join-Path $ShadersDir "$($p.stem -replace '_vert$','.vert' -replace '_frag$','.frag').spv"
    $hdr = Join-Path $OutDir "$($p.stem).h"
    & $Glslang -V --vn $p.arr -o $spv $src
    if ($LASTEXITCODE -ne 0) { throw "glslangValidator failed for $($p.stem)" }
    # Re-run with -o pointing at a .h to emit the C header (the .spv above is
    # the binary; we keep both — .spv for inspection, Shaders.swift for linking).
    & $Glslang -V --vn $p.arr -o $hdr $src
    if ($LASTEXITCODE -ne 0) { throw "glslangValidator (header) failed for $($p.stem)" }
    Write-Host "compiled $($p.stem)"
}

# 2) Regenerate Shaders.swift from the C headers.
$lines = @(
    "// Auto-generated from Shaders/*.spv. SPIR-V bytecode for the",
    "// textured-quad pipeline (full-screen clear / single-texture test) and the",
    "// layer-quad pipeline (per-layer composite with push-constant placement +",
    "// opacity). Committed as Swift byte arrays so no runtime resource loading",
    "// is needed and the shaders ship inside the binary.",
    "",
    "import CVulkan",
    "",
    "public enum Shaders {"
)
foreach ($p in $pairs) {
    $hdr = Join-Path $OutDir "$($p.stem).h"
    $raw = Get-Content -Raw $hdr
    $nums = [regex]::Matches($raw, '0x[0-9a-fA-F]+') | ForEach-Object { $_.Value }
    $lines += "    public static let $($p.arr): [UInt32] = ["
    for ($i = 0; $i -lt $nums.Count; $i += 12) {
        $chunk = $nums[$i..([Math]::Min($i + 11, $nums.Count - 1))]
        $sep = if ($i + 12 -lt $nums.Count) { "," } else { "" }
        $lines += "        " + ($chunk -join ", ") + $sep
    }
    $lines += "    ]"
    $lines += "    public static var $($p.size): Int { $($nums.Count * 4) }"
    $lines += ""
}
# Trim the trailing blank line before the closing brace.
if ($lines[-1] -eq "") { $lines = $lines[0..($lines.Count - 2)] }
$lines += "}"
$target = Join-Path $Root "Sources/PalmierWin/Shaders.swift"
$lines | Set-Content -Path $target
Remove-Item -Recurse -Force $OutDir
Write-Host "regenerated $target"
