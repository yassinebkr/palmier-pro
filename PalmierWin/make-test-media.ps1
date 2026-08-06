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

# Cover-art fixture: an h264+aac clip that also carries a PNG attached_pic
# stream — the shape that broke probing of YouTube-style downloads.
$png = Join-Path $outDir "cover.png"
& $ffmpeg -y -f lavfi -i "color=c=red:size=64x64" -frames:v 1 $png
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
$outCover = Join-Path $outDir "coverart.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=2" -i $png `
    -map 0:v -map 1:a -map 2:v -c:v:0 libx264 -pix_fmt yuv420p -c:a aac -c:v:1 png `
    -disposition:v:1 attached_pic $outCover
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Remove-Item $png
Write-Host "Generated $outCover"

# Audio-only fixture: no video stream at all.
$outAudio = Join-Path $outDir "audioonly.m4a"
& $ffmpeg -y -f lavfi -i "sine=frequency=330:duration=3" -c:a aac $outAudio
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $outAudio"

# Known-level fixture for waveform level checks. The engine remixes mono to
# stereo through swr (-3 dB), so target 1/sqrt(2) in-file for a 0.5 peak
# through palmier_waveform (sine defaults to 1/8 amplitude, hence 4*sqrt(2)).
$outLoud = Join-Path $outDir "loudsine.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=2" -af "volume=5.656854249" `
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $outLoud
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $outLoud"

# Attached-pic-only audio: an m4a whose ONLY video stream is cover art — the
# YouTube-rip shape. The probe must report audio-only, not the cover's pixels.
$png2 = Join-Path $outDir "cover2.png"
& $ffmpeg -y -f lavfi -i "color=c=blue:size=64x64" -frames:v 1 $png2
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
$outCoverOnly = Join-Path $outDir "coveronly.m4a"
& $ffmpeg -y -f lavfi -i "sine=frequency=220:duration=3" -i $png2 `
    -map 0:a -map 1:v -c:a aac -c:v png -disposition:v:0 attached_pic $outCoverOnly
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Remove-Item $png2
Write-Host "Generated $outCoverOnly"

# Hi-res cover: a small real video plus a LARGE attached pic. FFmpeg's area
# heuristic would decode the cover; the disposition-aware pick must not.
$png3 = Join-Path $outDir "cover3.png"
& $ffmpeg -y -f lavfi -i "color=c=green:size=1024x1024" -frames:v 1 $png3
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
$outHiRes = Join-Path $outDir "hirescover.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=2" -i $png3 `
    -map 0:v -map 1:a -map 2:v -c:v:0 libx264 -pix_fmt yuv420p -c:a aac -c:v:1 png `
    -disposition:v:1 attached_pic -shortest $outHiRes
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Remove-Item $png3
Write-Host "Generated $outHiRes"

# Click-track fixture: 2 s of silence with one 50 ms tone burst at t=1.0 —
# waveform resolution must place the peak in the middle columns, not smear it.
$outClick = Join-Path $outDir "click.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=0.05" -f lavfi -i "anullsrc=r=48000:cl=mono:d=2" `
    -filter_complex "[1:a]adelay=1000|1000[tone];[2:a][tone]amix=inputs=2:normalize=0[a]" `
    -map 0:v -map "[a]" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $outClick
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $outClick"
