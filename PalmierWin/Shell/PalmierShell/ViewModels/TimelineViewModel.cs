using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PalmierShell.Core;

namespace PalmierShell.ViewModels;

public enum TimelineTool { Select, Blade }

/// State behind the custom-drawn timeline: the latest core snapshot, playhead,
/// zoom/scroll, selection, and the filmstrip cache. All members are UI-thread
/// only except where noted.
public sealed partial class TimelineViewModel : ObservableObject {
    public const int TimelineFps = 30;

    readonly IntPtr project;

    [ObservableProperty] TimelineState? state;
    [ObservableProperty] int playheadFrame;
    [ObservableProperty] double pixelsPerFrame = 4.0;
    [ObservableProperty] double scrollOffsetX;
    [ObservableProperty] string? selectedClipId;
    [ObservableProperty] TimelineTool tool = TimelineTool.Select;
    [ObservableProperty] bool snapEnabled = true;
    /// Tighter rows so more tracks fit on screen.
    [ObservableProperty] bool compactRows;

    /// Every selected clip. `SelectedClipId` stays the primary one — it is
    /// what the inspector edits — and is always a member of this set.
    public HashSet<string> SelectedClipIds { get; } = new();

    public void SelectOnly(string? clipId) {
        SelectedClipIds.Clear();
        if (clipId is not null) SelectedClipIds.Add(clipId);
        SelectedClipId = clipId;
    }

    /// Shift/Ctrl-click: adds or removes without disturbing the rest.
    public void ToggleSelection(string clipId) {
        if (!SelectedClipIds.Add(clipId)) {
            SelectedClipIds.Remove(clipId);
            if (SelectedClipId == clipId) SelectedClipId = SelectedClipIds.FirstOrDefault();
            return;
        }
        SelectedClipId = clipId;
    }

    public bool IsSelected(string clipId) => SelectedClipIds.Contains(clipId);

    /// Selects everything from `clip` onward — its track only, or every
    /// track. The tool for "shift the whole back half of the edit".
    public void SelectForward(ClipState clip, bool allTracks) {
        if (State is not { } state) return;
        SelectedClipIds.Clear();
        var tracks = allTracks
            ? state.Tracks.AsEnumerable()
            : state.Tracks.Where(t => t.Clips.Any(c => c.Id == clip.Id));
        foreach (var c in tracks.SelectMany(t => t.Clips)
                     .Where(c => c.StartFrame >= clip.StartFrame))
            SelectedClipIds.Add(c.Id);
        SelectedClipId = clip.Id;
    }

    public void SelectAll() {
        if (State is not { } state) return;
        SelectedClipIds.Clear();
        foreach (var c in state.Tracks.SelectMany(t => t.Clips)) SelectedClipIds.Add(c.Id);
        SelectedClipId ??= SelectedClipIds.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedClipId));
    }

    /// Fired when the user scrubs or otherwise moves the playhead (the engine
    /// clock follows). Not fired for engine-driven advances.
    public event Action<int>? PlayheadScrubbed;

    /// Fired after every state reload so views repaint.
    public event Action? StateReloaded;

    /// Fired when the blade tool wants a split; the owner routes it through
    /// the undo stack.
    public event Action<string, int>? BladeRequested;

    public void RequestBlade(string clipId, int frame) => BladeRequested?.Invoke(clipId, frame);

    /// Fired when media is dropped on the timeline: (mediaPath, startFrame).
    public event Action<string, int>? MediaDropped;

    public void RequestMediaDrop(string mediaPath, int startFrame) =>
        MediaDropped?.Invoke(mediaPath, Math.Max(0, startFrame));

    /// Fired when an edge-trim drag ends: (clipId, edge 0=left/1=right,
    /// boundaryFrame). The owner commits it through the undo stack.
    public event Action<string, int, int>? TrimRequested;

    public void RequestTrim(string clipId, int edge, int boundaryFrame) =>
        TrimRequested?.Invoke(clipId, edge, boundaryFrame);

    /// Snaps `frame` to nearby clip edges / playhead / zero when snapping is
    /// on. `excludeClipIds` keeps a dragged group from snapping to itself.
    public int Snap(int frame, double pixelsPerFrame, ISet<string>? excludeClipIds = null) {
        if (!SnapEnabled || State is not { } state) return frame;
        int threshold = Math.Max(1, (int)(8 / Math.Max(0.01, pixelsPerFrame)));
        int best = frame, bestDistance = threshold + 1;
        void Consider(int candidate) {
            int d = Math.Abs(candidate - frame);
            if (d < bestDistance) { best = candidate; bestDistance = d; }
        }
        Consider(0);
        Consider(PlayheadFrame);
        foreach (var track in state.Tracks)
            foreach (var clip in track.Clips) {
                if (excludeClipIds?.Contains(clip.Id) == true) continue;
                Consider(clip.StartFrame);
                Consider(clip.EndFrame);
            }
        return best;
    }

    /// Fired when a clip drag ends: (clipId, newStartFrame). The owner
    /// commits it through the undo stack.
    public event Action<string, int>? MoveRequested;

    public void RequestMove(string clipId, int newStartFrame) =>
        MoveRequested?.Invoke(clipId, newStartFrame);

    /// Move a clip onto another track (clip id, track id, new start frame).
    public event Action<string, string, int>? MoveToTrackRequested;

    public void RequestMoveToTrack(string clipId, string trackId, int startFrame) =>
        MoveToTrackRequested?.Invoke(clipId, trackId, Math.Max(0, startFrame));

    /// Roll: both sides of a cut move together (left clip id, right clip id,
    /// new boundary).
    public event Action<string, string, int>? RollRequested;

    public void RequestRoll(string leftClipId, string rightClipId, int boundaryFrame) =>
        RollRequested?.Invoke(leftClipId, rightClipId, boundaryFrame);

    /// Break or restore the audio/video link on a clip.
    public event Action<string, bool>? LinkChangeRequested;

    public void RequestUnlink(string clipId) => LinkChangeRequested?.Invoke(clipId, false);

    /// Add ("video"/"audio") or remove a track.
    public event Action<string>? AddTrackRequested;
    public event Action<string>? RemoveTrackRequested;

    public void RequestAddTrack(string kind) => AddTrackRequested?.Invoke(kind);
    public void RequestRemoveTrack(string trackId) => RemoveTrackRequested?.Invoke(trackId);

    /// Rename a track: (trackId, the label it shows now, as the edit's seed).
    public event Action<string, string>? RenameTrackRequested;

    public void RequestRenameTrack(string trackId, string currentLabel) =>
        RenameTrackRequested?.Invoke(trackId, currentLabel);

    /// Delete a clip through the undo stack (the context menu's route).
    public event Action<string>? DeleteClipRequested;

    public void RequestDeleteClip(string clipId) => DeleteClipRequested?.Invoke(clipId);

    /// Remove Silence… (context menu): the owner opens the detection dialog.
    public event Action<string>? RemoveSilenceRequested;

    public void RequestRemoveSilence(string clipId) => RemoveSilenceRequested?.Invoke(clipId);

    /// Delete and close the hole: later clips on the affected tracks pull left.
    public event Action<string>? RippleDeleteRequested;

    public void RequestRippleDelete(string clipId) => RippleDeleteRequested?.Invoke(clipId);

    /// Close an empty span on a track: (trackId, gapStart, gapEnd).
    public event Action<string, int, int>? CloseGapRequested;

    public void RequestCloseGap(string trackId, int gapStart, int gapEnd) =>
        CloseGapRequested?.Invoke(trackId, gapStart, gapEnd);

    /// Split under the playhead, upstream's semantics: the selected clips when
    /// there are any, otherwise every clip the playhead crosses — a shortcut
    /// that silently does nothing teaches people the shortcut is broken.
    public void SplitAtPlayhead() {
        var targets = SelectedClipIds.Count > 0
            ? SelectedClipIds.ToList()
            : State?.Tracks.SelectMany(t => t.Clips)
                .Where(c => PlayheadFrame > c.StartFrame && PlayheadFrame < c.EndFrame)
                .Select(c => c.Id).ToList() ?? [];
        // Splitting one clip splits its link partners in the core, so drop
        // ids whose partner is already in the list — a double split at the
        // same frame would make a zero-length sliver.
        var seenGroups = new HashSet<string>();
        foreach (string id in targets) {
            var clip = State?.FindClip(id);
            if (clip is null || PlayheadFrame <= clip.StartFrame || PlayheadFrame >= clip.EndFrame)
                continue;
            if (clip.LinkGroupId is { } group && !seenGroups.Add(group)) continue;
            RequestBlade(id, PlayheadFrame);
        }
    }

    /// The marked range (in/out points), upstream's timeline range selection.
    /// Null until both ends exist; I and O grow a half-open [start, end).
    [ObservableProperty] int? rangeStart;
    [ObservableProperty] int? rangeEnd;

    public bool HasRange => RangeStart is { } s && RangeEnd is { } e && e > s;

    /// I: mark in at the playhead. An in past the current out restarts the range.
    public void MarkIn() {
        RangeStart = PlayheadFrame;
        if (RangeEnd is { } end && end <= PlayheadFrame) RangeEnd = null;
        OnPropertyChanged(nameof(HasRange));
    }

    /// O: mark out at the playhead (exclusive).
    public void MarkOut() {
        RangeEnd = PlayheadFrame;
        if (RangeStart is { } start && start >= PlayheadFrame) RangeStart = null;
        OnPropertyChanged(nameof(HasRange));
    }

    public void ClearRange() {
        RangeStart = null;
        RangeEnd = null;
        OnPropertyChanged(nameof(HasRange));
    }

    /// Ripple-delete the marked range: (start, end).
    public event Action<int, int>? DeleteRangeRequested;

    public void RequestDeleteRange() {
        if (RangeStart is { } s && RangeEnd is { } e && e > s) {
            DeleteRangeRequested?.Invoke(s, e);
            ClearRange();
        }
    }

    /// Delete every property's keyframe at one clip diamond: (clipId, frame).
    /// The timeline's diamonds merge all animated properties, so deleting what
    /// the diamond shows means deleting across all of them.
    public event Action<string, int>? DeleteKeyframeRequested;

    public void RequestDeleteKeyframe(string clipId, int timelineFrame) =>
        DeleteKeyframeRequested?.Invoke(clipId, timelineFrame);

    /// On-clip volume line released: (clipId, linear gain 0..2).
    public event Action<string, double>? SetVolumeRequested;

    public void RequestSetVolume(string clipId, double gain) =>
        SetVolumeRequested?.Invoke(clipId, Math.Clamp(gain, 0, 2));

    /// On-clip fade dot released: (clipId, fadeInFrames, fadeOutFrames).
    public event Action<string, int, int>? SetFadesRequested;

    public void RequestSetFades(string clipId, int fadeIn, int fadeOut) =>
        SetFadesRequested?.Invoke(clipId, Math.Max(0, fadeIn), Math.Max(0, fadeOut));

    /// Trim the selected clips' in (edge 0) or out (edge 1) point to the
    /// playhead — the keyboard form of dragging the edge there, so it goes
    /// through the exact same trim request and clamping.
    public void TrimSelectionToPlayhead(int edge) {
        foreach (string id in SelectedClipIds.ToList()) {
            var clip = State?.FindClip(id);
            if (clip is null || PlayheadFrame <= clip.StartFrame || PlayheadFrame >= clip.EndFrame)
                continue;
            RequestTrim(id, edge, PlayheadFrame);
        }
    }

    /// A cut or gap the user wants to fill with a generated transition.
    public event Action<ClipState, ClipState>? TransitionRequested;

    public void RequestTransition(ClipState left, ClipState right) =>
        TransitionRequested?.Invoke(left, right);

    /// Empty timeline space the user wants to fill with a generated shot:
    /// (trackId, startFrame, availableFrames — 0 when the space is open-ended).
    public event Action<string, int, int>? ShotRequested;

    public void RequestShot(string trackId, int startFrame, int availableFrames) =>
        ShotRequested?.Invoke(trackId, Math.Max(0, startFrame), Math.Max(0, availableFrames));

    /// Fired when a track header toggle (eye/mute) is clicked:
    /// (trackId, isAudioTrack, nextValue).
    public event Action<string, bool, bool>? TrackToggleRequested;

    public void RequestTrackToggle(TrackState track) =>
        TrackToggleRequested?.Invoke(track.Id, track.Type == "audio",
            track.Type == "audio" ? !track.Muted : !track.Hidden);

    public sealed record WaveformData(float[] MinMax, int SourceFrames) {
        /// Peak-normalising display gain — quiet media still reads as a waveform,
        /// silence stays flat. Bounded so noise floors don't fill the clip.
        public float Gain { get; } = GainFor(MinMax);

        static float GainFor(float[] minMax) {
            float peak = 0;
            foreach (float v in minMax) peak = Math.Max(peak, Math.Abs(v));
            return peak < 0.02f ? 1 : Math.Min(8f, 0.95f / peak);
        }
    }

    readonly Dictionary<string, WaveformData?> waveforms = new();

    /// Cached min/max waveform pairs (256 columns over the whole file) plus
    /// the source length in timeline frames, loading async on first request.
    /// Null while loading or when the file has no audio.
    public WaveformData? WaveformFor(string mediaPath) {
        if (waveforms.TryGetValue(mediaPath, out var wf)) return wf;
        waveforms[mediaPath] = null;
        _ = Task.Run(() => {
            var wave = CoreApi.GetWaveform(mediaPath, 256);
            if (wave is null) return;
            var probe = CoreApi.ProbeMedia(mediaPath);
            int sourceFrames = probe is { Fps: > 0, TotalFrames: > 0 } p
                ? Math.Max(1, (int)Math.Round(p.TotalFrames / p.Fps * TimelineFps))
                : 0;
            Dispatcher.UIThread.Post(() => {
                waveforms[mediaPath] = new WaveformData(wave, sourceFrames);
                StateReloaded?.Invoke();
            });
        });
        return null;
    }

    readonly Dictionary<string, Bitmap[]?> filmstrips = new();

    public TimelineViewModel(IntPtr project) {
        this.project = project;
        Reload();
    }

    public int TotalFrames => State?.TotalFrames ?? 0;

    public ClipState? SelectedClip => SelectedClipId is null ? null : State?.FindClip(SelectedClipId);

    /// Gates the silence-removal toolbar button: video clips only.
    public bool SelectedClipIsVideo => SelectedClip?.MediaType == "video";

    partial void OnSelectedClipIdChanged(string? value) =>
        OnPropertyChanged(nameof(SelectedClipIsVideo));

    partial void OnStateChanged(TimelineState? value) =>
        OnPropertyChanged(nameof(SelectedClipIsVideo));

    public void Reload() {
        State = TimelineState.Parse(CoreApi.GetTimelineJson(project));
        // Drop selections whose clips no longer exist (split, delete, undo).
        SelectedClipIds.RemoveWhere(id => State.FindClip(id) is null);
        if (SelectedClipId is not null && State.FindClip(SelectedClipId) is null)
            SelectedClipId = SelectedClipIds.FirstOrDefault();
        StateReloaded?.Invoke();
    }

    /// Restores a JSON snapshot (undo/redo path) and reloads.
    public bool RestoreSnapshot(string json) {
        if (CoreApi.palmier_timeline_load_json(project, json) != 1) return false;
        Reload();
        return true;
    }

    public string CaptureSnapshot() => CoreApi.GetTimelineJson(project);

    /// Media duration → timeline frames at the timeline's fixed 30 fps.
    public static int TimelineFramesFor(MediaItemViewModel item) {
        if (item.TotalFrames <= 0 || item.Fps <= 0) return TimelineFps * 5;
        return Math.Max(1, (int)Math.Round(item.TotalFrames / item.Fps * TimelineFps));
    }

    /// Same conversion for a file that is not in the library yet. Null when it
    /// cannot be probed.
    public static int? TimelineFramesFor(string mediaPath) {
        if (CoreApi.ProbeMedia(mediaPath) is not { Fps: > 0, TotalFrames: > 0 } probe) return null;
        return Math.Max(1, (int)Math.Round(probe.TotalFrames / probe.Fps * TimelineFps));
    }

    readonly Dictionary<string, int> sourceLengths = new();

    /// Source length in timeline frames, cached per file. Trim and roll bounds
    /// need it so the drag ghost promises only what the core will accept.
    /// Returns int.MaxValue for media with no measurable length (text clips).
    public int SourceFramesFor(string mediaRef) {
        if (sourceLengths.TryGetValue(mediaRef, out int cached)) return cached;
        int frames = TimelineFramesFor(mediaRef) ?? int.MaxValue;
        sourceLengths[mediaRef] = frames;
        return frames;
    }

    readonly HashSet<string> probesInFlight = new();

    /// The cached source length only, never probing on the caller's thread —
    /// the filmstrip asks during paint, and a file probe there stalls the
    /// frame. A miss kicks off a background probe and repaints when it lands;
    /// until then the caller falls back to the unwindowed strip.
    public int? CachedSourceFrames(string mediaRef) {
        if (sourceLengths.TryGetValue(mediaRef, out int cached))
            return cached == int.MaxValue ? null : cached;
        if (probesInFlight.Add(mediaRef)) {
            _ = Task.Run(() => {
                int frames = TimelineFramesFor(mediaRef) ?? int.MaxValue;
                Dispatcher.UIThread.Post(() => {
                    sourceLengths[mediaRef] = frames;
                    probesInFlight.Remove(mediaRef);
                    StateReloaded?.Invoke();
                });
            });
        }
        return null;
    }

    public string? AddClip(string mediaPath, int durationFrames) {
        string? id = CoreApi.AddClip(project, mediaPath, durationFrames);
        if (id is not null) Reload();
        return id;
    }

    public bool RemoveClip(string clipId) {
        bool removed = CoreApi.palmier_timeline_remove_clip(project, clipId) == 1;
        if (removed) Reload();
        return removed;
    }

    public string? SplitClip(string clipId, int frame) {
        string? rightId = CoreApi.SplitClip(project, clipId, frame);
        if (rightId is not null) Reload();
        return rightId;
    }

    /// Moves the playhead. A drag delivers a move per mouse event, many of
    /// them landing on the frame already shown; forwarding those anyway made
    /// the audio mixer discard and re-decode every buffered clip faster than
    /// its feeder could refill them, which is heard as the sound cutting out.
    public void Scrub(int frame) {
        int clamped = Math.Clamp(frame, 0, Math.Max(0, TotalFrames - 1));
        if (clamped == PlayheadFrame) return;
        PlayheadFrame = clamped;
        PlayheadScrubbed?.Invoke(clamped);
    }

    /// Returns the cached filmstrip for a media path, kicking off an async
    /// load on first request. Null while loading or when decode failed.
    public Bitmap[]? FilmstripFor(string mediaPath) {
        if (filmstrips.TryGetValue(mediaPath, out var strips)) return strips;
        filmstrips[mediaPath] = null;
        _ = Task.Run(() => {
            const int count = 8;
            var result = CoreApi.GetThumbnails(mediaPath, count);
            Dispatcher.UIThread.Post(() => {
                if (result is { } r) {
                    var bitmaps = new Bitmap[r.Count];
                    for (int i = 0; i < r.Count; i++)
                        bitmaps[i] = ThumbnailBitmaps.FromTiles(r.Tiles, i);
                    filmstrips[mediaPath] = bitmaps;
                    StateReloaded?.Invoke();
                }
            });
        });
        return null;
    }
}
