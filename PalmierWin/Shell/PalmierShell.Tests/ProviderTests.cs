using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class ProviderTests {
    [Fact]
    public void Providers_ExposeTheFiveSupportedBackends() {
        var providers = CoreApi.AgentProviders();
        Assert.Equal(
            ["anthropic", "openai", "zai", "moonshot", "openrouter"],
            providers.Select(p => p.Id));
        Assert.All(providers, p => {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.DefaultModel));
        });
    }

    [Fact]
    public void Configure_AcceptsEveryAdvertisedProviderAndRejectsUnknownOnes() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            foreach (var provider in CoreApi.AgentProviders())
                Assert.Equal(1, CoreApi.palmier_agent_configure(agent, provider.Id, "", provider.DefaultModel));
            Assert.Equal(0, CoreApi.palmier_agent_configure(agent, "not-a-provider", "", "x"));
        } finally {
            CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RefreshModels_WithoutAKey_ReportsThroughThePollChannel() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            // Pick a provider whose environment variable is unlikely to be set
            // here, so the no-key path is what we exercise.
            CoreApi.palmier_agent_configure(agent, "zai", "", "glm-4.6");
            if (Environment.GetEnvironmentVariable("ZAI_API_KEY") is { Length: > 0 }) return;

            Assert.Equal(0, CoreApi.palmier_agent_refresh_models(agent));
            string? events = CoreApi.PollAgent(agent);
            Assert.NotNull(events);
            Assert.Contains("models_error", events);
            Assert.Contains("Z.AI", events);
        } finally {
            CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }
}

public class SettingsTests {
    [Fact]
    public void KeysAndModels_AreTrackedPerProvider() {
        var settings = AppSettings.Default
            .WithKey("anthropic", "key-a")
            .WithKey("openrouter", "key-o")
            .WithModel("openrouter", "z-ai/glm-4.6");

        Assert.Equal("key-a", settings.KeyFor("anthropic"));
        Assert.Equal("key-o", settings.KeyFor("openrouter"));
        Assert.Equal("", settings.KeyFor("moonshot"));
        Assert.Equal("z-ai/glm-4.6", settings.Models["openrouter"]);
    }

    [Fact]
    public void WithKey_EmptyValueClearsThatProvidersKeyOnly() {
        var settings = AppSettings.Default
            .WithKey("anthropic", "key-a")
            .WithKey("openai", "key-b")
            .WithKey("anthropic", "");

        Assert.Equal("", settings.KeyFor("anthropic"));
        Assert.Equal("key-b", settings.KeyFor("openai"));
    }

    [Fact]
    public void WithKey_DoesNotMutateTheOriginal() {
        var original = AppSettings.Default.WithKey("anthropic", "key-a");
        var updated = original.WithKey("openai", "key-b");

        Assert.Equal("", original.KeyFor("openai"));
        Assert.Equal("key-a", updated.KeyFor("anthropic"));
    }
}
