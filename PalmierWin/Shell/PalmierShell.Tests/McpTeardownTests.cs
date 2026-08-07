using PalmierShell.Core;
using PalmierShell.Core.Mcp;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// App teardown stops the MCP server before the engine handles die — a
/// request abandoned mid-flight is then refused by the core's dead-handle
/// guards, never served by a dying handle. Isolated in the settings
/// collection because PathOverride is process-global.
[Collection("settings-file")]
public sealed class McpTeardownTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-settings-{Guid.NewGuid():N}.json");

    public McpTeardownTests() {
        SettingsStore.PathOverride = path;
        SettingsStore.Save(AppSettings.Default with {
            AgentMode = AppSettings.AgentModeExternal,
        });
    }

    public void Dispose() {
        SettingsStore.PathOverride = null;
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public async Task Dispose_StopsTheMcpServerBeforeDroppingTheProject() {
        var vm = new MainViewModel(mcpPort: 0);
        try {
            // Join every settings read the constructor kicked off before this
            // test's PathOverride goes away — an orphaned Task.Run load would
            // otherwise hold open whatever file the next settings test owns.
            await vm.PreferencesLoaded;
            await vm.Agent.Ready;
            await vm.Media.Generate.Initialized;
            Assert.Equal(McpServerState.Running, vm.Mcp.State);

            bool stoppedWithProjectAlive = false;
            vm.Mcp.Changed += () => {
                if (vm.Mcp.State == McpServerState.Stopped && vm.Project != IntPtr.Zero)
                    stoppedWithProjectAlive = true;
            };
            vm.Dispose();

            Assert.True(stoppedWithProjectAlive);
            Assert.Equal(McpServerState.Stopped, vm.Mcp.State);
            Assert.Equal(IntPtr.Zero, vm.Project);
        } finally {
            vm.Dispose();
        }
    }
}
