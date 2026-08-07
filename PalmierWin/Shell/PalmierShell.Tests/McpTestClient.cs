using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PalmierShell.Core.Mcp;
using Xunit;

namespace PalmierShell.Tests;

/// HTTP client helpers for the MCP suites, mirroring the slice-1 McpTests
/// private versions (which stay untouched).
static class McpTestClient {
    /// The direct-execution seam: no UI thread in tests, so tool work runs
    /// inline on the request thread.
    public static Task<object?> Direct(Func<object?> work) => Task.FromResult(work());

    public static async Task<HttpResponseMessage> Post(HttpClient client, int port, string body,
                                                       string? session = null) {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{port}/mcp") {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (session is not null) request.Headers.Add(McpServer.SessionHeader, session);
        return await client.SendAsync(request);
    }

    public static async Task<string> Initialize(HttpClient client, int port,
                                                string clientName = "TestClient",
                                                string clientVersion = "1.2.3") {
        using var res = await Post(client, port, $$"""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"{{clientName}}","version":"{{clientVersion}}"} } }
            """);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res.Headers.GetValues(McpServer.SessionHeader).Single();
    }

    public static async Task<JsonNode> Rpc(HttpClient client, int port, string? session,
                                           string method, string paramsJson) {
        using var res = await Post(client, port,
            $$"""{"jsonrpc":"2.0","id":7,"method":"{{method}}","params":{{paramsJson}} }""",
            session);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    public static async Task<(bool IsError, string Text)> CallTool(
            HttpClient client, int port, string session, string name, string argsJson = "{}") {
        var body = await Rpc(client, port, session, "tools/call",
            $$"""{"name":"{{name}}","arguments":{{argsJson}} }""");
        var result = body["result"]!;
        return (result["isError"]!.GetValue<bool>(),
                result["content"]![0]!["text"]!.GetValue<string>());
    }
}
