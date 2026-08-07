using System.Net;
using PalmierShell.Core;
using PalmierShell.Core.Mcp;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The Origin guard: a web page's fetch always carries an Origin header, so
/// refusing non-loopback origins blocks browser drive-bys, while curl, the
/// Node shim, and desktop clients send no Origin and pass.
public class McpOriginTests {
    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public McpServer Server { get; }

        public Harness() {
            var timeline = new TimelineViewModel(Project);
            var undo = new UndoStack(timeline.CaptureSnapshot, timeline.RestoreSnapshot);
            var tools = new McpTools(() => Project, timeline, () => [], undo);
            Server = new McpServer(0, tools, McpTestClient.Direct, "9.9.9-test");
            Server.Start();
            Assert.Equal(McpServerState.Running, Server.State);
        }

        public void Dispose() {
            Server.Dispose();
            CoreApi.palmier_project_destroy(Project);
        }
    }

    const string Init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}""";

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://127.0.0.1.evil.example")]
    [InlineData("null")]
    [InlineData("not a uri")]
    public async Task NonLoopbackOrigin_Gets403(string origin) {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await McpTestClient.Post(client, h.Server.Port, Init, origin: origin);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(h.Server.Sessions);
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://[::1]:5173")]
    public async Task LoopbackOrigin_Passes(string origin) {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await McpTestClient.Post(client, h.Server.Port, Init, origin: origin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(h.Server.Sessions);
    }

    [Fact]
    public async Task NoOrigin_Passes() {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await McpTestClient.Post(client, h.Server.Port, Init);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(h.Server.Sessions);
    }
}
