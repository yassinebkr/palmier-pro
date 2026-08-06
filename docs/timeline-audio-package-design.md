# Timeline audio package — design

Date: 2026-08-06 · Status: approved by user · Slices: a → e below

## Why

The audio side of the timeline is the weakest part of the Windows app.
Investigation findings that shaped this design:

- Waveforms looked absent: the pipeline works, but extraction is lazy-on-paint
  and whole-file (up to ~60 s of silence-looking clips on long media), a pure
  tone renders as a featureless band ("big rectangle"), and the user's report
  predates any visible feedback.
- `palmier_probe_media` fails on files with cover-art (`attached_pic`) streams
  — common in YouTube downloads — silently rejecting the import. Audio-only
  files (mp3/wav) cannot import at all.
- Per-track heights exist in the shared model (`Track.displayHeight`, clamped
  [32, 200], serialized in timeline JSON and project files) but the shell
  ignores them; the timeline has no vertical scroll.
- No per-track gain anywhere (model, engine, UI); no metering anywhere.
  Upstream macOS has a master meter only; the user wants per-track header
  meters, so this exceeds upstream deliberately.

## 1. Waveform pipeline (fix, don't rebuild)

- **Probe fix:** `palmier_probe_media` skips streams with
  `AV_DISPOSITION_ATTACHED_PIC` when picking the video stream. Files with
  cover art import normally. When no video stream exists, the probe falls back
  to the audio stream and reports an audio-only media (duration + no video
  dimensions) — such files import as audio clips on the audio track.
- **Import-time extraction:** `MediaPanelViewModel.ImportFileAsync` starts
  background waveform extraction per imported media (semaphore-bounded, 2
  concurrent, mirroring upstream's `AsyncSemaphore(2)`). The lazy on-paint
  path stays as fallback for media already in the library.
- **Streaming extraction:** `palmier_waveform` folds decode chunks directly
  into min/max buckets instead of materializing all samples (bounded memory
  regardless of file length).
- **Disk cache:** `%LOCALAPPDATA%\PalmierPro\Waveforms\<hash>.wf`, key =
  SHA-256 of path+size+mtime (upstream's scheme), raw min/max floats. The
  in-memory cache sits on top; a hit skips decode entirely.
- Sine-tone media still renders a band (correct), real content shows peaks
  (verified live with speech/music).

## 2. Per-track heights + drag resize

- Parse `displayHeight` into `TrackState`; all timeline geometry (render,
  clip rects, hit-testing, drag ghosts) moves to per-track cumulative Y
  offsets computed once per state (like upstream's `TimelineGeometry`).
- Defaults at track creation: audio 72 px, video 50 px. User drags persist
  exact values in the project file.
- Resize affordance: the track's bottom edge in the header column (±6 px,
  SizeNorthSouth cursor), live preview during the drag, a single "Resize
  Track" undo entry on release through a new `palmier_track_set_display_height`
  intent ABI. Clamp [32, 200] in the model and the UI.
- **Vertical scroll:** `ScrollOffsetY` added; wheel stays horizontal-scroll,
  Shift+wheel scrolls vertically. Rows above/below the viewport are skipped
  in render and hit-test. CompactRows toggle keeps its session-only uniform
  override while active.

## 3. Per-track gain (real model field)

- `Track.gainDb: Double = 0` in the shared PalmierCore (Codable; default
  decodes old files to unity — upstream-safe).
- `palmier_track_set_gain_db` intent ABI (clamped −96…+12 dB, one "Track
  Volume" undo entry). Linear conversion reuses `VolumeScale`.
- `WinAudioEngine` folds track gain into the per-chunk gain
  (`clip.volume × volumeTrack envelope × fades × trackGainLinear`).
- Audio track headers get a small dB slider (drag to change, double-click
  resets to 0 dB) with a value readout while interacting.

## 4. Per-track meters

- Engine: per-track peak accumulation in the audio render callback (cheap:
  max of |sample| per track per callback window), exposed atomically.
- ABI: `palmier_audio_track_peaks` → [Float] peaks in track order,
  reset-on-read.
- Shell polls at ~30 fps while playing, applies ballistics shell-side
  (decay ~24 dB/s, 1.5 s peak hold, clip latch — upstream's numbers), draws a
  slim vertical bar in each audio header (dB-mapped, green → amber → red).

## Out of scope (YAGNI)

Normalize, EQ, solo, multi-band/spectral waveforms, per-track height sync to
upstream macOS UI (it has no such defaults), any change to how upstream
renders its own timeline.

## Testing

- Unit: displayHeight parse/clamp/persistence round-trip; gainDb↔linear;
  peak ballistics; probe skips attached_pic and accepts audio-only; streaming
  extraction equals the previous whole-file result on fixtures.
- Live (screenshot-verified): sine band and speech peaks render; a cover-art
  mp4 imports; an mp3 lands on the audio track; resize drag persists across
  project reopen; gain slider is audible; meters move during playback.
- Regression: `dotnet test PalmierShell.sln` green; `swift build` clean.

## Ship order

a) probe fix + audio-only import · b) waveform import-time + streaming +
disk cache · c) heights + resize + vertical scroll · d) gain · e) meters.
Each slice is its own PR.
