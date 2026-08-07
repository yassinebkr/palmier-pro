using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PalmierShell.Core.Mcp;

public enum McpServerState { Stopped, Running, Busy }

/// MCP endpoint inside the shell: JSON-RPC 2.0 over plain HTTP on loopback,
/// Streamable-HTTP style with `application/json` responses (no SSE). External
/// clients reach the ten editing tools in McpTools; every tool execution hops
/// to the UI thread through the injected invoke seam, serialized one at a
/// time, because timeline mutations touch observable state.
///
/// Lifecycle: Start/Stop are idempotent; Start on a taken port reports Busy
/// instead of throwing. The caller must Stop before the engine handles die —
/// a tool call in flight touches the live project (MainWindow stops the server
/// ahead of MainViewModel.Dispose, mirroring the agent shutdown guard).
public sealed class McpServer : IDisposable {
    public const int DefaultPort = 19789;
    public const string SessionHeader = "Mcp-Session-Id";
    public const string ProtocolVersion = "2025-06-18";
    const int MaxBodyBytes = 1 << 20;

    static readonly TimeSpan SessionIdleLimit = TimeSpan.FromHours(1);
    const int SessionLimit = 32;

    readonly int requestedPort;
    readonly McpTools tools;
    readonly Func<Func<object?>, Task<object?>> invokeOnUi;
    readonly string version;
    readonly ConcurrentDictionary<string, McpSession> sessions = new();
    /// One tool executes at a time even when requests arrive in parallel; the
    /// direct-execution seam used in tests has no UI thread to serialize them.
    readonly SemaphoreSlim toolGate = new(1, 1);
    HttpListener? listener;
    CancellationTokenSource? loopCts;
    Task? acceptLoop;

    public McpServerState State { get; private set; } = McpServerState.Stopped;

    /// The bound port — after Start with port 0, the ephemeral port chosen.
    public int Port { get; private set; }

    /// Live sessions, oldest connection first. Slice 2's panel renders this.
    public IReadOnlyList<McpSession> Sessions =>
        sessions.Values.OrderBy(s => s.ConnectedAt).ToList();

    public McpServer(int port, McpTools tools, Func<Func<object?>, Task<object?>> invokeOnUi,
                     string version = "0.1.0") {
        requestedPort = port;
        this.tools = tools;
        this.invokeOnUi = invokeOnUi;
        this.version = version;
    }

    public void Start() {
        if (State == McpServerState.Running) return;
        int port = requestedPort == 0 ? ReserveFreePort() : requestedPort;
        var candidate = new HttpListener();
        // Loopback only: the editor must never be reachable from the LAN.
        candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
        try {
            candidate.Start();
        } catch (HttpListenerException) {
            candidate.Close();
            State = McpServerState.Busy;
            return;
        }
        listener = candidate;
        Port = port;
        State = McpServerState.Running;
        loopCts = new CancellationTokenSource();
        acceptLoop = Task.Run(() => AcceptLoop(candidate, loopCts.Token));
    }

    /// Stops the listener and drops every session; a later Start rebinds.
    /// In-flight handlers are abandoned, not awaited — Stop may run on the UI
    /// thread, where waiting on a handler queued behind it would deadlock.
    public void Stop() {
        if (listener is not { } l) {
            State = McpServerState.Stopped;
            return;
        }
        listener = null;
        State = McpServerState.Stopped;
        loopCts?.Cancel();
        l.Close();  // pending GetContextAsync throws, ending the accept loop
        try { acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        loopCts?.Dispose();
        loopCts = null;
        acceptLoop = null;
        sessions.Clear();
    }

    public void Dispose() {
        Stop();
        toolGate.Dispose();
    }

    /// Asks http.sys for a free loopback port by way of a throwaway socket.
    static int ReserveFreePort() {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    async Task AcceptLoop(HttpListener l, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            HttpListenerContext context;
            try {
                context = await l.GetContextAsync();
            } catch (HttpListenerException) when (ct.IsCancellationRequested) {
                return;
            } catch (ObjectDisposedException) {
                return;
            } catch (InvalidOperationException) {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    async Task HandleAsync(HttpListenerContext context) {
        try {
            var request = context.Request;
            var response = context.Response;
            if (request.Url?.AbsolutePath != "/mcp") {
                response.StatusCode = 404;
                return;
            }
            if (request.HttpMethod != "POST") {
                response.StatusCode = 405;
                return;
            }
            if (request.ContentLength64 > MaxBodyBytes) {
                response.StatusCode = 413;
                return;
            }
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            var (status, sessionId, responseBody) =
                await DispatchAsync(request.Headers[SessionHeader], body);
            if (sessionId is not null) response.Headers[SessionHeader] = sessionId;
            response.StatusCode = status;
            if (responseBody is not null) {
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
            }
        } catch (HttpListenerException) {
            // The client or the listener went away mid-exchange; nothing to send.
        } catch (ObjectDisposedException) {
        } catch (Exception ex) {
            SessionLog.Event("mcp", $"request failed: {ex.Message}");
            try { context.Response.StatusCode = 500; } catch { }
        } finally {
            try { context.Response.Close(); } catch { }
        }
    }

    /// One JSON-RPC request → (HTTP status, session id to assign, body).
    /// Protocol failures ride back as JSON-RPC errors under HTTP 200; HTTP
    /// status codes are reserved for transport-level answers (404 unknown
    /// session reads as "session expired" to the stdio shim).
    async Task<(int Status, string? SessionId, string? Body)> DispatchAsync(
        string? sessionHeader, string body) {
        JsonNode? root;
        try {
            root = JsonNode.Parse(body);
        } catch (JsonException) {
            return RpcError(null, -32700, "Parse error");
        }
        if (root is JsonArray)
            return RpcError(null, -32600, "Invalid Request: batches are not supported.");
        if (root is not JsonObject request)
            return RpcError(null, -32600, "Invalid Request: expected a request object.");
        var id = request.TryGetPropertyValue("id", out var idNode) ? idNode?.DeepClone() : null;
        bool hasId = id is not null;
        string? method = request["method"] is JsonValue m && m.TryGetValue<string>(out var ms)
            ? ms : null;
        if (method is null)
            return RpcError(id, -32600, "Invalid Request: missing method.");

        McpSession? session = null;
        if (method != "initialize") {
            if (sessionHeader is null || !sessions.TryGetValue(sessionHeader, out session))
                return (404, null, null);
            session.Touch();
        }

        // Notifications never produce a response; 202 acknowledges receipt.
        if (!hasId)
            return (202, null, null);

        switch (method) {
            case "initialize":
                return Initialize(request);
            case "ping":
                return RpcResult(id, new JsonObject());
            case "tools/list":
                return RpcResult(id, new JsonObject { ["tools"] = McpTools.ToolSchemas() });
            case "tools/call":
                return await CallTool(request, id!, session!);
            default:
                return RpcError(id, -32601, "Method not found");
        }
    }

    (int, string?, string?) Initialize(JsonObject request) {
        PruneSessions();
        string sessionId = Guid.NewGuid().ToString("N");
        var clientInfo = (request["params"] as JsonObject)?["clientInfo"] as JsonObject;
        string ClientField(string key) =>
            clientInfo?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var session = new McpSession(sessionId, ClientField("name"), ClientField("version"));
        sessions[sessionId] = session;
        SessionLog.Event("mcp",
            $"session started: {session.ClientName} {session.ClientVersion}".TrimEnd());
        var result = new JsonObject {
            // Clamped echo: 2025-06-18 is the only version this server speaks.
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject {
                ["name"] = "palmierwin",
                ["version"] = version,
            },
        };
        return (200, sessionId, Envelope(request["id"]?.DeepClone(), "result", result));
    }

    async Task<(int, string?, string?)> CallTool(JsonObject request, JsonNode id, McpSession session) {
        if (request["params"] is not JsonObject p)
            return RpcError(id, -32602, "Invalid params: expected an object.");
        if (p["name"] is not JsonValue nv || !nv.TryGetValue<string>(out string? name))
            return RpcError(id, -32602, "Invalid params: tool name must be a string.");
        if (p["arguments"] is { } argNode && argNode is not JsonObject)
            return RpcError(id, -32602, "Invalid params: arguments must be an object.");
        var args = (p["arguments"] as JsonObject) ?? new JsonObject();

        McpToolResult result;
        await toolGate.WaitAsync();
        try {
            result = (McpToolResult)(await invokeOnUi(() => tools.Execute(name, args)))!;
        } catch (Exception ex) {
            result = new McpToolResult(true, $"Tool {name} failed: {ex.Message}", "exception");
        } finally {
            toolGate.Release();
        }
        session.Record(new McpSession.ToolCall(name, result.Summary, !result.IsError,
                                               DateTimeOffset.UtcNow));
        return RpcResult(id, new JsonObject {
            ["content"] = new JsonArray {
                new JsonObject { ["type"] = "text", ["text"] = result.Text },
            },
            ["isError"] = result.IsError,
        });
    }

    static (int, string?, string?) RpcResult(JsonNode? id, JsonNode result) =>
        (200, null, Envelope(id, "result", result));

    static (int, string?, string?) RpcError(JsonNode? id, int code, string message) =>
        (200, null, Envelope(id, "error", new JsonObject {
            ["code"] = code,
            ["message"] = message,
        }));

    static string Envelope(JsonNode? id, string key, JsonNode payload) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            [key] = payload,
        };
        return envelope.ToJsonString();
    }

    /// Evicted clients recover transparently: their next request gets a 404
    /// and they re-initialize.
    void PruneSessions() {
        var cutoff = DateTimeOffset.UtcNow - SessionIdleLimit;
        foreach (var (sid, session) in sessions)
            if (session.LastActivityAt < cutoff) sessions.TryRemove(sid, out _);
        while (sessions.Count >= SessionLimit &&
               sessions.Values.MinBy(s => s.LastActivityAt) is { } oldest &&
               sessions.TryRemove(oldest.Id, out _)) { }
    }
}
