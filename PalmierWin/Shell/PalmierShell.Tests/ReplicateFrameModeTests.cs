using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

/// Replicate's Seedance 2.0 has two mutually exclusive image modes, and only
/// one of them is a transition.
///
/// `image` + `last_frame_image` travels between two frames. `reference_images`
/// carries a character's likeness into a new shot, and its schema says the two
/// cannot be combined. Sending endpoint frames as reference images asked for
/// the wrong thing, and the likeness path drew the input moderation flag —
/// every transition built from footage of a real person came back
/// "input or output was flagged as sensitive (E005)".
public class ReplicateFrameModeTests {
    static GenerationModel Seedance2 =>
        new ReplicateProvider().Models.Single(m => m.Id == "bytedance/seedance-2.0");

    [Fact]
    public void SeedanceTwoTakesAFirstAndLastFrameNotReferenceImages() =>
        Assert.Equal(FrameInput.FirstLast, Seedance2.Frames);

    /// Face rejection is a conditional input classifier, and the 1.x models
    /// are the fallback when it flags a frame — so the composer has to
    /// actually offer them with stills attached.
    [Theory]
    [InlineData("bytedance/seedance-1.5-pro")]
    [InlineData("bytedance/seedance-1-pro")]
    public void SeedanceOneModelsAlsoTakeAFirstAndLastFrame(string id) {
        var model = new ReplicateProvider().Models.Single(m => m.Id == id);
        Assert.Equal(FrameInput.FirstLast, model.Frames);
    }

    /// Every offered resolution needs a rate, or the estimate beside the
    /// Generate button quietly says nothing on the option the user picked.
    [Fact]
    public void EveryOfferedResolutionIsPriced() {
        foreach (string resolution in Seedance2.Resolutions) {
            Assert.NotNull(GenerationPricing.For("replicate", Seedance2.Id, 5, resolution));
        }
    }

    /// Kling 3.0 travels between the same two frames under its own field
    /// names; Veo 3 can only open on a still, so it must be declared
    /// first-only or an end frame would ride along under a key its schema
    /// rejects.
    [Theory]
    [InlineData("kwaivgi/kling-v3-video", FrameInput.FirstLast)]
    [InlineData("google/veo-3", FrameInput.FirstOnly)]
    [InlineData("google/veo-3-fast", FrameInput.FirstOnly)]
    public void NewModelsDeclareTheFrameShapeTheirSchemasHave(string id, FrameInput expected) {
        var model = new ReplicateProvider().Models.Single(m => m.Id == id);
        Assert.Equal(expected, model.Frames);
    }

    /// `generate_audio` defaults to true on the endpoints that offer it, so a
    /// silent omission means paying to synthesise dialogue and music under a
    /// clip that lands on a timeline with its own sound.
    [Theory]
    [InlineData("bytedance/seedance-2.0", true)]
    [InlineData("bytedance/seedance-1.5-pro", true)]
    [InlineData("bytedance/seedance-1-pro", false)]
    [InlineData("kwaivgi/kling-v3-video", true)]
    [InlineData("google/veo-3", true)]
    [InlineData("google/veo-3-fast", true)]
    public void AudioSynthesisIsDeclaredExactlyWhereTheSchemaOffersIt(string id, bool expected) {
        var model = new ReplicateProvider().Models.Single(m => m.Id == id);
        Assert.Equal(expected, model.SynthesisesAudio);
    }

    /// The prompt only labels [Image1]/[Image2] on a reference endpoint. On a
    /// first/last endpoint the frames arrive in named fields, so those labels
    /// would point at images the model was never handed.
    [Fact]
    public void FirstLastPromptsDoNotNumberTheImages() {
        var style = PromptStyles.For("bytedance/seedance-2.0");
        string built = style.Build("A kid rides past, camera locked off",
            new PromptContext(true, true, false, 5) { Frames = FrameInput.FirstLast });
        Assert.DoesNotContain("[Image1]", built);
    }

    [Fact]
    public void ReferencePromptsStillNumberTheImages() {
        var style = PromptStyles.For("bytedance/seedance-2.0");
        string built = style.Build("A kid rides past, camera locked off",
            new PromptContext(true, true, false, 5) { Frames = FrameInput.References });
        Assert.Contains("[Image1]", built);
    }
}
