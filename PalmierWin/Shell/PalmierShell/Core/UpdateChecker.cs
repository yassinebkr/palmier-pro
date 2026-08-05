using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;

namespace PalmierShell.Core;

/// A newer Windows build on GitHub: version, tag, notes excerpt, installer URL.
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl);

/// Polls the fork's GitHub releases for the newest `v*.*.*-win` tag (the list
/// endpoint — /releases/latest skips prereleases, and every Windows build is
/// one). Offline, rate-limited, or malformed responses all read as "no
/// update": a failed check must never surface to the user.
public static class UpdateChecker {
    const string ReleasesUrl =
        "https://api.github.com/repos/yassinebkr/palmier-pro/releases?per_page=10";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // No client timeout: installer size times connection speed is unbounded.
    // The dialog's cancellation is the only stop.
    static readonly HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// Test seam: replaces the GitHub fetch. Never set in production code.
    public static Func<CancellationToken, Task<string?>>? FetchOverride;

    /// The running build's version, from the informational version stamped at
    /// publish time (any +commit metadata stripped).
    public static Version CurrentVersion { get; } = ParseCurrentVersion();

    static Version ParseCurrentVersion() {
        string info = CrashLog.Version;
        int plus = info.IndexOf('+');
        if (plus >= 0) info = info[..plus];
        return Version.TryParse(info, out var v) ? v : new Version(0, 0, 0);
    }

    /// Returns the update to offer, or null when there is nothing to show:
    /// no network, no newer release, skipped, or snoozed.
    public static async Task<UpdateInfo?> CheckAsync(AppSettings settings, CancellationToken ct = default) {
        string? json = FetchOverride is { } fetch
            ? await fetch(ct).ConfigureAwait(false)
            : await FetchReleasesAsync(ct).ConfigureAwait(false);
        if (json is null) return null;
        return SelectUpdate(json, CurrentVersion, settings, DateTimeOffset.UtcNow);
    }

    static async Task<string?> FetchReleasesAsync(CancellationToken ct) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            request.Headers.UserAgent.ParseAdd($"PalmierWin/{CurrentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        } catch {
            return null;
        }
    }

    /// Pure selection over the releases JSON: the newest win tag above
    /// `current` that carries an installer asset, honouring skip and snooze.
    public static UpdateInfo? SelectUpdate(string releasesJson, Version current,
                                           AppSettings settings, DateTimeOffset now) {
        UpdateInfo? best = null;
        try {
            using var doc = JsonDocument.Parse(releasesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var release in doc.RootElement.EnumerateArray()) {
                if (!release.TryGetProperty("tag_name", out var tagProp)) continue;
                if (!TryParseTag(tagProp.GetString() ?? "", out var version)) continue;
                if (version <= current) continue;
                if (settings.UpdateSkipVersion.Length > 0 &&
                    version.ToString() == settings.UpdateSkipVersion) continue;
                if (FindInstallerUrl(release) is not { } url) continue;
                string notes = release.TryGetProperty("body", out var body)
                    ? body.GetString() ?? "" : "";
                if (best is null || version > best.Version)
                    best = new UpdateInfo(version, tagProp.GetString()!, notes, url);
            }
        } catch (JsonException) {
            return null;
        }
        if (best is null) return null;
        if (settings.UpdateSnoozeUntil is { } until && until > now) return null;
        return best;
    }

    /// `v1.2.3-win` → 1.2.3. Three numeric components exactly: macOS tags
    /// (v1.2.3) and anything looser are not Windows builds.
    public static bool TryParseTag(string tag, [NotNullWhen(true)] out Version? version) {
        version = null;
        if (!tag.StartsWith('v') || !tag.EndsWith("-win", StringComparison.Ordinal)) return false;
        string core = tag[1..^4];
        if (core.Count(c => c == '.') != 2) return false;
        return Version.TryParse(core, out version);
    }

    static string? FindInstallerUrl(JsonElement release) {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray()) {
            if (!asset.TryGetProperty("name", out var name)) continue;
            string file = name.GetString() ?? "";
            if (!file.Contains("-Setup-") || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (asset.TryGetProperty("browser_download_url", out var urlProp) &&
                urlProp.GetString() is { Length: > 0 } url)
                return url;
        }
        return null;
    }

    /// Streams the installer to %TEMP%, reporting 0–100. Partial downloads
    /// are deleted on failure or cancellation.
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int> progress,
                                                   CancellationToken ct) {
        string path = Path.Combine(Path.GetTempPath(), $"PalmierWin-Setup-{info.Version}.exe");
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            request.Headers.UserAgent.ParseAdd($"PalmierWin/{CurrentVersion}");
            using var response = await DownloadHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long received = 0;
            int reported = -1;
            while (true) {
                int n = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (total is > 0) {
                    int percent = (int)(received * 100 / total.Value);
                    if (percent != reported) {
                        reported = percent;
                        progress.Report(percent);
                    }
                }
            }
            progress.Report(100);
            return path;
        } catch {
            try { File.Delete(path); } catch { }
            throw;
        }
    }
}
