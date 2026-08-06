# Generation models manifest + FLUX.3 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The generation model list becomes data, not code: a JSON manifest the app polls (plus an "Update models" button) so new models (Seedance 2.5, Kling 3.x) arrive without an app release — with FLUX.3's video family (fal.ai) wired in from day one.

**Architecture:** Both providers' hardcoded `All` arrays move into a bundled `models.json` (identical content at first). A `ModelManifest` static class mirrors `UpdateChecker`'s shape: bundled fallback, fetch from `https://raw.githubusercontent.com/yassinebkr/palmierWin/main/models.json`, cache under `%APPDATA%\PalmierPro\models-cache.json`, honor offline/parse-failure as "use cached/bundled". The Generate panel gets an "Update models" button that forces a refresh and reports what changed. FLUX.3 (Black Forest Labs, fal.ai) joins with its exact workflow endpoints.

**Tech Stack:** C#/.NET 9 (shell + providers), xunit.

**Build/test:** `export PATH="/c/Program Files/dotnet:$PATH" && cd PalmierWin/Shell && dotnet test PalmierShell.sln 2>&1 | tail -3`

**Current-state anchors (verified):**
- `GenerationModel(string Id, string Name, int[] Durations)` + extras — `Core/Generation/GenerationProvider.cs:21`.
- Hardcoded lists: `ReplicateProvider.cs:27-66` (`All`: seedance-2.0/1.5/1, kling-v3/2.1, veo-3/3-fast) and `FalProvider.cs:22-32` (`All`: seedance-2.0 image/text + fast, kling v2.1, veo3-fast, hailuo-02).
- fal param conventions: `image_url` / `end_image_url` (FalProvider.cs:51-52); Replicate's own per-family fields (start_image/end_image for Seedance/Kling).
- FLUX.3 endpoints (fal.ai, verified 2026-08-06): `blackforestlabs/flux-3/first-last-frame-to-video` (transitions — start+end frame), `blackforestlabs/flux-3/text-to-video` (shots), `blackforestlabs/flux-3/extend-video` (continue a clip — upstream's "FLUX Enhance" workflow), `blackforestlabs/flux-3/image-to-video`, draft mode (720p preview → enhance). Native audio generation (skip like Kling: our timeline has its own sound).
- `UpdateChecker` (`Core/UpdateChecker.cs`) is the poll+fallback pattern to mirror.
- Generate panel model picker: `GeneratePanelViewModel.cs` (model list surface + provider switching).
- Requires a fal.ai API key for FLUX (the user's settings carry openrouter+replicate keys; fal key may need adding in settings — the app already has a fal provider path).

---

### Task M1: `models.json` bundled manifest + `ModelManifest` reader

**Files:**
- Create: `PalmierWin/Shell/PalmierShell/Assets/models.json` (bundled fallback — exact current model lists, both providers)
- Create: `PalmierWin/Shell/PalmierShell/Core/Generation/ModelManifest.cs`
- Modify: `ReplicateProvider.cs`, `FalProvider.cs` (read from ModelManifest; the arrays become the fallback JSON's content)
- Test: `PalmierWin/Shell/PalmierShell.Tests/ModelManifestTests.cs`

- [ ] **Step 1: The JSON schema** (versioned, per provider):

```json
{
  "version": 1,
  "providers": {
    "replicate": [
      { "id": "bytedance/seedance-2.0", "name": "Seedance 2.0", "durations": [4,5,6,8,10,12,15],
        "capabilities": ["firstLastFrame", "textToVideo"] }
    ],
    "fal": [
      { "id": "blackforestlabs/flux-3/first-last-frame-to-video", "name": "FLUX.3 · first/last frame",
        "durations": [4,5,6,8,10], "capabilities": ["firstLastFrame"], "family": "flux" }
    ]
  }
}
```

(ModelManifest must map every field the providers currently use — read both `All` arrays fully and carry their extras into the JSON objects (e.g. Seedance-1.x note, Veo no-last-frame behavior, draft flag).)

- [ ] **Step 2: `ModelManifest.cs`** — `IReadOnlyList<GenerationModel> For(string provider)`; parse order: cached file → bundled asset → (empty provider list = feature hidden). Fail-soft everywhere (malformed JSON = bundled).
- [ ] **Step 3: Providers consume it** — `ReplicateProvider.Models`/`FalProvider.Models` become `ModelManifest.For("replicate")` / `For("fal")`; the hardcoded `All` arrays are deleted and their content lands verbatim in `Assets/models.json`.
- [ ] **Step 4: Tests** — parse both providers' lists (every current model present with durations), capabilities map, malformed JSON falls back to bundled, unknown provider returns empty.
- [ ] **Step 5: Build + suite green; commit** `[feat] Generation models read from a bundled manifest`

---

### Task M2: Remote sync + "Update models" button

**Files:**
- Modify: `ModelManifest.cs` (fetch + cache + change report)
- Modify: `GeneratePanelViewModel.cs` (button + status)
- Test: `ModelManifestTests.cs` (fetch seam)

- [ ] **Step 1: Sync.** `SyncAsync()`: GET the raw manifest URL (15s timeout, User-Agent like UpdateChecker); on success, validate (parse + version == 1 + at least one provider non-empty), write `%APPDATA%\PalmierPro\models-cache.json` atomically, reload; return a change report (added/removed model names per provider vs current). Offline/invalid → null, current list untouched. `FetchOverride` test seam.
- [ ] **Step 2: The button** in the Generate panel next to the model picker: "Update models" → spinner → message ("2 new models: FLUX.3 · first/last frame, …" / "Already up to date" / "Couldn't check — using the bundled list"). Model list re-reads after sync.
- [ ] **Step 3: Tests** — sync swaps list on valid payload; invalid payload keeps current; cache file written+read; change report correct.
- [ ] **Step 4: Commit** `[feat] Manifest sync + update-models button`

---

### Task M3: FLUX.3 workflows on fal

**Files:**
- Modify: `PalmierWin/Shell/PalmierShell/Assets/models.json` (flux-3 family)
- Modify: `FalProvider.cs` (flux param conventions if they differ from kling's image_url/end_image_url — read fal's flux-3 first-last-frame API shape and extend the request builder minimally)
- Modify: `GeneratePanelViewModel.cs` (an "Enhance" affordance for extend-video — only if cheap this slice; otherwise note for the generation rework)
- Test: `ModelManifestTests.cs` (flux entries present with capabilities)

- [ ] **Step 1: Manifest entries** — flux-3 first/last-frame (transitions), text-to-video (shots), extend-video, draft mode flag; durations per fal docs (fetch https://fal.ai/models/blackforestlabs/flux-3/first-last-frame-to-video/api for exact duration range + param names BEFORE writing the request builder).
- [ ] **Step 2: Request building** — flux endpoints take their stills under fal's flux-3 names (verify from the API page; likely `start_image_url`/`end_image_url` or `image_url` variants) — extend FalProvider's body builder per-family (`family: "flux"` from the manifest).
- [ ] **Step 3: Suite green; commit** `[feat] FLUX.3 video models on fal`
- [ ] **Step 4 (needs the user's fal key):** one real transition generation end-to-end, or document precisely what to verify when a key is present. Do not mark done on a mocked path — say exactly what was and wasn't exercised.

---

### Task M4: Gates + PR (controller)

- [ ] Suite green (~510+), `models.json` committed at repo root level of the raw URL (`https://raw.githubusercontent.com/yassinebkr/palmierWin/main/models.json` MUST exist on main or the fetch 404s — add the root copy in this PR and keep it and the bundled asset IDENTICAL; a CI or test check compares them).
- [ ] PR, CI watch, merge.

---

## Self-review notes

- The root `models.json` and bundled asset staying identical is the one operational hazard — M4 gates it with a comparison test.
- Draft/enhance (extend-video) is v1-minimal: manifest + provider support now; the "Enhance" UI affordance is explicitly deferred to the generation rework unless trivial.
- `models.json` fetch is unsigned public content from OUR repo — same trust model as UpdateChecker's release metadata; validated before use (parse + version + non-empty).
