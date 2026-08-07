using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalmierShell.Core;

public sealed record AppSettings(string Provider, string Model) {
    public static readonly AppSettings Default = new("anthropic", "claude-opus-5");

    /// API keys by provider id. Encrypted individually at rest.
    public IReadOnlyDictionary<string, string> Keys { get; init; } =
        new Dictionary<string, string>();

    /// Model last chosen per provider, so switching back restores it.
    public IReadOnlyDictionary<string, string> Models { get; init; } =
        new Dictionary<string, string>();

    /// Accent colour as #RRGGBB; empty means the built-in amber.
    public string Accent { get; init; } = "";

    /// Display name behind the top-right badge; empty until the first-run
    /// welcome dialog collects one.
    public string UserName { get; init; } = "";

    public bool SnapEnabled { get; init; } = true;

    /// The composer's prompt builder section; expanded until the user folds it.
    public bool PromptBuilderExpanded { get; init; } = true;

    /// "Later" on an update prompt suppresses it until this moment.
    public DateTimeOffset? UpdateSnoozeUntil { get; init; }

    /// "Skip this version" — the normalized version string (no v/-win).
    public string UpdateSkipVersion { get; init; } = "";

    public string KeyFor(string provider) => Keys.GetValueOrDefault(provider, "");

    public AppSettings WithKey(string provider, string key) {
        var keys = new Dictionary<string, string>(Keys);
        if (string.IsNullOrEmpty(key)) keys.Remove(provider); else keys[provider] = key;
        return this with { Keys = keys };
    }

    public AppSettings WithModel(string provider, string model) {
        var models = new Dictionary<string, string>(Models) { [provider] = model };
        return this with { Models = models };
    }
}

/// Persists app settings under %APPDATA%\PalmierPro. API keys are encrypted
/// with DPAPI (current user) so they never sit on disk in plain text.
/// Load/Save are synchronous file I/O — call off the UI thread.
public static class SettingsStore {
    /// One file, many writers: the settings pane, the generation model
    /// memory, and every job reading its key at submit time. Unserialized,
    /// a read can land mid-write, fall back to defaults, and a generation
    /// fails with "no API key" while the key sits on disk.
    static readonly object Gate = new();

    /// Test seam: redirects the settings file so tests never touch the
    /// user's real keys. Never set in production code.
    public static string? PathOverride;

    static string SettingsPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PalmierPro", "settings.json");

    sealed record Persisted(string Provider, string Model, string ApiKeyProtected) {
        public Dictionary<string, string> KeysProtected { get; init; } = new();
        public Dictionary<string, string> Models { get; init; } = new();
        public string Accent { get; init; } = "";
        public string UserName { get; init; } = "";
        public bool SnapEnabled { get; init; } = true;
        public bool PromptBuilderExpanded { get; init; } = true;
        public DateTimeOffset? UpdateSnoozeUntil { get; init; }
        public string UpdateSkipVersion { get; init; } = "";
    }

    public static AppSettings Load() {
        lock (Gate) return LoadLocked();
    }

    static AppSettings LoadLocked() {
        try {
            if (!File.Exists(SettingsPath)) return AppSettings.Default;
            var persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(SettingsPath));
            if (persisted is null) return AppSettings.Default;

            var keys = new Dictionary<string, string>();
            foreach (var (provider, encrypted) in persisted.KeysProtected)
                if (Decrypt(encrypted) is { Length: > 0 } key) keys[provider] = key;
            // Pre-multi-provider settings kept a single Anthropic key.
            if (keys.Count == 0 && Decrypt(persisted.ApiKeyProtected) is { Length: > 0 } legacy)
                keys["anthropic"] = legacy;

            return new AppSettings(persisted.Provider, persisted.Model) {
                Keys = keys,
                Models = new Dictionary<string, string>(persisted.Models),
                Accent = persisted.Accent,
                UserName = persisted.UserName,
                SnapEnabled = persisted.SnapEnabled,
                PromptBuilderExpanded = persisted.PromptBuilderExpanded,
                UpdateSnoozeUntil = persisted.UpdateSnoozeUntil,
                UpdateSkipVersion = persisted.UpdateSkipVersion,
            };
        } catch {
            // Corrupt or foreign-machine settings: start fresh rather than crash.
            return AppSettings.Default;
        }
    }

    public static void Save(AppSettings settings) {
        lock (Gate) SaveLocked(settings);
    }

    static void SaveLocked(AppSettings settings) {
        var encrypted = new Dictionary<string, string>();
        foreach (var (provider, key) in settings.Keys)
            if (key.Length > 0) encrypted[provider] = Encrypt(key);

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new Persisted(settings.Provider, settings.Model, "") {
                    KeysProtected = encrypted,
                    Models = new Dictionary<string, string>(settings.Models),
                    Accent = settings.Accent,
                    UserName = settings.UserName,
                    SnapEnabled = settings.SnapEnabled,
                    PromptBuilderExpanded = settings.PromptBuilderExpanded,
                    UpdateSnoozeUntil = settings.UpdateSnoozeUntil,
                    UpdateSkipVersion = settings.UpdateSkipVersion,
                }));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // An unwritable AppData must not take the app down; the session
            // keeps the settings in memory and nothing persists.
            Console.Error.WriteLine($"settings: could not save to {SettingsPath}: {ex.Message}");
        }
    }

    /// Read-modify-write under the gate, so a pane that owns one section
    /// never drops another section's values — and two concurrent updates
    /// never lose each other's writes. Returns the saved settings.
    public static AppSettings Update(Func<AppSettings, AppSettings> change) {
        lock (Gate) {
            var next = change(LoadLocked());
            SaveLocked(next);
            return next;
        }
    }

    static string Encrypt(string value) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    static string Decrypt(string protectedValue) {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
        } catch {
            return "";  // key written by another user or machine
        }
    }
}
