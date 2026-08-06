# Timeline audio package — slice E Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Live level meters in audio track headers — per-track peak from the engine, drawn with upstream-style ballistics.

**Architecture:** Spec at `docs/timeline-audio-package-design.md` §4. The audio render callback already sums per-clip chunks; mix entries now also carry a peak-slot index (one per track). Each callback accumulates max |sample| per slot locally and publishes to a fixed-capacity atomic array once per callback (no locks in the hot loop). `palmier_audio_track_peaks` returns the published peaks (reset-on-read) alongside the track order the shell already knows. The shell polls at ~30 fps during playback, applies ballistics shell-side (upstream's `AudioMeterChannelState` numbers: 24 dB/s level decay, 18 dB/s peak decay, 1.5 s peak hold, clip latch, −60…0 dB window), and draws a slim vertical meter in each audio header (dB-mapped green → amber → red).

**Tech Stack:** Swift 6 (engine + host), C#/.NET 9 (shell), xunit interop (no-device no-op pattern like the existing audio test).

**Build/test commands (Windows, Git Bash):**
- Swift: `cmd //c 'cd /d C:\Users\yassi\Documents\code\palmier-pro\PalmierWin && .\build.bat'`
- Shell tests: `export PATH="/c/Program Files/dotnet:$PATH" && cd PalmierWin/Shell && dotnet test PalmierShell.sln 2>&1 | tail -3`

**Current-state anchors (verified):**
- Mix path: `AudioContext.syncIfNeeded` (AudioHost.swift:24-44) builds `ClipSource` entries per audio track (track gain baked in since D2); realtime `render` (WinAudioEngine.swift:379-448) sums chunks per clip with per-chunk gain; feeder thread (`feedOnce`/`topUp` :276-359) keeps rings full.
- `ClipSource` (WinAudioEngine.swift:33-55): id, volume, trackGain (D2), etc.
- Upstream meter reference: `Sources/PalmierPro/Audio/AudioMeter.swift:19-62` (`AudioMeterChannelState`: 24 dB/s level decay, 18 dB/s peak decay, 1.5 s hold, clip latch, −60…0 dB) and `AudioMeterView.swift` (ticker-driven redraw).
- Shell header (post-C/D): `RenderTrack` draws label/link/speaker (+ gain strip on audio rows ≥ 56); `HeaderWidth = 100`.
- Playback state: shell knows `session.Playing` (TransportViewModel/MainViewModel) for the poll timer's active gate.

---

### Task E0: Branch

- [ ] `git checkout main && git pull --ff-only && git checkout -b win-audio-pkg-e` (after slice D merges).

---

### Task E1: Engine peak accumulation + `palmier_audio_track_peaks`

**Files:**
- Modify: `PalmierWin/Sources/PalmierWin/WinAudioEngine.swift` (peak slots on ClipSource, per-callback accumulation, atomic publish)
- Modify: `PalmierWin/Sources/PalmierCoreHost/AudioHost.swift` (slot assignment at sync, peaks cdecl)
- Modify: `PalmierWin/Shell/PalmierShell/Core/CoreApi.cs` (P/Invoke)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Slots at sync.** Each mix entry gets `peakSlot: Int` = the audio track's ordinal among `track.type == .audio` tracks in the CURRENT timeline (0-based in track order — the shell's `state.Tracks` order is the same JSON, so indices line up 1:1). Fixed capacity 64 slots; tracks beyond that share slot 63 (document the ceiling in one line).

- [ ] **Step 2: Accumulate + publish.** In `render`, per clip chunk: local per-slot `maxAbs` over the post-gain samples already being summed (compute alongside, no extra pass). At the END of each callback, fold local maxima into the published array: per slot, `published[slot] = max(published[slot], localMax)` using simple atomic-word exchange (Swift `UnsafeAtomic` or a tiny spinlock taken once per callback — never per sample/clip). Reset-on-read clears the array.

- [ ] **Step 3: The ABI.**

```swift
/// Writes per-track peaks (max |sample| since the previous call) as f32 in
/// timeline track order (audio tracks only), up to `maxCount` entries, and
/// resets them. Returns the entry count, 0 when no audio device/entries.
@_cdecl("palmier_audio_track_peaks")
public func palmierAudioTrackPeaks(_ handle: UnsafeMutableRawPointer?,
                                   _ buf: UnsafeMutablePointer<Float>?, _ maxCount: Int32) -> Int32
```

No-audio-device path returns 0 (CI safe). Match the buffer contract of the other array-returning cdecls.

- [ ] **Step 4: Interop smoke test** (no-device no-op pattern like `AudioEngine_CreatePlaySeekDestroy`): create project, add testav.mp4 clip, audio create, play ~200 ms, read peaks — when a device exists, slot 0 > 0; then read again immediately — second read returns ≤ first (reset-on-read). When no device, assert the function returns 0 and skip.
- [ ] **Step 5: Build + suite green (503/503).**
- [ ] **Step 6: Commit** `[feat] Per-track peak metering in the audio engine + peaks ABI`

---

### Task E2: Shell meter state + polling + ballistics

**Files:**
- Create: `PalmierWin/Shell/PalmierShell/Core/TrackMeters.cs`
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/TimelineViewModel.cs` (poll timer + exposure)
- Test: `PalmierWin/Shell/PalmierShell.Tests/TrackMetersTests.cs`

- [ ] **Step 1: `TrackMeters` — one channel's ballistics, upstream's numbers:**

```csharp
namespace PalmierShell.Core;

/// One meter channel: instantaneous engine peaks in, display level out, with
/// upstream's ballistics — 24 dB/s level decay, 18 dB/s peak decay, 1.5 s
/// peak hold, clip latch, −60…0 dB window.
public sealed class TrackMeters {
    public double LevelDb { get; private set; } = -60;
    public double PeakDb { get; private set; } = -60;
    public bool Clipped { get; private set; }

    double holdSeconds;

    public void Reset() { LevelDb = -60; PeakDb = -60; Clipped = false; holdSeconds = 0; }

    static double ToDb(float peak) => peak <= 0 ? -60 : Math.Max(-60, 20 * Math.Log10(peak));

    public void Tick(float enginePeak, double dtSeconds) {
        double sample = ToDb(enginePeak);
        // Level: rise instantly, decay 24 dB/s.
        LevelDb = Math.Max(sample, LevelDb - 24 * dtSeconds);
        // Peak: hold 1.5 s, then decay 18 dB/s; clip latches at ≥ 0 dB.
        if (sample >= PeakDb) { PeakDb = sample; holdSeconds = 1.5; }
        else if (holdSeconds > 0) holdSeconds -= dtSeconds;
        else PeakDb = Math.Max(-60, PeakDb - 18 * dtSeconds);
        if (sample >= 0) Clipped = true;
        LevelDb = Math.Clamp(LevelDb, -60, 0);
        PeakDb = Math.Clamp(PeakDb, -60, 0);
    }
}
```

- [ ] **Step 2: Tests** (`TrackMetersTests.cs`): instant rise vs 24 dB/s decay slope; peak hold exactly 1.5 s then 18 dB/s fall; clip latch sets at ≥ 0 and survives decay; Reset clears; silence decays to −60. Pure functions, no seams.
- [ ] **Step 3: Polling in TimelineViewModel:** a `DispatcherTimer` (~33 ms) that runs only while `session.Playing` (wire the existing playing state; stop the timer when paused — meters freeze, then `Reset` on stop? No: upstream freezes and decays continue via the ticker; simplest correct: poll only while playing; on pause, keep last level drawn). Each tick: read peaks (`CoreApi` wrapper → float[]), map by index to audio tracks in `State.Tracks` order, `Tick` each channel, fire a narrow change notification the header listens to (avoid StateReloaded — a 30 fps whole-view invalidation is wrong; the header meter is a tiny redraw region, expose `event Action? MetersChanged` the view uses to InvalidateVisual — TimelineView is owner-drawn so one InvalidateVisual per tick is the existing playhead pattern's cost; acceptable if the playhead already ticks similarly — check).
- [ ] **Step 4: Build + suite green (≈ 508).**
- [ ] **Step 5: Commit** `[feat] Meter ballistics + playback polling`

---

### Task E3: Header meter rendering

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/Views/TimelineView.cs` (header render + MetersChanged subscription)

- [ ] **Step 1: The meter.** In `RenderTrack` for audio tracks, a slim vertical bar at the header's right edge (x ≈ 84-92, inset 4px from row top/bottom, or right of the speaker glyph when no gain strip is present — compose with D3's split: meter at x 84-92 always, gain strip stays x 10-80). dB-mapped fill from the bottom: level fill (green < −12 dB, amber −12…−3, red ≥ −3 or clipped), a peak-hold tick line, and a red cap when `Clipped`. Draw even when paused (frozen last level), `Reset` (empty) before first playback.
- [ ] **Step 2: Redraw wiring.** Subscribe to the view model's meter notification in `AttachViewModel` (mirror the `StateReloaded` subscription pattern with proper detach).
- [ ] **Step 3: Live screenshot:** playing testav/realaudio → A1 header meter moves (fill + peak tick); pause → frozen; A2 (no clips) → empty. Screenshot playing + paused and read them.
- [ ] **Step 4: Commit** `[feat] Per-track level meters in audio headers`

---

### Task E4: Gates + live verification + PR (controller)

- [ ] Full suite green (≈ 508-510), swift build clean.
- [ ] Live: meters during playback (fill/peak/clip states), pause freeze, stop reset behavior, no UI stutter (30 fps invalidate cost observed).
- [ ] PR `[feat] Per-track level meters (audio package E — final slice)`, CI watch, merge, delete branch.

---

## Self-review notes

- Spec coverage: engine peaks + ABI (E1), ballistics + polling (E2), header render (E3), gates (E4) — all of design §4.
- The slot index maps by "audio tracks in timeline track order" on both sides — the single ordering contract; no id passing across the poll ABI (indices suffice and the shell re-reads state each Reload).
- No engine locks in the realtime callback (per-callback fold once); the poll ABI is value-only (float array out).
- Baseline 502 → ~509 across E1-E3.
