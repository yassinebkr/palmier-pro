# Timeline audio package — slice D Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Real per-track gain — a dB slider in every audio track header that writes a model field the audio engine actually mixes.

**Architecture:** Spec at `docs/timeline-audio-package-design.md` §3. `Track.gainDb` joins the shared PalmierCore model (Codable, default 0 dB = unity, upstream-safe). One intent ABI (`palmier_track_set_gain_db`, clamped −96…+12 dB like the clip volume intent, `VolumeScale` for linear conversion). The engine folds track gain into the same per-chunk multiplication as `clip.volume` × keyframes × fades. The audio header (72px rows since slice C) gets a second row: label/toggle above, slim gain slider below.

**Tech Stack:** Swift 6 (model + host + engine), C#/.NET 9 (shell UI), xunit interop tests.

**Build/test commands (Windows, Git Bash):**
- Swift: `cmd //c 'cd /d C:\Users\yassi\Documents\code\palmier-pro\PalmierWin && .\build.bat'`
- Shell tests: `export PATH="/c/Program Files/dotnet:$PATH" && cd PalmierWin/Shell && dotnet test PalmierShell.sln 2>&1 | tail -3`

**Current-state anchors (verified):**
- PalmierCore `Track` fields (`Sources/PalmierCore/Timeline.swift:104-120`): id, type, muted, hidden, syncLocked, clips, name, displayHeight — plus `displayHeightRange` (slice C). Custom decode with per-field `try? decode ?? default` fallbacks (same file :160-180).
- **CRITICAL — two PalmierCore trees:** `Sources/PalmierCore/` (tracked, used by CI symlink) AND `PalmierWin/Sources/PalmierCore/` (gitignored local copy the local build uses; currently differs — 50 vs 44 height default). Model edits must land in BOTH or the local build breaks while CI passes (learned in slice C).
- `VolumeScale.linearFromDb` (`Sources/PalmierCore/VolumeScale.swift:4`); `palmier_clip_set_volume_db` clamps −96…+12 dB with a finite guard (`TimelineHost.swift:1481-1490`) — mirror that shape for the track intent.
- Engine mix: `WinAudioEngine.swift` per-chunk gain = `clip.volume` × volume-keyframe envelope × fade envelope at chunk midpoint (:411-429); mix list built in `AudioContext.syncIfNeeded` (`AudioHost.swift:24-44`) from `track.type == .audio && !track.muted` clips.
- Shell header: `RenderTrack` (TimelineView.cs:243-276) draws label + link icon + eye/speaker glyph; audio rows are 72px by default (slice C).
- Undo wiring: `MainViewModel` `Undo.Execute("…", intent)` + `Timeline.Reload()` (siblings at MainViewModel.cs:71-164).

---

### Task D0: Branch

- [ ] `git checkout main && git pull --ff-only && git checkout -b win-audio-pkg-d` (after slice C merges).

---

### Task D1: `Track.gainDb` + `palmier_track_set_gain_db`

**Files:**
- Modify: `Sources/PalmierCore/Timeline.swift` (field + decode fallback) AND `PalmierWin/Sources/PalmierCore/Timeline.swift` (SAME edit — see anchors)
- Modify: `PalmierWin/Sources/PalmierCoreHost/TimelineHost.swift` (intent, near `palmier_track_set_display_height`)
- Modify: `PalmierWin/Shell/PalmierShell/Core/CoreApi.cs` (P/Invoke)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Model field (BOTH PalmierCore trees).** In `Track`:

```swift
    /// Per-track gain in dB; 0 = unity. Older files decode to unity.
    public var gainDb: Double = 0
```

and in its custom decode: `gainDb: (try? c.decode(Double.self, forKey: .gainDb)) ?? 0,` next to the other fallbacks. Apply the identical edit to both `Sources/PalmierCore/Timeline.swift` and `PalmierWin/Sources/PalmierCore/Timeline.swift`.

- [ ] **Step 2: Failing interop test.**

```csharp
    [Fact]
    public void SetTrackGainDb_RoundTripsClampsAndRefuses() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string audioId = TimelineState.Parse(CoreApi.GetTimelineJson(project))
                .Tracks.Single(t => t.Type == "audio").Id;
            Assert.Equal(1, CoreApi.palmier_track_set_gain_db(project, audioId, -6));
            Assert.Equal(-6, TimelineState.Parse(CoreApi.GetTimelineJson(project))
                .Tracks.Single(t => t.Id == audioId).GainDb);
            Assert.Equal(1, CoreApi.palmier_track_set_gain_db(project, audioId, 40));
            Assert.Equal(12, TimelineState.Parse(CoreApi.GetTimelineJson(project))
                .Tracks.Single(t => t.Id == audioId).GainDb);
            Assert.Equal(1, CoreApi.palmier_track_set_gain_db(project, audioId, -120));
            Assert.Equal(-96, TimelineState.Parse(CoreApi.GetTimelineJson(project))
                .Tracks.Single(t => t.Id == audioId).GainDb);
            Assert.Equal(0, CoreApi.palmier_track_set_gain_db(project, "no-such-track", -6));
            Assert.Equal(0, CoreApi.palmier_track_set_gain_db(project, audioId, double.NaN));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
```

(Requires `GainDb` parsed on `TrackState` — add the init property, mirroring `DisplayHeight`, in `TimelineState.cs`.)

- [ ] **Step 3: Host intent** (mirror `palmier_track_set_display_height`; clamp −96…+12, finite guard):

```swift
/// Sets a track's gain in dB, clamped to [-96, 12]. Returns 1, or 0 for an
/// unknown track or a non-finite gain.
@_cdecl("palmier_track_set_gain_db")
public func palmierTrackSetGainDb(_ handle: UnsafeMutableRawPointer?, _ trackId: UnsafePointer<CChar>?,
                                  _ gainDb: Double) -> Int32 {
    guard let ctx = projectContext(handle), let trackId else { return 0 }
    let id = String(cString: trackId)
    guard gainDb.isFinite else { return 0 }
    return ctx.withTimeline { timeline in
        guard let index = timeline.tracks.firstIndex(where: { $0.id == id }) else { return 0 }
        timeline.tracks[index].gainDb = min(12, max(-96, gainDb))
        return 1
    }
}
```

P/Invoke (next to the display-height one): `[LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] public static partial int palmier_track_set_gain_db(IntPtr project, string trackId, double gainDb);`

- [ ] **Step 4: Rebuild BOTH engines** (build.bat uses the local copy; CI uses the real one — both edits must exist): `cmd //c 'cd /d C:\Users\yassi\Documents\code\palmier-pro\PalmierWin && .\build.bat'` → "Build complete!". Test → PASS. Full suite → 500/500 (499 + 1).
- [ ] **Step 5: Commit** `[feat] Track gain model field + intent ABI`

---

### Task D2: Engine mixes track gain

**Files:**
- Modify: `PalmierWin/Sources/PalmierCoreHost/AudioHost.swift` (mix-list build, :24-44)
- Modify: `PalmierWin/Sources/PalmierWin/WinAudioEngine.swift` (per-chunk gain, :411-429 — if the track factor belongs there instead)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Read the sync/mix path and pick the single injection point.** The mix entries carry clip gain already; multiply by the track's linear gain (`VolumeScale.linearFromDb(track.gainDb)`) exactly once per entry at sync time (stored on the entry), and apply it in the same per-chunk multiplication as the other factors. Do NOT multiply at both sync and chunk time (double-count). Implementer's report must state the chosen point and why there's exactly one.

- [ ] **Step 2: Testable seam.** If the per-chunk gain computation is (or can cheaply become) a pure function of (clip volume, envelope, fades, track gain), cover it with a small interop test: set track gain −6 dB, sync audio, and assert the effective entry gain field is ≈ half (0.501 ±0.01) via whatever readable state exists (if the engine exposes nothing, add a narrow read accessor on the audio context for tests — prefer that over testing through sound). If no clean seam exists, document why and rely on D4's live check; do not add plumbing for its own sake.
- [ ] **Step 3: Build + suite green.**
- [ ] **Step 4: Commit** `[feat] Audio engine mixes per-track gain`

---

### Task D3: Header gain slider

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/Core/TimelineState.cs` (`TrackState.GainDb` — if not added in D1)
- Modify: `PalmierWin/Shell/PalmierShell/Views/TimelineView.cs` (header render + slider hit/drag)
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/TimelineViewModel.cs` (`RequestTrackGain` event)
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/MainViewModel.cs` (`Undo.Execute("Track Volume", ...)` + Reload)

- [ ] **Step 1: Header layout (audio rows only).** In `RenderTrack`, for `track.Type == "audio"` when the row is ≥ 56px tall, split the header: top row = label + link + speaker (current layout, centered in the top ~40px), bottom strip (~20px, inset x 10-90) = the gain slider: a thin track bar (RaisedColor) with a filled portion (accent) for the current dB mapped from [−96…+12] onto [0…1] with −96 treated as −∞ (show 0 fill), and a small dB readout text to its right only while interacting.
- [ ] **Step 2: The drag.** Pointer pressed inside the slider strip (audio tracks, Select tool) arms a gain drag: capture; move maps x → dB = −96 + fraction × 108 (clamped, snap to 0 within ±0.5 dB); the header renders the preview value live; release commits once when |previewDb − originalDb| > 0.1 via `RequestTrackGain`; DisarmGesture cancels. Double-tap the strip resets to 0 dB (commits immediately when current ≠ 0). Follow the existing gesture patterns (arm/capture/preview/commit-once/disarm); add `gainDragTrackId`, `gainDragDbPreview`, `gainDragActive` fields with the other gesture state.
- [ ] **Step 3: Intent wiring.** `RequestTrackGain(trackId, db)` event → MainViewModel: `Undo.Execute("Track Volume", () => CoreApi.palmier_track_set_gain_db(Project, trackId, db) == 1)` + `Timeline.Reload()`.
- [ ] **Step 4: Build + suite green; live screenshot:** A1 header shows the slider; drag left → readout shows negative dB live, release persists (Reload shows new value); double-tap → back to 0; undo restores; V1 header has NO slider. Screenshot each stage and read them.
- [ ] **Step 5: Commit** `[feat] Per-track gain slider in audio track headers`

---

### Task D4: Gates + live verification + PR (controller)

- [ ] Full suite green (≈ 502), swift build clean (local copy AND CI symlink source both edited — verify `git status` clean and `grep -n gainDb Sources/PalmierCore/Timeline.swift` present).
- [ ] Live: slider drag persists + undo; playing audio audibly reflects the gain (or state readback proves the engine holds it); screenshot matrix (slider at −6 dB, at 0 after double-tap).
- [ ] PR `[feat] Per-track gain: model field, engine mix, header slider (audio package D)`, CI watch, merge, delete branch.

---

## Self-review notes

- Spec coverage: model field (D1), engine mix (D2), header slider + undo (D3), live gates (D4) — all of design §3.
- `gainDb` clamp bounds mirror the clip volume intent (−96…+12); the linear conversion uses the existing `VolumeScale` — no second dB↔linear rule.
- Both PalmierCore trees edited (D1 step 1 + D4 gate) — the slice-C lesson is a plan step this time.
- The engine injection is exactly one multiplication (D2 step 1 explicitly forbids double-counting).
- Baseline ~499 → ~503 across D1-D3.
