namespace PalmierShell.Core.Generation;

public enum GenerationState { Queued, Running, Succeeded, Failed }

/// How an endpoint takes the stills a transition travels between. The two
/// shapes are not interchangeable: one has dedicated first/last fields, the
/// other takes an array the prompt addresses by name.
public enum FrameInput {
    /// Text only. Attached stills cannot be sent at all.
    None,
    /// Dedicated fields for the starting and ending frame.
    FirstLast,
    /// A starting-frame field only; an attached end frame cannot be sent.
    FirstOnly,
    /// A reference array, addressed in the prompt as [Image1], [Image2].
    References,
}

/// One video model a provider exposes. `Durations` are the clip lengths the
/// model accepts, in seconds.
public sealed record GenerationModel(string Id, string Name, int[] Durations) {
    /// The manifest's capability labels. The stills labels are the contract
    /// with the request builders: "firstLastFrame" (dedicated first/last
    /// fields), "firstFrame" (an opening frame only), "references" (an array
    /// the prompt addresses as [Image1]…). A model with none of them takes
    /// text only — a schema we have not read must not be sent fields it may
    /// reject.
    public string[] Capabilities { get; init; } = [];

    /// The model family the request builders branch on when an id convention
    /// is not enough ("flux" names its fields nothing like seedance or kling).
    public string? Family { get; init; }

    /// Kept in the manifest (and priced) but not offered in the picker — e.g.
    /// the extend workflow until the composer's Enhance affordance lands.
    public bool Hidden { get; init; }

    /// How this endpoint takes reference stills, from its capabilities.
    public FrameInput Frames =>
        Capabilities.Contains("firstLastFrame", StringComparer.OrdinalIgnoreCase) ? FrameInput.FirstLast :
        Capabilities.Contains("firstFrame", StringComparer.OrdinalIgnoreCase) ? FrameInput.FirstOnly :
        Capabilities.Contains("references", StringComparer.OrdinalIgnoreCase) ? FrameInput.References :
        FrameInput.None;

    public bool AcceptsFrames => Frames != FrameInput.None;

    /// Output resolutions the endpoint accepts; the first is its default.
    public string[] Resolutions { get; init; } = ["720p"];

    /// The endpoint synthesises audio — dialogue, effects, music — and its
    /// schema default is on. A generated clip lands in a timeline that already
    /// has its own sound, so this is switched off explicitly for any endpoint
    /// that offers the choice. Leaving it defaulted meant paying for a track
    /// nobody asked for on every run.
    public bool SynthesisesAudio { get; init; }

    /// Reference media the endpoint accepts alongside the prompt, addressed
    /// in the prompt as [Image1]… / [Video1]…. Zero for models whose schemas
    /// declare none — sending them anyway fails the request.
    public int MaxReferenceImages { get; init; }
    public int MaxReferenceVideos { get; init; }

    /// The schema forbids combining first/last frames with reference media;
    /// the composer refuses that mix instead of paying to find out.
    public bool FramesAndReferencesExclusive { get; init; }

    public bool AcceptsReferences => MaxReferenceImages > 0 || MaxReferenceVideos > 0;
}

/// `FirstFrame`/`LastFrame` are local PNG paths the model should travel
/// between — the mechanism behind a generated transition.
public sealed record GenerationRequest(string Prompt, string Model, int Seconds) {
    public string? FirstFrame { get; init; }
    public string? LastFrame { get; init; }
    public string Resolution { get; init; } = "720p";
    /// For endpoints with a dedicated negative field; ignored elsewhere.
    public string? NegativePrompt { get; init; }
    /// A fast low-cost preview pass. Sent only to models that declare the
    /// "draft" capability; everything else ignores it.
    public bool Draft { get; init; }
    /// Local paths of reference media, in the order the prompt names them:
    /// [Image1] is ReferenceImages[0], [Video1] is ReferenceVideos[0].
    public IReadOnlyList<string> ReferenceImages { get; init; } = [];
    public IReadOnlyList<string> ReferenceVideos { get; init; } = [];
}

/// Terminal states carry either `VideoUrl` (Succeeded) or `Error` (Failed).
/// `Progress` is the provider's own completion figure (0–100) when its
/// job logs expose one; null means the provider reports none.
public sealed record GenerationStatus(GenerationState State, string? VideoUrl = null, string? Error = null) {
    public int? Progress { get; init; }
}

/// A video-generation backend. Submit returns a provider job id; the caller
/// polls until the state is terminal, then downloads `VideoUrl`.
public interface IGenerationProvider {
    string Id { get; }
    string Name { get; }
    /// Where to get an API key, shown in settings.
    string KeyHint { get; }
    IReadOnlyList<GenerationModel> Models { get; }

    Task<string> SubmitAsync(GenerationRequest request, string apiKey, CancellationToken ct);
    Task<GenerationStatus> PollAsync(string jobId, string apiKey, CancellationToken ct);
}

public static class GenerationProviders {
    public static IReadOnlyList<IGenerationProvider> All { get; } =
        [new ReplicateProvider(), new FalProvider()];

    public static IGenerationProvider? ById(string id) =>
        All.FirstOrDefault(p => p.Id == id);
}
