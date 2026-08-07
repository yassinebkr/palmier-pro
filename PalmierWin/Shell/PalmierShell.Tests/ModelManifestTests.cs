using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Platform;
using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

/// The suite never reads the real %APPDATA% manifest cache: on a machine
/// where the app has synced, that file would change what every
/// manifest-reading test sees. Runs at assembly load, before any test.
static class ManifestSuiteCache {
    public static readonly string Dir =
        Path.Combine(Path.GetTempPath(), $"palmier-models-suite-{Guid.NewGuid():N}");

    [ModuleInitializer]
    public static void Redirect() => ModelManifest.CacheDirectoryOverride = Dir;
}

public sealed class ModelManifestTests : IDisposable {
    public void Dispose() {
        ModelManifest.BundledOverride = null;
        ModelManifest.FetchOverride = null;
        ModelManifest.CacheDirectoryOverride = ManifestSuiteCache.Dir;
        ModelManifest.ResetSynced();
    }

    [Fact]
    public void EveryCurrentModelIsPresentWithDurations() {
        string[] replicateIds = [
            "bytedance/seedance-2.0", "bytedance/seedance-1.5-pro", "bytedance/seedance-1-pro",
            "kwaivgi/kling-v3-video", "kwaivgi/kling-v2.1", "google/veo-3", "google/veo-3-fast",
            "minimax/video-01", "black-forest-labs/flux-3",
        ];
        var replicate = ModelManifest.For("replicate");
        Assert.Equal(replicateIds.Length, replicate.Count);
        Assert.All(replicateIds, id =>
            Assert.Contains(replicate, m => m.Id == id && m.Durations.Length > 0));
        Assert.Contains(replicate, m => m.Id == "bytedance/seedance-2.0" && m.Durations.Contains(15));

        string[] falIds = [
            "bytedance/seedance-2.0/image-to-video", "bytedance/seedance-2.0/text-to-video",
            "bytedance/seedance-2.0/fast/image-to-video", "bytedance/seedance-2.0/fast/text-to-video",
            "fal-ai/kling-video/v2.1/standard/text-to-video", "fal-ai/veo3/fast",
            "fal-ai/minimax/hailuo-02/standard/text-to-video",
            "blackforestlabs/flux-3/first-last-frame-to-video",
            "blackforestlabs/flux-3/text-to-video",
            "blackforestlabs/flux-3/extend-video",
        ];
        var fal = ModelManifest.For("fal");
        Assert.Equal(falIds.Length, fal.Count);
        Assert.All(falIds, id =>
            Assert.Contains(fal, m => m.Id == id && m.Durations.Length > 0));
    }

    [Fact]
    public void UnknownProviderReturnsEmpty() {
        Assert.Empty(ModelManifest.For("runway"));
    }

    [Fact]
    public void MalformedManifestFallsBackToBundled() {
        ModelManifest.BundledOverride = () => "{not json";
        Assert.NotEmpty(ModelManifest.For("replicate"));
    }

    [Fact]
    public void CapabilitiesDriveFirstLastFrameSupport() {
        var fal = ModelManifest.For("fal");
        Assert.All(fal.Where(m => m.Id.Contains("text-to-video")),
            m => Assert.DoesNotContain("firstLastFrame", m.Capabilities));
        Assert.Contains(fal, m => m.Id.Contains("image-to-video") && m.Capabilities.Contains("firstLastFrame"));
    }

    /// FLUX.3 is one switched endpoint on Replicate and a family of
    /// single-workflow endpoints on fal; both sides declare the family and
    /// the audio toggle their request builders branch on.
    [Fact]
    public void FluxEntriesCarryTheirCapabilitiesOnBothProviders() {
        var replicate = ModelManifest.For("replicate").Single(m => m.Id == "black-forest-labs/flux-3");
        Assert.Equal("flux", replicate.Family);
        Assert.Equal(["textToVideo", "firstFrame", "firstLastFrame", "extend", "draft"],
            replicate.Capabilities);
        Assert.True(replicate.SynthesisesAudio);

        var fal = ModelManifest.For("fal").Where(m => m.Family == "flux").ToList();
        Assert.Equal(3, fal.Count);
        Assert.All(fal, m => Assert.True(m.SynthesisesAudio));
        Assert.Equal(["firstLastFrame"],
            fal.Single(m => m.Id == "blackforestlabs/flux-3/first-last-frame-to-video").Capabilities);
        Assert.Equal(["textToVideo"],
            fal.Single(m => m.Id == "blackforestlabs/flux-3/text-to-video").Capabilities);
        // In the picker since the composer's Enhance affordance landed: it is
        // the armed enhance that attaches the source video the endpoint needs.
        var extend = fal.Single(m => m.Id == "blackforestlabs/flux-3/extend-video");
        Assert.Equal(["extend"], extend.Capabilities);
        Assert.Equal(1, extend.MaxReferenceVideos);
    }

    /// The manifest's own rule: every offered resolution carries a verified
    /// per-second rate, or the estimate beside the Generate button goes quiet.
    [Fact]
    public void EveryFluxResolutionIsPriced() {
        foreach (var (provider, models) in new[] { ("replicate", ModelManifest.For("replicate")),
                                                   ("fal", ModelManifest.For("fal")) })
        foreach (var model in models.Where(m => m.Family == "flux"))
        foreach (string resolution in model.Resolutions)
            Assert.NotNull(GenerationPricing.For(provider, model.Id, 5, resolution));
    }

    [Fact]
    public async Task SyncSwapsListOnValidPayload() {
        var tempCache = Path.Combine(Path.GetTempPath(), $"palmier-models-{Guid.NewGuid():N}");
        // A synced list is visible process-wide until the finally; the bundled
        // set plus one model can no more fail another class's assertions
        // mid-flight than the bundled set itself can.
        ModelManifest.CacheDirectoryOverride = tempCache;
        ModelManifest.FetchOverride = _ => Task.FromResult<string?>(BundledJsonPlusNewModel());
        try {
            var report = await ModelManifest.SyncAsync();
            Assert.NotNull(report);
            Assert.Equal("New Model", Assert.Single(report!.Added));
            Assert.Empty(report.Removed);
            Assert.Contains(ModelManifest.For("replicate"), m => m.Id == "x/y");
            Assert.True(File.Exists(Path.Combine(tempCache, "models-cache.json")));
            // Dropping the in-memory swap re-resolves from the cache file
            // just written — the leg the next launch reads.
            ModelManifest.ResetSynced();
            Assert.Contains(ModelManifest.For("replicate"), m => m.Id == "x/y");
        } finally {
            ModelManifest.FetchOverride = null;
            ModelManifest.CacheDirectoryOverride = ManifestSuiteCache.Dir;
            ModelManifest.ResetSynced();
            try { Directory.Delete(tempCache, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SyncWithIdenticalPayloadReportsNoChanges() {
        var tempCache = Path.Combine(Path.GetTempPath(), $"palmier-models-{Guid.NewGuid():N}");
        ModelManifest.CacheDirectoryOverride = tempCache;
        ModelManifest.FetchOverride = _ => Task.FromResult<string?>(BundledJson());
        try {
            var report = await ModelManifest.SyncAsync();
            Assert.NotNull(report);
            Assert.Empty(report!.Added);
            Assert.Empty(report.Removed);
        } finally {
            ModelManifest.FetchOverride = null;
            ModelManifest.CacheDirectoryOverride = ManifestSuiteCache.Dir;
            ModelManifest.ResetSynced();
            try { Directory.Delete(tempCache, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InvalidPayloadKeepsCurrentList() {
        ModelManifest.FetchOverride = _ => Task.FromResult<string?>("{junk");
        try {
            Assert.Null(await ModelManifest.SyncAsync());
            Assert.Contains(ModelManifest.For("replicate"), m => m.Id == "bytedance/seedance-2.0");
        } finally {
            ModelManifest.FetchOverride = null;
        }
    }

    [Fact]
    public async Task VersionMismatchKeepsCurrentList() {
        ModelManifest.FetchOverride = _ => Task.FromResult<string?>(
            """{"version":2,"providers":{"replicate":[{"id":"x/y","name":"X","durations":[5]}]}}""");
        try {
            Assert.Null(await ModelManifest.SyncAsync());
            Assert.DoesNotContain(ModelManifest.For("replicate"), m => m.Id == "x/y");
        } finally {
            ModelManifest.FetchOverride = null;
        }
    }

    [Fact]
    public async Task EmptyProvidersKeepsCurrentList() {
        ModelManifest.FetchOverride = _ => Task.FromResult<string?>(
            """{"version":1,"providers":{}}""");
        try {
            Assert.Null(await ModelManifest.SyncAsync());
            Assert.Contains(ModelManifest.For("replicate"), m => m.Id == "bytedance/seedance-2.0");
        } finally {
            ModelManifest.FetchOverride = null;
        }
    }

    static string BundledJson() {
        var loader = new StandardAssetLoader();
        using var stream = loader.Open(new Uri("avares://PalmierShell/Assets/models.json"));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// The bundled manifest with one extra replicate model appended.
    static string BundledJsonPlusNewModel() {
        var node = JsonNode.Parse(BundledJson(), documentOptions: new JsonDocumentOptions {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!;
        ((JsonArray)node["providers"]!["replicate"]!).Add(new JsonObject {
            ["id"] = "x/y",
            ["name"] = "New Model",
            ["durations"] = new JsonArray(5),
            ["capabilities"] = new JsonArray("textToVideo"),
        });
        return node.ToJsonString();
    }
}
