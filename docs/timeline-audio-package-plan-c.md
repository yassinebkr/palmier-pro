# Timeline audio package — slice C Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Per-track heights end-to-end — audio tracks taller by default, any track resizable by dragging its header's bottom edge, and vertical scrolling when tracks overflow.

**Architecture:** Spec at `docs/timeline-audio-package-design.md` §2. The shared model already owns `Track.displayHeight` (clamped [32, 200], serialized in timeline JSON and project files); the shell just parses and uses it. A small `TrackLayout` helper computes cumulative per-track offsets once per state so render and every hit-test share one geometry (the pattern upstream calls `TimelineGeometry`). Resize is a pointer gesture in the header committing one intent through a new `palmier_track_set_display_height` ABI. Slice A+B are merged; slices D/E get their own plans after.

**Tech Stack:** Swift 6 (host ABI), C#/.NET 9 (shell), xunit (InteropTests + new TrackLayoutTests), live screenshot verification via `PalmierWin/.build/*.ps1` harness conventions.

**Build/test commands (Windows, Git Bash):**
- Swift: `cmd //c 'cd /d C:\Users\yassi\Documents\code\palmier-pro\PalmierWin && .\build.bat'`
- Shell tests: `export PATH="/c/Program Files/dotnet:$PATH" && cd PalmierWin/Shell && dotnet test PalmierShell.sln 2>&1 | tail -3`
- Close any running PalmierShell.exe before rebuilding (locks PalmierCoreHost.dll).

**Current-state anchors (verified):**
- `TimelineView.cs:17` `double TrackHeight => vm?.CompactRows == true ? 28 : 50;` — single uniform height used by render (`:154-158` row stacking, `:244,250` row bg/header, `:304` clip rect) and by row-division hit tests (`TrackAt :862-866`, `ClipRect :874-879`, `JunctionAt :902`, `HitTestClip :972-980`, `GapAt`, `EmptyTrackAt`).
- Header drawn in `RenderTrack :243-276` (label, link icon, eye/speaker toggle at x 66-80). `HeaderWidth = 100`.
- No `ScrollOffsetY` anywhere; wheel = horizontal scroll (`:1189-1204`), Ctrl+wheel zoom.
- JSON already carries `"displayHeight":50` per track (host sets 50 at track creation — find it via `grep -n "displayHeight" PalmierWin/Sources/PalmierCoreHost/*.swift` and `Sources/PalmierCore/Timeline.swift:117` for the core default 44 + decode clamp [32, 200]).
- Undo/intent pattern for track mutations: see `palmier_track_set_muted` (TimelineHost.swift:997-1005 + TimelineViewModel.cs:344-348 + MainViewModel Undo.Execute wiring).
- Escape disarm pattern: `DisarmGesture`/`CancelTimelineGesture` (TimelineView.cs:1151-1187), invoked from MainWindow.axaml.cs:537-541.

---

### Task C0: Branch

- [ ] `git checkout main && git pull --ff-only && git checkout -b win-audio-pkg-c` (after slice B merges).

---

### Task C1: `TrackState.DisplayHeight` + `TrackLayout` geometry

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/Core/TimelineState.cs:78` (`TrackState` record)
- Create: `PalmierWin/Shell/PalmierShell/Core/TrackLayout.cs`
- Test: `PalmierWin/Shell/PalmierShell.Tests/TrackLayoutTests.cs` (new)

- [ ] **Step 1: Parse displayHeight.** Add to the `TrackState` record:

```csharp
    /// Per-track height from the model; 0 when an older file lacks the key.
    public double DisplayHeight { get; init; }

    /// Height this track renders at: the persisted height, or the per-type
    /// default when unset; clamped to the model's [32, 200].
    public double RenderHeight =>
        Math.Clamp(DisplayHeight > 0 ? DisplayHeight : Type == "audio" ? 72 : 50, 32, 200);
```

(System.Text.Json maps `displayHeight` case-insensitively via the existing options; a missing key decodes to 0 and falls back to the per-type default.)

- [ ] **Step 2: `TrackLayout` — one geometry for render and hit tests.**

The constructor takes the height rule as a function so CompactRows (uniform 28) and per-track heights share one path: the view passes `t => vm.CompactRows ? 28 : t.RenderHeight`.

```csharp
public sealed class TrackLayout {
    public TrackLayout(IReadOnlyList<TrackState> tracks, double top, Func<TrackState, double> heightOf);
    public double Bottom { get; }                                    // total content height
    public double YOf(string trackId);                               // row top
    public double HeightOf(string trackId);                          // row height
    public TrackState? TrackAt(double y);                            // row containing y
    public IReadOnlyList<(TrackState Track, double Y, double Height)> Rows { get; }
}
```

- [ ] **Step 3: Tests** (`TrackLayoutTests.cs`):

```csharp
using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public sealed class TrackLayoutTests {
    static TrackState Track(string type, double height, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N")[..8], type, false, false, []) { DisplayHeight = height };

    [Fact]
    public void RowsStackCumulativelyInModelOrder() {
        var v = Track("video", 50); var a1 = Track("audio", 72); var a2 = Track("audio", 100);
        var layout = new TrackLayout([v, a1, a2], top: 24, t => t.RenderHeight);
        Assert.Equal(24, layout.YOf(v.Id));
        Assert.Equal(74, layout.YOf(a1.Id));
        Assert.Equal(146, layout.YOf(a2.Id));
        Assert.Equal(246, layout.Bottom);
    }

    [Fact]
    public void TrackAtReturnsTheRowContainingY() {
        var v = Track("video", 50); var a = Track("audio", 72);
        var layout = new TrackLayout([v, a], top: 24, t => t.RenderHeight);
        Assert.Equal(v.Id, layout.TrackAt(24)!.Id);
        Assert.Equal(v.Id, layout.TrackAt(73.9)!.Id);
        Assert.Equal(a.Id, layout.TrackAt(74)!.Id);
        Assert.Equal(a.Id, layout.TrackAt(145.9)!.Id);
        Assert.Null(layout.TrackAt(146));
        Assert.Null(layout.TrackAt(23.9));
    }

    [Fact]
    public void HeightOfOverrideAppliesUniformly() {
        var v = Track("video", 50); var a = Track("audio", 72);
        var layout = new TrackLayout([v, a], top: 0, _ => 28);
        Assert.Equal(28, layout.HeightOf(v.Id));
        Assert.Equal(28, layout.YOf(a.Id));
    }

    [Theory]
    [InlineData(0, "audio", 72)]     // unset → per-type default
    [InlineData(0, "video", 50)]
    [InlineData(10, "audio", 32)]    // clamped to the model's floor
    [InlineData(500, "video", 200)]  // and ceiling
    [InlineData(96, "video", 96)]    // exact value passes through
    public void RenderHeightDefaultsAndClamps(double height, string type, double expected) {
        Assert.Equal(expected, Track(type, height).RenderHeight);
    }

    [Fact]
    public void MissingDisplayHeightKeyDeserializesToDefault() {
        const string json = """
        {"id":"t","name":"T","fps":30,"width":1920,"height":1080,
         "tracks":[{"clips":[],"displayHeight":0,"hidden":false,"id":"v1","muted":false,"type":"video"},
                   {"clips":[],"displayHeight":96,"hidden":false,"id":"a1","muted":false,"type":"audio"}]}
        """;
        var state = TimelineState.Parse(json);
        Assert.Equal(50, state.Tracks[0].RenderHeight);
        Assert.Equal(96, state.Tracks[1].RenderHeight);
    }
}
```

- [ ] **Step 4: Run — PASS.** `dotnet test PalmierShell.sln --filter "FullyQualifiedName~TrackLayoutTests" 2>&1 | tail -3`
- [ ] **Step 5: Commit** `[feat] Per-track layout geometry with persisted heights`

---

### Task C2: TimelineView + ViewModel render/hit-test on `TrackLayout`

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/Views/TimelineView.cs` (render stacking, RenderTrack, clip rects, all row-division hit tests)
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/TimelineViewModel.cs` (expose a cached layout)

- [ ] **Step 1: Cached layout on the view model.** Recompute per state/geometry change (not per paint):

```csharp
    /// Geometry for the current state; rebuilt on state reload / compact toggle.
    public TrackLayout Layout { get; private set; } = new([], 0, _ => 50);

    // wherever the state is (re)loaded and in OnCompactRowsChanged:
    Layout = new TrackLayout(State.Tracks, TimelineView.RulerHeight,
        t => CompactRows ? 28 : t.RenderHeight);
```

(`RulerHeight` is `internal const` on TimelineView — expose it as a shared const, e.g. move to TimelineMath or make it public const on TimelineViewModel; pick the smaller-diff option.)

- [ ] **Step 2: TimelineView uses it.** Replace `y += TrackHeight` stacking with `foreach (var row in vm.Layout.Rows) RenderTrack(ctx, bounds, row.Track, label, row.Y)`; `RenderTrack`/`ClipRect` take the row's height; hit tests (`TrackAt`, `HitTestClip`, `JunctionAt`, `GapAt`, `EmptyTrackAt`, envelope/fade `ClipRect` callers) go through `vm.Layout.TrackAt(y)`/`YOf`/`HeightOf` instead of `(p.Y - RulerHeight) / TrackHeight`. Remove the `TrackHeight` property; CompactRows still works via the heightOf override.

- [ ] **Step 3: Build + full suite green** (existing timeline tests must pass unchanged; the uniform-50 default renders identically for video tracks).
- [ ] **Step 4: Live screenshot** — launch with longtest+testav (V1 50px, A1 72px): the audio row is visibly taller; clip hit-testing (click the A1 clip → selects) works. Screenshot and read it.
- [ ] **Step 5: Commit** `[feat] Timeline renders per-track heights (audio taller by default)`

---

### Task C3: Host defaults at track creation (audio 72)

**Files:**
- Modify: wherever the host creates tracks (find via `grep -n "displayHeight" PalmierWin/Sources/PalmierCoreHost/*.swift` and `grep -n "addTrack\|add_track" ...`)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Failing test.**

```csharp
    [Fact]
    public void NewProjectTracksGetPerTypeDefaultHeights() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(50, state.Tracks.Single(t => t.Type == "video").DisplayHeight);
            Assert.Equal(72, state.Tracks.Single(t => t.Type == "audio").DisplayHeight);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
```

- [ ] **Step 2: Run — FAIL if the host writes something else; implement so video=50, audio=72 at creation.** Also check `palmier_timeline_add_track` gets the same per-type default for user-added tracks.
- [ ] **Step 3: Build + test — PASS.**
- [ ] **Step 4: Commit** `[feat] Per-type default track heights (video 50, audio 72)`

---

### Task C4: `palmier_track_set_display_height` intent ABI

**Files:**
- Modify: `PalmierWin/Sources/PalmierCoreHost/TimelineHost.swift` (near `palmier_track_set_muted` :997-1005)
- Modify: `PalmierWin/Shell/PalmierShell/Core/CoreApi.cs` (P/Invoke)
- Test: `PalmierWin/Shell/PalmierShell.Tests/InteropTests.cs`

- [ ] **Step 1: Failing interop test.**

```csharp
    [Fact]
    public void SetTrackDisplayHeight_RoundTripsAndClamps() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string audioId = TimelineState.Parse(CoreApi.GetTimelineJson(project))
                .Tracks.Single(t => t.Type == "audio").Id;
            Assert.Equal(1, CoreApi.palmier_track_set_display_height(project, audioId, 120));
            Assert.Equal(120, TimelineState.Parse(CoreApi.GetTimelineJson(project))
                .Tracks.Single(t => t.Id == audioId).DisplayHeight);
            Assert.Equal(1, CoreApi.palmier_track_set_display_height(project, audioId, 10));
            Assert.Equal(32, TimelineState.Parse(CoreApi.GetTimelineJson(project))
                .Tracks.Single(t => t.Id == audioId).DisplayHeight);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
```

- [ ] **Step 2: Implement in the host** (mirror `palmier_track_set_muted`'s shape): `palmier_track_set_display_height(handle, trackId, height)` → find track, `track.displayHeight = min(200, max(32, height))`, return 1; unknown id → 0. The core's decode clamp already enforces [32, 200] on load; set it clamped here too.
- [ ] **Step 3: Build + test — PASS.**
- [ ] **Step 4: Commit** `[feat] Track display-height intent ABI`

---

### Task C5: Header drag-resize gesture

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/Views/TimelineView.cs` (pointer pressed/moved/released, cursor)
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/TimelineViewModel.cs` (`RequestTrackResize` event, mirroring `RequestTrackToggle`)
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/MainViewModel.cs` (commit through `Undo.Execute("Resize Track", ...)`)

- [ ] **Step 1: The gesture.** In OnPointerPressed, BEFORE clip hit tests (header column only): the track's bottom edge ±6 px (`Math.Abs(p.Y - rowBottom) <= 6 && p.X < HeaderWidth`) arms a resize: capture pointer, record `resizeTrackId`, `resizeStartY`, `resizeOriginalHeight`. OnPointerMoved: live-update a preview height (clamp 32-200) and InvalidateVisual. OnPointerReleased: if the height changed, fire the intent once; disarm. Escape (`DisarmGesture`) cancels without committing. Cursor: `SizeNorthSouth` over the edge (extend `UpdateEdgeCursor`).

```csharp
    // State (with the other gesture fields):
    string? resizeTrackId;
    double resizeStartY, resizeOriginalHeight, resizePreviewHeight;

    // OnPointerReleased (with the other commits):
    if (resizeTrackId is { } rtid && Math.Abs(resizePreviewHeight - resizeOriginalHeight) > 0.5)
        vm?.RequestTrackResize(rtid, (int)Math.Round(resizePreviewHeight));
```

`RequestTrackResize` → MainViewModel: `Undo.Execute("Resize Track", () => CoreApi.palmier_track_set_display_height(Project, trackId, height) == 1)`.

- [ ] **Step 2: During the drag the row renders at the preview height** (the layout takes a per-gesture override: TimelineView passes `t => t.Id == resizeTrackId ? resizePreviewHeight : (CompactRows ? 28 : t.RenderHeight)` — reconstruct the layout on state change only, not per move; the override applies at render).
- [ ] **Step 3: Build + suite green; live screenshot:** drag A1's bottom edge taller → row grows live, persists after release (re-launch: still 72→dragged value via project save? — note: displayHeight persists in the .palmier project; untitled session restore keeps it while the app runs). Screenshot and read it. Escape mid-drag cancels.
- [ ] **Step 4: Commit** `[feat] Drag-resize tracks from the header edge`

---

### Task C6: Vertical scroll

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/ViewModels/TimelineViewModel.cs` (`ScrollOffsetY` ObservableProperty)
- Modify: `PalmierWin/Shell/PalmierShell/Views/TimelineView.cs` (render offset, wheel, hit tests)

- [ ] **Step 1: `ScrollOffsetY` (≥ 0) on the view model;** render subtracts it from row Y (rows fully above/below the viewport skipped — layout rows are cumulative, so skip via `row.Y + row.Height - scrollY < RulerHeight || row.Y - scrollY > bounds.Height`). Hit tests add `scrollY` back (`y + ScrollOffsetY` before `Layout.TrackAt`).
- [ ] **Step 2: Wheel routing** (OnPointerWheelChanged): Shift+wheel → vertical (`ScrollOffsetY = Math.Clamp(ScrollOffsetY - e.Delta.Y * 60, 0, Math.Max(0, Layout.Bottom - viewportHeight))`); plain wheel stays horizontal; Ctrl+wheel zoom (unchanged). When content fits (Layout.Bottom ≤ viewport), offset clamps to 0.
- [ ] **Step 3: Build + suite green; live screenshot:** 2 video + 3 audio tracks at 72px in a short window, Shift+wheel scrolls the rows, clips stay hit-testable. Screenshot and read it.
- [ ] **Step 4: Commit** `[feat] Vertical timeline scrolling (Shift+wheel)`

---

### Task C7: Gates + live verification + PR (controller)

- [ ] Full suite green (485 + new ≈ 495), swift build clean.
- [ ] Screenshot matrix: default (A1 taller), resized height persists in a saved project reopen, CompactRows toggle overrides uniformly, scroll + resize + hit tests together.
- [ ] PR `[feat] Per-track heights, header drag-resize, vertical scroll (audio package C)`, CI watch, merge, delete branch.

---

## Self-review notes

- Spec coverage: displayHeight parse/use (C1/C2), per-type defaults (C3), intent ABI (C4), drag resize with undo (C5), vertical scroll (C6) — all of design §2.
- `RenderHeight` is the one defaults/clamps rule; `TrackLayout` is the one geometry; `palmier_track_set_display_height` is the one mutation path — no second copies.
- CompactRows remains a session-only override layered on the layout, not on the model.
- The 485 baseline grows by ~10 (5 layout + 1 parse + 1 host default + 1 ABI + 2 gesture-adjacent) — recount at C7.
