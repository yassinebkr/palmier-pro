# Timeline audio package — slices A+B Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make imports robust (cover-art + audio-only files) and make waveforms appear fast and cheap (import-time extraction, streaming decode, disk cache).

**Architecture:** Spec at `docs/timeline-audio-package-design.md`. Slice A: `palmier_probe_media` skips `attached_pic` streams and falls back to audio-only probing; `addClip` routes audio-only media to the audio track. Slice B: `palmier_waveform` streams decode chunks into bounded granules; a new static `WaveformCache` (C#) adds a semaphore-bounded, disk-cached extraction used by both the timeline paint path and media import. Slices C/D/E (heights, gain, meters) get their own plans after this lands.

**Tech Stack:** Swift 6 (PalmierCoreHost @_cdecl ABI), C#/.NET 9 (shell), xunit interop tests (`PalmierShell.Tests`, fixtures via `TestMediaPath`), ffmpeg fixtures via `PalmierWin/make-test-media.ps1`.

**Build/test commands (Windows, Git Bash):**
- Swift: `cmd //c 'cd /d C:\Users\yassi\Documents\code\palmier-pro\PalmierWin && .\build.bat'`
- Shell tests: `export PATH="/c/Program Files/dotnet:$PATH" && cd PalmierWin/Shell && dotnet test PalmierShell.sln 2>&1 | tail -3`
- Focused: `dotnet test PalmierShell.sln --filter "FullyQualifiedName~<Name>" 2>&1 | tail -3`
- Close any running PalmierShell.exe before rebuilding (locks PalmierCoreHost.dll).

---

### Task 0: Branch + fixtures

**Files:**
- Modify: `PalmierWin/make-test-media.ps1` (append at end)

- [ ] **Step 1: Branch**

```bash
cd /c/Users/yassi/Documents/code/palmier-pro
git checkout -b win-audio-pkg-a
```

- [ ] **Step 2: Append three fixtures to make-test-media.ps1**

```powershell
# Cover-art fixture: an h264+aac clip that also carries a PNG attached_pic
# stream — the shape that broke probing of YouTube-style downloads.
$png = Join-Path $outDir "cover.png"
& $ffmpeg -y -f lavfi -i "color=c=red:size=64x64" -frames:v 1 $png
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
$outCover = Join-Path $outDir "coverart.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=2" -i $png `
    -map 0:v -map 1:a -map 2:v -c:v:0 libx264 -pix_fmt:yuv420p -c:a aac -c:v:1 png `
    -disposition:v:1 attached_pic -shortest $outCover
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Remove-Item $png
Write-Host "Generated $outCover"

# Audio-only fixture: no video stream at all.
$outAudio = Join-Path $outDir "audioonly.m4a"
& $ffmpeg -y -f lavfi -i "sine=frequency=330:duration=3" -c:a aac $outAudio
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $outAudio"

# Known-level fixture: 440 Hz at exactly 0.5 amplitude, for waveform level checks.
$outLoud = Join-Path $outDir "loudsine.mp4"
& $ffmpeg -y -f lavfi -i "testsrc=duration=2:size=320x240:rate=30" `
    -f lavfi -i "sine=frequency=440:duration=2" -af "volume=0.5" `
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $outLoud
if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }
Write-Host "Generated $outLoud"
```

- [ ] **Step 3: Generate + verify**

```powershell
cd PalmierWin; powershell -NoProfile -File make-test-media.ps1 -Root (Get-Location)
```
Expected: coverart.mp4, audioonly.m4a, loudsine.mp4 exist in `PalmierWin/test_media/`.
`ThirdParty/ffmpeg/bin/ffprobe.exe test_media\coverart.mp4` shows 3 streams: h264, aac, png (attached_pic).

- [ ] **Step 4: Commit**

```bash
git add PalmierWin/make-test-media.ps1
git commit -m "[test] Cover-art, audio-only, known-level media fixtures"
```

---

### Task A1: Probe skips attached_pic streams

**Files:**
- Modify: `PalmierWin/Sources/PalmierCoreHost/TimelineHost.swift:416-450` (`palmierProbeMedia`)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Failing interop test**

Add to `InteropTests.cs`:

```csharp
    [Fact]
    public void ProbeMedia_SkipsAttachedPicStreams() {
        var probe = CoreApi.ProbeMedia(TestMediaPath("coverart.mp4"));
        Assert.NotNull(probe);
        Assert.Equal(320, probe.Value.Width);
        Assert.Equal(240, probe.Value.Height);
        Assert.Equal(30, probe.Value.Fps, 1);
    }
```

- [ ] **Step 2: Run — expect FAIL** (probe returns null → `Assert.NotNull` fails)

`dotnet test PalmierShell.sln --filter "FullyQualifiedName~ProbeMedia_SkipsAttachedPicStreams" 2>&1 | tail -3`

- [ ] **Step 3: Implement stream selection in `palmierProbeMedia`**

Replace the `av_find_best_stream` + guard block (TimelineHost.swift:426-431) with a manual pick that rejects attached pictures:

```swift
    var stream: UnsafeMutablePointer<AVStream>? = nil
    if let streamsBase = fmt.pointee.streams {
        for i in 0..<Int(fmt.pointee.nb_streams) {
            guard let s = streamsBase[i], let p = s.pointee.codecpar,
                  p.pointee.codec_type == AVMEDIA_TYPE_VIDEO,
                  (s.pointee.disposition & AV_DISPOSITION_ATTACHED_PIC) == 0 else { continue }
            stream = s
            break
        }
    }
    guard let videoStream = stream, let par = videoStream.pointee.codecpar else { return 0 }
```

Then `let rate = videoStream.pointee.avg_frame_rate` (rename the existing use of `stream` to `videoStream`). Audio-only fallback lands in Task A2 — this task keeps `return 0` for no-video.

- [ ] **Step 4: Swift build + focused test — PASS**

`cmd //c 'cd /d C:\Users\yassi\Documents\code\palmier-pro\PalmierWin && .\build.bat'` → Build complete.
`dotnet test ... --filter "FullyQualifiedName~ProbeMedia"` → the new test passes, existing probe tests still pass.

- [ ] **Step 5: Commit**

`git commit -am "[fix] Media probe skips attached_pic streams (cover-art files import)"`

---

### Task A2: Probe falls back to audio-only

**Files:**
- Modify: `PalmierWin/Sources/PalmierCoreHost/TimelineHost.swift` (`palmierProbeMedia`)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public void ProbeMedia_AudioOnlyFileReportsZeroDimensionsAndDuration() {
        var probe = CoreApi.ProbeMedia(TestMediaPath("audioonly.m4a"));
        Assert.NotNull(probe);
        Assert.Equal(0, probe.Value.Width);
        Assert.Equal(0, probe.Value.Height);
        Assert.True(probe.Value.TotalFrames >= 80 && probe.Value.TotalFrames <= 100,
            $"3s at 30fps expected ~90 frames, got {probe.Value.TotalFrames}");
    }
```

- [ ] **Step 2: Run — expect FAIL** (probe returns null today)

- [ ] **Step 3: Implement the audio fallback**

After the video pick (which leaves `stream` nil when no usable video), replace `guard let videoStream … else { return 0 }` with:

```swift
    guard let videoStream = stream, let par = videoStream.pointee.codecpar else {
        // No usable video stream: an audio-only file still imports, with zero
        // dimensions and a 30 fps duration so the timeline can place it.
        guard av_find_best_stream(fmt, AVMEDIA_TYPE_AUDIO, -1, -1, nil, 0) >= 0 else { return 0 }
        let seconds = fmt.pointee.duration > 0 ? Double(fmt.pointee.duration) / 1_000_000 : 0
        guard seconds > 0 else { return 0 }
        return writeCString("0,0,3000,\(Int((seconds * 30).rounded()))", into: buf, size: bufSize)
    }
```

- [ ] **Step 4: Build + test — PASS** (both probe tests green)

- [ ] **Step 5: Commit**

`git commit -am "[feat] Audio-only media probes with zero dimensions + duration"`

---

### Task A3: addClip routes audio-only media to the audio track

**Files:**
- Modify: `PalmierWin/Sources/PalmierWin/FFmpegAudioDecoder.swift` (add `hasVideoStream`, next to `hasAudioStream` at :35-41)
- Modify: `PalmierWin/Sources/PalmierCoreHost/TimelineHost.swift:477-502` (`addClip`)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public void AddClip_AudioOnlyLandsOnAudioTrackWithoutVideoClip() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string? clipId = CoreApi.AddClip(project, TestMediaPath("audioonly.m4a"), 90);
            Assert.NotNull(clipId);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var video = state.Tracks.Single(t => t.Type == "video");
            var audio = state.Tracks.Single(t => t.Type == "audio");
            Assert.Empty(video.Clips);
            var clip = Assert.Single(audio.Clips);
            Assert.Equal("audio", clip.MediaType);
            Assert.Equal(90, clip.DurationFrames);
            Assert.Null(clip.LinkGroupId);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
```

- [ ] **Step 2: Run — expect FAIL** (today: clip goes to the video track)

- [ ] **Step 3: Add `hasVideoStream` (mirror of the existing `hasAudioStream`)**

In `FFmpegAudioDecoder.swift` next to `hasAudioStream`:

```swift
    /// Container probe: true when the file has a usable (non-attached-pic) video stream.
    public static func hasVideoStream(path: String) -> Bool {
        var fmtCtx: UnsafeMutablePointer<AVFormatContext>? = nil
        guard path.withCString({ avformat_open_input(&fmtCtx, $0, nil, nil) }) == 0, let fmt = fmtCtx else { return false }
        defer { var f: UnsafeMutablePointer<AVFormatContext>? = fmt; avformat_close_input(&f) }
        guard avformat_find_stream_info(fmt, nil) >= 0, let streamsBase = fmt.pointee.streams else { return false }
        for i in 0..<Int(fmt.pointee.nb_streams) {
            guard let s = streamsBase[i], let p = s.pointee.codecpar else { continue }
            if p.pointee.codec_type == AVMEDIA_TYPE_VIDEO,
               (s.pointee.disposition & AV_DISPOSITION_ATTACHED_PIC) == 0 { return true }
        }
        return false
    }
```

- [ ] **Step 4: Route in `addClip`**

At the top of the `ctx.withTimeline` closure in `addClip` (before the video-track guard):

```swift
        if !FFmpegAudioDecoder.hasVideoStream(path: path) {
            // Audio-only media: one clip on the audio track, no video twin, no link.
            guard let audioIndex = timeline.tracks.firstIndex(where: { $0.type == .audio }) else { return false }
            var audioClip = clip
            audioClip.mediaType = .audio
            audioClip.sourceClipType = .audio
            audioClip.startFrame = startFrame ?? timeline.tracks[audioIndex].endFrame
            insertOverwriting(audioClip, into: &timeline.tracks[audioIndex].clips)
            return true
        }
```

- [ ] **Step 5: Build + test — PASS**; run the whole InteropTests class to catch regressions in the video+linked-audio path.

- [ ] **Step 6: Commit**

`git commit -am "[feat] Audio-only files add as audio-track clips"`

---

### Task A4: Import accepts zero-dimension (audio-only) media

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/MediaPanelViewModel.cs:303-314`

- [ ] **Step 1: Skip the video thumbnail when there is no video**

In `ImportFileAsync`, wrap the thumbnail fetch:

```csharp
        if (probe.Value.Width > 0) {
            var thumb = await Task.Run(() => CoreApi.GetThumbnails(path, 1));
            if (thumb is not null)
                item.Thumbnail = ThumbnailBitmaps.FromTiles(thumb.Value.Tiles, 0);
        }
```

(`ProbeMedia` already passes 0-width through; the probe is non-null, so the item imports. No test at this layer — the import path is covered live in Task A5; viewmodel tests use no real files.)

- [ ] **Step 2: Build the shell** — 0 errors.

- [ ] **Step 3: Commit**

`git commit -am "[feat] Media library imports audio-only files without a video thumbnail"`

---

### Task A5: Live verification + PR

- [ ] **Step 1: Full gates**

`dotnet test PalmierShell.sln` → 469+3 = 472 green. `cmd //c 'cd /d C:\Users\yassi\Documents\code\palmier-pro\PalmierWin && .\build.bat'` → clean.

- [ ] **Step 2: Live import check**

Launch the shell (`PalmierWin/.build/nostderr-launch.ps1` pattern, no special args), import `test_media\coverart.mp4` and `test_media\audioonly.m4a` via `--add-to-timeline`:
Expected: cover-art file shows a filmstrip clip (V1) + waveform clip (A1); the m4a lands as one audio clip on A1, nothing on V1. Screenshot via `uix.ps1 shot` and read it.

- [ ] **Step 3: PR**

`git push -u origin win-audio-pkg-a` + `gh pr create` (title `[fix] Robust imports: cover-art and audio-only media`), watch `gh pr checks <n> --watch`, merge when green, delete branch.

---

### Task B0: Branch

- [ ] `git checkout -b win-audio-pkg-b` from main (after slice A merges).

---

### Task B1: Streaming waveform extraction

**Files:**
- Modify: `PalmierWin/Sources/PalmierCoreHost/WaveformHost.swift` (whole-file buffer → granule streaming)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Failing tests for the known-level fixture and bounds**

```csharp
    [Fact]
    public void Waveform_KnownLevelSinePeaksAtHalfScale() {
        var wave = CoreApi.GetWaveform(TestMediaPath("loudsine.mp4"), 400);
        Assert.NotNull(wave);
        float max = wave.Max(Math.Abs);
        Assert.True(max > 0.45f && max < 0.55f, $"expected ~0.5 peak, got {max}");
    }

    [Fact]
    public void Waveform_PairsAreOrderedAndBounded() {
        var wave = CoreApi.GetWaveform(TestMediaPath("testav.mp4"), 256);
        Assert.NotNull(wave);
        Assert.Equal(512, wave!.Length);
        for (int i = 0; i < wave.Length; i += 2) {
            Assert.True(wave[i] <= wave[i + 1], $"pair {i / 2} inverted");
            Assert.True(wave[i] >= -1f && wave[i + 1] <= 1f, $"pair {i / 2} out of range");
        }
    }
```

These pass on the OLD implementation too — they pin the contract the rewrite must preserve.

- [ ] **Step 2: Rewrite `palmierWaveform` streaming**

Replace the `samples` whole-file accumulation in WaveformHost.swift with bounded granules:

```swift
    let channels = FFmpegAudioDecoder.channels
    let chunkFrames = 4096
    var chunk = [Float](repeating: 0, count: chunkFrames * channels)
    var granules: [(lo: Float, hi: Float)] = []  // one per chunk: bounded memory
    while true {
        let got = chunk.withUnsafeMutableBufferPointer {
            decoder.read(into: $0.baseAddress!, sampleFrames: chunkFrames)
        }
        guard got > 0 else { break }
        var lo: Float = 1, hi: Float = -1
        for i in 0..<got {
            var sum: Float = 0
            for c in 0..<channels { sum += chunk[i * channels + c] }
            let mono = sum / Float(channels)
            lo = min(lo, mono); hi = max(hi, mono)
        }
        granules.append((min(lo, hi), max(lo, hi)))
        if got < chunkFrames { break }
    }
    guard !granules.isEmpty else { return 0 }

    let cols = Int(columns)
    for col in 0..<cols {
        let g0 = granules.count * col / cols
        let g1 = min(granules.count, max(g0 + 1, granules.count * (col + 1) / cols))
        var lo: Float = 1, hi: Float = -1
        for g in granules[g0..<g1] { lo = min(lo, g.lo); hi = max(hi, g.hi) }
        buf[col * 2] = lo
        buf[col * 2 + 1] = hi
    }
    return 1
```

Note: granule-wise min/max equals the old per-sample result exactly only when columns ≥ granules; for columns < granules the old code bucketed per-sample and the new buckets per-granule — min/max over the same sample span, so values are identical in practice (min/max commutes). The tests pin this.

- [ ] **Step 3: Build + tests — PASS** (both new tests green on the rewrite)

- [ ] **Step 4: Commit**

`git commit -am "[perf] Waveform extraction streams in bounded granules (no whole-file buffer)"`

---

### Task B2: `WaveformCache` — disk cache + bounded concurrency

**Files:**
- Create: `PalmierWin/Shell/PalmierShell/Core/WaveformCache.cs`
- Test: `PalmierWin/Shell/PalmierShell.Tests/WaveformCacheTests.cs`

- [ ] **Step 1: Implementation**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace PalmierShell.Core;

/// Disk-cached, semaphore-bounded waveform extraction. One extraction per
/// media ever: the raw min/max floats persist under Waveforms/, keyed by
/// path+size+mtime+columns, so a reload skips decoding entirely.
public static class WaveformCache {
    /// Test seam: redirects the cache directory. Never set in production code.
    public static string? DirectoryOverride;
    /// Test seam: replaces the native decode. Never set in production code.
    public static Func<string, int, float[]?>? DecodeOverride;

    static string Dir => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalmierPro", "Waveforms");

    static readonly SemaphoreSlim Gate = new(2);

    public static async Task<float[]?> GetAsync(string path, int columns, CancellationToken ct = default) {
        string file = Path.Combine(Dir, Key(path, columns) + ".wf");
        if (TryRead(file, columns, out var cached)) return cached;
        await Gate.WaitAsync(ct);
        try {
            if (TryRead(file, columns, out cached)) return cached;  // filled while we waited
            var floats = await Task.Run(
                () => (DecodeOverride ?? CoreApi.GetWaveform)(path, columns), ct);
            if (floats is null) return null;
            WriteAtomic(file, floats);
            return floats;
        } finally {
            Gate.Release();
        }
    }

    static string Key(string path, int columns) {
        var info = new FileInfo(path);
        string seed = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{columns}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..32];
    }

    static bool TryRead(string file, int columns, out float[] floats) {
        floats = [];
        try {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length != columns * 2 * sizeof(float)) return false;  // stale/corrupt: treat as miss
            floats = new float[columns * 2];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return true;
        } catch {
            return false;
        }
    }

    static void WriteAtomic(string file, float[] floats) {
        try {
            Directory.CreateDirectory(Dir);
            var bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            string tmp = file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, true);
        } catch {
            // A cache that cannot persist only costs a re-decode next run.
        }
    }
}
```

- [ ] **Step 2: Tests**

```csharp
using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

[Collection("waveform-cache")]
public sealed class WaveformCacheTests : IDisposable {
    readonly string dir = Path.Combine(Path.GetTempPath(), $"palmier-wfcache-{Guid.NewGuid():N}");
    readonly string media;

    public WaveformCacheTests() {
        WaveformCache.DirectoryOverride = dir;
        media = Path.Combine(dir, "clip.mp4");
        File.WriteAllText(media, "fake");
    }

    public void Dispose() {
        WaveformCache.DirectoryOverride = null;
        WaveformCache.DecodeOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task DecodeHappensOnceThenPersists() {
        int calls = 0;
        WaveformCache.DecodeOverride = (_, cols) => { calls++; return Enumerable.Repeat(0.5f, cols * 2).ToArray(); };
        var first = await WaveformCache.GetAsync(media, 64);
        var second = await WaveformCache.GetAsync(media, 64);
        Assert.Equal(1, calls);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CorruptCacheFileIsTreatedAsMiss() {
        Directory.CreateDirectory(dir);
        int calls = 0;
        WaveformCache.DecodeOverride = (_, cols) => { calls++; return new float[cols * 2]; };
        _ = await WaveformCache.GetAsync(media, 64);
        foreach (var f in Directory.GetFiles(dir, "*.wf")) File.WriteAllText(f, "garbage");
        _ = await WaveformCache.GetAsync(media, 64);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task KeyChangesWithMediaMtime() {
        int calls = 0;
        WaveformCache.DecodeOverride = (_, cols) => { calls++; return new float[cols * 2]; };
        _ = await WaveformCache.GetAsync(media, 64);
        File.SetLastWriteTimeUtc(media, DateTime.UtcNow.AddHours(1));
        _ = await WaveformCache.GetAsync(media, 64);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NullDecodeStaysNullAndIsNotCached() {
        int calls = 0;
        WaveformCache.DecodeOverride = (_, _) => { calls++; return null; };
        Assert.Null(await WaveformCache.GetAsync(media, 64));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.wf"));
    }
}
```

- [ ] **Step 3: Run — PASS**

`dotnet test PalmierShell.sln --filter "FullyQualifiedName~WaveformCacheTests" 2>&1 | tail -3`

- [ ] **Step 4: Commit**

`git commit -am "[feat] Disk-cached waveform extraction with bounded concurrency"`

---

### Task B3: Timeline paint path uses `WaveformCache`

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/TimelineViewModel.cs:358-374` (`WaveformFor`)

- [ ] **Step 1: Rewire the lazy load to the cache**

```csharp
    public WaveformData? WaveformFor(string mediaPath) {
        if (waveforms.TryGetValue(mediaPath, out var wf)) return wf;
        waveforms[mediaPath] = null;
        _ = Task.Run(async () => {
            var probe = CoreApi.ProbeMedia(mediaPath);
            double seconds = probe is { Fps: > 0, TotalFrames: > 0 } p ? p.TotalFrames / p.Fps : 0;
            int columns = seconds > 0 ? Math.Clamp((int)Math.Ceiling(seconds * 200), 256, 240_000) : 2048;
            var wave = await WaveformCache.GetAsync(mediaPath, columns);
            if (wave is null) return;
            int sourceFrames = seconds > 0 ? Math.Max(1, (int)Math.Round(seconds * TimelineFps)) : 0;
            Dispatcher.UIThread.Post(() => {
                waveforms[mediaPath] = new WaveformData(wave, sourceFrames);
                StateReloaded?.Invoke();
            });
        });
        return null;
    }
```

- [ ] **Step 2: Full test suite** — 472+4 green (no behavior change expected).

- [ ] **Step 3: Commit**

`git commit -am "[refactor] Timeline waveform loads through the shared cache"`

---

### Task B4: Import kicks extraction

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/MediaPanelViewModel.cs` (`ImportFileAsync`)

- [ ] **Step 1: Kick after the item is added**

At the end of `ImportFileAsync` (after the thumbnail block):

```csharp
        // Warm the waveform cache in the background (bounded inside the
        // cache) so the first timeline paint of this media already has peaks.
        double seconds = probe.Value.Fps > 0 && probe.Value.TotalFrames > 0
            ? probe.Value.TotalFrames / probe.Value.Fps : 0;
        if (seconds > 0) {
            int columns = Math.Clamp((int)Math.Ceiling(seconds * 200), 256, 240_000);
            _ = WaveformCache.GetAsync(path, columns);
        }
```

- [ ] **Step 2: Build + suite green.**

- [ ] **Step 3: Commit**

`git commit -am "[feat] Waveform extraction warms at import time"`

---

### Task B5: Live verification + PR

- [ ] **Step 1: Gates** — `dotnet test PalmierShell.sln` all green; swift build clean.

- [ ] **Step 2: Live: waveform appears immediately on add**

Fresh launch with `--add-to-timeline realaudio.mp4 --no-update`: the A1 clip should show peaks within ~2 s (import-warmed), and `%LOCALAPPDATA%\PalmierPro\Waveforms\` gains a `.wf` file. Relaunch and re-add: instant. Screenshot both and read them.

- [ ] **Step 3: PR**

`gh pr create` (title `[perf] Waveforms at import time: streaming decode + disk cache`), CI watch, merge, delete branch.

---

## Self-review notes

- Spec coverage: probe fix (A1), audio-only import (A2/A3/A4), import-time extraction (B4), streaming (B1), disk cache (B2), paint-path rewire (B3). Sine-band note: no code — correct behavior, no task needed.
- `WaveformData` shape unchanged; `GetWaveform`/`palmier_waveform` ABI unchanged; `hasVideoStream` new but mirrors `hasAudioStream`; `WaveformCache.GetAsync` is the only new C# API and is used identically in B3 and B4.
- The 469 baseline grows by +3 (slice A) and +6 (slice B: 2 waveform ABI + 4 cache) — recount at each gate.
