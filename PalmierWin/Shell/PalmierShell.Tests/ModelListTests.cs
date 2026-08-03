using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class ModelListTests {
    /// OpenRouter serves GET /models without credentials, which is the only
    /// reason the picker can show a real catalogue before a key is entered.
    [Fact]
    public void OpenRouterIsTheOnlyProviderAdvertisingAPublicModelList() {
        var providers = CoreApi.AgentProviders();
        Assert.True(providers.Single(p => p.Id == "openrouter").PublicModelList);
        Assert.All(providers.Where(p => p.Id != "openrouter"), p => Assert.False(p.PublicModelList));
    }

    [Fact]
    public void RefreshModels_WithoutAKey_StillStartsForOpenRouter() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            CoreApi.palmier_agent_configure(agent, "openrouter", "", "anthropic/claude-opus-5");
            Assert.Equal(1, CoreApi.palmier_agent_refresh_models(agent));
        } finally {
            CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RefreshModels_WithoutAKey_IsRefusedForKeyedProviders() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            CoreApi.palmier_agent_configure(agent, "openai", "", "gpt-5");
            if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 }) return;
            Assert.Equal(0, CoreApi.palmier_agent_refresh_models(agent));
            Assert.Contains("OpenAI", CoreApi.PollAgent(agent) ?? "");
        } finally {
            CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }
}
