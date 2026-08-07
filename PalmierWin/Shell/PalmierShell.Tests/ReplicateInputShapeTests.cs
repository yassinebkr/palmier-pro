using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

/// Every model family names its inputs differently, and Replicate rejects
/// keys a schema does not declare — so the request shape per family is a
/// contract, checked here without a network call.
public class ReplicateInputShapeTests : IDisposable {
    readonly string first;
    readonly string last;

    public ReplicateInputShapeTests() {
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

    GenerationRequest Request(string model, string resolution = "720p") =>
        new("A shot", model, 5) {
            FirstFrame = first, LastFrame = last, Resolution = resolution,
            NegativePrompt = "flicker",
        };

    [Fact]
    public void SeedanceUsesItsOwnFrameKeysAndResolution() {
        var input = ReplicateProvider.BuildInput(Request("bytedance/seedance-2.0"));
        Assert.Contains("image", input.Keys);
        Assert.Contains("last_frame_image", input.Keys);
        Assert.Equal("720p", input["resolution"]);
        Assert.Equal(false, input["generate_audio"]);
        Assert.DoesNotContain("negative_prompt", input.Keys);
        Assert.DoesNotContain("start_image", input.Keys);
    }

    [Fact]
    public void KlingThreeUsesStartEndKeysModeAndNegativePrompt() {
        var input = ReplicateProvider.BuildInput(Request("kwaivgi/kling-v3-video", "1080p"));
        Assert.Contains("start_image", input.Keys);
        Assert.Contains("end_image", input.Keys);
        Assert.Equal("pro", input["mode"]);
        Assert.Equal("flicker", input["negative_prompt"]);
        Assert.Equal(false, input["generate_audio"]);
        Assert.DoesNotContain("image", input.Keys);
        Assert.DoesNotContain("resolution", input.Keys);
    }

    [Theory]
    [InlineData("720p", "standard")]
    [InlineData("1080p", "pro")]
    public void KlingResolutionsMapToModes(string resolution, string mode) {
        var input = ReplicateProvider.BuildInput(Request("kwaivgi/kling-v3-video", resolution));
        Assert.Equal(mode, input["mode"]);
    }

    /// Veo 3 has no last-frame field. The opening frame goes under `image`
    /// and the end frame must not be sent under any key — an undeclared key
    /// fails the whole request.
    [Theory]
    [InlineData("google/veo-3")]
    [InlineData("google/veo-3-fast")]
    public void VeoSendsTheOpeningFrameOnly(string model) {
        var input = ReplicateProvider.BuildInput(Request(model, "1080p"));
        Assert.Contains("image", input.Keys);
        Assert.DoesNotContain("last_frame_image", input.Keys);
        Assert.DoesNotContain("end_image", input.Keys);
        Assert.Equal("1080p", input["resolution"]);
        Assert.Equal(false, input["generate_audio"]);
        Assert.DoesNotContain("negative_prompt", input.Keys);
    }

    /// An uncurated model gets no frames, no resolution, no audio toggle —
    /// only fields every Replicate video model declares.
    [Fact]
    public void AnUnknownModelGetsOnlyUniversalFields() {
        var input = ReplicateProvider.BuildInput(Request("someone/new-model"));
        Assert.Equal(["duration", "prompt"], input.Keys.Order());
    }

    /// Reference mode: images ride inline, videos as the hosted URLs the
    /// upload step produced, and the frame fields stay out — the schema
    /// forbids the combination.
    [Fact]
    public void SeedanceReferencesReplaceFramesAndVideosTravelAsUrls() {
        var request = Request("bytedance/seedance-2.0") with {
            ReferenceImages = [first, last],
            ReferenceVideos = ["ignored-local-path.mp4"],
        };
        var input = ReplicateProvider.BuildInput(request, ["https://replicate.delivery/ref1"]);
        var images = Assert.IsType<List<object>>(input["reference_images"]);
        Assert.Equal(2, images.Count);
        Assert.All(images, uri => Assert.StartsWith("data:image/png", (string)uri!));
        var videos = Assert.IsType<List<object>>(input["reference_videos"]);
        Assert.Equal("https://replicate.delivery/ref1", videos.Single());
        Assert.DoesNotContain("image", input.Keys);
        Assert.DoesNotContain("last_frame_image", input.Keys);
    }

    /// A model whose schema declares no reference fields never gets them,
    /// whatever rides on the request — the composer refuses that mix, and
    /// this layer must not leak undeclared keys if it slips through.
    [Fact]
    public void ReferencesAreNeverSentToAModelWithoutThem() {
        var request = Request("kwaivgi/kling-v3-video") with {
            ReferenceImages = [first],
        };
        var input = ReplicateProvider.BuildInput(request, []);
        Assert.DoesNotContain("reference_images", input.Keys);
        Assert.Contains("start_image", input.Keys);
    }

    /// FLUX.3's transition shape: both stills in one `images` array, duration
    /// as a string (its schema's type, "auto" being the other value), audio
    /// off, and none of the other families' frame keys.
    [Fact]
    public void FluxSendsBothStillsAsAnImagesArray() {
        var input = ReplicateProvider.BuildInput(Request("black-forest-labs/flux-3", "1080p"));
        var images = Assert.IsType<List<object>>(input["images"]);
        Assert.Equal(2, images.Count);
        Assert.All(images, uri => Assert.StartsWith("data:image/png", (string)uri!));
        Assert.Equal("5", input["duration"]);
        Assert.Equal("1080p", input["resolution"]);
        Assert.Equal(false, input["generate_audio"]);
        Assert.DoesNotContain("image", input.Keys);
        Assert.DoesNotContain("last_frame_image", input.Keys);
        Assert.DoesNotContain("start_image", input.Keys);
        Assert.DoesNotContain("draft", input.Keys);
    }

    /// One still opens the clip; the array carries just it.
    [Fact]
    public void FluxWithOnlyAFirstFrameOpensTheClip() {
        var input = ReplicateProvider.BuildInput(Request("black-forest-labs/flux-3") with { LastFrame = null });
        Assert.Single(Assert.IsType<List<object>>(input["images"]));
    }

    /// No stills is text-to-video: the images key stays out entirely.
    [Fact]
    public void FluxTextOnlySendsNoImages() {
        var input = ReplicateProvider.BuildInput(
            Request("black-forest-labs/flux-3") with { FirstFrame = null, LastFrame = null });
        Assert.Equal(["duration", "generate_audio", "prompt", "resolution"], input.Keys.Order());
    }

    /// A lone end frame is never sent: as images[0] it would open the clip —
    /// the exact opposite of the intent, and the run would be paid for.
    [Fact]
    public void FluxLoneLastFrameSendsNoImages() {
        var input = ReplicateProvider.BuildInput(
            Request("black-forest-labs/flux-3") with { FirstFrame = null, LastFrame = "last.png" });
        Assert.DoesNotContain("images", input.Keys);
    }

    /// A video continues from its final frames under `start_video`, and the
    /// schema forbids combining it with images — the stills stay out.
    [Fact]
    public void FluxExtendSendsStartVideoInsteadOfImages() {
        var input = ReplicateProvider.BuildInput(
            Request("black-forest-labs/flux-3") with { ReferenceVideos = ["local.mp4"] },
            ["https://replicate.delivery/clip"]);
        Assert.Equal("https://replicate.delivery/clip", input["start_video"]);
        Assert.DoesNotContain("images", input.Keys);
    }

    /// The draft flag only travels to a model that declares it.
    [Fact]
    public void FluxDraftFlagFollowsTheCapability() {
        var draft = ReplicateProvider.BuildInput(Request("black-forest-labs/flux-3") with { Draft = true });
        Assert.Equal(true, draft["draft"]);
        var seedance = ReplicateProvider.BuildInput(Request("bytedance/seedance-2.0") with { Draft = true });
        Assert.DoesNotContain("draft", seedance.Keys);
    }
}
