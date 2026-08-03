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
}
