# Generates a small H.264 test clip (testsrc pattern, 320x240, 2s, 30fps) for
# the FFmpegDecoder + VulkanTexture spike. The clip is gitignored — run this
# once before invoking palmierwin-spike.exe locally. Requires ffmpeg.exe on
# PATH (the BtbN bundle ships one at ThirdParty/ffmpeg/bin/ffmpeg.exe).
[CmdletBinding()]
param(
    [string]$Root = (Get-Location)
)
$ErrorActionPreference = "Stop"
$outDir = Join-Path $Root "test_media"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$ffmpeg = Join-Path $Root "ThirdParty/ffmpeg/bin/ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) { $ffmpeg = "ffmpeg" }  # fall back to PATH
$out = Join-Path $outDir "testsrc.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" -c:v libx264 -pix_fmt yuv420p $out
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $out"
