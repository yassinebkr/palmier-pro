using PalmierShell.Core;
using PalmierShell.Core.Mcp;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The connection panel's view model against a live host: sessions arrive,
/// approvals flip rows, and tool calls land in the recent list — all through
/// real HTTP on an ephemeral port, with the direct seam running the host's
/// change notifications inline.
public class McpPanelViewModelTests {
    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public McpHost Host { get; }
        public McpPanelViewModel Panel { get; }

        public Harness() {
            var timeline = new TimelineViewModel(Project);
            var undo = new UndoStack(timeline.CaptureSnapshot, timeline.RestoreSnapshot);
            var tools = new McpTools(() => Project, timeline, () => [], undo);
            Host = new McpHost(tools, McpTestClient.Direct, "9.9.9-test", 0,
                change => Task.FromResult(change(AppSettings.Default)));
            Host.ApplySettings(AppSettings.Default with {
                AgentMode = AppSettings.AgentModeExternal,
            });
            Panel = new McpPanelViewModel(Host);
        }

        public void Dispose() {
            Panel.Dispose();
            Host.Dispose();
            CoreApi.palmier_project_destroy(Project);
        }
    }

    [Fact]
    public async Task SessionsArrive_AcceptFlipsTheRow_ToolCallsLand() {
        using var h = new Harness();
        Assert.True(h.Panel.Listening);
        Assert.Equal(h.Host.StatusLine, h.Panel.StatusLine);
        Assert.Empty(h.Panel.Sessions);
        Assert.False(h.Panel.HasSessions);

        using var client = new HttpClient();
        await McpTestClient.Initialize(client, h.Host.Port, "Claude", "2.0");
        var row = Assert.Single(h.Panel.Sessions);
        Assert.True(row.Pending);
        Assert.True(h.Panel.HasSessions);
        Assert.Equal("Claude", row.Name);
        Assert.Contains("2.0", row.Caption);
        Assert.Contains("connected just now", row.Caption);
        Assert.Empty(h.Panel.RecentCalls);  // pending sessions show no calls

        row.AcceptCommand.Execute(null);
        row = Assert.Single(h.Panel.Sessions);
        Assert.False(row.Pending);

        await McpTestClient.CallTool(client, h.Host.Port, h.Host.Sessions.Single().Id,
            "add_text_clip", """{"text":"Title","start_frame":0}""");
        var call = Assert.Single(h.Panel.RecentCalls);
        Assert.True(h.Panel.HasCalls);
        Assert.Equal("add_text_clip", call.Tool);
        Assert.True(call.Ok);
        Assert.Equal("just now", call.Age);
    }

    [Fact]
    public async Task DenyAndDisconnect_DropTheRow() {
        using var h = new Harness();
        using var client = new HttpClient();
        await McpTestClient.Initialize(client, h.Host.Port, "Claude");
        h.Panel.Sessions.Single().DenyCommand.Execute(null);
        Assert.Empty(h.Panel.Sessions);

        // A live session drops the same way through Disconnect.
        h.Host.ApplySettings(AppSettings.Default with {
            AgentMode = AppSettings.AgentModeExternal,
            ApprovedMcpClients = ["Claude"],
        });
        await McpTestClient.Initialize(client, h.Host.Port, "Claude");
        var row = Assert.Single(h.Panel.Sessions);
        Assert.False(row.Pending);
        row.DisconnectCommand.Execute(null);
        Assert.Empty(h.Panel.Sessions);
    }

    [Fact]
    public void StatusLine_FollowsTheServerState() {
        using var h = new Harness();
        Assert.StartsWith("Listening on 127.0.0.1:", h.Panel.StatusLine);
        h.Host.Shutdown();
        Assert.Equal("Off", h.Panel.StatusLine);
        Assert.False(h.Panel.Listening);
    }
}
