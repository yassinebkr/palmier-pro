using System.Text.Json;
using Avalonia.Platform;

namespace PalmierShell.Core.Generation;

/// The generation model list as data: bundled fallback now, remote sync later.
/// Fail-soft everywhere — a bad manifest means the bundled list, never an error.
public static class ModelManifest {
    static readonly Uri BundledUri = new("avares://PalmierShell/Assets/models.json");

    // Tolerant on purpose: the manifest is hand-maintained, so comments and
    // trailing commas are data-entry conveniences, not errors.
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// Test seam: replaces the bundled asset read. Never set in production.
    /// Overrides must stay malformed-or-identical: valid divergent payloads
    /// are unsafe under xunit's parallel test runs.
    public static Func<string>? BundledOverride;

    /// The bundled asset never changes at runtime, so it is read and parsed once.
    static readonly Lazy<Dictionary<string, IReadOnlyList<GenerationModel>>> Bundled =
        new(() => Read(ReadBundledAsset) ?? new Dictionary<string, IReadOnlyList<GenerationModel>>());

    public static IReadOnlyList<GenerationModel> For(string provider) {
        if (BundledOverride is { } source && Read(source) is { } overridden)
            return overridden.TryGetValue(provider, out var models) ? models : [];
        return Bundled.Value.TryGetValue(provider, out var bundled) ? bundled : [];
    }

    /// Standalone rather than the app's registered loader, so the manifest
    /// stays readable in a host that never started Avalonia (the test runner).
    static readonly IAssetLoader Loader = new StandardAssetLoader();

    static string? ReadBundledAsset() {
        try {
            using var stream = Loader.Open(BundledUri);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        } catch (Exception) {
            return null;
        }
    }

    /// Reads and parses the whole manifest. Null on any read or parse failure;
    /// a valid manifest that simply lacks a provider yields an empty list.
    static Dictionary<string, IReadOnlyList<GenerationModel>>? Read(Func<string?> source) {
        string? json;
        try { json = source(); } catch (Exception) { return null; }
        if (json is null) return null;
        try {
            var manifest = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
            var result = new Dictionary<string, IReadOnlyList<GenerationModel>>(StringComparer.OrdinalIgnoreCase);
            if (manifest?.Providers is null) return result;
            foreach (var (provider, entries) in manifest.Providers) {
                if (entries is null) continue;
                result[provider] = entries
                    .Where(e => e is { Id.Length: > 0, Name.Length: > 0 })
                    .Select(e => e!.ToModel())
                    .ToList();
            }
            return result;
        } catch (JsonException) {
            return null;
        }
    }

    sealed class ManifestDto {
        public Dictionary<string, List<ModelDto?>?>? Providers { get; set; }
    }

    /// Missing members take the record's own defaults: no durations is an empty
    /// list, no resolutions is 720p, no capability labels is text-only.
    sealed class ModelDto {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int[]? Durations { get; set; }
        public string[]? Capabilities { get; set; }
        public string[]? Resolutions { get; set; }
        public bool SynthesisesAudio { get; set; }
        public int MaxReferenceImages { get; set; }
        public int MaxReferenceVideos { get; set; }
        public bool FramesAndReferencesExclusive { get; set; }

        public GenerationModel ToModel() => new(Id!, Name!, Durations ?? []) {
            Capabilities = Capabilities ?? [],
            Resolutions = Resolutions ?? ["720p"],
            SynthesisesAudio = SynthesisesAudio,
            MaxReferenceImages = MaxReferenceImages,
            MaxReferenceVideos = MaxReferenceVideos,
            FramesAndReferencesExclusive = FramesAndReferencesExclusive,
        };
    }
}
