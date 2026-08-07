using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// Agent-mode and approved-MCP-client persistence. Isolated in the settings
/// collection because PathOverride is process-global.
[Collection("settings-file")]
public sealed class SettingsAgentModeTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-settings-{Guid.NewGuid():N}.json");

    public SettingsAgentModeTests() => SettingsStore.PathOverride = path;

    public void Dispose() {
        SettingsStore.PathOverride = null;
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void OldFileWithoutAgentFields_DefaultsToInlineWithNoApprovedClients() {
        File.WriteAllText(path, """{"Provider":"anthropic","Model":"claude-opus-5","ApiKeyProtected":""}""");
        var settings = SettingsStore.Load();
        Assert.Equal(AppSettings.AgentModeInline, settings.AgentMode);
        Assert.Empty(settings.ApprovedMcpClients);
    }

    [Fact]
    public void ExternalModeAndApprovedClients_RoundTrip() {
        SettingsStore.Save(AppSettings.Default with {
            AgentMode = AppSettings.AgentModeExternal,
            ApprovedMcpClients = ["claude-desktop", "kimi"],
        });
        var settings = SettingsStore.Load();
        Assert.Equal(AppSettings.AgentModeExternal, settings.AgentMode);
        Assert.Equal(["claude-desktop", "kimi"], settings.ApprovedMcpClients);
    }

    [Fact]
    public void WithApprovedMcpClient_AppendsNewNamesOnce() {
        var settings = AppSettings.Default.WithApprovedMcpClient("claude-desktop");
        Assert.Equal(["claude-desktop"], settings.ApprovedMcpClients);
        Assert.Same(settings, settings.WithApprovedMcpClient("claude-desktop"));
    }
}
