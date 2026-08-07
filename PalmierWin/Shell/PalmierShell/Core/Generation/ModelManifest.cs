using System.Net.Http;
using System.Text.Json;
using Avalonia.Platform;

namespace PalmierShell.Core.Generation;

/// The generation model list as data: a bundled asset, replaced at runtime by
/// a synced remote copy cached under %APPDATA%. Fail-soft everywhere — a bad
/// manifest means the bundled list, never an error.
public static class ModelManifest {
    static readonly Uri BundledUri = new("avares://PalmierShell/Assets/models.json");

    const string ManifestUrl =
        "https://raw.githubusercontent.com/yassinebkr/palmierWin/main/models.json";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

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

    /// Test seam: replaces the remote fetch. Never set in production code.
    public static Func<CancellationToken, Task<string?>>? FetchOverride;

    /// Test seam: redirects the cache directory. Never set in production code.
    public static string? CacheDirectoryOverride;

    static string CacheDir => CacheDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PalmierPro");

    static string CachePath => Path.Combine(CacheDir, "models-cache.json");

    /// The bundled asset never changes at runtime, so it is read and parsed once.
    static readonly Lazy<Dictionary<string, IReadOnlyList<GenerationModel>>> Bundled =
        new(() => Read(ReadBundledAsset) ?? new Dictionary<string, IReadOnlyList<GenerationModel>>());

    /// The synced manifest, once fetched and validated. Read from the cache
    /// file on first use; SyncAsync swaps it wholesale. An unreadable or
    /// unusable cache parses to null — the bundled list, same as no cache.
    static volatile Lazy<Dictionary<string, IReadOnlyList<GenerationModel>>?> Synced =
        new(ReadCacheFile);

    public static IReadOnlyList<GenerationModel> For(string provider) {
        if (BundledOverride is { } source && Read(source) is { } overridden)
            return overridden.TryGetValue(provider, out var models) ? models : [];
        if (Synced.Value is { } synced)
            return synced.TryGetValue(provider, out var cached) ? cached : [];
        return Bundled.Value.TryGetValue(provider, out var bundled) ? bundled : [];
    }

    /// What a sync changed, in display names: models the new manifest adds
    /// and ones it drops, across every provider either side lists.
    public sealed record ManifestSyncReport(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

    /// Fetches the remote manifest, validates it (parses, version == 1, at
    /// least one provider non-empty), atomically replaces the cache, reloads,
    /// and reports what changed. Null on any failure — the current list is
    /// then left untouched.
    public static async Task<ManifestSyncReport?> SyncAsync(CancellationToken ct = default) {
        string? json;
        try {
            json = FetchOverride is { } fetch
                ? await fetch(ct).ConfigureAwait(false)
                : await FetchManifestAsync(ct).ConfigureAwait(false);
        } catch {
            return null;  // a throwing seam must not escape the never-throw contract
        }
        if (json is null || ReadUsable(json) is not { } next) return null;
        // Diffed against the list in force before the cache file is replaced,
        // so the report describes the swap the panel is about to show.
        var report = Diff(Current(), next);
        WriteCacheAtomic(json);
        Synced = new Lazy<Dictionary<string, IReadOnlyList<GenerationModel>>?>(() => next);
        return report;
    }

    /// Test seam: drops the in-memory synced manifest so the next read
    /// re-resolves from the cache file. Never called in production code.
    public static void ResetSynced() =>
        Synced = new Lazy<Dictionary<string, IReadOnlyList<GenerationModel>>?>(ReadCacheFile);

    /// The map For reads from, whole: the override in tests, else the synced
    /// manifest, else the bundled one.
    static IReadOnlyDictionary<string, IReadOnlyList<GenerationModel>> Current() {
        if (BundledOverride is { } source && Read(source) is { } overridden) return overridden;
        return Synced.Value ?? Bundled.Value;
    }

    /// Names gained and lost between two manifests, keyed on model id — a
    /// rename reads as a removal plus an addition.
    static ManifestSyncReport Diff(
        IReadOnlyDictionary<string, IReadOnlyList<GenerationModel>> previous,
        IReadOnlyDictionary<string, IReadOnlyList<GenerationModel>> next) {
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var provider in previous.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase)) {
            var oldIds = Ids(previous, provider);
            var newIds = Ids(next, provider);
            added.AddRange(ModelsIn(next, provider).Where(m => !oldIds.Contains(m.Id)).Select(m => m.Name));
            removed.AddRange(ModelsIn(previous, provider).Where(m => !newIds.Contains(m.Id)).Select(m => m.Name));
        }
        return new ManifestSyncReport(added, removed);

        static HashSet<string> Ids(IReadOnlyDictionary<string, IReadOnlyList<GenerationModel>> map,
                                   string provider) =>
            new(ModelsIn(map, provider).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        static IReadOnlyList<GenerationModel> ModelsIn(
            IReadOnlyDictionary<string, IReadOnlyList<GenerationModel>> map, string provider) =>
            map.TryGetValue(provider, out var models) ? models : [];
    }

    static async Task<string?> FetchManifestAsync(CancellationToken ct) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.UserAgent.ParseAdd($"PalmierWin/{UpdateChecker.CurrentVersion}");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        } catch {
            return null;
        }
    }

    static Dictionary<string, IReadOnlyList<GenerationModel>>? ReadCacheFile() {
        try {
            return ReadUsable(File.ReadAllText(CachePath));
        } catch {
            return null;
        }
    }

    /// Temp-then-move, so a crash mid-write never leaves half a manifest
    /// where the next launch reads it.
    static void WriteCacheAtomic(string json) {
        try {
            Directory.CreateDirectory(CacheDir);
            string tmp = CachePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, true);
        } catch {
            // A cache that cannot persist only costs a re-fetch next launch;
            // the in-memory swap still stands for this session.
        }
    }

    /// The bar fetched or cached content must clear before it may replace the
    /// bundled list: it parses, it is the schema version this build
    /// understands, and it lists at least one usable model. Anything less
    /// leaves the current list untouched.
    static Dictionary<string, IReadOnlyList<GenerationModel>>? ReadUsable(string json) {
        if (Parse(json) is not { Version: 1 } manifest) return null;
        var map = Map(manifest);
        return map is not null && map.Values.Any(list => list.Count > 0) ? map : null;
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
        return json is null ? null : Map(Parse(json));
    }

    static ManifestDto? Parse(string json) {
        try {
            return JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
        } catch (JsonException) {
            return null;
        }
    }

    /// Null in, null out: a parse failure stays a read failure so callers
    /// fall back instead of seeing an empty manifest.
    static Dictionary<string, IReadOnlyList<GenerationModel>>? Map(ManifestDto? manifest) {
        if (manifest is null) return null;
        var result = new Dictionary<string, IReadOnlyList<GenerationModel>>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Providers is null) return result;
        foreach (var (provider, entries) in manifest.Providers) {
            if (entries is null) continue;
            result[provider] = entries
                .Where(e => e is { Id.Length: > 0, Name.Length: > 0 })
                .Select(e => e!.ToModel())
                .Where(m => !m.Hidden)
                .ToList();
        }
        return result;
    }

    sealed class ManifestDto {
        public int Version { get; set; }
        public Dictionary<string, List<ModelDto?>?>? Providers { get; set; }
    }

    /// Missing members take the record's own defaults: no durations is an empty
    /// list, no resolutions is 720p, no capability labels is text-only.
    sealed class ModelDto {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int[]? Durations { get; set; }
        public string[]? Capabilities { get; set; }
        public string? Family { get; set; }
        public string[]? Resolutions { get; set; }
        public bool SynthesisesAudio { get; set; }
        public int MaxReferenceImages { get; set; }
        public int MaxReferenceVideos { get; set; }
        public bool FramesAndReferencesExclusive { get; set; }
        public bool Hidden { get; set; }

        public GenerationModel ToModel() => new(Id!, Name!, Durations ?? []) {
            Capabilities = Capabilities ?? [],
            Family = Family,
            Resolutions = Resolutions ?? ["720p"],
            SynthesisesAudio = SynthesisesAudio,
            MaxReferenceImages = MaxReferenceImages,
            MaxReferenceVideos = MaxReferenceVideos,
            FramesAndReferencesExclusive = FramesAndReferencesExclusive,
            Hidden = Hidden,
        };
    }
}
