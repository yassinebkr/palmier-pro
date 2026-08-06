using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

public sealed class ModelManifestTests : IDisposable {
    public void Dispose() => ModelManifest.BundledOverride = null;

    [Fact]
    public void EveryCurrentModelIsPresentWithDurations() {
        var replicate = ModelManifest.For("replicate");
        Assert.Contains(replicate, m => m.Id == "bytedance/seedance-2.0" && m.Durations.Contains(15));
        Assert.Contains(replicate, m => m.Id == "kwaivgi/kling-v3-video");
        Assert.Contains(replicate, m => m.Id == "google/veo-3-fast");
        var fal = ModelManifest.For("fal");
        Assert.Contains(fal, m => m.Id == "bytedance/seedance-2.0/image-to-video");
        Assert.Contains(fal, m => m.Id == "fal-ai/veo3/fast");
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
