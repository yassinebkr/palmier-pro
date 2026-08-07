using PalmierShell.Core;
using PalmierShell.Core.Generation;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

public class GeneratePanelTests {
    static GeneratePanelViewModel NewPanel() => new((_, _) => Task.CompletedTask, () => []);

    [Fact]
    public void DefaultsToTheFirstModelOfTheFirstProvider() {
        var panel = NewPanel();
        Assert.Equal(GenerationProviders.All[0].Models[0].Id, panel.ModelId);
        Assert.Contains(panel.SelectedDuration, GenerationProviders.All[0].Models[0].Durations);
    }

    [Fact]
    public void SwitchingProviderKeepsAValidDurationSelected() {
        var panel = NewPanel();
        foreach (var provider in GenerationProviders.All) {
            panel.SelectedProvider = provider;
            Assert.Equal(provider.Models[0].Id, panel.ModelId);
            Assert.NotEmpty(panel.Durations);
            // Regression: clearing the list first made the ComboBox write null
            // back into SelectedDuration, throwing InvalidCastException.
            Assert.Contains(panel.SelectedDuration, panel.Durations);
        }
    }

    [Fact]
    public void APastedModelIdGetsTheCommonDurationSet() {
        var panel = NewPanel();
        panel.ModelId = "bytedance/seedance-2.5-not-shipped-yet";
        Assert.Equal([5, 10], panel.Durations);
        Assert.Contains(panel.SelectedDuration, panel.Durations);
    }

    [Fact]
    public void GenerateStaysBlockedWithoutAPromptOrKey() {
        var panel = NewPanel();
        Assert.False(panel.CanGenerate);       // no prompt, no key
        panel.Prompt = "a palm tree at sunset";
        Assert.False(panel.CanGenerate);       // still no key
    }

    [Fact]
    public void APlainOpenLandsInTheMediaLibrary() {
        Assert.Equal("New shot into the media library", NewPanel().TargetSummary);
    }

    [Fact]
    public async Task AnArmedTransitionNamesItsCutAndTheMessageOpensWithIt() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.BeginTransition(new TransitionTarget("L", "R", 300, 150), "a.png", "b.png");
        Assert.Equal("Transition across the cut at 00:00:10:00", panel.TargetSummary);
        Assert.StartsWith(panel.TargetSummary, panel.Message);
    }

    [Fact]
    public async Task AnArmedGapTransitionNamesTheGap() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.BeginTransition(new TransitionTarget("L", "R", 300, 150) { FillsGap = true },
                              "a.png", "b.png");
        Assert.Equal("Transition across the gap at 00:00:10:00", panel.TargetSummary);
    }

    [Fact]
    public async Task AnArmedShotNamesTheGapItFills() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.BeginShot(new ShotTarget("T", 300, 60));
        Assert.Equal("New shot for the 2 s gap at 00:00:10:00", panel.TargetSummary);
    }

    [Fact]
    public async Task AnOpenEndedShotNamesOnlyWhereItStarts() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.BeginShot(new ShotTarget("T", 300, 0));
        Assert.Equal("New shot at 00:00:10:00", panel.TargetSummary);
    }

    [Fact]
    public async Task ShotFramesRewriteTheTargetToWhatTheySitBetween() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.BeginShot(new ShotTarget("T", 300, 60));
        panel.DescribeShotFrames(hasFirst: true, hasLast: false);
        Assert.Equal("New shot to sit between the clip before it", panel.TargetSummary);
    }

    [Fact]
    public async Task AnArmedEnhanceNamesTheClipItContinues() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        Assert.Equal("Extend 'Clip One'", panel.TargetSummary);
    }

    [Fact]
    public async Task DisarmingReturnsTheTargetToTheLibrary() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.BeginEnhance(new EnhanceTarget("clip-1", "Clip One"), "tail.mp4");
        panel.ClearPlacement();
        Assert.Equal("New shot into the media library", panel.TargetSummary);
    }

    [Fact]
    public async Task TheLocationStatusRidesTheTargetSummary() {
        var panel = NewPanel();
        await panel.Initialized;
        panel.DescribeLocation = _ => Task.FromResult<string?>("Paris, France");
        panel.BeginTransition(new TransitionTarget("L", "R", 300, 150), "a.png", "b.png",
                              locationTag: "48.8566,2.3522");
        panel.UseLocationContext = true;
        await panel.LocationResolution;
        Assert.Equal("Transition across the cut at 00:00:10:00 · Setting: Paris, France",
                     panel.TargetSummary);
    }
}

/// The prompt builder's fold persists like every other setting, and settings
/// files written before the field existed load expanded (the old behaviour).
[Collection("settings-file")]
public sealed class SettingsPromptBuilderTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-settings-{Guid.NewGuid():N}.json");

    public SettingsPromptBuilderTests() => SettingsStore.PathOverride = path;

    public void Dispose() {
        SettingsStore.PathOverride = null;
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void PromptBuilderExpanded_RoundTrips() {
        SettingsStore.Save(AppSettings.Default with { PromptBuilderExpanded = false });
        Assert.False(SettingsStore.Load().PromptBuilderExpanded);
    }

    [Fact]
    public void PromptBuilderExpanded_MissingInOldFiles_LoadsExpanded() {
        File.WriteAllText(path, "{\"Provider\":\"anthropic\",\"Model\":\"m\",\"ApiKeyProtected\":\"\"}");
        Assert.True(SettingsStore.Load().PromptBuilderExpanded);
    }
}
