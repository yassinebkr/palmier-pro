using CommunityToolkit.Mvvm.ComponentModel;

namespace PalmierShell.Core.Mcp;

/// Owns the MCP server's lifecycle so the agent-mode setting, the --mcp dev
/// flag, and app teardown share one path: external mode (or --mcp) runs the
/// server, inline stops it, and Shutdown stops it before the engine handles
/// die. The approval gate's shell side lives here too: AcceptSession persists
/// the client name so its future sessions skip the pending state.
///
/// Public members are called on the UI thread. Server-originated changes
/// (connect, tool call) arrive on listener threads and are marshaled through
/// the same invoke seam tool execution uses — the direct seam in tests runs
/// them inline.
public sealed partial class McpHost : ObservableObject, IDisposable {
    readonly McpTools tools;
    readonly Func<Func<object?>, Task<object?>> invokeOnUi;
    readonly string version;
    readonly int port;
    readonly Func<Func<AppSettings, AppSettings>, Task<AppSettings>> updateSettings;
    /// Live behind the server's isApproved predicate; the lock pairs with the
    /// predicate's listener-thread reads.
    readonly HashSet<string> approvedNames = new(StringComparer.Ordinal);
    McpServer? server;
    /// --mcp: external mode for the session only — nothing persisted, and
    /// every client is pre-approved because the flag is an explicit dev act.
    bool devMode;

    [ObservableProperty] bool externalActive;
    [ObservableProperty] string statusLine = "Off";

    /// Session set or server state changed; raised on the UI thread. The
    /// connection panel rebuilds from it.
    public event Action? Changed;

    public McpHost(McpTools tools, Func<Func<object?>, Task<object?>> invokeOnUi, string version,
                   int port = McpServer.DefaultPort,
                   Func<Func<AppSettings, AppSettings>, Task<AppSettings>>? updateSettings = null) {
        this.tools = tools;
        this.invokeOnUi = invokeOnUi;
        this.version = version;
        this.port = port;
        this.updateSettings = updateSettings ??
            (change => Task.Run(() => SettingsStore.Update(change)));
    }

    public McpServerState State => server?.State ?? McpServerState.Stopped;
    public int Port => server?.Port ?? port;
    public IReadOnlyList<McpSession> Sessions => server?.Sessions ?? [];

    /// Applies persisted settings: the approved-client list always, the mode
    /// only outside dev mode — --mcp owns this session's lifecycle.
    public void ApplySettings(AppSettings settings) {
        lock (approvedNames) {
            approvedNames.Clear();
            foreach (var name in settings.ApprovedMcpClients) approvedNames.Add(name);
        }
        if (!devMode) SetMode(settings.AgentMode);
    }

    /// Persists a mode choice (the settings window radios) and applies it.
    public async Task SetAgentModeAsync(string mode) {
        var updated = await updateSettings(s => s with { AgentMode = mode });
        ApplySettings(updated);
    }

    void SetMode(string mode) {
        ExternalActive = mode == AppSettings.AgentModeExternal;
        if (ExternalActive) Start(port);
        else StopServer();
    }

    /// --mcp [--mcp-port N]: serve this session regardless of the saved mode.
    public void StartDevServer(int devPort) {
        devMode = true;
        ExternalActive = true;
        Start(devPort);
    }

    void Start(int listenPort) {
        if (server is { State: McpServerState.Running } && server.Port == listenPort) return;
        if (server is not null && server.RequestedPort != listenPort) DisposeServer();
        server ??= CreateServer(listenPort);
        server.Start();
        SessionLog.Event("mcp", server.State switch {
            McpServerState.Running => $"listening on http://127.0.0.1:{server.Port}/mcp",
            McpServerState.Busy => $"port {listenPort} busy — MCP server not started",
            _ => $"MCP server failed to start on port {listenPort}",
        });
        NotifyChanged();
    }

    void StopServer() {
        server?.Stop();
        NotifyChanged();
    }

    /// App teardown, ahead of the engine handles dying. Idempotent.
    public void Shutdown() => StopServer();

    public void Dispose() {
        StopServer();
        DisposeServer();
    }

    void DisposeServer() {
        if (server is null) return;
        server.Changed -= OnServerChanged;
        server.Dispose();
        server = null;
    }

    McpServer CreateServer(int listenPort) {
        var created = new McpServer(listenPort, tools, invokeOnUi, version, IsApproved);
        created.Changed += OnServerChanged;
        return created;
    }

    bool IsApproved(string clientName) {
        if (devMode) return true;
        // Client names are self-reported: the gate is a consent UX for the
        // user, not a boundary against local processes — loopback trust.
        lock (approvedNames) return approvedNames.Contains(clientName);
    }

    /// The panel's Accept: the session goes live now, and the client name is
    /// remembered so its future sessions skip the gate. The in-memory set
    /// leads the persist so a reconnect during the write still auto-approves.
    public void AcceptSession(McpSession session) {
        if (server?.AcceptSession(session.Id) != true) return;
        if (session.ClientName.Length == 0) return;
        lock (approvedNames) approvedNames.Add(session.ClientName);
        string name = session.ClientName;
        _ = updateSettings(s => s.WithApprovedMcpClient(name));
    }

    /// Deny (pending) and Disconnect (live) both drop the session; the
    /// client's next request reads the 404 as "re-initialize".
    public void DropSession(McpSession session) => server?.DropSession(session.Id);

    void OnServerChanged() => _ = invokeOnUi(() => { NotifyChanged(); return null; });

    void NotifyChanged() {
        StatusLine = State switch {
            McpServerState.Running => $"Listening on 127.0.0.1:{Port}",
            McpServerState.Busy => $"Port {Port} is in use by another app",
            McpServerState.Failed => "The MCP server could not start — see the session log",
            _ => "Off",
        };
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Port));
        Changed?.Invoke();
    }
}
