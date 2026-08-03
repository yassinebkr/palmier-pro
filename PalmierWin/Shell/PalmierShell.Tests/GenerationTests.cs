using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

public class GenerationTests {
    [Fact]
    public void Providers_ExposeModelsWithUsableDurations() {
        Assert.NotEmpty(GenerationProviders.All);
        foreach (var provider in GenerationProviders.All) {
            Assert.False(string.IsNullOrWhiteSpace(provider.Name));
            Assert.NotEmpty(provider.Models);
            Assert.All(provider.Models, m => {
                Assert.False(string.IsNullOrWhiteSpace(m.Id));
                Assert.NotEmpty(m.Durations);
                Assert.All(m.Durations, d => Assert.InRange(d, 1, 60));
            });
        }
        Assert.NotNull(GenerationProviders.ById("replicate"));
        Assert.Null(GenerationProviders.ById("nope"));
    }

    [Theory]
    [InlineData("starting", GenerationState.Queued)]
    [InlineData("processing", GenerationState.Running)]
    public void ReplicateStatus_MapsNonTerminalStates(string status, GenerationState expected) {
        var parsed = ReplicateProvider.ParseStatus($$"""{"status":"{{status}}"}""");
        Assert.Equal(expected, parsed.State);
    }

    [Fact]
    public void ReplicateStatus_TakesTheVideoUrlFromASucceededPrediction() {
        var parsed = ReplicateProvider.ParseStatus(
            """{"status":"succeeded","output":"https://replicate.delivery/out.mp4"}""");
        Assert.Equal(GenerationState.Succeeded, parsed.State);
        Assert.Equal("https://replicate.delivery/out.mp4", parsed.VideoUrl);
    }

    [Fact]
    public void ReplicateStatus_HandlesAnArrayOutput() {
        var parsed = ReplicateProvider.ParseStatus(
            """{"status":"succeeded","output":["https://replicate.delivery/a.mp4"]}""");
        Assert.Equal("https://replicate.delivery/a.mp4", parsed.VideoUrl);
    }

    [Fact]
    public void ReplicateStatus_SurfacesTheFailureMessage() {
        var parsed = ReplicateProvider.ParseStatus("""{"status":"failed","error":"NSFW content detected"}""");
        Assert.Equal(GenerationState.Failed, parsed.State);
        Assert.Equal("NSFW content detected", parsed.Error);
    }

    [Fact]
    public void ReplicateStatus_SucceededWithoutOutputIsAFailure() {
        var parsed = ReplicateProvider.ParseStatus("""{"status":"succeeded"}""");
        Assert.Equal(GenerationState.Failed, parsed.State);
        Assert.NotNull(parsed.Error);
    }

    [Theory]
    [InlineData("IN_QUEUE", GenerationState.Queued)]
    [InlineData("IN_PROGRESS", GenerationState.Running)]
    [InlineData("COMPLETED", GenerationState.Succeeded)]
    [InlineData("ERROR", GenerationState.Failed)]
    public void FalStatus_MapsQueueStates(string status, GenerationState expected) {
        Assert.Equal(expected, FalProvider.ParseState($$"""{"status":"{{status}}"}"""));
    }

    [Fact]
    public void FalResult_ReadsTheVideoUrlFromEitherShape() {
        Assert.Equal("https://fal.media/a.mp4",
            FalProvider.ParseResult("""{"video":{"url":"https://fal.media/a.mp4"}}""").VideoUrl);
        Assert.Equal("https://fal.media/b.mp4",
            FalProvider.ParseResult("""{"videos":[{"url":"https://fal.media/b.mp4"}]}""").VideoUrl);
    }

    [Fact]
    public void FalResult_WithoutAVideoIsAFailure() {
        var parsed = FalProvider.ParseResult("""{"seed":42}""");
        Assert.Equal(GenerationState.Failed, parsed.State);
    }

    [Theory]
    [InlineData("a wide shot of a palm tree at sunset", "a-wide-shot-of")]
    [InlineData("  ", "generated")]
    [InlineData("Café / 100% ***", "café-100")]  // letters stay, punctuation goes
    public void OutputFileNames_AreDerivedSafelyFromThePrompt(string prompt, string expected) {
        Assert.Equal(expected, GenerationService.SafeName(prompt));
    }
}
