using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

/// fal's families name their inputs differently per endpoint, and an
/// undeclared key fails the run — so the request shape per family is a
/// contract, checked here without a network call.
public class FalInputShapeTests : IDisposable {
    readonly string first;
    readonly string last;

    public FalInputShapeTests() {
        string dir = Path.Combine(Path.GetTempPath(), "palmier-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        first = Path.Combine(dir, "first.png");
        last = Path.Combine(dir, "last.png");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(last, [4, 5, 6]);
    }

    public void Dispose() {
        Directory.Delete(Path.GetDirectoryName(first)!, recursive: true);
    }

    GenerationRequest Request(string model) =>
        new("A shot", model, 5) { FirstFrame = first, LastFrame = last };

    /// The kling-style convention the seedance endpoints follow: stills under
    /// image_url/end_image_url, duration as a string, no audio toggle (the
    /// endpoint does not declare one we have read).
    [Fact]
    public void SeedanceImageToVideoKeepsItsFrameKeys() {
        var body = FalProvider.BuildBody(Request("bytedance/seedance-2.0/image-to-video"));
        Assert.StartsWith("data:image/png", (string)body["image_url"]);
        Assert.StartsWith("data:image/png", (string)body["end_image_url"]);
        Assert.Equal("5", body["duration"]);
        Assert.DoesNotContain("generate_audio", body.Keys);
    }

    /// A text-to-video endpoint is never sent stills — it would ignore them
    /// and the run would be paid for either way.
    [Fact]
    public void TextToVideoNeverGetsStills() {
        var body = FalProvider.BuildBody(Request("bytedance/seedance-2.0/text-to-video"));
        Assert.DoesNotContain("image_url", body.Keys);
        Assert.DoesNotContain("start_image_url", body.Keys);
    }

    /// FLUX.3's first/last-frame endpoint names the first still start_image_url
    /// (the end key matches the kling convention), declares duration as a
    /// number, and defaults audio on — so it is sent off.
    [Fact]
    public void FluxFirstLastFrameUsesFluxNames() {
        var body = FalProvider.BuildBody(Request("blackforestlabs/flux-3/first-last-frame-to-video"));
        Assert.StartsWith("data:image/png", (string)body["start_image_url"]);
        Assert.StartsWith("data:image/png", (string)body["end_image_url"]);
        Assert.Equal(5, body["duration"]);
        Assert.Equal(false, body["generate_audio"]);
        Assert.DoesNotContain("image_url", body.Keys);
    }

    /// Text-only flux: no still fields at all, audio still sent off.
    [Fact]
    public void FluxTextToVideoSendsNoStills() {
        var body = FalProvider.BuildBody(Request("blackforestlabs/flux-3/text-to-video"));
        Assert.Equal(["duration", "generate_audio", "prompt", "resolution"], body.Keys.Order());
    }

    /// Extend continues a clip from `video_url`, sent as a data URI — fal
    /// accepts one wherever it takes a file URL.
    [Fact]
    public void FluxExtendSendsTheClipAsVideoUrl() {
        var request = Request("blackforestlabs/flux-3/extend-video") with { ReferenceVideos = [last] };
        var body = FalProvider.BuildBody(request);
        Assert.StartsWith("data:video/mp4;base64,", (string)body["video_url"]);
        Assert.DoesNotContain("start_image_url", body.Keys);
    }

    /// Without a clip there is nothing to continue from — no video_url is
    /// invented, and fal's own validation says what is missing.
    [Fact]
    public void FluxExtendWithoutAClipSendsNoVideoUrl() {
        var body = FalProvider.BuildBody(Request("blackforestlabs/flux-3/extend-video"));
        Assert.DoesNotContain("video_url", body.Keys);
    }
}
