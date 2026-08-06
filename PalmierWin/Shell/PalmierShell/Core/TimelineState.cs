using System.Text.Json;

namespace PalmierShell.Core;

/// Immutable C# mirror of the Swift timeline JSON snapshot. Field names match
/// the Codable keys in PalmierCore (camelCase, matched case-insensitively).
public sealed record TransformState(
    double CenterX, double CenterY, double Width, double Height, double Rotation);

public sealed record KeyframeState(int Frame);
public sealed record KeyframeTrackState(List<KeyframeState> Keyframes);

/// One parameter of an effect: a number or a string, mirroring EffectParam.
public sealed record EffectParamState(double? Value, string? String);

/// One entry in a clip's effect stack.
public sealed record EffectState(string Type, Dictionary<string, EffectParamState>? Params) {
    public bool Enabled { get; init; } = true;

    public double Number(string key, double fallback) =>
        Params?.TryGetValue(key, out var p) == true && p.Value is { } v ? v : fallback;

    public string? Text(string key) =>
        Params?.TryGetValue(key, out var p) == true ? p.String : null;
}

/// The style of a text clip, mirroring the Codable keys of PalmierCore's
/// TextStyle that the shell exposes (fontSize, color, alignment).
public sealed record TextColorState(double R, double G, double B, double A);

public sealed record TextStyleState(double FontSize, TextColorState? Color, string? Alignment);

public sealed record ClipState(
    string Id, string MediaRef, string MediaType,
    int StartFrame, int DurationFrames, int TrimStartFrame,
    double Speed, double Volume, double Opacity,
    int FadeInFrames, int FadeOutFrames,
    TransformState Transform, string? LinkGroupId, string? TextContent,
    KeyframeTrackState? OpacityTrack, KeyframeTrackState? PositionTrack,
    KeyframeTrackState? ScaleTrack, KeyframeTrackState? RotationTrack,
    KeyframeTrackState? VolumeTrack, List<EffectState>? Effects = null,
    TextStyleState? TextStyle = null) {

    /// The clip's value for one effect parameter, falling back when the
    /// effect or the key is absent — the shape the Adjust fields read.
    public double EffectNumber(string type, string key, double fallback) =>
        Effects?.FirstOrDefault(f => f.Type == type)?.Number(key, fallback) ?? fallback;

    public EffectState? EffectOf(string type) =>
        Effects?.FirstOrDefault(f => f.Type == type);
    public int EndFrame => StartFrame + DurationFrames;
    /// Source frames this clip consumes (timeline-fps domain).
    public int SourceFramesConsumed => (int)Math.Round(DurationFrames * Speed);

    /// Where in the source this clip is being read at a timeline frame: the
    /// head trim plus the speed-scaled local offset, in the timeline's frame
    /// domain. Mirrors `TimelineLookup.sourceFrame` on the engine side — the
    /// preview, the export and a captured still must agree on where a clip is
    /// being read, or a transition is built from frames the user never saw.
    public int SourceFrameAt(int timelineFrame) =>
        Math.Max(0, TrimStartFrame + (int)Math.Round((timelineFrame - StartFrame) * Speed));

    /// The first and last source frames this clip shows.
    public int FirstSourceFrame => SourceFrameAt(StartFrame);
    public int LastSourceFrame => SourceFrameAt(Math.Max(StartFrame, EndFrame - 1));

    public IEnumerable<KeyframeTrackState?> AllKeyframeTracks =>
        [OpacityTrack, PositionTrack, ScaleTrack, RotationTrack, VolumeTrack];

    public bool HasKeyframes => AllKeyframeTracks.Any(t => t is { Keyframes.Count: > 0 });

    /// Distinct clip-relative keyframe frames across all animated properties.
    public IEnumerable<int> KeyframeFrames =>
        AllKeyframeTracks.Where(t => t is not null)
            .SelectMany(t => t!.Keyframes).Select(k => k.Frame).Distinct().OrderBy(f => f);
}

public sealed record TrackState(string Id, string Type, bool Muted, bool Hidden, List<ClipState> Clips) {
    /// User-given name; null means the derived label (V1, A2…).
    public string? Name { get; init; }

    /// Per-track height from the model; 0 when an older file lacks the key.
    public double DisplayHeight { get; init; }

    /// Height this track renders at: the persisted height, or the per-type
    /// default when unset; clamped to the model's [32, 200].
    public double RenderHeight =>
        Math.Clamp(DisplayHeight > 0 ? DisplayHeight : Type == "audio" ? 72 : 50, 32, 200);

    /// The clips bracketing the empty span `[startFrame, endFrame)`, used to
    /// seed a generated shot with the frames it has to sit between. Either side
    /// is null at the head or tail of the track. Text clips are skipped: they
    /// have no media to read a still from.
    public (ClipState? Before, ClipState? After) ClipsAround(int startFrame, int endFrame) {
        var usable = Clips.Where(c => c.MediaType != "text").ToList();
        return (usable.Where(c => c.EndFrame <= startFrame).MaxBy(c => c.EndFrame),
                usable.Where(c => c.StartFrame >= endFrame).MinBy(c => c.StartFrame));
    }
}

public sealed record TimelineState(string Id, string Name, int Fps, int Width, int Height, List<TrackState> Tracks) {
    public int TotalFrames => Tracks.SelectMany(t => t.Clips).Select(c => c.EndFrame)
                                    .DefaultIfEmpty(0).Max();

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static TimelineState Parse(string json) =>
        JsonSerializer.Deserialize<TimelineState>(json, Options)
        ?? throw new InvalidOperationException("timeline JSON deserialized to null");

    public ClipState? FindClip(string clipId) =>
        Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == clipId);
}
