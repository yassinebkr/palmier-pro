namespace PalmierShell.Core.Mcp;

/// One MCP client session: who connected, when, and what they asked the
/// editor to do. The rolling call log is what the connection panel renders.
public sealed class McpSession {
    public sealed record ToolCall(string Tool, string Summary, bool Ok, DateTimeOffset At);

    const int LogLimit = 50;

    public string Id { get; }
    public string ClientName { get; }
    public string ClientVersion { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.UtcNow;

    readonly List<ToolCall> calls = new();
    readonly object gate = new();

    public McpSession(string id, string clientName, string clientVersion) {
        Id = id;
        ClientName = clientName;
        ClientVersion = clientVersion;
    }

    /// Newest last; bounded — the oldest entries drop off past the limit.
    public IReadOnlyList<ToolCall> RecentCalls {
        get { lock (gate) return calls.ToList(); }
    }

    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

    public void Record(ToolCall call) {
        lock (gate) {
            calls.Add(call);
            if (calls.Count > LogLimit) calls.RemoveRange(0, calls.Count - LogLimit);
        }
    }
}
