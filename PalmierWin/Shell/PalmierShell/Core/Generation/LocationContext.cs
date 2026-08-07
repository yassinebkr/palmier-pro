using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PalmierShell.Core.Generation;

/// Location context for generation prompts: an opt-in "Setting: <place>" line
/// derived from the source footage's location tag. The privacy contract: raw
/// coordinates only ever go to the Nominatim geocoder, and only the textual
/// place description is ever embedded in a prompt.
public static class LocationContext {
    // Decimal-degrees ISO 6709 (the iPhone form): ±DD.D±DDD.D, optional
    // altitude, optional trailing slash. Degrees/minutes variants from exotic
    // cameras intentionally do not match — an unresolvable tag is better than
    // a misparsed one.
    static readonly Regex Iso6709 = new(
        @"^(?<lat>[+-]\d{1,2}(?:\.\d+)?)(?<lon>[+-]\d{1,3}(?:\.\d+)?)(?:[+-]\d+(?:\.\d+)?)?/?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// Parses a decimal-degrees ISO 6709 tag. Null on any other shape and on
    /// out-of-range values.
    public static (double Lat, double Lon)? ParseIso6709(string tag) {
        var match = Iso6709.Match(tag.Trim());
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["lat"].Value, NumberStyles.Float,
                             CultureInfo.InvariantCulture, out double lat)
            || !double.TryParse(match.Groups["lon"].Value, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double lon))
            return null;
        if (lat is < -90 or > 90 || lon is < -180 or > 180) return null;
        return (lat, lon);
    }

    /// A tag made only of coordinate characters is still coordinates even when
    /// it does not parse — it must never fall through into a prompt as "text".
    public static bool LooksLikeCoordinates(string tag) {
        string trimmed = tag.Trim();
        if (trimmed.Length == 0) return false;
        foreach (char c in trimmed) {
            if (c is not ('+' or '-' or '.' or '/' or (>= '0' and <= '9'))) return false;
        }
        return true;
    }
}

/// Reverse-geocodes footage coordinates to a "City, Country" line via
/// Nominatim. Results are cached on disk keyed by ~100 m-rounded coordinates;
/// failures are remembered for the session only, never persisted, and always
/// surface as null rather than thrown.
public sealed class GeocodeService {
    public static GeocodeService Shared { get; } = new();

    const string ReverseUrl = "https://nominatim.openstreetmap.org/reverse";

    readonly HttpClient http;
    readonly string cacheDir;
    readonly object gate = new();
    readonly Dictionary<string, Task<string?>> inflight = new();
    readonly HashSet<string> sessionFailures = new();
    Dictionary<string, string>? diskCache;

    public GeocodeService(HttpMessageHandler? handler = null, string? cacheDirectory = null) {
        http = new HttpClient(handler ?? new HttpClientHandler()) {
            Timeout = TimeSpan.FromSeconds(5),
        };
        cacheDir = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PalmierPro");
    }

    string CachePath => Path.Combine(cacheDir, "geocode-cache.json");

    /// Resolves a media location tag to prompt-ready place text. A free-text
    /// tag passes through untouched with no network call; empty tags and
    /// unresolvable coordinates return null.
    public Task<string?> DescribeAsync(string locationTag, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(locationTag)) return Task.FromResult<string?>(null);
        if (LocationContext.ParseIso6709(locationTag) is not { } coords)
            return Task.FromResult<string?>(
                LocationContext.LooksLikeCoordinates(locationTag) ? null : locationTag);
        string key = Key(coords.Lat, coords.Lon);
        lock (gate) {
            if (diskCache is not null && diskCache.TryGetValue(key, out string? hit))
                return Task.FromResult<string?>(hit);
            if (sessionFailures.Contains(key)) return Task.FromResult<string?>(null);
            if (inflight.TryGetValue(key, out Task<string?>? pending)) return pending;
            var task = ResolveAsync(key, coords.Lat, coords.Lon, ct);
            inflight[key] = task;
            return task;
        }
    }

    static string Key(double lat, double lon) =>
        FormattableString.Invariant($"{lat:F3},{lon:F3}");

    async Task<string?> ResolveAsync(string key, double lat, double lon, CancellationToken ct) {
        try {
            var cache = await CacheAsync().ConfigureAwait(false);
            lock (gate) {
                if (cache.TryGetValue(key, out string? hit)) return hit;
            }
            string? place = await FetchAsync(lat, lon, ct).ConfigureAwait(false);
            if (place is null) {
                lock (gate) sessionFailures.Add(key);
                return null;
            }
            lock (gate) cache[key] = place;
            WriteCacheAtomic(cache);
            return place;
        } finally {
            lock (gate) inflight.Remove(key);
        }
    }

    /// The cache file reads once per service, off the caller's thread.
    async Task<Dictionary<string, string>> CacheAsync() {
        lock (gate) {
            if (diskCache is not null) return diskCache;
        }
        var loaded = await Task.Run(() => {
            try {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                           File.ReadAllText(CachePath)) ?? new Dictionary<string, string>();
            } catch {
                return new Dictionary<string, string>();  // an unreadable cache is an empty one
            }
        }).ConfigureAwait(false);
        lock (gate) {
            diskCache ??= loaded;
            return diskCache;
        }
    }

    async Task<string?> FetchAsync(double lat, double lon, CancellationToken ct) {
        try {
            string url = FormattableString.Invariant(
                $"{ReverseUrl}?format=jsonv2&lat={lat:F3}&lon={lon:F3}&zoom=14&accept-language=en");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Nominatim's usage policy asks for an identifying agent.
            request.Headers.UserAgent.ParseAdd("PalmierWin/0.1 (github.com/yassinebkr/palmierWin)");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return PlaceFrom(json);
        } catch {
            return null;  // offline, timeout and cancellation all read as "unavailable"
        }
    }

    /// "City, Country" from the structured address, else the first two
    /// display_name segments. Null when the payload offers neither.
    public static string? PlaceFrom(string json) {
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("address", out var address) &&
                address.ValueKind == JsonValueKind.Object) {
                string? place = null;
                foreach (string field in new[] { "city", "town", "village", "county" }) {
                    if (address.TryGetProperty(field, out var value) &&
                        value.GetString() is { Length: > 0 } found) {
                        place = found;
                        break;
                    }
                }
                string? country = address.TryGetProperty("country", out var c)
                    ? c.GetString() : null;
                if (place is not null)
                    return country is { Length: > 0 } ? $"{place}, {country}" : place;
            }
            if (root.TryGetProperty("display_name", out var display) &&
                display.GetString() is { Length: > 0 } name) {
                var segments = name.Split(',', StringSplitOptions.TrimEntries);
                if (segments.Length > 0 && segments[0].Length > 0)
                    return string.Join(", ", segments.Take(2));
            }
            return null;
        } catch (JsonException) {
            return null;
        }
    }

    /// Temp-then-move, so a crash mid-write never leaves half a cache where
    /// the next launch reads it.
    void WriteCacheAtomic(Dictionary<string, string> cache) {
        string json;
        lock (gate) json = JsonSerializer.Serialize(cache);
        try {
            Directory.CreateDirectory(cacheDir);
            string tmp = CachePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, true);
        } catch {
            // A cache that cannot persist only costs a re-fetch next launch.
        }
    }
}
