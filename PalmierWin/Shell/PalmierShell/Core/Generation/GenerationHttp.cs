using System.Text.Json;

namespace PalmierShell.Core.Generation;

/// A generation request that failed for a reason worth showing the user.
public sealed class GenerationException(string message) : Exception(message);

static class GenerationHttp {
    /// Both providers accept a data URI wherever they take an image URL, which
    /// avoids needing somewhere public to host the still.
    public static string? DataUri(string? path) {
        if (path is null || !File.Exists(path)) return null;
        return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
    }

    public static HttpClient Client(params (string Name, string Value)[] headers) {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        foreach (var (name, value) in headers)
            http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        return http;
    }

    /// Providers wrap failures differently; try the shapes they actually use.
    public static string? ErrorMessage(string json) {
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("detail", out var detail)) {
                if (detail.ValueKind == JsonValueKind.String) return detail.GetString();
                if (detail.ValueKind == JsonValueKind.Array && detail.GetArrayLength() > 0
                    && detail[0].TryGetProperty("msg", out var msg)) return msg.GetString();
            }
            if (root.TryGetProperty("error", out var error)) {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m))
                    return m.GetString();
            }
            if (root.TryGetProperty("message", out var message)) return message.GetString();
            return null;
        } catch (JsonException) {
            return null;
        }
    }

    /// Pulls a video URL out of a string, a {url:…} object, or an array of
    /// either — the three shapes these APIs return.
    public static string? FirstUrl(JsonElement element) {
        switch (element.ValueKind) {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Object:
                return element.TryGetProperty("url", out var url) ? url.GetString() : null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (FirstUrl(item) is { Length: > 0 } found) return found;
                return null;
            default:
                return null;
        }
    }
}
