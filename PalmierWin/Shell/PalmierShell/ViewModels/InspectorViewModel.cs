using CommunityToolkit.Mvvm.ComponentModel;
using PalmierShell.Core;
using PalmierShell.Core.Generation;

namespace PalmierShell.ViewModels;

/// Inspector for the selected clip: transform, speed, and levels. Field
/// edits commit through the undo stack; fields refresh from every snapshot
/// reload so they always mirror core state.
public sealed partial class InspectorViewModel : ObservableObject {
    readonly IntPtr project;
    readonly TimelineViewModel timeline;
    readonly MediaPanelViewModel media;
    readonly UndoStack undo;
    bool refreshing;

    [ObservableProperty] bool hasMediaSelection;
    [ObservableProperty] bool nothingSelected = true;
    [ObservableProperty] string projectDuration = "00:00:00:00";
    [ObservableProperty] string projectResolution = "";
    [ObservableProperty] string projectFrameRate = "";
    [ObservableProperty] string projectAspect = "";
    [ObservableProperty] string mediaName = "";
    [ObservableProperty] string mediaPath = "";
    [ObservableProperty] string mediaResolution = "";
    [ObservableProperty] string mediaFps = "";
    [ObservableProperty] string mediaDuration = "";

    [ObservableProperty] bool hasSelection;
    [ObservableProperty] string clipName = "";
    [ObservableProperty] double centerX;
    [ObservableProperty] double centerY;
    [ObservableProperty] double scaleWidth;
    [ObservableProperty] double scaleHeight;
    [ObservableProperty] double rotation;
    [ObservableProperty] double opacity;
    [ObservableProperty] double speed;
    [ObservableProperty] double volumeDb;
    [ObservableProperty] double fadeInSeconds;
    [ObservableProperty] double fadeOutSeconds;
    [ObservableProperty] bool isTextClip;
    [ObservableProperty] string textContent = "";

    // Adjust: the engine's grading effects, one field per parameter. Zero (or
    // an empty LUT path) means "effect off" — committing defaults removes the
    // stack entry so the effect list never fills with no-ops.
    [ObservableProperty] bool isVideoClip;
    [ObservableProperty] double blacks;
    [ObservableProperty] double whites;
    [ObservableProperty] double highlights;
    [ObservableProperty] double shadows;
    [ObservableProperty] double clarity;
    [ObservableProperty] double dehaze;
    [ObservableProperty] double vignetteAmount;
    [ObservableProperty] double grainAmount;
    [ObservableProperty] double glowIntensity;
    [ObservableProperty] string lutPath = "";
    [ObservableProperty] double lutIntensity = 1;

    // Provenance for generated media: what made it, and from which stills.
    [ObservableProperty] bool isGenerated;
    [ObservableProperty] string generationModel = "";
    [ObservableProperty] string generationProvider = "";
    [ObservableProperty] string generationPrompt = "";
    [ObservableProperty] Avalonia.Media.Imaging.Bitmap? generationFirstFrame;
    [ObservableProperty] Avalonia.Media.Imaging.Bitmap? generationLastFrame;

    public InspectorViewModel(IntPtr project, TimelineViewModel timeline,
                              MediaPanelViewModel media, UndoStack undo) {
        this.project = project;
        this.timeline = timeline;
        this.media = media;
        this.undo = undo;
        timeline.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(TimelineViewModel.SelectedClipId)) Refresh();
        };
        media.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(MediaPanelViewModel.SelectedItem)) Refresh();
        };
        timeline.StateReloaded += Refresh;
        Refresh();
    }

    /// Re-reads core state into the fields. Public so a preview drag can pull
    /// the inspector back in step after committing.
    public void Refresh() {
        refreshing = true;
        try {
            var clip = timeline.SelectedClip;
            HasSelection = clip is not null;
            // Library details show when a media item is selected and no clip is.
            var item = media.SelectedItem;
            HasMediaSelection = clip is null && item is not null;
            NothingSelected = clip is null && item is null;
            RefreshProjectSummary();
            if (HasMediaSelection && item is not null) {
                MediaName = item.Name;
                MediaPath = item.Path;
                MediaResolution = $"{item.Width} × {item.Height}";
                MediaFps = $"{item.Fps:0.##} fps";
                MediaDuration = item.DurationText;
            }
            ShowProvenance(clip?.MediaRef ?? (HasMediaSelection ? item?.Path : null));
            if (clip is null) {
                ClipName = "";
                return;
            }
            ClipName = Path.GetFileNameWithoutExtension(clip.MediaRef);
            // Four decimals of a normalised coordinate is finer than a pixel at
            // 4K; a preview drag otherwise fills these fields with float noise.
            CenterX = Round(clip.Transform.CenterX);
            CenterY = Round(clip.Transform.CenterY);
            ScaleWidth = Round(clip.Transform.Width);
            ScaleHeight = Round(clip.Transform.Height);
            Rotation = Math.Round(clip.Transform.Rotation, 2);
            Opacity = clip.Opacity;
            Speed = clip.Speed;
            VolumeDb = clip.Volume <= 0 ? -96 : Math.Round(20 * Math.Log10(clip.Volume), 1);
            FadeInSeconds = Math.Round((double)clip.FadeInFrames / TimelineViewModel.TimelineFps, 2);
            FadeOutSeconds = Math.Round((double)clip.FadeOutFrames / TimelineViewModel.TimelineFps, 2);
            IsTextClip = clip.MediaType == "text";
            TextContent = clip.TextContent ?? "";
            if (IsTextClip) ClipName = "Text";

            IsVideoClip = clip.MediaType == "video";
            Blacks = clip.EffectNumber("color.blacksWhites", "blacks", 0);
            Whites = clip.EffectNumber("color.blacksWhites", "whites", 0);
            Highlights = clip.EffectNumber("color.highlightsShadows", "highlights", 0);
            Shadows = clip.EffectNumber("color.highlightsShadows", "shadows", 0);
            Clarity = clip.EffectNumber("detail.clarity", "clarity", 0);
            Dehaze = clip.EffectNumber("detail.clarity", "dehaze", 0);
            VignetteAmount = clip.EffectNumber("stylize.vignette", "amount", 0);
            GrainAmount = clip.EffectNumber("stylize.grain", "amount", 0);
            GlowIntensity = clip.EffectNumber("stylize.glow", "intensity", 0);
            LutPath = clip.EffectOf("color.lut")?.Text("path") ?? "";
            LutIntensity = clip.EffectNumber("color.lut", "intensity", 1);
        } finally {
            refreshing = false;
        }
    }

    /// Reads the generation sidecar, if the selected media has one.
    void ShowProvenance(string? mediaPath) {
        var record = mediaPath is null ? null : GenerationRecord.Read(mediaPath);
        IsGenerated = record is not null;
        if (record is null) {
            GenerationFirstFrame = GenerationLastFrame = null;
            return;
        }
        GenerationProvider = record.Provider;
        GenerationModel = record.Model;
        GenerationPrompt = record.Prompt;
        GenerationFirstFrame = LoadStill(record.FirstFrame);
        GenerationLastFrame = LoadStill(record.LastFrame);
    }

    static Avalonia.Media.Imaging.Bitmap? LoadStill(string? path) {
        if (path is null || !File.Exists(path)) return null;
        try {
            return new Avalonia.Media.Imaging.Bitmap(path);
        } catch {
            return null;
        }
    }

    /// Project/sequence summary shown when nothing is selected.
    void RefreshProjectSummary() {
        ProjectDuration = Timecode.Format(timeline.TotalFrames, TimelineViewModel.TimelineFps);
        if (timeline.State is not { } state) return;
        ProjectResolution = $"{state.Width} × {state.Height}";
        ProjectFrameRate = $"{state.Fps:0.##} fps";
        int divisor = Gcd(state.Width, state.Height);
        ProjectAspect = divisor > 0 ? $"{state.Width / divisor}:{state.Height / divisor}" : "";
    }

    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    static double Round(double value) => Math.Round(value, 4);

    void CommitTransform() => Commit("Transform", id =>
        CoreApi.palmier_clip_set_transform(project, id, CenterX, CenterY, ScaleWidth, ScaleHeight, Rotation) == 1);

    partial void OnCenterXChanged(double value) => CommitTransform();
    partial void OnCenterYChanged(double value) => CommitTransform();
    partial void OnScaleWidthChanged(double value) => CommitTransform();
    partial void OnScaleHeightChanged(double value) => CommitTransform();
    partial void OnRotationChanged(double value) => CommitTransform();

    partial void OnOpacityChanged(double value) => Commit("Opacity", id =>
        CoreApi.palmier_clip_set_opacity(project, id, value) == 1);

    partial void OnSpeedChanged(double value) => Commit("Speed", id =>
        CoreApi.palmier_clip_set_speed(project, id, value) == 1);

    partial void OnVolumeDbChanged(double value) => Commit("Volume", id =>
        CoreApi.palmier_clip_set_volume_db(project, id, value) == 1);

    void CommitFades() => Commit("Fade", id => CoreApi.palmier_clip_set_fades(project, id,
        (int)Math.Round(Math.Max(0, FadeInSeconds) * TimelineViewModel.TimelineFps),
        (int)Math.Round(Math.Max(0, FadeOutSeconds) * TimelineViewModel.TimelineFps)) == 1);

    partial void OnFadeInSecondsChanged(double value) => CommitFades();
    partial void OnFadeOutSecondsChanged(double value) => CommitFades();

    partial void OnTextContentChanged(string value) => Commit("Edit Text", id =>
        CoreApi.palmier_clip_set_text(project, id, value) == 1);

    /// Records a keyframe at the playhead for one property, from the field's
    /// current value. `property` is the core's own key, so the diamond next to
    /// a field and the renderer agree on what is animated.
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void AddKeyframe(string? property) {
        int frame = timeline.PlayheadFrame;
        switch (property) {
            case "position":
                Commit("Position Keyframe", id => CoreApi.palmier_clip_add_keyframe(
                    project, id, "position", frame,
                    CenterX - ScaleWidth / 2, CenterY - ScaleHeight / 2) == 1);
                break;
            case "scale":
                Commit("Scale Keyframe", id => CoreApi.palmier_clip_add_keyframe(
                    project, id, "scale", frame, ScaleWidth, ScaleHeight) == 1);
                break;
            case "rotation":
                Commit("Rotation Keyframe", id => CoreApi.palmier_clip_add_keyframe(
                    project, id, "rotation", frame, Rotation, 0) == 1);
                break;
            case "opacity":
                Commit("Opacity Keyframe", id => CoreApi.palmier_clip_add_keyframe(
                    project, id, "opacity", frame, Opacity, 0) == 1);
                break;
            case "volume":
                Commit("Volume Keyframe", id => CoreApi.palmier_clip_add_keyframe(
                    project, id, "volume", frame, VolumeDb, 0) == 1);
                break;
        }
    }

    /// Upserts one effect, or removes it when `values` is null. Removal of an
    /// effect that is not on the clip is a quiet no-op — refreshing a default
    /// field must not spam refusals.
    void CommitEffect(string name, string type, Dictionary<string, object>? values) {
        if (refreshing || timeline.SelectedClipId is not { } clipId) return;
        if (values is null && timeline.SelectedClip?.EffectOf(type) is null) return;
        string json = values is null
            ? "{}"
            : System.Text.Json.JsonSerializer.Serialize(values);
        Commit(name, id => CoreApi.palmier_clip_set_effect(project, id, type, json) == 1);
    }

    partial void OnBlacksChanged(double v) => CommitLevels();
    partial void OnWhitesChanged(double v) => CommitLevels();
    void CommitLevels() => CommitEffect("Blacks / Whites", "color.blacksWhites",
        Blacks == 0 && Whites == 0 ? null : new() { ["blacks"] = Blacks, ["whites"] = Whites });

    partial void OnHighlightsChanged(double v) => CommitHighlights();
    partial void OnShadowsChanged(double v) => CommitHighlights();
    void CommitHighlights() => CommitEffect("Highlights / Shadows", "color.highlightsShadows",
        Highlights == 0 && Shadows == 0 ? null
            : new() { ["highlights"] = Highlights, ["shadows"] = Shadows });

    partial void OnClarityChanged(double v) => CommitClarity();
    partial void OnDehazeChanged(double v) => CommitClarity();
    void CommitClarity() => CommitEffect("Clarity", "detail.clarity",
        Clarity == 0 && Dehaze == 0 ? null : new() { ["clarity"] = Clarity, ["dehaze"] = Dehaze });

    partial void OnVignetteAmountChanged(double v) => CommitEffect("Vignette", "stylize.vignette",
        v == 0 ? null : new() { ["amount"] = v, ["midpoint"] = 0.3, ["feather"] = 0.5 });

    partial void OnGrainAmountChanged(double v) => CommitEffect("Grain", "stylize.grain",
        v == 0 ? null : new() { ["amount"] = v, ["size"] = 1.0 });

    partial void OnGlowIntensityChanged(double v) => CommitEffect("Glow", "stylize.glow",
        v == 0 ? null : new() { ["intensity"] = v, ["radius"] = 20.0, ["threshold"] = 0.6 });

    partial void OnLutPathChanged(string v) => CommitLut();
    partial void OnLutIntensityChanged(double v) => CommitLut();
    void CommitLut() => CommitEffect("LUT", "color.lut",
        string.IsNullOrWhiteSpace(LutPath) ? null
            : new() { ["path"] = LutPath, ["intensity"] = LutIntensity });

    void Commit(string name, Func<string, bool> intent) {
        if (refreshing || timeline.SelectedClipId is not { } id) return;
        if (undo.Execute(name, () => intent(id)))
            timeline.Reload();
        else
            Refresh();  // rejected edit: snap the field back to core state
    }
}
