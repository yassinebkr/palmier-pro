using System.Net;
using System.Text.Json.Nodes;
using PalmierShell.Core;
using PalmierShell.Core.Mcp;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The approval gate over real HTTP on an ephemeral port: an unknown client's
/// session waits pending until Accept, Deny drops it, and an approved client
/// name skips the gate on its next session.
public class McpApprovalTests {
    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public HashSet<string> Approved { get; } = new(StringComparer.Ordinal);
        public McpServer Server { get; }
        public int ChangedCount;

        public Harness() {
            var timeline = new TimelineViewModel(Project);
            var undo = new UndoStack(timeline.CaptureSnapshot, timeline.RestoreSnapshot);
            var tools = new McpTools(() => Project, timeline, () => [], undo);
            Server = new McpServer(0, tools, McpTestClient.Direct, "9.9.9-test",
                                   name => Approved.Contains(name));
            Server.Changed += () => Interlocked.Increment(ref ChangedCount);
            Server.Start();
            Assert.Equal(McpServerState.Running, Server.State);
        }

        public int Port => Server.Port;

        public void Dispose() {
            Server.Dispose();
            CoreApi.palmier_project_destroy(Project);
        }
    }

    [Fact]
    public async Task PendingSession_ToolAccessIsRefused_ButPingInitializeAndNotificationsWork() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await McpTestClient.Initialize(client, h.Port);
        Assert.True(h.Server.Sessions.Single().Pending);

        var ping = await McpTestClient.Rpc(client, h.Port, session, "ping", "{}");
        Assert.Empty(ping["result"]!.AsObject());

        var list = await McpTestClient.Rpc(client, h.Port, session, "tools/list", "{}");
        Assert.Equal(-32001, list["error"]!["code"]!.GetValue<int>());
        Assert.Contains("Waiting for user approval in PalmierWin",
            list["error"]!["message"]!.GetValue<string>());

        var call = await McpTestClient.Rpc(client, h.Port, session, "tools/call",
            """{"name":"get_timeline","arguments":{}}""");
        Assert.Equal(-32001, call["error"]!["code"]!.GetValue<int>());

        // Unknown methods keep their own error; notifications are still 202.
        var unknown = await McpTestClient.Rpc(client, h.Port, session, "resources/list", "{}");
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
        using var notification = await McpTestClient.Post(client, h.Port,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""", session);
        Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);

        // The refused session executed nothing.
        Assert.Empty(h.Server.Sessions.Single().RecentCalls);
    }

    [Fact]
    public async Task Accept_MovesTheSessionLive() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await McpTestClient.Initialize(client, h.Port);

        Assert.True(h.Server.AcceptSession(session));
        Assert.False(h.Server.Sessions.Single().Pending);

        var list = await McpTestClient.Rpc(client, h.Port, session, "tools/list", "{}");
        Assert.Equal(10, list["result"]!["tools"]!.AsArray().Count);
        var (isError, _) = await McpTestClient.CallTool(client, h.Port, session, "get_timeline");
        Assert.False(isError);
    }

    [Fact]
    public async Task Deny_DropsTheSession() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await McpTestClient.Initialize(client, h.Port);

        Assert.True(h.Server.DropSession(session));
        Assert.Empty(h.Server.Sessions);

        using var res = await McpTestClient.Post(client, h.Port,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", session);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ApprovedClientName_SkipsTheGateOnANewSession() {
        using var h = new Harness();
        using var client = new HttpClient();

        string first = await McpTestClient.Initialize(client, h.Port, "Claude");
        Assert.True(h.Server.Sessions.Single().Pending);
        // The host's accept flow: go live now, remember the name for next time.
        h.Server.AcceptSession(first);
        h.Approved.Add("Claude");

        string second = await McpTestClient.Initialize(client, h.Port, "Claude");
        var session = h.Server.Sessions.Single(s => s.Id == second);
        Assert.False(session.Pending);
        var list = await McpTestClient.Rpc(client, h.Port, second, "tools/list", "{}");
        Assert.NotNull(list["result"]);
    }

    [Fact]
    public async Task Changed_FiresOnConnectAcceptAndToolCall() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await McpTestClient.Initialize(client, h.Port);
        Assert.Equal(1, h.ChangedCount);

        h.Server.AcceptSession(session);
        Assert.Equal(2, h.ChangedCount);

        await McpTestClient.CallTool(client, h.Port, session, "add_text_clip",
            """{"text":"Title","start_frame":0}""");
        Assert.Equal(3, h.ChangedCount);
    }
}
