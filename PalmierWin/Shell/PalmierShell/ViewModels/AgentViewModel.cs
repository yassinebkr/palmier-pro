using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;

namespace PalmierShell.ViewModels;

public enum AgentEntryKind { User, Assistant, Tool, Error }

public sealed partial class AgentEntryViewModel : ObservableObject {
    public AgentEntryKind Kind { get; }
    [ObservableProperty] string text;
    [ObservableProperty] bool toolSucceeded = true;

    /// Errors the core marked as worth another attempt (transport, billing,
    /// missing key) offer a Retry button.
    [ObservableProperty] bool canRetry;

    // Tool rows expand to show what was actually sent and what came back.
    [ObservableProperty] bool expanded;
    [ObservableProperty] string detail = "";

    public bool HasDetail => Detail.Length > 0;

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void ToggleExpanded() => Expanded = !Expanded;

    public AgentEntryViewModel(AgentEntryKind kind, string text) {
        Kind = kind;
        this.text = text;
    }

    public bool IsUser => Kind == AgentEntryKind.User;
    public bool IsAssistant => Kind == AgentEntryKind.Assistant;
    public bool IsTool => Kind == AgentEntryKind.Tool;
    public bool IsError => Kind == AgentEntryKind.Error;
}

/// Chat with the editing agent: sends turns to the Swift agent host and
/// polls its event queue to render text, tool chips, and errors. One agent
/// turn commits as one undo entry.
public sealed partial class AgentViewModel : ObservableObject {
    readonly IntPtr agent;
    readonly TimelineViewModel timeline;
    readonly MediaPanelViewModel media;
    readonly UndoStack undo;
    readonly DispatcherTimer pollTimer;
    string? turnBeforeSnapshot;
    AgentEntryViewModel? streamingEntry;

    public ObservableCollection<AgentEntryViewModel> Entries { get; } = new();

    [ObservableProperty] string inputText = "";
    [ObservableProperty] bool busy;

    // Settings (provider + key + model). Loaded at startup, saved on demand.
    public IReadOnlyList<ProviderInfo> Providers { get; } = CoreApi.AgentProviders();
    /// Model ids for the selected provider: the provider's own live list once
    /// loaded, otherwise a seed so the picker is never bound to nothing.
    public ObservableCollection<string> Models { get; } = new();
    [ObservableProperty] ProviderInfo selectedProvider;
    [ObservableProperty] string selectedModel = "claude-opus-5";
    [ObservableProperty] string apiKeyInput = "";
    [ObservableProperty] bool hasApiKey;
    [ObservableProperty] bool refreshingModels;
    [ObservableProperty] string? modelsMessage;

    /// Anthropic's line-up is stable enough to seed; every other provider gets
    /// only its default model until the live list lands. A hardcoded catalogue
    /// is a catalogue that goes stale, and a stale list reads as the truth.
    static readonly Dictionary<string, string[]> SeedModels = new() {
        ["anthropic"] = ["claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5"],
    };

    // Pending permission prompt from the agent (destructive tool).
    [ObservableProperty] string? permissionText;

    // @-mention completion state.
    [ObservableProperty] bool mentionOpen;
    public ObservableCollection<string> MentionCandidates { get; } = new();

    public AgentViewModel(IntPtr agent, TimelineViewModel timeline,
                          MediaPanelViewModel media, UndoStack undo,
                          Func<Task<AppSettings>>? settingsLoader = null) {
        this.agent = agent;
        this.timeline = timeline;
        this.media = media;
        this.undo = undo;
        this.settingsLoader = settingsLoader ?? (() => Task.Run(SettingsStore.Load));
        pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        pollTimer.Tick += (_, _) => Poll();
        selectedProvider = Providers[0];  // AgentProviders() never returns empty
        // Fill the picker synchronously so it is never bound to an empty list.
        SetModelList(SeedFor(selectedProvider), selectedModel);
        Ready = LoadSettingsAsync();
    }

    readonly Func<Task<AppSettings>> settingsLoader;

    /// The initial settings load; completes even when shutdown cuts it short.
    public Task Ready { get; }

    bool shutdown;

    async Task LoadSettingsAsync() {
        var settings = await settingsLoader();
        if (shutdown) return;   // teardown won the race: the handle is already dead
        loadingSettings = true;
        SelectedProvider = Providers.FirstOrDefault(p => p.Id == settings.Provider) ?? Providers[0];
        SetModelList(SeedFor(SelectedProvider),
                     settings.Models.GetValueOrDefault(SelectedProvider.Id, settings.Model));
        loadingSettings = false;
        ApplyProviderToCore(settings);
    }

    bool loadingSettings;

    /// Pushes the provider, its key, and the model into the core. Keys are
    /// per provider, so switching provider swaps the key too.
    void ApplyProviderToCore(AppSettings settings) {
        if (shutdown) return;
        string key = settings.KeyFor(SelectedProvider.Id);
        HasApiKey = key.Length > 0;
        if (agent == IntPtr.Zero) return;
        CoreApi.palmier_agent_configure(agent, SelectedProvider.Id, key, SelectedModel);
        // Load the real catalogue rather than making the user press Refresh
        // (or type an id from memory). OpenRouter serves its list unauthed, so
        // that one populates even before a key exists.
        if (liveModelsProvider == SelectedProvider.Id) return;
        if (HasApiKey || SelectedProvider.PublicModelList) RefreshModels();
        else ModelsMessage = $"Add a {SelectedProvider.Name} key to load its models.";
    }

    /// Replaces the picker's list and re-selects `desired`. The list must be
    /// filled *before* the selection: a ComboBox bound to an item that is not
    /// in its source clears itself and writes the null straight back.
    void SetModelList(IEnumerable<string> models, string desired) {
        bool wasLoading = loadingSettings;
        loadingSettings = true;
        Models.Clear();
        foreach (var model in models) Models.Add(model);
        if (desired.Length > 0 && !Models.Contains(desired)) Models.Insert(0, desired);
        SelectedModel = desired;
        loadingSettings = wasLoading;
    }

    static string[] SeedFor(ProviderInfo provider) =>
        SeedModels.GetValueOrDefault(provider.Id, [provider.DefaultModel]);

    string? liveModelsProvider;

    /// Saves the key/provider/model and reconfigures the agent. An empty key
    /// field keeps the provider's previously saved key.
    [RelayCommand]
    async Task SaveSettingsAsync() {
        string key = ApiKeyInput.Trim();
        string provider = SelectedProvider.Id;
        string model = SelectedModel;
        var next = await Task.Run(() => SettingsStore.Update(current => {
            var updated = current with { Provider = provider, Model = model };
            if (key.Length > 0) updated = updated.WithKey(provider, key);
            return updated.WithModel(provider, model);
        }));
        ApiKeyInput = "";
        ApplyProviderToCore(next);
    }

    /// Asks the provider for its real model list; the answer arrives through
    /// the poll loop as a "models" event.
    [RelayCommand]
    void RefreshModels() {
        if (shutdown || agent == IntPtr.Zero) return;
        ModelsMessage = null;
        if (CoreApi.palmier_agent_refresh_models(agent) == 1) {
            RefreshingModels = true;
            pollTimer.Start();
        }
    }

    partial void OnSelectedProviderChanged(ProviderInfo value) {
        if (loadingSettings) return;
        liveModelsProvider = null;
        ModelsMessage = null;
        _ = SwitchProviderAsync(value);
    }

    /// Switching provider restores that provider's own key and last model.
    async Task SwitchProviderAsync(ProviderInfo provider) {
        var settings = await Task.Run(SettingsStore.Load);
        if (shutdown) return;
        loadingSettings = true;
        SetModelList(SeedFor(provider),
                     settings.Models.GetValueOrDefault(provider.Id, provider.DefaultModel));
        loadingSettings = false;
        ApplyProviderToCore(settings);
    }

    partial void OnSelectedModelChanged(string value) {
        if (loadingSettings || agent == IntPtr.Zero || string.IsNullOrWhiteSpace(value)) return;
        CoreApi.palmier_agent_configure(agent, SelectedProvider.Id, "", value);
    }

    partial void OnInputTextChanged(string value) => UpdateMentions(value);

    void UpdateMentions(string text) {
        int at = text.LastIndexOf('@');
        if (at < 0 || (at > 0 && !char.IsWhiteSpace(text[at - 1]))) {
            MentionOpen = false;
            return;
        }
        string token = text[(at + 1)..];
        if (token.Contains(' ')) {
            MentionOpen = false;
            return;
        }
        MentionCandidates.Clear();
        foreach (var item in media.Items.Where(i =>
                     i.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase)).Take(6))
            MentionCandidates.Add(item.Name);
        MentionOpen = MentionCandidates.Count > 0;
    }

    public void ApplyMention(string name) {
        int at = InputText.LastIndexOf('@');
        if (at >= 0) InputText = InputText[..(at + 1)] + name + " ";
        MentionOpen = false;
    }

    [RelayCommand] void AllowPermission() => AnswerPermission(1, 0);
    [RelayCommand] void AlwaysAllowPermission() => AnswerPermission(1, 1);
    [RelayCommand] void DenyPermission() => AnswerPermission(0, 0);

    void AnswerPermission(int allow, int always) {
        CoreApi.palmier_agent_permission(agent, allow, always);
        PermissionText = null;
    }

    public bool CanSend => !Busy && agent != IntPtr.Zero;

    [RelayCommand]
    void Send() {
        string text = InputText.Trim();
        if (text.Length == 0 || Busy || agent == IntPtr.Zero) return;

        SyncMedia();
        turnBeforeSnapshot = timeline.CaptureSnapshot();
        if (CoreApi.palmier_agent_send(agent, ExpandMentions(text)) != 1) return;

        Entries.Add(new AgentEntryViewModel(AgentEntryKind.User, text));
        InputText = "";
        MentionOpen = false;
        Busy = true;
        OnPropertyChanged(nameof(CanSend));
        pollTimer.Start();
    }

    /// Stops the running turn at the next tool boundary.
    [RelayCommand]
    void Stop() {
        if (agent == IntPtr.Zero) return;
        CoreApi.palmier_agent_cancel(agent);
    }

    /// Re-runs the failed turn. The core still holds the conversation, so
    /// this costs one request and never duplicates the user's message.
    [RelayCommand]
    void Retry() {
        if (Busy || agent == IntPtr.Zero) return;
        foreach (var entry in Entries.Where(e => e.CanRetry)) entry.CanRetry = false;
        turnBeforeSnapshot ??= timeline.CaptureSnapshot();
        if (CoreApi.palmier_agent_retry(agent) != 1) {
            Entries.Add(new AgentEntryViewModel(AgentEntryKind.Error,
                "Nothing to retry — send a new message instead."));
            return;
        }
        Busy = true;
        OnPropertyChanged(nameof(CanSend));
        pollTimer.Start();
    }

    /// Plain-text transcript for the clipboard.
    public string TranscriptText() => string.Join(Environment.NewLine + Environment.NewLine,
        Entries.Select(e => e.Kind switch {
            AgentEntryKind.User => $"You: {e.Text}",
            AgentEntryKind.Assistant => e.Text,
            AgentEntryKind.Tool => $"[tool] {e.Text}",
            _ => $"[error] {e.Text}",
        }));

    /// Replaces "@Name" tokens with the media item's name plus its path so
    /// the model can reference the exact file.
    string ExpandMentions(string text) {
        foreach (var item in media.Items.OrderByDescending(i => i.Name.Length))
            text = text.Replace("@" + item.Name, $"\"{item.Name}\" (file: {item.Path})");
        return text;
    }

    void SyncMedia() {
        var items = media.Items.Select(i => new Dictionary<string, object> {
            ["path"] = i.Path,
            ["name"] = i.Name,
            ["duration_frames"] = TimelineViewModel.TimelineFramesFor(i),
            ["width"] = i.Width,
            ["height"] = i.Height,
        }).ToList();
        CoreApi.palmier_agent_set_media(agent, JsonSerializer.Serialize(items));
    }

    /// App shutdown: stops the poll timer and cancels any in-flight turn.
    /// Must run before the agent handle is destroyed — a later tick would
    /// poll a dead handle.
    public void Shutdown() {
        shutdown = true;
        pollTimer.Stop();
        if (agent != IntPtr.Zero) CoreApi.palmier_agent_cancel(agent);
    }

    void Poll() {
        string? json = CoreApi.PollAgent(agent);
        if (json is not null) {
            try {
                HandleEvents(JsonDocument.Parse(json).RootElement);
            } catch (JsonException) {
                // Malformed batch: drop it; the next poll continues the turn.
            }
        }
        if (CoreApi.palmier_agent_busy(agent) == 0 && json is null && !RefreshingModels) {
            pollTimer.Stop();
            Busy = false;
            OnPropertyChanged(nameof(CanSend));
        }
    }

    void HandleEvents(JsonElement events) {
        foreach (var ev in events.EnumerateArray()) {
            string type = ev.GetProperty("type").GetString() ?? "";
            switch (type) {
                case "text_delta": {
                    string delta = ev.GetProperty("text").GetString() ?? "";
                    if (streamingEntry is null) {
                        streamingEntry = new AgentEntryViewModel(AgentEntryKind.Assistant, delta);
                        Entries.Add(streamingEntry);
                    } else {
                        streamingEntry.Text += delta;
                    }
                    break;
                }
                case "text_end":
                    streamingEntry = null;
                    break;
                case "text":
                    Entries.Add(new AgentEntryViewModel(AgentEntryKind.Assistant,
                        ev.GetProperty("text").GetString() ?? ""));
                    break;
                case "tool_use": {
                    string name = ev.GetProperty("name").GetString() ?? "";
                    string summary = ev.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                    string label = summary.Length > 0 ? $"{name} · {summary}" : name;
                    string input = ev.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "";
                    Entries.Add(new AgentEntryViewModel(AgentEntryKind.Tool, label) { Detail = input });
                    break;
                }
                case "tool_result": {
                    bool ok = ev.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
                    if (Entries.LastOrDefault(e => e.IsTool) is { } chip) {
                        chip.ToolSucceeded = ok;
                        if (ev.TryGetProperty("detail", out var d) && d.GetString() is { Length: > 0 } text)
                            chip.Detail = chip.Detail.Length > 0 ? $"{chip.Detail}\n\n→ {text}" : text;
                    }
                    timeline.Reload();
                    break;
                }
                case "playhead":
                    if (ev.TryGetProperty("frame", out var frame))
                        timeline.Scrub(frame.GetInt32());
                    break;
                case "models": {
                    string provider = ev.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "";
                    if (provider != SelectedProvider.Id) break;  // answer for a provider we left
                    var ids = ev.GetProperty("models").EnumerateArray()
                        .Select(m => m.GetString())
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Cast<string>()
                        .ToList();
                    SetModelList(ids, SelectedModel);  // keeps the current choice listed
                    liveModelsProvider = provider;
                    RefreshingModels = false;
                    ModelsMessage = $"{ids.Count} models from {SelectedProvider.Name}";
                    break;
                }
                case "models_error":
                    RefreshingModels = false;
                    ModelsMessage = ev.GetProperty("message").GetString() ?? "Could not list models.";
                    break;
                case "permission": {
                    string name = ev.GetProperty("name").GetString() ?? "";
                    string summary = ev.TryGetProperty("summary", out var ps) ? ps.GetString() ?? "" : "";
                    PermissionText = summary.Length > 0
                        ? $"The agent wants to use {name} · {summary}"
                        : $"The agent wants to use {name}";
                    break;
                }
                case "error": {
                    bool retryable = ev.TryGetProperty("retryable", out var r) && r.GetBoolean();
                    // Only the newest failure keeps its Retry button: the core
                    // can only resume the turn still sitting at the end of the
                    // conversation.
                    foreach (var stale in Entries.Where(e => e.CanRetry)) stale.CanRetry = false;
                    string message = ev.GetProperty("message").GetString() ?? "Unknown error";
                    SessionLog.Event("agent", $"error: {message}");
                    Entries.Add(new AgentEntryViewModel(AgentEntryKind.Error, message) { CanRetry = retryable });
                    break;
                }
                case "done":
                    streamingEntry = null;
                    PermissionText = null;
                    CommitTurn();
                    break;
            }
        }
    }

    void CommitTurn() {
        timeline.Reload();
        if (turnBeforeSnapshot is { } before) {
            undo.Push("Agent Edit", before, timeline.CaptureSnapshot());
            turnBeforeSnapshot = null;
        }
    }
}
