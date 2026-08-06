using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

public sealed class ModelManifestTests : IDisposable {
    public void Dispose() => ModelManifest.BundledOverride = null;

    [Fact]
    public void EveryCurrentModelIsPresentWithDurations() {
        string[] replicateIds = [
            "bytedance/seedance-2.0", "bytedance/seedance-1.5-pro", "bytedance/seedance-1-pro",
            "kwaivgi/kling-v3-video", "kwaivgi/kling-v2.1", "google/veo-3", "google/veo-3-fast",
            "minimax/video-01",
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
}
