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

# A/V clip for audio-path tests: testsrc video + 440 Hz sine, 2s.
$outAV = Join-Path $outDir "testav.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=2" `
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $outAV
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $outAV"

# 24 fps, one solid colour per second. Frame indices in the app are in the
# timeline's 30 fps domain, so anything that reads a frame out of a file has to
# convert with the timeline's rate and not the file's — reading at the file's
# rate lands 25% late here, which is a different colour and an obvious failure.
$out24 = Join-Path $outDir "colorbands24.mp4"
$bands = @("red", "green", "blue", "yellow", "magenta") | ForEach-Object {
    "color=c=$($_):size=160x120:rate=24:duration=1"
}
$inputs = ($bands | ForEach-Object { "-f", "lavfi", "-i", $_ })
& $ffmpeg -y @inputs `
    -filter_complex "[0:v][1:v][2:v][3:v][4:v]concat=n=5:v=1:a=0[v]" -map "[v]" `
    -c:v libx264 -pix_fmt yuv420p -r 24 $out24
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $out24"

# Silence-detection fixture: 1s 440 Hz tone, 2s silence, 1s tone, so the
# detector should report one span covering roughly 1s–3s of source time.
$outSilence = Join-Path $outDir "silence.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=4:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=1" `
    -f lavfi -i "anullsrc=r=44100:cl=mono:d=2" `
    -f lavfi -i "sine=frequency=440:duration=1" `
    -filter_complex "[1:a][2:a][3:a]concat=n=3:v=0:a=1[a]" -map 0:v -map "[a]" `
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $outSilence
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $outSilence"
