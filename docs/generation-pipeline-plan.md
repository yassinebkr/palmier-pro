# Generation pipeline rework — plan

Windows-side redesign of the AI generation pipeline. Four workstreams, each
shipped as its own PR: guided prompt builder, GPS context (opt-in), Enhance
(extend-video) mode, composer UI/UX pass.

## Current state (map)

- One composer window serves shots and transitions: `Views/GenerateWindow.axaml`
  + `ViewModels/GeneratePanelViewModel.cs` (`TransitionTarget`/`ShotTarget`,
  `BeginTransition`/`BeginShot` differ only in arming and landing).
- Providers: `Core/Generation/{ReplicateProvider,FalProvider}.cs` behind
  `IGenerationProvider`; models from the repo-root `models.json` (the csproj
  links it into the app as an AvaloniaResource — single source of truth),
  remote-synced at startup by `ModelManifest.cs` into
  `%APPDATA%/PalmierPro/models-cache.json`.
- Prompts are already styled, not raw: `Core/Generation/PromptStyle.cs`
  (`SeedanceTwoStyle`, `KlingThreeStyle`) appends transition direction,
  reference-look matching, avoid-clauses; `Review()` gives live advice; the
  assembled prompt is previewable. Sources: `docs/kling-prompt-engineering.md`,
  `docs/seedance-prompt-engineering.md`.
- Landing: finished take → `GenerationRecord` sidecar → media import →
  `MainViewModel.InsertTransition`/`InsertShot`, one undo entry each.
- Transition/shot inputs are composited timeline frames
  (`FrameCapture.SaveTimelineFrame`); optional video context via
  `Core/ClipExtract.cs` (ffmpeg re-encode of head/tail).
- No generation agent tools (AgentHost exposes only the 10 timeline/media
  tools). No EXIF/GPS probing anywhere. `flux-3/extend-video` sits
  `"hidden": true` in `models.json` with provider support already implemented
  (`FalProvider.BuildBody` sends `video_url`, Replicate sends `start_video`);
  pricing exists.

## A. Guided prompt builder

Users write better prompts when the parts are laid out. Add a guided builder to
the composer that assembles the base prompt from five explained sections —
**Scene, Subject, Camera, Lighting, Mood** — each with a one-line explanation
and quick-pick chips (e.g. Camera: "slow push-in", "orbit", "handheld follow";
Lighting: "golden hour", "overcast softbox", "neon practicals").

- Chip vocabularies are adapted from the Apache-2.0 kling-3 prompting tables
  (camera/lighting taxonomies), with attribution in the doc and source header.
  Our operator-grade per-model docs stay authoritative for model behavior.
- The builder fills the existing prompt box; the text stays fully editable and
  `PromptStyle.Build`/`Review()` keep working on top unchanged. The builder is
  an assist, never a gate — a hand-written prompt round-trips losslessly.
- Per-model-family chip sets where the vocabularies genuinely differ;
  otherwise one shared set.

## B. GPS context (opt-in)

Goal: outdoor footage can ground generation in a real place ("Setting: …"
line in the prompt).

- **Tier 1 (this program):** probe location metadata on import
  (`av_dict_get` on format metadata: `com.apple.quicktime.location.ISO6709`,
  DJI/GoPro telemetry keys) in `palmier_probe_media` — extending the existing
  ASCII probe contract with an extra field. Shell side: reverse-geocode once
  via Nominatim (free, identifying User-Agent, results cached on disk), expose
  a per-generation opt-in toggle "Use location context" that appends
  `Setting: <place description>` to the base prompt.
- Privacy rules: off by default; coordinates never leave the machine except to
  the geocoder; only the textual place description is embedded in prompts —
  never raw coordinates.
- **Tier 2 (later, not in this program):** Street View Static API stills as
  reference images with camera-heading matching. **Tier 3:** 3D Tiles —
  out of scope.

## C. Enhance mode (extend-video)

Unhide `blackforestlabs/flux-3/extend-video` (both models.json copies) and add
the composer's missing affordance: attach the selected clip's video as the
source (the `ReferenceVideos` path and per-provider request bodies already
exist). Entry points: composer mode + media/timeline context menu
"Enhance with AI…". Lands like a shot (library + optional timeline insert).

## D. Composer UI/UX pass

- A target header that always states what is armed ("Transition · 2.0 s ·
  between 'A' and 'B'" / "Shot · 4.0 s · in gap on V1") so the user never
  guesses where the take will land.
- Integrate the guided builder (A) without crowding: collapsed by default for
  returning users, expanded on first use.
- Job history: `GenerationRecord` sidecars are written but never read back —
  surface recent generations (prompt, model, takes) with re-import.
- Cold-open latency: first composer open reportedly stalls 5–10 s. Suspect the
  remote manifest sync touching the UI path — profile, move fully to
  background, and open instantly from cache.

## Slices (merge order)

1. This plan doc.
2. Guided prompt builder + unit tests for assembly and round-trip.
3. GPS probe (engine + shell geocode + opt-in Setting line) + tests.
4. Enhance mode (unhide + attach source video) + tests.
5. Composer UI/UX pass (target header, builder integration, job history,
   cold-open fix).

Each slice: `build.bat`, `dotnet test PalmierShell.sln -c Release`, review,
PR, merge. Out of scope: generation agent tools (revisit with the MCP server
work), Street View tier 2.
