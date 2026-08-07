using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PalmierShell.Core;
using PalmierShell.Core.Mcp;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The MCP server driving the real engine: every test runs the full
/// HttpListener + JSON-RPC + tool path on an ephemeral loopback port, with
/// the direct-execution seam standing in for the UI-thread hop.
public class McpTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public TimelineViewModel Timeline { get; }
        public UndoStack Undo { get; }
        public List<MediaItemViewModel> Media { get; } = new();
        public McpTools Tools { get; }
        public McpServer Server { get; }

        public Harness(Func<Func<object?>, Task<object?>>? seam = null,
                       Func<string, int?>? probe = null) {
            Timeline = new TimelineViewModel(Project);
            Undo = new UndoStack(Timeline.CaptureSnapshot, Timeline.RestoreSnapshot);
            Tools = new McpTools(() => Project, Timeline, () => Media, Undo, probe);
            Server = new McpServer(0, Tools, seam ?? DirectSeam, "9.9.9-test");
            Server.Start();
            Assert.Equal(McpServerState.Running, Server.State);
        }

        public static Task<object?> DirectSeam(Func<object?> work) => Task.FromResult(work());

        public int Port => Server.Port;
        public TimelineState State => TimelineState.Parse(CoreApi.GetTimelineJson(Project));

        public MediaItemViewModel Import(string name) {
            string path = TestMediaPath(name);
            var item = new MediaItemViewModel(path, CoreApi.ProbeMedia(path)!.Value);
            Media.Add(item);
            return item;
        }

        public void Dispose() {
            Server.Dispose();
            CoreApi.palmier_project_destroy(Project);
        }
    }

    static async Task<HttpResponseMessage> Post(HttpClient client, int port, string body,
                                                string? session = null) {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{port}/mcp") {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (session is not null) request.Headers.Add(McpServer.SessionHeader, session);
        return await client.SendAsync(request);
    }

    static async Task<string> Initialize(HttpClient client, int port,
                                         string protocolVersion = "2025-06-18") {
        using var res = await Post(client, port, $$"""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{protocolVersion}}","capabilities":{},"clientInfo":{"name":"TestClient","version":"1.2.3"} } }
            """);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res.Headers.GetValues(McpServer.SessionHeader).Single();
    }

    static async Task<JsonNode> Rpc(HttpClient client, int port, string? session,
                                    string method, string paramsJson) {
        using var res = await Post(client, port,
            $$"""{"jsonrpc":"2.0","id":7,"method":"{{method}}","params":{{paramsJson}} }""",
            session);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    static async Task<(bool IsError, string Text)> CallTool(HttpClient client, int port,
            string session, string name, string argsJson = "{}") {
        var body = await Rpc(client, port, session, "tools/call",
            $$"""{"name":"{{name}}","arguments":{{argsJson}} }""");
        var result = body["result"]!;
        return (result["isError"]!.GetValue<bool>(),
                result["content"]![0]!["text"]!.GetValue<string>());
    }

    // --- JSON-RPC matrix -------------------------------------------------

    [Fact]
    public async Task Initialize_AssignsSessionCapturesClientInfoAndEchoesProtocol() {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await Post(client, h.Port, """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"TestClient","version":"1.2.3"}}}
            """);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string sessionId = res.Headers.GetValues(McpServer.SessionHeader).Single();

        var result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["result"]!;
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.False(result["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.Equal("palmierwin", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("9.9.9-test", result["serverInfo"]!["version"]!.GetValue<string>());

        var session = Assert.Single(h.Server.Sessions);
        Assert.Equal(sessionId, session.Id);
        Assert.Equal(("TestClient", "1.2.3"), (session.ClientName, session.ClientVersion));
    }

    [Fact]
    public async Task Initialize_ClampsAnOlderClientProtocolVersion() {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await Post(client, h.Port, """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}
            """);
        var result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["result"]!;
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requests_WithoutALiveSession_Get404() {
        using var h = new Harness();
        using var client = new HttpClient();
        using var noSession = await Post(client, h.Port,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        Assert.Equal(HttpStatusCode.NotFound, noSession.StatusCode);
        using var bogus = await Post(client, h.Port,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", "no-such-session");
        Assert.Equal(HttpStatusCode.NotFound, bogus.StatusCode);
    }

    [Fact]
    public async Task Notifications_Get202WithNoBody() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        foreach (string method in new[] { "notifications/initialized", "notifications/cancelled" }) {
            using var res = await Post(client, h.Port,
                $$"""{"jsonrpc":"2.0","method":"{{method}}"}""", session);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            Assert.Equal("", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMinus32601() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var body = await Rpc(client, h.Port, session, "resources/list", "{}");
        Assert.Equal(-32601, body["error"]!["code"]!.GetValue<int>());
        Assert.Equal(7, body["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedJson_ReturnsMinus32700() {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await Post(client, h.Port, "{not json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(-32700, body["error"]!["code"]!.GetValue<int>());
        Assert.Null(body["id"]);
    }

    [Fact]
    public async Task BatchRequest_ReturnsMinus32600() {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await Post(client, h.Port,
            """[{"jsonrpc":"2.0","id":1,"method":"ping"}]""");
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(-32600, body["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Ping_ReturnsAnEmptyObject() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var body = await Rpc(client, h.Port, session, "ping", "{}");
        Assert.Empty(body["result"]!.AsObject());
    }

    [Fact]
    public async Task CallTool_UnknownTool_ReturnsMinus32602() {
        // The 2025-06-18 tools/call error table puts an unknown tool name at
        // the protocol level, not in a tool-result isError.
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var body = await Rpc(client, h.Port, session, "tools/call", """{"name":"bogus"}""");
        Assert.Equal(-32602, body["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task CallTool_WithoutAName_ReturnsMinus32602() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var body = await Rpc(client, h.Port, session, "tools/call", """{"arguments":{}}""");
        Assert.Equal(-32602, body["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Get_WithSession_Gets405() {
        // The stdio shim reads 405 as "no standalone stream" (POST-only mode)
        // — this pins the answer its reconnect logic depends on.
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{h.Port}/mcp");
        request.Headers.Add(McpServer.SessionHeader, session);
        using var res = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task ExplicitNullId_GetsAResponseWithNullId() {
        // Only a missing id is a notification; an explicit null is a request.
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        using var res = await Post(client, h.Port,
            """{"jsonrpc":"2.0","id":null,"method":"ping"}""", session);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(body.ContainsKey("id"));
        Assert.Null(body["id"]);
        Assert.NotNull(body["result"]);
    }

    [Fact]
    public async Task Body_OverOneMegabyte_Gets413() {
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await Post(client, h.Port, new string('x', (1 << 20) + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Body_ExactlyOneMegabyte_IsReadAndAnswered() {
        // At the cap the whole body is read; it fails as unparseable, not
        // as oversized.
        using var h = new Harness();
        using var client = new HttpClient();
        using var res = await Post(client, h.Port, new string('x', 1 << 20));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(-32700, body["error"]!["code"]!.GetValue<int>());
    }

    // --- Schema discovery -------------------------------------------------

    [Fact]
    public async Task ToolsList_ExposesExactlyTheTenAgentTools() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var body = await Rpc(client, h.Port, session, "tools/list", "{}");
        var tools = body["result"]!["tools"]!.AsArray();
        Assert.Equal(10, tools.Count);

        // The AgentHost required-parameter contract, verbatim.
        var expected = new Dictionary<string, string[]> {
            ["get_timeline"] = [],
            ["list_media"] = [],
            ["add_clip"] = ["media_path"],
            ["add_text_clip"] = ["text", "start_frame"],
            ["remove_clip"] = ["clip_id"],
            ["split_clip"] = ["clip_id", "frame"],
            ["move_clip"] = ["clip_id", "start_frame"],
            ["trim_clip"] = ["clip_id", "edge", "frame"],
            ["set_clip_properties"] = ["clip_id"],
            ["set_playhead"] = ["frame"],
        };
        foreach (var tool in tools) {
            string name = tool!["name"]!.GetValue<string>();
            Assert.True(expected.Remove(name, out var required), $"unexpected tool {name}");
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            var schema = tool["inputSchema"]!;
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.Equal(required,
                schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        }
        Assert.Empty(expected);
    }

    // --- Execution through the real engine --------------------------------

    [Fact]
    public async Task AddClip_ReceiptNamesTheClipAndTimelineReadsItBack() {
        using var h = new Harness();
        h.Import("testsrc.mp4");
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (isError, text) = await CallTool(client, h.Port, session, "add_clip",
            $$"""{"media_path":"{{TestMediaPath("testsrc.mp4").Replace("\\", "\\\\")}}"}""");
        Assert.False(isError, text);
        var receipt = JsonNode.Parse(text)!;
        string clipId = receipt["clip_id"]!.GetValue<string>();
        Assert.Equal(60, receipt["duration_frames"]!.GetValue<int>());  // probed: 2s @ 30fps
        Assert.Equal(1, h.Undo.Count);

        var (timelineError, timelineJson) = await CallTool(client, h.Port, session, "get_timeline");
        Assert.False(timelineError);
        var clip = TimelineState.Parse(timelineJson).FindClip(clipId);
        Assert.NotNull(clip);
        Assert.Equal((0, 60), (clip.StartFrame, clip.DurationFrames));
    }

    [Fact]
    public async Task AddClip_DurationProbe_RunsOffTheUiPathAndPrefersTheLibrary() {
        bool insideSeam = false;
        Task<object?> Seam(Func<object?> work) {
            insideSeam = true;
            try { return Task.FromResult(work()); } finally { insideSeam = false; }
        }
        var probeInsideSeam = new List<bool>();
        using var h = new Harness(Seam,
            _ => { probeInsideSeam.Add(insideSeam); return 42; });
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        string pathJson = TestMediaPath("testsrc.mp4").Replace("\\", "\\\\");

        // Not in the library: the probe answers, outside the seam's "UI thread".
        var (isError, text) = await CallTool(client, h.Port, session, "add_clip",
            $$"""{"media_path":"{{pathJson}}"}""");
        Assert.False(isError, text);
        Assert.Equal(42, JsonNode.Parse(text)!["duration_frames"]!.GetValue<int>());
        Assert.Equal(new[] { false }, probeInsideSeam);

        // In the library: the item's duration wins without probing at all.
        h.Import("testsrc.mp4");
        var (_, libraryText) = await CallTool(client, h.Port, session, "add_clip",
            $$"""{"media_path":"{{pathJson}}"}""");
        Assert.Equal(60, JsonNode.Parse(libraryText)!["duration_frames"]!.GetValue<int>());
        Assert.Single(probeInsideSeam);
    }

    [Fact]
    public async Task AddClip_MissingMediaPath_IsValidationError() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var (isError, text) = await CallTool(client, h.Port, session, "add_clip");
        Assert.True(isError);
        Assert.Equal("media_path is required.", text);
        Assert.Equal(0, h.Undo.Count);
    }

    [Fact]
    public async Task ListMedia_ReportsEmptyThenLibraryItems() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (_, empty) = await CallTool(client, h.Port, session, "list_media");
        Assert.Equal("The media library is empty — the user has not imported any files.", empty);

        var item = h.Import("testsrc.mp4");
        var (isError, text) = await CallTool(client, h.Port, session, "list_media");
        Assert.False(isError);
        var entry = Assert.Single(JsonNode.Parse(text)!.AsArray())!;
        Assert.Equal(item.Path, entry["path"]!.GetValue<string>());
        Assert.Equal(item.Name, entry["name"]!.GetValue<string>());
        Assert.Equal(60, entry["duration_frames"]!.GetValue<int>());
    }

    [Fact]
    public async Task MoveClip_ClampsAgainstNeighborsAndReportsActual() {
        using var h = new Harness();
        CoreApi.AddClipAt(h.Project, TestMediaPath("testsrc.mp4"), 60, 0);
        string second = CoreApi.AddClipAt(h.Project, TestMediaPath("testsrc.mp4"), 60, 90)!;
        h.Timeline.Reload();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (isError, text) = await CallTool(client, h.Port, session, "move_clip",
            $$"""{"clip_id":"{{second}}","start_frame":30}""");
        Assert.False(isError, text);
        var receipt = JsonNode.Parse(text)!;
        Assert.Equal(30, receipt["requested_frame"]!.GetValue<int>());
        Assert.Equal(60, receipt["start_frame"]!.GetValue<int>());  // flush against the first clip
        Assert.False(receipt["no_op"]!.GetValue<bool>());
        Assert.Equal(60, h.State.FindClip(second)!.StartFrame);
        Assert.Equal(1, h.Undo.Count);
    }

    [Fact]
    public async Task MoveClip_NoOp_ReportsNoOpAndCreatesNoUndoEntry() {
        using var h = new Harness();
        string clipId = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        h.Timeline.Reload();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (isError, text) = await CallTool(client, h.Port, session, "move_clip",
            $$"""{"clip_id":"{{clipId}}","start_frame":0}""");
        Assert.False(isError, text);
        Assert.True(JsonNode.Parse(text)!["no_op"]!.GetValue<bool>());
        Assert.Equal(0, h.Undo.Count);
    }

    [Fact]
    public async Task TrimClip_ReceiptReportsTheNewSpan() {
        using var h = new Harness();
        string clipId = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        h.Timeline.Reload();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (isError, text) = await CallTool(client, h.Port, session, "trim_clip",
            $$"""{"clip_id":"{{clipId}}","edge":"right","frame":40}""");
        Assert.False(isError, text);
        var receipt = JsonNode.Parse(text)!;
        Assert.Equal((0, 40), (receipt["start_frame"]!.GetValue<int>(),
                               receipt["end_frame"]!.GetValue<int>()));
        Assert.Equal(40, h.State.FindClip(clipId)!.EndFrame);
        Assert.Equal(1, h.Undo.Count);
    }

    [Fact]
    public async Task SplitClip_OutsideTheClip_FailsWithoutAnUndoEntry() {
        using var h = new Harness();
        string clipId = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        h.Timeline.Reload();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (isError, text) = await CallTool(client, h.Port, session, "split_clip",
            $$"""{"clip_id":"{{clipId}}","frame":60}""");
        Assert.True(isError);
        Assert.Equal("Split failed — frame 60 is not strictly inside that clip.", text);
        Assert.Single(h.State.Tracks.SelectMany(t => t.Clips));
        Assert.Equal(0, h.Undo.Count);
    }

    [Fact]
    public async Task RemoveClip_UnknownId_IsErrorWithoutAnUndoEntry() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var (isError, text) = await CallTool(client, h.Port, session, "remove_clip",
            """{"clip_id":"missing"}""");
        Assert.True(isError);
        Assert.Equal("No clip with id missing. Call get_timeline for current ids.", text);
        Assert.Equal(0, h.Undo.Count);
    }

    [Fact]
    public async Task SetClipProperties_AppliesAndReportsPerProperty() {
        using var h = new Harness();
        string clipId = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        h.Timeline.Reload();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (isError, text) = await CallTool(client, h.Port, session, "set_clip_properties",
            $$"""{"clip_id":"{{clipId}}","opacity":0.5,"speed":2}""");
        Assert.False(isError, text);
        var receipt = JsonNode.Parse(text)!;
        Assert.Equal(new[] { "opacity", "speed" },
            receipt["applied"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray());
        Assert.Empty(receipt["rejected"]!.AsArray());
        Assert.Equal(1, h.Undo.Count);
        var clip = h.State.FindClip(clipId)!;
        Assert.Equal((0.5, 2.0), (clip.Opacity, clip.Speed));

        var (noneError, none) = await CallTool(client, h.Port, session, "set_clip_properties",
            $$"""{"clip_id":"{{clipId}}"}""");
        Assert.False(noneError);
        Assert.Equal("No properties were provided — nothing changed.", none);
        Assert.Equal(1, h.Undo.Count);
    }

    [Fact]
    public async Task SetPlayhead_MovesThePlayheadClampedToTheTimeline() {
        using var h = new Harness();
        CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60);
        h.Timeline.Reload();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var (isError, text) = await CallTool(client, h.Port, session, "set_playhead",
            """{"frame":30}""");
        Assert.False(isError, text);
        Assert.Equal(30, JsonNode.Parse(text)!["frame"]!.GetValue<int>());
        Assert.Equal(30, h.Timeline.PlayheadFrame);

        var (_, clamped) = await CallTool(client, h.Port, session, "set_playhead",
            """{"frame":500}""");
        Assert.Equal(59, JsonNode.Parse(clamped)!["frame"]!.GetValue<int>());
        Assert.Equal(0, h.Undo.Count);  // playback state, never undoable
    }

    [Fact]
    public async Task ToolCalls_LandInTheSessionLog() {
        using var h = new Harness();
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        await CallTool(client, h.Port, session, "add_text_clip",
            """{"text":"Title","start_frame":0}""");
        await CallTool(client, h.Port, session, "remove_clip", """{"clip_id":"missing"}""");

        var calls = h.Server.Sessions.Single().RecentCalls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(("add_text_clip", true), (calls[0].Tool, calls[0].Ok));
        Assert.Equal(("remove_clip", false), (calls[1].Tool, calls[1].Ok));
    }

    // --- HTTP lifecycle ----------------------------------------------------

    [Fact]
    public async Task FullClientSequence_InitializeListCallReadBack() {
        using var h = new Harness();
        h.Import("testav.mp4");
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var list = await Rpc(client, h.Port, session, "tools/list", "{}");
        Assert.Equal(10, list["result"]!["tools"]!.AsArray().Count);

        var (addError, addText) = await CallTool(client, h.Port, session, "add_clip",
            $$"""{"media_path":"{{TestMediaPath("testav.mp4").Replace("\\", "\\\\")}}","duration_frames":45}""");
        Assert.False(addError, addText);
        string clipId = JsonNode.Parse(addText)!["clip_id"]!.GetValue<string>();

        var (_, timelineJson) = await CallTool(client, h.Port, session, "get_timeline");
        var state = TimelineState.Parse(timelineJson);
        Assert.NotNull(state.FindClip(clipId));
        // Linked audio came along, on its own track at the same frame.
        var audio = state.Tracks.Single(t => t.Type == "audio").Clips.Single();
        Assert.Equal(state.FindClip(clipId)!.StartFrame, audio.StartFrame);
    }

    [Fact]
    public async Task PortBusy_SecondServerReportsBusyAndTheFirstKeepsServing() {
        using var h = new Harness();
        var second = new McpServer(h.Port, h.Tools, Harness.DirectSeam);
        second.Start();
        Assert.Equal(McpServerState.Busy, second.State);

        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var body = await Rpc(client, h.Port, session, "ping", "{}");
        Assert.Empty(body["result"]!.AsObject());
    }

    [Fact]
    public async Task StopAndStart_AreIdempotentAndRebind() {
        using var h = new Harness();
        h.Server.Stop();
        h.Server.Stop();
        Assert.Equal(McpServerState.Stopped, h.Server.State);

        h.Server.Start();
        Assert.Equal(McpServerState.Running, h.Server.State);
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);
        var (isError, _) = await CallTool(client, h.Port, session, "get_timeline");
        Assert.False(isError);
    }

    [Fact]
    public async Task ParallelToolCalls_SerializeThroughTheSeamAndBothLand() {
        int inFlight = 0, maxInFlight = 0;
        Task<object?> Seam(Func<object?> work) {
            int n = Interlocked.Increment(ref inFlight);
            int seen;
            do {
                seen = maxInFlight;
            } while (n > seen && Interlocked.CompareExchange(ref maxInFlight, n, seen) != seen);
            try {
                // Widen the overlap window: without the server's tool gate the
                // two parallel executions would collide here.
                Thread.SpinWait(2_000_000);
                return Task.FromResult(work());
            } finally {
                Interlocked.Decrement(ref inFlight);
            }
        }
        using var h = new Harness(Seam);
        using var client = new HttpClient();
        string session = await Initialize(client, h.Port);

        var first = CallTool(client, h.Port, session, "add_text_clip",
            """{"text":"A","start_frame":0}""");
        var second = CallTool(client, h.Port, session, "add_text_clip",
            """{"text":"B","start_frame":200}""");
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.False(r.IsError, r.Text));
        Assert.Equal(1, maxInFlight);
        Assert.Equal(2, h.State.Tracks.SelectMany(t => t.Clips).Count());
        Assert.Equal(2, h.Undo.Count);
    }
}
