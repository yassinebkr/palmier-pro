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
    [ObservableProperty] double textFontSize = 96;
    [ObservableProperty] string textColorHex = "#FFFFFF";
    [ObservableProperty] string textAlignment = "center";

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
    [ObservableProperty] double glowRadius = 20;
    [ObservableProperty] double glowThreshold = 0.6;
    [ObservableProperty] double glowWarmth;
    [ObservableProperty] bool invertEnabled;
    [ObservableProperty] double liftR;
    [ObservableProperty] double liftG;
    [ObservableProperty] double liftB;
    [ObservableProperty] double gainR = 1;
    [ObservableProperty] double gainG = 1;
    [ObservableProperty] double gainB = 1;
    [ObservableProperty] double gammaR = 1;
    [ObservableProperty] double gammaG = 1;
    [ObservableProperty] double gammaB = 1;
    [ObservableProperty] bool chromaKeyEnabled;
    [ObservableProperty] double chromaKeyR;
    [ObservableProperty] double chromaKeyG = 1;
    [ObservableProperty] double chromaKeyB;
    [ObservableProperty] double chromaThreshold = 0.4;
    [ObservableProperty] double chromaSpill = 0.5;
    [ObservableProperty] string lutPath = "";
    [ObservableProperty] double lutIntensity = 1;

    // Readouts under the wheels: the committed triplet, or the live gesture
    // mid-drag. Not editable — the wheel is the editor.
    [ObservableProperty] string liftReadout = "0.00  0.00  0.00";
    [ObservableProperty] string gainReadout = "1.00  1.00  1.00";
    [ObservableProperty] string gammaReadout = "1.00  1.00  1.00";

    /// Fired at the end of Refresh so the wheel controls can re-place their
    /// handles from the committed params.
    public event Action? WheelsRefreshed;

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
            TextFontSize = clip.TextStyle?.FontSize ?? 96;
            TextColorHex = HexOf(clip.TextStyle?.Color);
            TextAlignment = clip.TextStyle?.Alignment ?? "center";

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
            GlowRadius = clip.EffectNumber("stylize.glow", "radius", 20);
            GlowThreshold = clip.EffectNumber("stylize.glow", "threshold", 0.6);
            GlowWarmth = clip.EffectNumber("stylize.glow", "warmth", 0);
            InvertEnabled = clip.EffectOf("stylize.invert") is not null;
            LiftR = clip.EffectNumber("color.wheels", "lift.r", 0);
            LiftG = clip.EffectNumber("color.wheels", "lift.g", 0);
            LiftB = clip.EffectNumber("color.wheels", "lift.b", 0);
            GainR = clip.EffectNumber("color.wheels", "gain.r", 1);
            GainG = clip.EffectNumber("color.wheels", "gain.g", 1);
            GainB = clip.EffectNumber("color.wheels", "gain.b", 1);
            GammaR = clip.EffectNumber("color.wheels", "gamma.r", 1);
            GammaG = clip.EffectNumber("color.wheels", "gamma.g", 1);
            GammaB = clip.EffectNumber("color.wheels", "gamma.b", 1);
            ChromaKeyEnabled = clip.EffectOf("key.chroma") is not null;
            ChromaKeyR = clip.EffectNumber("key.chroma", "keyColor.r", 0);
            ChromaKeyG = clip.EffectNumber("key.chroma", "keyColor.g", 1);
            ChromaKeyB = clip.EffectNumber("key.chroma", "keyColor.b", 0);
            ChromaThreshold = clip.EffectNumber("key.chroma", "threshold", 0.4);
            ChromaSpill = clip.EffectNumber("key.chroma", "spill", 0.5);
            LutPath = clip.EffectOf("color.lut")?.Text("path") ?? "";
            LutIntensity = clip.EffectNumber("color.lut", "intensity", 1);
            UpdateWheelReadouts();
        } finally {
            refreshing = false;
        }
        WheelsRefreshed?.Invoke();
    }

    /// The gesture state that would reproduce the committed params — the
    /// wheel controls pull this after every refresh.
    public (double X, double Y, double Offset) WheelVector(ColorWheelMath.WheelKind kind) => kind switch {
        ColorWheelMath.WheelKind.Lift => ColorWheelMath.ToVector(kind, LiftR, LiftG, LiftB),
        ColorWheelMath.WheelKind.Gain => ColorWheelMath.ToVector(kind, GainR, GainG, GainB),
        _ => ColorWheelMath.ToVector(kind, GammaR, GammaG, GammaB),
    };

    /// Live drag: only the readout moves; the commit waits for release.
    public void PreviewWheel(ColorWheelMath.WheelKind kind, double x, double y, double offset) {
        var (r, g, b) = ColorWheelMath.ToParams(kind, x, y, offset);
        SetWheelReadout(kind, r, g, b);
    }

    /// Drag end: one undo entry for the whole gesture. The assignments run
    /// under the refresh guard so their per-property hooks stay quiet and
    /// CommitWheels below is the only intent.
    public void CommitWheel(ColorWheelMath.WheelKind kind, double x, double y, double offset) {
        if (timeline.SelectedClipId is null) return;
        var (r, g, b) = ColorWheelMath.ToParams(kind, x, y, offset);
        refreshing = true;
        try {
            switch (kind) {
                case ColorWheelMath.WheelKind.Lift: LiftR = r; LiftG = g; LiftB = b; break;
                case ColorWheelMath.WheelKind.Gain: GainR = r; GainG = g; GainB = b; break;
                default: GammaR = r; GammaG = g; GammaB = b; break;
            }
        } finally {
            refreshing = false;
        }
        CommitWheels();
        SetWheelReadout(kind, r, g, b);
    }

    void UpdateWheelReadouts() {
        SetWheelReadout(ColorWheelMath.WheelKind.Lift, LiftR, LiftG, LiftB);
        SetWheelReadout(ColorWheelMath.WheelKind.Gain, GainR, GainG, GainB);
        SetWheelReadout(ColorWheelMath.WheelKind.Gamma, GammaR, GammaG, GammaB);
    }

    void SetWheelReadout(ColorWheelMath.WheelKind kind, double r, double g, double b) {
        string text = $"{r:0.00;-0.00}  {g:0.00;-0.00}  {b:0.00;-0.00}";
        switch (kind) {
            case ColorWheelMath.WheelKind.Lift: LiftReadout = text; break;
            case ColorWheelMath.WheelKind.Gain: GainReadout = text; break;
            default: GammaReadout = text; break;
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

    static string HexOf(TextColorState? color) => color is null
        ? "#FFFFFF"
        : $"#{(int)Math.Round(color.R * 255):X2}{(int)Math.Round(color.G * 255):X2}{(int)Math.Round(color.B * 255):X2}";

    static bool IsValidHexColor(string s) {
        s = s.TrimStart('#');
        return s.Length is 3 or 6 or 8 && s.All(Uri.IsHexDigit);
    }

    // Invalid halves of an in-progress edit (a hex triplet mid-typing) are
    // ignored, not refused — a refusal would snap the field back under the
    // user's cursor. The ABI validates again on commit.
    void CommitTextStyle() {
        if (refreshing || !IsTextClip) return;
        string alignment = TextAlignment.Trim().ToLowerInvariant();
        if (TextFontSize <= 0 || !IsValidHexColor(TextColorHex)
            || alignment is not ("left" or "center" or "right")) return;
        string hex = TextColorHex.StartsWith('#') ? TextColorHex : "#" + TextColorHex;
        string json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
            ["fontSize"] = TextFontSize, ["color"] = hex, ["alignment"] = alignment });
        Commit("Text Style", id => CoreApi.palmier_clip_set_text_style(project, id, json) == 1);
    }

    partial void OnTextFontSizeChanged(double v) => CommitTextStyle();
    partial void OnTextColorHexChanged(string v) => CommitTextStyle();
    partial void OnTextAlignmentChanged(string v) => CommitTextStyle();

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

    /// macOS EffectRegistry param ranges. Free-text fields can type anything;
    /// out-of-range values produce degenerate output (e.g. whites > 1.25 drives
    /// the levels kernel's white point negative → the frame goes pure white).
    static readonly Dictionary<string, (double Min, double Max)> EffectParamRanges = new() {
        ["color.blacksWhites:blacks"] = (-1, 1),
        ["color.blacksWhites:whites"] = (-1, 1),
        ["color.highlightsShadows:highlights"] = (-1, 1),
        ["color.highlightsShadows:shadows"] = (-1, 1),
        ["detail.clarity:clarity"] = (-1, 1),
        ["detail.clarity:dehaze"] = (-1, 1),
        ["stylize.vignette:amount"] = (-1, 1),
        ["stylize.vignette:midpoint"] = (0, 1),
        ["stylize.vignette:roundness"] = (-1, 1),
        ["stylize.vignette:feather"] = (0, 1),
        ["stylize.grain:amount"] = (0, 1),
        ["stylize.grain:size"] = (0.5, 4),
        ["stylize.glow:intensity"] = (0, 1),
        ["stylize.glow:radius"] = (0, 100),
        ["stylize.glow:threshold"] = (0, 1),
        ["stylize.glow:warmth"] = (0, 1),
        ["key.chroma:keyColor.r"] = (0, 1),
        ["key.chroma:keyColor.g"] = (0, 1),
        ["key.chroma:keyColor.b"] = (0, 1),
        ["key.chroma:threshold"] = (0, 1),
        ["key.chroma:spill"] = (0, 1),
    };

    /// Clamps effect params to their EffectRegistry ranges in place.
    public static void ClampEffectParams(string type, Dictionary<string, object> values) {
        foreach (var key in values.Keys.ToList())
            if (values[key] is double d && EffectParamRanges.TryGetValue($"{type}:{key}", out var range))
                values[key] = Math.Clamp(d, range.Min, range.Max);
    }

    /// Upserts one effect, or removes it when `values` is null. Removal of an
    /// effect that is not on the clip is a quiet no-op — refreshing a default
    /// field must not spam refusals.
    void CommitEffect(string name, string type, Dictionary<string, object>? values) {
        if (refreshing || timeline.SelectedClipId is not { } clipId) return;
        if (values is null && timeline.SelectedClip?.EffectOf(type) is null) return;
        if (values is not null) ClampEffectParams(type, values);
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

    partial void OnGlowIntensityChanged(double v) => CommitGlow();
    partial void OnGlowRadiusChanged(double v) => CommitGlow();
    partial void OnGlowThresholdChanged(double v) => CommitGlow();
    partial void OnGlowWarmthChanged(double v) => CommitGlow();

    void CommitGlow() => CommitEffect("Glow", "stylize.glow",
        GlowIntensity == 0 ? null : new() {
            ["intensity"] = GlowIntensity,
            ["radius"] = GlowRadius,
            ["threshold"] = GlowThreshold,
            ["warmth"] = GlowWarmth,
        });

    // "amount" is a placeholder — the ABI removes effects with empty params,
    // and invert has no real parameters.
    partial void OnInvertEnabledChanged(bool v) => CommitEffect("Invert", "stylize.invert",
        v ? new() { ["amount"] = 1.0 } : null);

    partial void OnLiftRChanged(double v) => CommitWheels();
    partial void OnLiftGChanged(double v) => CommitWheels();
    partial void OnLiftBChanged(double v) => CommitWheels();
    partial void OnGainRChanged(double v) => CommitWheels();
    partial void OnGainGChanged(double v) => CommitWheels();
    partial void OnGainBChanged(double v) => CommitWheels();
    partial void OnGammaRChanged(double v) => CommitWheels();
    partial void OnGammaGChanged(double v) => CommitWheels();
    partial void OnGammaBChanged(double v) => CommitWheels();
    void CommitWheels() => CommitEffect("Color Wheels", "color.wheels",
        LiftR == 0 && LiftG == 0 && LiftB == 0
            && GainR == 1 && GainG == 1 && GainB == 1
            && GammaR == 1 && GammaG == 1 && GammaB == 1 ? null
            : new() {
                ["lift.r"] = LiftR, ["lift.g"] = LiftG, ["lift.b"] = LiftB,
                ["gain.r"] = GainR, ["gain.g"] = GainG, ["gain.b"] = GainB,
                ["gamma.r"] = GammaR, ["gamma.g"] = GammaG, ["gamma.b"] = GammaB });

    partial void OnChromaKeyEnabledChanged(bool v) => CommitChromaKey();
    partial void OnChromaKeyRChanged(double v) => CommitChromaKey();
    partial void OnChromaKeyGChanged(double v) => CommitChromaKey();
    partial void OnChromaKeyBChanged(double v) => CommitChromaKey();
    partial void OnChromaThresholdChanged(double v) => CommitChromaKey();
    partial void OnChromaSpillChanged(double v) => CommitChromaKey();
    void CommitChromaKey() => CommitEffect("Chroma Key", "key.chroma",
        !ChromaKeyEnabled ? null
            : new() {
                ["keyColor.r"] = ChromaKeyR, ["keyColor.g"] = ChromaKeyG, ["keyColor.b"] = ChromaKeyB,
                ["threshold"] = ChromaThreshold, ["spill"] = ChromaSpill });

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
