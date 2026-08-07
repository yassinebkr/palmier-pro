using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;
using PalmierShell.Core.Mcp;

namespace PalmierShell.ViewModels;

/// One connected (or waiting) MCP client in the connection panel.
public sealed partial class McpSessionRow : ObservableObject {
    readonly McpHost host;

    public McpSession Session { get; }
    public string Name => Session.ClientName.Length > 0 ? Session.ClientName : "Unknown client";
    public bool Pending => Session.Pending;
    [ObservableProperty] string caption = "";

    public McpSessionRow(McpHost host, McpSession session) {
        this.host = host;
        Session = session;
        Refresh(DateTimeOffset.UtcNow);
    }

    public void Refresh(DateTimeOffset now) {
        string ago = RelativeTime.Ago(Session.ConnectedAt, now);
        Caption = Session.ClientVersion.Length > 0
            ? $"{Session.ClientVersion} · connected {ago}"
            : $"Connected {ago}";
    }

    [RelayCommand] void Accept() => host.AcceptSession(Session);
    [RelayCommand] void Deny() => host.DropSession(Session);
    [RelayCommand] void Disconnect() => host.DropSession(Session);
}

/// One recent tool call of the active session: name, outcome dot, age.
public sealed partial class McpCallRow : ObservableObject {
    readonly McpSession.ToolCall call;

    public string Tool => call.Tool;
    public bool Ok => call.Ok;
    [ObservableProperty] string age = "";

    public McpCallRow(McpSession.ToolCall call) {
        this.call = call;
        Refresh(DateTimeOffset.UtcNow);
    }

    public void Refresh(DateTimeOffset now) => Age = RelativeTime.Ago(call.At, now);
}

/// The external-mode face of the left panel: server state, connected and
/// pending clients with their approvals, and the active session's recent
/// tool calls. Rebuilds on the host's Changed (UI thread); a 1 s timer, started
/// by the view once attached, keeps only the relative-time strings fresh.
public sealed partial class McpPanelViewModel : ObservableObject, IDisposable {
    readonly McpHost host;
    readonly DispatcherTimer ages;

    public ObservableCollection<McpSessionRow> Sessions { get; } = new();
    public ObservableCollection<McpCallRow> RecentCalls { get; } = new();

    public string StatusLine => host.StatusLine;
    public bool Listening => host.State == McpServerState.Running;
    public bool HasSessions => Sessions.Count > 0;
    public bool HasCalls => RecentCalls.Count > 0;
    public string Endpoint => $"http://127.0.0.1:{host.Port}/mcp";

    /// Claude Desktop config pointing at the repo's stdio→HTTP shim.
    public string ConfigSnippet { get; } = """
        {
          "mcpServers": {
            "palmierwin": {
              "command": "node",
              "args": ["C:\\path\\to\\palmier-pro\\mcpb\\server\\index.js"]
            }
          }
        }
        """;

    public McpPanelViewModel(McpHost host) {
        this.host = host;
        host.Changed += Rebuild;
        host.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(McpHost.StatusLine) or nameof(McpHost.State)
                    or nameof(McpHost.Port)) {
                OnPropertyChanged(nameof(StatusLine));
                OnPropertyChanged(nameof(Listening));
                OnPropertyChanged(nameof(Endpoint));
            }
        };
        ages = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ages.Tick += (_, _) => RefreshAges();
        Rebuild();
    }

    /// The view starts the age refresh once attached — a DispatcherTimer
    /// needs a running app, so headless tests drive Rebuild directly.
    public void Start() => ages.Start();
    public void Stop() => ages.Stop();

    public void Rebuild() {
        Sessions.Clear();
        foreach (var session in host.Sessions) Sessions.Add(new McpSessionRow(host, session));
        RecentCalls.Clear();
        var active = host.Sessions.Where(s => !s.Pending)
                                  .OrderByDescending(s => s.LastActivityAt)
                                  .FirstOrDefault();
        if (active is not null)
            foreach (var call in active.RecentCalls.TakeLast(20).Reverse())
                RecentCalls.Add(new McpCallRow(call));
        RefreshAges();
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasCalls));
    }

    void RefreshAges() {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in Sessions) row.Refresh(now);
        foreach (var row in RecentCalls) row.Refresh(now);
    }

    public void Dispose() {
        ages.Stop();
        host.Changed -= Rebuild;
    }
}
