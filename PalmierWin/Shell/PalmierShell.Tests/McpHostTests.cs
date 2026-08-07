using PalmierShell.Core;
using PalmierShell.Core.Mcp;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The host's lifecycle: the agent mode starts and stops the server, --mcp
/// overrides the saved mode for its session, and accepting a client persists
/// its name. Servers bind port 0 — an ephemeral loopback port, never 19789.
public class McpHostTests {
    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public McpHost Host { get; }
        public List<AppSettings> SavedSettings { get; } = new();

        public Harness(int port = 0) {
            var timeline = new TimelineViewModel(Project);
            var undo = new UndoStack(timeline.CaptureSnapshot, timeline.RestoreSnapshot);
            var tools = new McpTools(() => Project, timeline, () => [], undo);
            Host = new McpHost(tools, McpTestClient.Direct, "9.9.9-test", port,
                change => {
                    var next = change(AppSettings.Default);
                    SavedSettings.Add(next);
                    return Task.FromResult(next);
                });
        }

        public void Dispose() {
            Host.Dispose();
            CoreApi.palmier_project_destroy(Project);
        }
    }

    static readonly AppSettings External =
        AppSettings.Default with { AgentMode = AppSettings.AgentModeExternal };

    [Fact]
    public void ExternalMode_StartsTheServer_InlineModeStopsIt() {
        using var h = new Harness();
        Assert.Equal(McpServerState.Stopped, h.Host.State);

        h.Host.ApplySettings(External);
        Assert.Equal(McpServerState.Running, h.Host.State);
        Assert.True(h.Host.ExternalActive);
        Assert.Equal($"Listening on 127.0.0.1:{h.Host.Port}", h.Host.StatusLine);

        h.Host.ApplySettings(AppSettings.Default);
        Assert.Equal(McpServerState.Stopped, h.Host.State);
        Assert.False(h.Host.ExternalActive);
        Assert.Equal("Off", h.Host.StatusLine);
        Assert.Empty(h.Host.Sessions);
    }

    [Fact]
    public void BusyPort_ReportsBusyWithoutThrowing() {
        using var h = new Harness();
        h.Host.ApplySettings(External);
        int taken = h.Host.Port;

        using var second = new Harness(port: taken);
        second.Host.ApplySettings(External);
        Assert.Equal(McpServerState.Busy, second.Host.State);
        Assert.Equal($"Port {taken} is in use by another app", second.Host.StatusLine);

        // The first server is untouched.
        Assert.Equal(McpServerState.Running, h.Host.State);
    }

    [Fact]
    public async Task Accept_PersistsTheClientName_AndApprovesItsNextSession() {
        using var h = new Harness();
        h.Host.ApplySettings(External);
        using var client = new HttpClient();

        string first = await McpTestClient.Initialize(client, h.Host.Port, "Claude");
        var pending = h.Host.Sessions.Single();
        Assert.True(pending.Pending);

        h.Host.AcceptSession(pending);
        Assert.False(pending.Pending);
        Assert.Contains("Claude", h.SavedSettings.Single().ApprovedMcpClients);

        // The name leads the persist: a reconnect is live immediately.
        string second = await McpTestClient.Initialize(client, h.Host.Port, "Claude");
        Assert.False(h.Host.Sessions.Single(s => s.Id == second).Pending);
    }

    [Fact]
    public async Task DevMode_ApprovesEveryClient_AndIgnoresTheSavedMode() {
        using var h = new Harness();
        h.Host.StartDevServer(0);
        Assert.True(h.Host.ExternalActive);
        Assert.Equal(McpServerState.Running, h.Host.State);

        using var client = new HttpClient();
        string session = await McpTestClient.Initialize(client, h.Host.Port, "Anything");
        Assert.False(h.Host.Sessions.Single().Pending);
        var list = await McpTestClient.Rpc(client, h.Host.Port, session, "tools/list", "{}");
        Assert.NotNull(list["result"]);

        // A settings landing with inline mode must not stop the dev server.
        h.Host.ApplySettings(AppSettings.Default);
        Assert.Equal(McpServerState.Running, h.Host.State);
        Assert.True(h.Host.ExternalActive);
    }

    [Fact]
    public async Task SetAgentModeAsync_PersistsAndApplies() {
        using var h = new Harness();
        await h.Host.SetAgentModeAsync(AppSettings.AgentModeExternal);
        Assert.Equal(AppSettings.AgentModeExternal, h.SavedSettings.Single().AgentMode);
        Assert.Equal(McpServerState.Running, h.Host.State);

        await h.Host.SetAgentModeAsync(AppSettings.AgentModeInline);
        Assert.Equal(AppSettings.AgentModeInline, h.SavedSettings.Last().AgentMode);
        Assert.Equal(McpServerState.Stopped, h.Host.State);
    }

    [Fact]
    public async Task Shutdown_StopsServing_AndIsIdempotent() {
        using var h = new Harness();
        h.Host.ApplySettings(External);
        int port = h.Host.Port;
        using var client = new HttpClient();
        await McpTestClient.Initialize(client, port);

        h.Host.Shutdown();
        Assert.Equal(McpServerState.Stopped, h.Host.State);
        Assert.Empty(h.Host.Sessions);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            McpTestClient.Initialize(client, port));
        h.Host.Shutdown();  // a second stop is a no-op, not an error
        Assert.Equal(McpServerState.Stopped, h.Host.State);
    }
}
