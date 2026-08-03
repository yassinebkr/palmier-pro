using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;

namespace PalmierShell.ViewModels;

/// Root view model: owns the Swift project handle, the undo stack, and the
/// panel view models, and connects the engine render clock to the timeline.
public sealed partial class MainViewModel : ObservableObject, IDisposable {
    public IntPtr Project { get; private set; }
    public MediaPanelViewModel Media { get; } = new();
    public TimelineViewModel Timeline { get; }
    public TimelineTabsViewModel Tabs { get; }
    public ViewerTabsViewModel Viewer { get; }
    public UndoStack Undo { get; }
    public InspectorViewModel Inspector { get; }
    public AgentViewModel Agent { get; }

    EngineSession? engine;
    IntPtr audio;
    IntPtr agentHandle;
    bool followingEngine;

    public MainViewModel() {
        Project = CoreApi.palmier_project_create();
        audio = CoreApi.palmier_audio_create(Project);  // Zero: no output device; app runs silent
        Timeline = new TimelineViewModel(Project);
        Tabs = new TimelineTabsViewModel(Project, Timeline);
        Viewer = new ViewerTabsViewModel(Project);
        // Undo entries carry their timeline tab so a restore always lands on
        // the timeline the edit was made on.
        Undo = new UndoStack(Timeline.CaptureSnapshot, Timeline.RestoreSnapshot,
                             () => Tabs.ActiveIndex, Tabs.Activate);
        Tabs.TimelineClosed += index => Undo.ForgetScope(index);
        Inspector = new InspectorViewModel(Project, Timeline, Media, Undo);
        agentHandle = CoreApi.palmier_agent_create(Project);
        Agent = new AgentViewModel(agentHandle, Timeline, Media, Undo);
        // Clip selection and library selection are mutually exclusive.
        Timeline.PropertyChanged += (_, e) => {
            if (e.PropertyName != nameof(TimelineViewModel.SelectedClipId)) return;
            if (Timeline.SelectedClipId is not null) Media.SelectedItem = null;
            // The preview draws the manipulation frame, so it has to know.
            engine?.SetSelection(Timeline.SelectedClipId);
        };
        Media.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(MediaPanelViewModel.SelectedItem) &&
                Media.SelectedItem is not null)
                Timeline.SelectedClipId = null;
        };
        Undo.Changed += () => {
            PerformUndoCommand.NotifyCanExecuteChanged();
            PerformRedoCommand.NotifyCanExecuteChanged();
        };
        // Any edit that reaches the timeline marks the project unsaved.
        Timeline.StateReloaded += () => ProjectDirty = true;
        Media.Items.CollectionChanged += (_, _) => ProjectDirty = true;
        Media.AddToTimelineRequested += OnAddToTimeline;
        Timeline.BladeRequested += OnBlade;
        Timeline.TrackToggleRequested += OnTrackToggle;
        Timeline.MediaDropped += (path, frame) => {
            var item = Media.Items.FirstOrDefault(i => i.Path == path);
            if (item is null) return;
            int frames = TimelineViewModel.TimelineFramesFor(item);
            Undo.Execute("Add Clip", () => CoreApi.AddClipAt(Project, path, frames, frame) is not null);
            Timeline.Reload();
        };
        Timeline.TrimRequested += (clipId, edge, boundary) => {
            Undo.Execute("Trim Clip", () => CoreApi.palmier_clip_trim(Project, clipId, edge, boundary) == 1);
            Timeline.Reload();
        };
        Timeline.MoveRequested += (clipId, newStart) => {
            Undo.Execute("Move Clip", () => CoreApi.palmier_timeline_move_clip(Project, clipId, newStart) == 1);
            Timeline.Reload();
        };
        Timeline.MoveToTrackRequested += (clipId, trackId, start) => {
            Undo.Execute("Move Clip to Track",
                () => CoreApi.palmier_timeline_move_clip_to_track(Project, clipId, trackId, start) == 1);
            Timeline.Reload();
        };
        Timeline.RollRequested += (left, right, boundary) => {
            Undo.Execute("Roll Edit",
                () => CoreApi.palmier_timeline_roll_edit(Project, left, right, boundary) >= 0);
            Timeline.Reload();
        };
        Timeline.LinkChangeRequested += (clipId, link) => {
            Undo.Execute(link ? "Link Clips" : "Unlink Audio",
                () => CoreApi.palmier_clip_unlink(Project, clipId) == 1);
            Timeline.Reload();
        };
        Timeline.AddTrackRequested += kind => {
            Undo.Execute("Add Track", () => CoreApi.AddTrack(Project, kind) is not null);
            Timeline.Reload();
        };
        Timeline.RemoveTrackRequested += trackId => {
            Undo.Execute("Remove Track",
                () => CoreApi.palmier_timeline_remove_track(Project, trackId) == 1);
            Timeline.Reload();
        };
        Timeline.RenameTrackRequested += (trackId, current) =>
            _ = RenameTrackAsync(trackId, current);
        Timeline.DeleteClipRequested += clipId => {
            // One intent, one undo entry, even across a multi-selection.
            var ids = Timeline.SelectedClipIds.Contains(clipId)
                ? Timeline.SelectedClipIds.ToList()
                : [clipId];
            Undo.Execute(ids.Count > 1 ? "Delete Clips" : "Delete Clip",
                () => ids.Count(id => CoreApi.palmier_timeline_remove_clip(Project, id) == 1) > 0);
            Timeline.Reload();
        };
        Timeline.SetVolumeRequested += (clipId, gain) => {
            // The line drags in the mixer's linear domain; dB only at the ABI.
            double db = gain <= 0.001 ? -60 : Math.Round(20 * Math.Log10(gain), 1);
            Undo.Execute("Adjust Volume",
                () => CoreApi.palmier_clip_set_volume_db(Project, clipId, db) == 1);
            Timeline.Reload();
        };
        Timeline.SetFadesRequested += (clipId, fadeIn, fadeOut) => {
            Undo.Execute("Adjust Fade",
                () => CoreApi.palmier_clip_set_fades(Project, clipId, fadeIn, fadeOut) == 1);
            Timeline.Reload();
        };
        Timeline.RippleDeleteRequested += clipId => {
            var ids = Timeline.SelectedClipIds.Contains(clipId)
                ? Timeline.SelectedClipIds.ToList()
                : [clipId];
            Undo.Execute(ids.Count > 1 ? "Ripple Delete Clips" : "Ripple Delete",
                () => CoreApi.RippleDelete(Project, ids) > 0);
            Timeline.Reload();
        };
        Timeline.RemoveSilenceRequested += clipId => _ = RemoveSilenceAsync(clipId);
        Timeline.CloseGapRequested += (trackId, start, end) => {
            Undo.Execute("Ripple Delete Gap",
                () => CoreApi.palmier_timeline_close_gap(Project, trackId, start, end) == 1);
            Timeline.Reload();
        };
        Timeline.DeleteKeyframeRequested += (clipId, frame) => {
            // One diamond, one undo entry, across however many properties are
            // keyed at that frame.
            string[] properties = ["position", "scale", "rotation", "opacity", "volume"];
            Undo.Execute("Delete Keyframe", () => properties.Count(p =>
                CoreApi.palmier_clip_remove_keyframe(Project, clipId, p, frame) == 1) > 0);
            Timeline.Reload();
        };
        Timeline.DeleteRangeRequested += (start, end) => {
            Undo.Execute("Ripple Delete Range",
                () => CoreApi.palmier_timeline_delete_range(Project, start, end, 1) > 0);
            Timeline.Reload();
        };
        Timeline.TransitionRequested += (left, right) => _ = BeginTransitionAsync(left, right);
        Timeline.ShotRequested += (trackId, start, available) =>
            _ = BeginShotAsync(trackId, start, available);
        Media.Generate.TransitionReady += InsertTransition;
        Media.Generate.ShotReady += InsertShot;
        // The composer's frame-nudge buttons recapture through the same
        // composite path the automatic stills use.
        Media.Generate.CaptureTimelineFrame = frame =>
            Task.Run(() => FrameCapture.SaveTimelineFrame(Project, frame, "pick"));
        Media.Generate.VideoContextRequested += () => _ = AttachVideoContextAsync();
        Timeline.PlayheadScrubbed += frame => {
            if (engine is { } e) e.PlayheadFrame = frame;
            if (audio != IntPtr.Zero) CoreApi.palmier_audio_seek(audio, frame);
        };
        Timeline.StateReloaded += () => {
            SyncEngineTotalFrames();
            if (audio != IntPtr.Zero) CoreApi.palmier_audio_sync(audio);
        };
        Media.OpenInViewerRequested += Viewer.Open;
        Viewer.ActiveChanged += OnViewerChanged;
        Viewer.PropertyChanged += (_, e) => {
            // Scrubbing the source monitor drives the engine directly; the
            // timeline playhead stays where the edit left it.
            if (e.PropertyName == nameof(ViewerTabsViewModel.SourceFrame)
                && Viewer.ShowingSource && !followingEngine)
                SeekEngine(Viewer.SourceFrame);
        };
        _ = ApplyPreferencesAsync();
    }

    /// Appearance and timeline preferences from the settings pane. Read off
    /// the UI thread; applied once it returns.
    async Task ApplyPreferencesAsync() {
        var settings = await Task.Run(SettingsStore.Load);
        Accent.Apply(settings.Accent);
        Timeline.SnapEnabled = settings.SnapEnabled;
        PreferencesApplied?.Invoke();
    }

    /// Lets the timeline toolbar refresh its snap button once preferences land.
    public event Action? PreferencesApplied;

    /// Agent panel collapsed to its icon rail, giving the width back to the
    /// preview and timeline. Persisted with the rest of the layout.
    [ObservableProperty] bool agentCollapsed;

    // MARK: Project file

    [ObservableProperty] string? projectPath;
    [ObservableProperty] bool projectDirty;
    [ObservableProperty] bool renamingProject;
    [ObservableProperty] string projectNameEdit = "";

    /// Name held separately from the path: an unsaved project can still be
    /// named, and saving adopts the name as the filename.
    [ObservableProperty] string projectName = "Untitled Project";

    public string ProjectTitle => ProjectName + (ProjectDirty ? " •" : "");

    partial void OnProjectNameChanged(string value) => OnPropertyChanged(nameof(ProjectTitle));
    partial void OnProjectDirtyChanged(bool value) => OnPropertyChanged(nameof(ProjectTitle));

    partial void OnProjectPathChanged(string? value) {
        if (value is not null) ProjectName = Path.GetFileNameWithoutExtension(value);
    }

    public void BeginRenameProject() {
        ProjectNameEdit = ProjectName;
        RenamingProject = true;
    }

    /// Commits a rename. On a saved project this renames the file too, so the
    /// title and what is on disk cannot drift apart.
    public async Task CommitRenameProjectAsync() {
        RenamingProject = false;
        string name = ProjectNameEdit.Trim();
        if (name.Length == 0 || name == ProjectName) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;

        if (ProjectPath is { } current && File.Exists(current)) {
            string target = Path.Combine(Path.GetDirectoryName(current)!, name + "." + ProjectStore.Extension);
            try {
                await Task.Run(() => File.Move(current, target, overwrite: false));
                ProjectPath = target;
            } catch {
                return;  // name taken or locked: keep the old one rather than lie
            }
        }
        ProjectName = name;
    }

    /// Everything worth saving: the core's timelines plus the library, which
    /// the core does not know about.
    ProjectDocument BuildDocument() =>
        new(ProjectDocument.CurrentVersion, CoreApi.GetProjectJson(Project)) {
            Media = Media.SaveLibrary(),
            Folders = Media.Folders.ToList(),
        };

    public async Task SaveProjectAsync(string path) {
        var document = BuildDocument();
        await Task.Run(() => {
            ProjectStore.Save(path, document);
            RecentProjects.Add(path);
        });
        ProjectPath = path;
        ProjectDirty = false;
        await Task.Run(ProjectStore.ClearRecovery);
    }

    /// Loads a project. Returns any media files that could not be found, so
    /// the caller can tell the user rather than quietly losing them.
    public async Task<IReadOnlyList<string>> OpenProjectAsync(string path) {
        var document = await Task.Run(() => ProjectStore.Load(path));
        if (document is null) return ["This project could not be read."];

        if (CoreApi.palmier_project_load_json(Project, document.Core) != 1)
            return ["This project's timeline could not be restored."];

        var missing = await Media.RestoreLibraryAsync(document.Media, document.Folders);
        await Task.Run(() => RecentProjects.Add(path));
        Tabs.Reload();
        Timeline.SelectedClipId = null;
        Timeline.Reload();
        Timeline.Scrub(0);
        Undo.Clear();
        ProjectPath = path;
        ProjectDirty = false;
        return missing;
    }

    public async Task NewProjectAsync() {
        CoreApi.palmier_project_load_json(Project, "{\"timelines\":[],\"activeIndex\":0}");
        // An empty timelines array is rejected by the core, so build a fresh
        // one the same way a new project does.
        await Media.RestoreLibraryAsync([], []);
        while (CoreApi.palmier_project_timeline_count(Project) > 1)
            CoreApi.palmier_project_remove_timeline(Project, 1);
        foreach (var clip in Timeline.State?.Tracks.SelectMany(t => t.Clips).ToList() ?? [])
            CoreApi.palmier_timeline_remove_clip(Project, clip.Id);
        Tabs.Reload();
        Timeline.SelectedClipId = null;
        Timeline.Reload();
        Timeline.Scrub(0);
        Undo.Clear();
        ProjectPath = null;
        ProjectDirty = false;
    }

    /// Parks unsaved work so a crash or a forgotten save is recoverable.
    public void SaveRecovery() {
        if (!ProjectDirty) return;
        var document = BuildDocument();
        _ = Task.Run(() => ProjectStore.SaveRecovery(document));
    }

    /// Saves the frame under the playhead as a still and imports it. This is
    /// the front door to the transition workflow: capture a real frame, have
    /// the model make a matching one, generate the motion between them.
    [RelayCommand]
    async Task CaptureFrame() {
        CaptureStatus = "Capturing…";
        string? saved;
        if (Viewer.ShowingSource && Viewer.Active is { IsTimeline: false } tab) {
            // Source monitor: the file is on screen unprocessed, so the still
            // comes straight from it.
            string mediaPath = tab.Path;
            int sourceFrame = Viewer.SourceFrame;
            saved = await Task.Run(() => FrameCapture.SaveFrame(
                mediaPath, sourceFrame, TimelineViewModel.TimelineFps, "frame"));
        } else if (Timeline.State?.Tracks.Any(t => t.Type == "video" && t.Clips.Any(
                       c => Timeline.PlayheadFrame >= c.StartFrame &&
                            Timeline.PlayheadFrame < c.EndFrame)) == true) {
            // Timeline: capture the composite, so the still is exactly the
            // preview — layers, transforms and trims included.
            int frame = Timeline.PlayheadFrame;
            saved = await Task.Run(() => FrameCapture.SaveTimelineFrame(Project, frame, "frame"));
        } else {
            CaptureStatus = "Nothing under the playhead to capture.";
            return;
        }
        if (saved is null) {
            CaptureStatus = "That frame could not be decoded.";
            return;
        }
        await Media.ImportFileAsync(saved);
        CaptureStatus = null;
    }

    [ObservableProperty] string? captureStatus;

    /// Default length of a generated transition when the cut gives no better
    /// hint, in seconds.
    const int DefaultTransitionSeconds = 5;

    /// Grabs the outgoing clip's last frame and the incoming clip's first
    /// frame, then arms the Generate composer to travel between them. Both
    /// decodes happen off the UI thread.
    async Task BeginTransitionAsync(ClipState left, ClipState right) {
        // Decoding both stills takes a couple of seconds. Show the composer
        // first so the click has an immediate effect instead of dead time.
        int gapFrames = right.StartFrame - left.EndFrame;
        bool fillsGap = gapFrames > 0;
        Media.Generate.BeginTransitionPending(left.EndFrame, fillsGap);

        // Composited timeline frames, the way upstream seeds a transition: the
        // last frame the viewer shows before the boundary and the first one
        // after it. Re-deriving per-clip source positions instead is a second
        // implementation of "what is on screen", and it drifted — wrong frames
        // after splits, and blind to transforms and layered tracks.
        int firstFrame = left.EndFrame - 1;
        int lastFrame = right.StartFrame;
        var (first, last) = await Task.Run(() => (
            FrameCapture.SaveTimelineFrame(Project, firstFrame, "transition-from"),
            FrameCapture.SaveTimelineFrame(Project, lastFrame, "transition-to")));

        if (first is null || last is null) {
            Media.Generate.Message = "Could not read a frame on one side of the cut.";
            return;
        }
        Media.Generate.BeginTransition(
            new TransitionTarget(left.Id, right.Id,
                                 fillsGap ? left.EndFrame : left.EndFrame,
                                 fillsGap ? gapFrames
                                          : DefaultTransitionSeconds * TimelineViewModel.TimelineFps) {
                FillsGap = fillsGap,
                LeftClipStartFrame = left.StartFrame,
                RightClipEndFrame = right.EndFrame,
            },
            first, last, firstFrame, lastFrame);
    }

    /// Arms a generated shot for empty timeline space, seeded with the frames
    /// it has to sit between: the outgoing clip's last and the incoming clip's
    /// first. A shot dropped into a gap has to match its neighbours, so making
    /// the user go and find those two stills by hand was busywork the timeline
    /// already knows the answer to. Either side may be missing at the head or
    /// tail of a track; whatever exists is filled.
    async Task BeginShotAsync(string trackId, int start, int available) {
        var track = Timeline.State?.Tracks.FirstOrDefault(t => t.Id == trackId);
        // The neighbours decide *whether* each side has a frame to offer; the
        // pixels come from the composite, so they match the viewer exactly.
        var (before, after) = track?.ClipsAround(start, start + available) ?? (null, null);
        var target = new ShotTarget(trackId, start, available) {
            BeforeClipId = before?.Id,
            AfterClipId = after?.Id,
            BeforeClipStartFrame = before?.StartFrame ?? 0,
            AfterClipEndFrame = after?.EndFrame ?? int.MaxValue,
        };
        Media.Generate.BeginShot(target);
        if (before is null && after is null) return;

        // Anchored to the neighbours' edges, not the click: empty space past
        // the last clip would otherwise composite as black.
        var (first, last) = await Task.Run(() => (
            before is null ? null
                : FrameCapture.SaveTimelineFrame(Project, before.EndFrame - 1, "shot-from"),
            after is null ? null
                : FrameCapture.SaveTimelineFrame(Project, after.StartFrame, "shot-to")));

        // The user may have armed something else, or cleared the slots, while
        // the stills were decoding.
        if (Media.Generate.PendingShot != target) return;
        if (first is not null) Media.Generate.SetFirstFrame(first, before!.EndFrame - 1);
        if (last is not null) Media.Generate.SetLastFrame(last, after!.StartFrame);
        Media.Generate.DescribeShotFrames(first is not null, last is not null);
    }

    /// Drops a finished transition into place. Across a cut it straddles the
    /// boundary, overwriting the tail of the outgoing clip and the head of the
    /// incoming one — that is what a transition is. Across a gap it starts at
    /// the gap instead, because the space is already there. One undo entry.
    void InsertTransition(TransitionTarget target, string mediaPath) {
        int clipFrames = TimelineViewModel.TimelineFramesFor(mediaPath) ?? target.DurationFrames;
        Undo.Execute("Add Transition", () => {
            if (target.FillsGap) {
                // The gap defines the extent: a longer generation is trimmed
                // to land exactly, never overwriting a neighbour.
                int frames = target.DurationFrames > 0
                    ? Math.Min(clipFrames, target.DurationFrames) : clipFrames;
                return CoreApi.AddClipAt(Project, mediaPath, frames, target.BoundaryFrame) is not null;
            }

            // Across a cut the transition replaces the chosen span of both
            // neighbours: trim them back (linked audio follows the shared
            // edge), then land the clip in the space that opened up.
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(Project));
            var left = state.FindClip(target.LeftClipId);
            var right = state.FindClip(target.RightClipId);
            if (left is null || right is null
                || target.ReplaceBeforeFrames + target.ReplaceAfterFrames <= 0) {
                // Neighbours re-edited away, or a legacy no-span target:
                // straddle the boundary without cutting anything.
                int frames = Math.Min(clipFrames, target.DurationFrames);
                return CoreApi.AddClipAt(Project, mediaPath,
                    frames, Math.Max(0, target.BoundaryFrame - frames / 2)) is not null;
            }
            // Clamp to what the neighbours can give up — at least one frame
            // of each must survive or the trim would delete the clip.
            int before = Math.Min(target.ReplaceBeforeFrames,
                                  target.BoundaryFrame - left.StartFrame - 1);
            int after = Math.Min(target.ReplaceAfterFrames,
                                 right.EndFrame - right.StartFrame - 1);
            if (before < 0) before = 0;
            if (after < 0) after = 0;
            if (before + after == 0) return false;
            int spanStart = target.BoundaryFrame - before;
            if (before > 0 && CoreApi.palmier_clip_trim(Project, left.Id, 1, spanStart) != 1)
                return false;
            if (after > 0 && CoreApi.palmier_clip_trim(Project, right.Id, 0,
                                                       target.BoundaryFrame + after) != 1)
                return false;
            int landing = Math.Min(clipFrames, before + after);
            if (CoreApi.AddClipAt(Project, mediaPath, landing, spanStart) is not { } id)
                return false;
            // The core drops new clips on the first track with room; the
            // transition belongs on the track it is bridging.
            var track = state.Tracks.FirstOrDefault(t => t.Clips.Any(c => c.Id == left.Id));
            if (track is not null)
                CoreApi.palmier_timeline_move_clip_to_track(Project, id, track.Id, spanStart);
            return true;
        });
        Timeline.Reload();
    }

    /// Drops a finished shot into the space it was generated for, moving it to
    /// the track the user asked from when that is not the default one.
    void InsertShot(ShotTarget target, string mediaPath) {
        int clipFrames = TimelineViewModel.TimelineFramesFor(mediaPath)
            ?? DefaultTransitionSeconds * TimelineViewModel.TimelineFps;
        Undo.Execute("Add Generated Shot", () => {
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(Project));
            // Spans extend the shot into the neighbours; clamp so at least
            // one frame of each survives, then trim them back first.
            var beforeClip = target.BeforeClipId is null ? null : state.FindClip(target.BeforeClipId);
            var afterClip = target.AfterClipId is null ? null : state.FindClip(target.AfterClipId);
            int gapEnd = target.StartFrame + target.AvailableFrames;
            int before = beforeClip is null ? 0 : Math.Clamp(target.ReplaceBeforeFrames, 0,
                target.StartFrame - beforeClip.StartFrame - 1);
            int after = afterClip is null || target.AvailableFrames <= 0 ? 0
                : Math.Clamp(target.ReplaceAfterFrames, 0, afterClip.EndFrame - gapEnd - 1);
            if (before > 0 && CoreApi.palmier_clip_trim(Project, beforeClip!.Id, 1,
                                                        target.StartFrame - before) != 1)
                return false;
            if (after > 0 && CoreApi.palmier_clip_trim(Project, afterClip!.Id, 0,
                                                       gapEnd + after) != 1)
                return false;
            // A bounded space defines the extent; the clip is trimmed to it.
            int extent = target.AvailableFrames > 0
                ? target.AvailableFrames + before + after : clipFrames;
            int frames = Math.Min(clipFrames, extent);
            if (CoreApi.AddClipAt(Project, mediaPath, frames, target.StartFrame - before)
                is not { } id)
                return false;
            // The core drops new clips on the first video track; read back
            // where it actually landed before deciding to move it.
            var landed = TimelineState.Parse(CoreApi.GetTimelineJson(Project))
                .Tracks.FirstOrDefault(t => t.Clips.Any(c => c.Id == id));
            if (landed is not null && landed.Id != target.TrackId)
                CoreApi.palmier_timeline_move_clip_to_track(Project, id, target.TrackId,
                                                            target.StartFrame - before);
            return true;
        });
        Timeline.Reload();
    }

    /// Extracts the clips around the pending target and attaches them to the
    /// composer as [Video1]/[Video2] — real moving footage for the models
    /// that take reference videos. Uses the pending spans when set, two
    /// seconds a side otherwise.
    async Task AttachVideoContextAsync() {
        var composer = Media.Generate;
        if (Timeline.State is not { } state) return;
        var (beforeClip, afterClip, beforeFrames, afterFrames) = composer.PendingTransition is { } t
            ? (state.FindClip(t.LeftClipId), state.FindClip(t.RightClipId),
               t.ReplaceBeforeFrames, t.ReplaceAfterFrames)
            : composer.PendingShot is { } s
                ? (s.BeforeClipId is null ? null : state.FindClip(s.BeforeClipId),
                   s.AfterClipId is null ? null : state.FindClip(s.AfterClipId),
                   s.ReplaceBeforeFrames, s.ReplaceAfterFrames)
                : (null, null, 0, 0);
        if (beforeClip is null && afterClip is null) {
            composer.Message = "Arm a transition or shot first — the video context comes " +
                               "from the clips around it.";
            return;
        }
        int fps = TimelineViewModel.TimelineFps;
        if (beforeFrames <= 0) beforeFrames = 2 * fps;
        if (afterFrames <= 0) afterFrames = 2 * fps;
        composer.Message = "Extracting the surrounding video…";
        var (tail, head) = await Task.Run(() => (
            beforeClip is null ? null : ClipExtract.SaveTail(beforeClip, beforeFrames, fps),
            afterClip is null ? null : ClipExtract.SaveHead(afterClip, afterFrames, fps)));
        if (tail is null && head is null) {
            composer.Message = "Could not extract the surrounding video (ffmpeg).";
            return;
        }
        if (tail is not null) composer.AddReferenceVideo(tail);
        if (head is not null) composer.AddReferenceVideo(head);
        composer.ClearFramesForReferences();
        composer.Message = "Surrounding clips attached as video references — say what to " +
                           "take from [Video1] and [Video2] in the prompt.";
    }

    void OnTrackToggle(string trackId, bool isAudioTrack, bool nextValue) {
        string name = isAudioTrack ? "Mute Track" : "Hide Track";
        Undo.Execute(name, () => (isAudioTrack
            ? CoreApi.palmier_track_set_muted(Project, trackId, nextValue ? 1 : 0)
            : CoreApi.palmier_track_set_hidden(Project, trackId, nextValue ? 1 : 0)) == 1);
        Timeline.Reload();
    }

    /// The window modal dialogs hang off. Set by the shell window.
    public Func<Avalonia.Controls.Window?>? DialogOwner { get; set; }

    /// Opens the silence-removal dialog for a clip. The dialog detects in the
    /// background and calls back into ApplySilenceRemoval.
    async Task RemoveSilenceAsync(string clipId) {
        if (DialogOwner?.Invoke() is not { } owner) return;
        if (Timeline.State?.FindClip(clipId) is not { } clip) return;
        await new Views.SilenceDialog(this, clipId, clip.MediaRef).ShowDialog(owner);
    }

    /// Cuts the detected silent spans out of the clip's time range across all
    /// tracks — one ripple-delete intent, so one snapshot and one undo entry.
    public void ApplySilenceRemoval(string clipId, IReadOnlyList<SilentRange> ranges) {
        if (Timeline.State?.FindClip(clipId) is not { } clip) return;
        var spans = SilenceRemoval.TimelineRanges(clip, ranges, Timeline.State.Fps);
        if (spans.Count == 0) return;
        Undo.Execute("Remove Silence", () => SilenceRemoval.Apply(Project, spans) > 0);
        Timeline.Reload();
    }

    /// Asks for a new track name and commits it as one undo step. A cancelled
    /// or unchanged prompt leaves no entry behind.
    async Task RenameTrackAsync(string trackId, string currentLabel) {
        if (DialogOwner?.Invoke() is not { } owner) return;
        string? name = await Views.MessageDialog.PromptAsync(owner, "Rename track",
            "Leave it empty to go back to the numbered label.", currentLabel);
        if (name is null || name == currentLabel) return;
        Undo.Execute("Rename Track", () => CoreApi.palmier_track_rename(Project, trackId, name) == 1);
        Timeline.Reload();
    }

    /// Called once the preview's native HWND exists and the engine is running.
    /// Dev flag: start playback as soon as the engine attaches.
    public bool AutoPlay { get; set; }

    public void AttachEngine(EngineSession session) {
        engine = session;
        session.SetProject(Project);
        session.SetSelection(Timeline.SelectedClipId);
        session.PlayingChanged += (playing, frame) => {
            if (audio != IntPtr.Zero) CoreApi.palmier_audio_set_playing(audio, playing ? 1 : 0, frame);
        };
        session.PlayheadLooped += frame => {
            if (audio != IntPtr.Zero) CoreApi.palmier_audio_seek(audio, frame);
        };
        if (AutoPlay) session.Playing = true;
        session.PlayheadAdvanced += frame => Dispatcher.UIThread.Post(() => {
            if (!Viewer.ShowingSource) {
                Timeline.PlayheadFrame = frame;
                return;
            }
            // Mirror the engine's own position back without seeking it again.
            followingEngine = true;
            Viewer.SourceFrame = frame;
            followingEngine = false;
        });
        Transport.Attach(session, Timeline, Viewer);
        SyncEngineTotalFrames();
    }

    /// Direct manipulation in the preview: click a clip to select it, drag to
    /// move it, drag a handle to scale it, drag just outside the frame (or hold
    /// Alt) to rotate. The engine draws the frame and handles this hit-tests —
    /// see `SelectionOverlay` — so the pointer grabs what the eye sees.
    ///
    /// The gesture writes straight to the core for live feedback, then rewinds
    /// and commits once on release, so one drag is one undo step.
    public void AttachPreviewInput(Views.PreviewInput input) {
        var grip = PreviewGesture.Grip.None;
        ClipTransform? original = null;
        string? gestureClipId = null;

        input.Hovered += at => {
            if (TransformOf(Timeline.SelectedClipId) is not { } clip) {
                input.Cursor = Views.PreviewInput.CursorShape.Arrow;
                return;
            }
            var (width, height) = input.SurfaceSize;
            input.Cursor = CursorFor(
                PreviewGesture.GripFor(clip, at.X, at.Y, at.Alt, width, height),
                clip.Rotation);
        };

        input.Pressed += at => {
            var (width, height) = input.SurfaceSize;
            // A press on the selected clip's own handles keeps that selection;
            // anywhere else it picks whatever clip is under the pointer.
            var selected = TransformOf(Timeline.SelectedClipId);
            grip = selected is { } current
                ? PreviewGesture.GripFor(current, at.X, at.Y, at.Alt, width, height)
                : PreviewGesture.Grip.None;
            if (grip is PreviewGesture.Grip.None or PreviewGesture.Grip.Move) {
                string? hit = ClipUnderPreview(at.X, at.Y);
                if (hit != Timeline.SelectedClipId) {
                    Timeline.SelectOnly(hit);
                    selected = TransformOf(hit);
                    grip = selected is null ? PreviewGesture.Grip.None : PreviewGesture.Grip.Move;
                }
            }
            gestureClipId = grip == PreviewGesture.Grip.None ? null : Timeline.SelectedClipId;
            original = gestureClipId is null ? null : selected;
        };

        input.Dragging += drag => {
            if (original is not { } start || gestureClipId is not { } id) return;
            SetTransform(id, PreviewGesture.Apply(
                start, grip, drag.FromX, drag.FromY, drag.X, drag.Y, drag.Shift, drag.Control));
        };

        input.Cancelled += () => {
            // Put the clip back exactly where the drag found it.
            if (original is { } start && gestureClipId is { } id) SetTransform(id, start);
            EndPreviewGesture(ref original, ref gestureClipId);
        };

        input.Dropped += drag => {
            if (original is not { } start || gestureClipId is not { } id) {
                EndPreviewGesture(ref original, ref gestureClipId);
                return;
            }
            var end = PreviewGesture.Apply(start, grip, drag.FromX, drag.FromY,
                                           drag.X, drag.Y, drag.Shift, drag.Control);
            // Rewind, then commit the whole gesture as one step so undo returns
            // to where the drag started, not to a mid-drag frame. A drag that
            // changed nothing must not leave an undo entry behind.
            SetTransform(id, start);
            if (end != start) {
                Undo.Execute(grip switch {
                    PreviewGesture.Grip.Rotate => "Rotate Clip",
                    PreviewGesture.Grip.Move => "Move Clip in Frame",
                    _ => "Scale Clip",
                }, () => SetTransform(id, end));
            }
            EndPreviewGesture(ref original, ref gestureClipId);
            Timeline.Reload();
            Inspector.Refresh();
        };
    }

    void EndPreviewGesture(ref ClipTransform? original, ref string? clipId) {
        original = null;
        clipId = null;
    }

    /// The transform of a clip in the current snapshot, or null if it is gone.
    ClipTransform? TransformOf(string? clipId) {
        if (clipId is null || Timeline.State?.FindClip(clipId) is not { } clip) return null;
        return new ClipTransform(clip.Transform.CenterX, clip.Transform.CenterY,
                                 clip.Transform.Width, clip.Transform.Height,
                                 clip.Transform.Rotation);
    }

    /// The topmost visible clip covering (x, y) at the playhead. Track order
    /// matches the compositor's: index 0 draws last, so it wins.
    string? ClipUnderPreview(double x, double y) {
        if (Timeline.State is not { } state) return null;
        int frame = Timeline.PlayheadFrame;
        foreach (var track in state.Tracks) {
            if (track.Type != "video") continue;
            foreach (var clip in track.Clips) {
                if (frame < clip.StartFrame || frame >= clip.EndFrame) continue;
                if (TransformOf(clip.Id) is { } t && PreviewGesture.Contains(t, x, y))
                    return clip.Id;
            }
        }
        return null;
    }

    /// The handle's cursor, turned with the clip so a rotated corner still
    /// points along the diagonal it actually resizes.
    static Views.PreviewInput.CursorShape CursorFor(PreviewGesture.Grip grip, double rotation) {
        var shape = grip switch {
            PreviewGesture.Grip.Move => Views.PreviewInput.CursorShape.Move,
            PreviewGesture.Grip.Rotate => Views.PreviewInput.CursorShape.Rotate,
            PreviewGesture.Grip.ScaleN or PreviewGesture.Grip.ScaleS =>
                Views.PreviewInput.CursorShape.SizeNS,
            PreviewGesture.Grip.ScaleE or PreviewGesture.Grip.ScaleW =>
                Views.PreviewInput.CursorShape.SizeWE,
            PreviewGesture.Grip.ScaleNW or PreviewGesture.Grip.ScaleSE =>
                Views.PreviewInput.CursorShape.SizeNWSE,
            PreviewGesture.Grip.ScaleNE or PreviewGesture.Grip.ScaleSW =>
                Views.PreviewInput.CursorShape.SizeNESW,
            _ => Views.PreviewInput.CursorShape.Arrow,
        };
        return Rotated(shape, rotation);
    }

    /// Steps a resize cursor round by the nearest 45°, so the arrow keeps
    /// pointing at the edge it moves.
    static Views.PreviewInput.CursorShape Rotated(Views.PreviewInput.CursorShape shape, double rotation) {
        var wheel = new[] {
            Views.PreviewInput.CursorShape.SizeNS, Views.PreviewInput.CursorShape.SizeNESW,
            Views.PreviewInput.CursorShape.SizeWE, Views.PreviewInput.CursorShape.SizeNWSE,
        };
        int index = Array.IndexOf(wheel, shape);
        if (index < 0) return shape;
        int steps = (int)Math.Round(((rotation % 180) + 180) % 180 / 45);
        return wheel[(index + steps) % wheel.Length];
    }

    bool SetTransform(string clipId, ClipTransform t) =>
        CoreApi.palmier_clip_set_transform(
            Project, clipId, t.CenterX, t.CenterY, t.Width, t.Height, t.Rotation) == 1;

    void SyncEngineTotalFrames() {
        if (engine is not { } e) return;
        e.TotalFrames = Math.Max(1, Viewer.ShowingSource ? Viewer.SourceTotalFrames : Timeline.TotalFrames);
    }

    void SeekEngine(int frame) {
        if (engine is { } e) e.PlayheadFrame = frame;
        if (audio != IntPtr.Zero) CoreApi.palmier_audio_seek(audio, frame);
    }

    /// Switching viewer tabs retargets the engine's length and position and
    /// resyncs the mixer against the new preview source.
    void OnViewerChanged() {
        if (engine is { } e) e.Playing = false;
        SyncEngineTotalFrames();
        SeekEngine(Viewer.ShowingSource ? Viewer.SourceFrame : Timeline.PlayheadFrame);
        if (audio != IntPtr.Zero) CoreApi.palmier_audio_sync(audio);
    }

    void OnAddToTimeline(MediaItemViewModel item) {
        int frames = TimelineViewModel.TimelineFramesFor(item);
        Undo.Execute("Add Clip", () => Timeline.AddClip(item.Path, frames) is not null);
    }

    void OnBlade(string clipId, int frame) =>
        Undo.Execute("Split Clip", () => Timeline.SplitClip(clipId, frame) is not null);

    public TransportViewModel Transport { get; } = new();

    /// Adds a 4-second text clip at the playhead (timeline toolbar T).
    public void AddTextClipAtPlayhead() {
        int start = Timeline.PlayheadFrame;
        Undo.Execute("Add Text", () =>
            CoreApi.AddTextClip(Project, "Text", start, TimelineViewModel.TimelineFps * 4) is not null);
        Timeline.Reload();
    }

    public void TogglePlayback() => Transport.PlayPauseCommand.Execute(null);

    /// Clip clipboard: a serialized payload, not ids — deleting or editing
    /// the originals cannot corrupt a later paste.
    string? clipClipboard;

    public void CopySelection() {
        if (Timeline.SelectedClipIds.Count == 0) return;
        clipClipboard = CoreApi.CopyClips(Project, Timeline.SelectedClipIds) ?? clipClipboard;
    }

    /// Cut = copy + delete as one undoable intent.
    public void CutSelection() {
        if (Timeline.SelectedClipIds.Count == 0) return;
        var ids = Timeline.SelectedClipIds.ToList();
        string? payload = CoreApi.CopyClips(Project, ids);
        if (payload is null) return;
        clipClipboard = payload;
        Undo.Execute(ids.Count > 1 ? "Cut Clips" : "Cut Clip",
            () => ids.Count(id => CoreApi.palmier_timeline_remove_clip(Project, id) == 1) > 0);
        Timeline.Reload();
    }

    /// Pastes with the payload's earliest clip at the playhead. Atomic in the
    /// core: an overlap or a missing track refuses the whole paste, so a
    /// failed paste changes nothing and adds no undo entry.
    public void PasteAtPlayhead() {
        if (clipClipboard is not { } payload) return;
        int frame = Timeline.PlayheadFrame;
        Undo.Execute("Paste", () => CoreApi.palmier_timeline_paste(Project, payload, frame) > 0);
        Timeline.Reload();
    }

    /// JKL shuttle. L steps 1→2→4→8, J the same in reverse, K stops; pressing
    /// against the current direction drops back toward 1 before reversing,
    /// the way editors expect to feather speed. Audio follows only at 1×
    /// forward — the mixer has no varispeed, and silence beats drift.
    public void Shuttle(int direction) {
        if (engine is not { } e) return;
        if (direction == 0) {
            e.Playing = false;
            return;
        }
        int current = e.Playing ? e.Rate : 0;
        int next = Math.Sign(current) == direction
            ? Math.Clamp(current * 2, -8, 8)
            : direction;
        e.Rate = next;
        if (audio != IntPtr.Zero)
            CoreApi.palmier_audio_set_playing(audio, next == 1 ? 1 : 0, e.PlayheadFrame);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    void PerformUndo() => Undo.Undo();
    bool CanUndo => Undo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    void PerformRedo() => Undo.Redo();
    bool CanRedo => Undo.CanRedo;

    public void Dispose() {
        if (agentHandle != IntPtr.Zero) {
            CoreApi.palmier_agent_destroy(agentHandle);
            agentHandle = IntPtr.Zero;
        }
        if (audio != IntPtr.Zero) {
            CoreApi.palmier_audio_destroy(audio);
            audio = IntPtr.Zero;
        }
        if (Project != IntPtr.Zero) {
            CoreApi.palmier_project_destroy(Project);
            Project = IntPtr.Zero;
        }
    }
}
