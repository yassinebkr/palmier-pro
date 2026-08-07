using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

public class PromptStyleTests {
    static readonly SeedanceTwoStyle Seedance = new();

    [Theory]
    [InlineData("bytedance/seedance-2.0/text-to-video", true)]
    [InlineData("bytedance/seedance-2.0/fast/image-to-video", true)]
    [InlineData("bytedance/seedance-2.0", true)]
    [InlineData("bytedance/seedance-1.5-pro", false)]
    [InlineData("fal-ai/veo3/fast", false)]
    public void OnlySeedanceTwoModelsGetSeedanceTuning(string modelId, bool tuned) {
        Assert.Equal(tuned, PromptStyles.For(modelId) is SeedanceTwoStyle);
    }

    [Fact]
    public void AnUnknownModelSendsThePromptAsWritten() {
        Assert.Equal("a cat", PromptStyles.For("some/new-model").Build(" a cat ", PromptContext.Plain));
    }

    [Fact]
    public void TheUsersWordsComeFirstAndSurviveIntact() {
        string built = Seedance.Build("A woman walks into the light", PromptContext.Plain);
        Assert.StartsWith("A woman walks into the light.", built);
    }

    /// Character for character: nothing the tuning does may edit the user's
    /// own text, punctuation and digits included.
    [Fact]
    public void TheUsersTextIsCopiedVerbatim() {
        const string prompt = "3 girls step through a portal — 35mm, f/1.4, slow dolly in";
        Assert.Contains(prompt, Seedance.Build(prompt, PromptContext.Plain));
    }

    [Fact]
    public void TheOfficialAvoidClauseIsAppended() {
        string built = Seedance.Build("A cat on a windowsill", PromptContext.Plain);
        Assert.Contains("Avoid jitter, bent limbs, temporal flicker, identity drift.", built);
    }

    /// The official guide lists quality-tag suffixes as an anti-pattern, and
    /// the output is 720p anyway — asking for 4K is noise the model pays for.
    [Fact]
    public void NoQualityTagSuffixIsAdded() {
        string built = Seedance.Build("A cat on a windowsill", PromptContext.Plain);
        foreach (string tag in new[] { "4K", "ultra HD", "cinematic texture", "sharp clarity" })
            Assert.DoesNotContain(tag, built, StringComparison.OrdinalIgnoreCase);
    }

    /// The single worst failure of the old template: it told the model "no new
    /// subjects entering" over a prompt whose whole point was subjects
    /// entering. Nothing appended may contradict the user's own direction.
    [Fact]
    public void NothingAppendedForbidsWhatThePromptAsksFor() {
        var context = new PromptContext(HasFirstFrame: true, HasLastFrame: true,
                                        IsTransition: true, Seconds: 5);
        string built = Seedance.Build(
            "a portal opens and three women step out of it", context);
        Assert.DoesNotContain("no new subjects", built, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("three women step out of it", built);
    }

    [Fact]
    public void TheAssembledPromptStaysInsideTheOfficialLengthBand() {
        var context = new PromptContext(HasFirstFrame: true, HasLastFrame: true,
                                        IsTransition: true, Seconds: 5) {
            Frames = FrameInput.References,
        };
        string prompt = "A portal opens at the centre, created by the man on the left; " +
                        "three women step through it. Slow dolly in.";
        int words = Seedance.Build(prompt, context)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.InRange(words, 30, SeedanceTwoStyle.MaxWords);
    }

    [Fact]
    public void TwoFramesAddTheTransitionDirection() {
        var context = new PromptContext(HasFirstFrame: true, HasLastFrame: true,
                                        IsTransition: true, Seconds: 5);
        string built = Seedance.Build("Push in across the counter", context);
        Assert.Contains("Start on the first frame and land on the last, in one continuous shot.",
                        built);
    }

    [Fact]
    public void AReferenceEndpointIsToldWhichStillIsWhich() {
        var context = new PromptContext(HasFirstFrame: true, HasLastFrame: true,
                                        IsTransition: true, Seconds: 5) {
            Frames = FrameInput.References,
        };
        string built = Seedance.Build("Push in across the counter", context);
        Assert.StartsWith("[Image1] is the first frame, [Image2] the last.", built);
        Assert.Contains("Push in across the counter", built);
    }

    [Fact]
    public void AFirstLastEndpointNeedsNoImageMarkers() {
        var context = new PromptContext(HasFirstFrame: true, HasLastFrame: true,
                                        IsTransition: true, Seconds: 5) {
            Frames = FrameInput.FirstLast,
        };
        Assert.DoesNotContain("[Image1]", Seedance.Build("Push in", context));
    }

    [Fact]
    public void OneFrameAloneIsNotATransition() {
        var context = new PromptContext(HasFirstFrame: true, HasLastFrame: false,
                                        IsTransition: false, Seconds: 5);
        Assert.DoesNotContain("land exactly on the last frame",
            Seedance.Build("Push in across the counter", context));
    }

    [Fact]
    public void AnEmptyPromptStaysEmpty() {
        Assert.Equal("", Seedance.Build("   ", PromptContext.Plain));
    }

    [Fact]
    public void TheHardCapIsRespectedAndTheUsersWordsAreKept() {
        string long1 = new('x', SeedanceTwoStyle.MaxCharacters - 10);
        string built = Seedance.Build(long1, PromptContext.Plain);
        Assert.True(built.Length <= SeedanceTwoStyle.MaxCharacters);
        Assert.StartsWith(long1, built);
    }

    [Fact]
    public void AVeryShortPromptIsFlagged() {
        Assert.Contains(Seedance.Review("a cat", PromptContext.Plain),
            w => w.Contains("Very short"));
    }

    /// The highest-value thing a review can say: without a named move the
    /// model picks one, and it is usually wrong.
    [Fact]
    public void AMissingCameraMoveIsFlagged() {
        const string prompt = "A portal appears at the centre and three women step out of it";
        Assert.Contains(Seedance.Review(prompt, PromptContext.Plain),
            w => w.Contains("No camera move"));
    }

    [Theory]
    [InlineData("A woman turns, slow dolly in")]
    [InlineData("The camera tracks left across the room")]
    [InlineData("A 360 orbit around the car")]
    [InlineData("Handheld, following her through the door")]
    public void ANamedCameraMoveSatisfiesTheCheck(string prompt) {
        Assert.DoesNotContain(Seedance.Review(prompt, PromptContext.Plain),
            w => w.Contains("No camera move"));
    }

    [Fact]
    public void ACameraWordInsideAnotherWordDoesNotCount() {
        Assert.Contains(Seedance.Review("The soundtrack swells over the company logo",
                                        PromptContext.Plain),
            w => w.Contains("No camera move"));
    }

    [Theory]
    [InlineData("a cat on a windowsill, slow dolly in", 0)]
    [InlineData("a woman turns to the window", 1)]
    [InlineData("two men shake hands", 2)]
    [InlineData("three women step through a portal", 3)]
    [InlineData("the 3 girls come from it", 3)]
    // A definite noun is a back-reference, not another body in the room.
    [InlineData("a woman turns. the woman smiles.", 1)]
    // A bare plural is at least a pair.
    [InlineData("dancers cross the floor", 2)]
    [InlineData("a portal appears at the center created by the male character on the left " +
                "and the 3 girls at the center come from it", 4)]
    public void PeopleAreCountedConservatively(string prompt, int expected) {
        Assert.Equal(expected, SeedanceTwoStyle.SubjectCount(prompt));
    }

    [Fact]
    public void MoreThanTwoPeopleIsFlagged() {
        const string prompt = "a portal appears at the center created by the male character " +
                              "on the left and the 3 girls at the center come from it, slow dolly in";
        Assert.Contains(Seedance.Review(prompt, PromptContext.Plain),
            w => w.Contains("4 people in shot"));
    }

    [Fact]
    public void TwoPeopleAreFine() {
        Assert.DoesNotContain(Seedance.Review("two men shake hands, slow dolly in",
                                              PromptContext.Plain),
            w => w.Contains("people in shot"));
    }

    [Fact]
    public void VagueQualityWordsAreFlagged() {
        Assert.Contains(Seedance.Review("An epic cinematic 4K shot, slow dolly in",
                                        PromptContext.Plain),
            w => w.Contains("vague quality words"));
    }

    [Fact]
    public void AWellFormedPromptDrawsNoWarnings() {
        const string prompt = "A woman steps out of a doorway into low evening sun, " +
                              "slow dolly in from a medium wide, warm side light";
        Assert.Empty(Seedance.Review(prompt, PromptContext.Plain));
    }

    /// A reference the prompt never names is money spent on an input the
    /// model may never look at.
    [Fact]
    public void UnmentionedReferencesAreFlagged() {
        var context = PromptContext.Plain with { ImageReferences = 1, VideoReferences = 1 };
        var warnings = Seedance.Review("A woman turns, slow dolly in", context);
        Assert.Contains(warnings, w => w.Contains("[Image1]"));
        Assert.Contains(warnings, w => w.Contains("[Video1]"));
    }

    [Fact]
    public void MentionedReferencesAreNot() {
        var context = PromptContext.Plain with { ImageReferences = 1, VideoReferences = 1 };
        var warnings = Seedance.Review(
            "The boy from [Image1] walks the pier, match the motion of [Video1], slow dolly in",
            context);
        Assert.DoesNotContain(warnings, w => w.Contains("[Image1]"));
        Assert.DoesNotContain(warnings, w => w.Contains("[Video1]"));
    }

    [Fact]
    public void AnOverlongPromptIsFlagged() {
        string wordy = "slow dolly in " + string.Join(' ', Enumerable.Repeat("subject", 120));
        Assert.Contains(Seedance.Review(wordy, PromptContext.Plain),
            w => w.Contains($"Over {SeedanceTwoStyle.MaxWords} words"));
    }
}

public class KlingThreeStyleTests {
    static readonly KlingThreeStyle Kling = new();

    [Theory]
    [InlineData("kwaivgi/kling-v3-video", true)]
    [InlineData("kwaivgi/kling-v2.1", false)]
    [InlineData("bytedance/seedance-2.0", false)]
    public void OnlyKlingThreeModelsGetKlingTuning(string modelId, bool tuned) {
        Assert.Equal(tuned, PromptStyles.For(modelId) is KlingThreeStyle);
    }

    [Fact]
    public void TheUsersWordsComeFirstAndSurviveIntact() {
        const string prompt = "The boy turns from the rail — handheld tracking, 35mm";
        Assert.StartsWith(prompt, Kling.Build(prompt, PromptContext.Plain));
    }

    [Fact]
    public void TwoFramesAddTheTransitionDirection() {
        var context = new PromptContext(true, true, IsTransition: true, Seconds: 5);
        Assert.Contains("start on the first frame and end exactly on the last frame",
            Kling.Build("Push in across the deck", context));
    }

    [Fact]
    public void APlainShotGetsNoTransitionDirection() {
        Assert.DoesNotContain("first frame",
            Kling.Build("Push in across the deck", PromptContext.Plain));
    }

    /// Kling's exclusions go in the dedicated field, never into the prompt.
    [Fact]
    public void TheNegativeChannelIsUsedInsteadOfAnAvoidClause() {
        Assert.DoesNotContain("Avoid", Kling.Build("Push in across the deck", PromptContext.Plain));
        string? negative = Kling.Negative(PromptContext.Plain);
        Assert.NotNull(negative);
        Assert.Contains("flicker", negative);
    }

    [Fact]
    public void SeedanceHasNoNegativeChannel() {
        IPromptStyle seedance = new SeedanceTwoStyle();
        Assert.Null(seedance.Negative(PromptContext.Plain));
    }

    [Fact]
    public void QuotedDialogueIsFlaggedBecauseModelAudioIsOff() {
        Assert.Contains(Kling.Review("The boy shouts \"wait for me\", handheld tracking",
                                     PromptContext.Plain),
            w => w.Contains("dialogue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMissingCameraMoveIsFlagged() {
        Assert.Contains(Kling.Review("A sea monster rises from the swell far behind the boat",
                                     PromptContext.Plain),
            w => w.Contains("camera move", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AWellFormedPromptDrawsNoWarnings() {
        const string prompt = "Handheld from the boat, the camera tilts up from the boy " +
                              "to the open sea as a dark shape breaks the surface far out";
        Assert.Empty(Kling.Review(prompt, PromptContext.Plain));
    }

    [Fact]
    public void AnEmptyPromptStaysEmpty() {
        Assert.Equal("", Kling.Build("   ", PromptContext.Plain));
    }
}

public class GenerationPricingTests {
    [Fact]
    public void SeedanceStandardOnFalIsPricedPerSecond() {
        var estimate = GenerationPricing.For("fal", "bytedance/seedance-2.0/image-to-video", 5, "720p");
        Assert.NotNull(estimate);
        Assert.Equal(1.52m, estimate!.Amount);
        Assert.False(estimate.Approximate);
    }

    [Fact]
    public void TheFastTierCostsLessThanStandard() {
        var fast = GenerationPricing.For("fal", "bytedance/seedance-2.0/fast/text-to-video", 10, "720p");
        var standard = GenerationPricing.For("fal", "bytedance/seedance-2.0/text-to-video", 10, "720p");
        Assert.True(fast!.Amount < standard!.Amount);
    }

    [Fact]
    public void ReplicateRatesAreMarkedApproximate() {
        var estimate = GenerationPricing.For("replicate", "bytedance/seedance-2.0", 5, "720p");
        Assert.True(estimate!.Approximate);
    }

    [Fact]
    public void AModelWithNoKnownRateReportsNothingRatherThanGuessing() {
        Assert.Null(GenerationPricing.For("fal", "fal-ai/veo3/fast", 8, "720p"));
        Assert.Null(GenerationPricing.For("replicate", "bytedance/seedance-2.0", 5, "1080p"));
    }

    [Fact]
    public void ZeroLengthHasNoPrice() {
        Assert.Null(GenerationPricing.For("fal", "bytedance/seedance-2.0/text-to-video", 0, "720p"));
    }

    [Fact]
    public void ModelIdsAreTrimmedBeforeLookup() {
        Assert.NotNull(GenerationPricing.For("fal", "  bytedance/seedance-2.0/text-to-video ", 5, "720p"));
    }

    /// FLUX.3 publishes per-resolution rates on both providers, so these
    /// estimates are exact, not approximate.
    [Theory]
    [InlineData("replicate", "black-forest-labs/flux-3", "720p", 0.85)]
    [InlineData("replicate", "black-forest-labs/flux-3", "1080p", 1.45)]
    [InlineData("fal", "blackforestlabs/flux-3/first-last-frame-to-video", "720p", 0.85)]
    [InlineData("fal", "blackforestlabs/flux-3/text-to-video", "1080p", 1.45)]
    public void FluxIsPricedPerSecondPerResolution(string provider, string model, string resolution, decimal expected) {
        var estimate = GenerationPricing.For(provider, model, 5, resolution);
        Assert.NotNull(estimate);
        Assert.Equal(expected, estimate!.Amount);
        Assert.False(estimate.Approximate);
    }

    /// Extending a clip bills higher than generating one on fal.
    [Fact]
    public void FluxExtendBillsAboveTheGenerateRate() {
        var extend = GenerationPricing.For("fal", "blackforestlabs/flux-3/extend-video", 5, "720p");
        var generate = GenerationPricing.For("fal", "blackforestlabs/flux-3/text-to-video", 5, "720p");
        Assert.Equal(2.05m, extend!.Amount);
        Assert.True(extend.Amount > generate!.Amount);
    }
}
