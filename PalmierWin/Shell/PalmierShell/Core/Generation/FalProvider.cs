using System.Net.Http.Json;
using System.Text.Json;

namespace PalmierShell.Core.Generation;

/// fal.ai's queue API: POST to queue.fal.run/{model} for a request id, poll
/// the status endpoint, then read the result once COMPLETED.
/// https://docs.fal.ai/model-apis/queue
public sealed class FalProvider : IGenerationProvider {
    public string Id => "fal";
    public string Name => "fal.ai";
    public string KeyHint => "fal.ai/dashboard/keys";

    /// Seedance 2.0 takes 4–15 s and 480p/720p on both tiers. The image-to-
    /// video endpoints are the ones that accept a first and last frame — the
    /// text-to-video ones ignore them, so they are listed separately rather
    /// than switched behind the user's back.
    static readonly int[] SeedanceDurations = [4, 5, 6, 8, 10, 12, 15];
    static readonly string[] SeedanceResolutions = ["720p", "480p"];

    public IReadOnlyList<GenerationModel> Models { get; } = [
        new("bytedance/seedance-2.0/image-to-video", "Seedance 2.0 · first/last frame",
            SeedanceDurations) { Frames = FrameInput.FirstLast, Resolutions = SeedanceResolutions },
        new("bytedance/seedance-2.0/text-to-video", "Seedance 2.0 · text only",
            SeedanceDurations) { Resolutions = SeedanceResolutions },
        new("bytedance/seedance-2.0/fast/image-to-video", "Seedance 2.0 Fast · first/last frame",
            SeedanceDurations) { Frames = FrameInput.FirstLast, Resolutions = SeedanceResolutions },
        new("bytedance/seedance-2.0/fast/text-to-video", "Seedance 2.0 Fast · text only",
            SeedanceDurations) { Resolutions = SeedanceResolutions },
        new("fal-ai/kling-video/v2.1/standard/text-to-video", "Kling 2.1 Standard", [5, 10]),
        new("fal-ai/veo3/fast", "Veo 3 Fast", [8]),
        new("fal-ai/minimax/hailuo-02/standard/text-to-video", "Hailuo 02", [6, 10]),
    ];

    /// How the endpoint takes stills. An id we do not curate gets None: we
    /// have not read its schema, so we send it no frame fields.
    FrameInput Frames(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)?.Frames ?? FrameInput.None;

    public async Task<string> SubmitAsync(GenerationRequest request, string apiKey, CancellationToken ct) {
        using var http = GenerationHttp.Client(("Authorization", $"Key {apiKey}"));
        var body = new Dictionary<string, object> {
            ["prompt"] = request.Prompt,
            ["duration"] = request.Seconds.ToString(),
            ["resolution"] = request.Resolution,
        };
        // Only the image-to-video endpoints declare these; text-to-video
        // rejects nothing but ignores them, which is worse — a paid run that
        // throws the stills away. The composer steers the user first.
        if (Frames(request.Model) == FrameInput.FirstLast) {
            if (GenerationHttp.DataUri(request.FirstFrame) is { } first) body["image_url"] = first;
            if (GenerationHttp.DataUri(request.LastFrame) is { } last) body["end_image_url"] = last;
        }
        using var response = await http.PostAsJsonAsync($"https://queue.fal.run/{request.Model}", body, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GenerationException(GenerationHttp.ErrorMessage(json) ?? $"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        // The queue hands back per-request status/response URLs; keep both so
        // polling does not have to reconstruct the model path.
        string? requestId = doc.RootElement.TryGetProperty("request_id", out var id) ? id.GetString() : null;
        string? statusUrl = doc.RootElement.TryGetProperty("status_url", out var s) ? s.GetString() : null;
        if (requestId is null || statusUrl is null)
            throw new GenerationException("fal.ai did not return a request id.");
        return statusUrl;
    }

    /// `jobId` is the status URL returned by Submit.
    public async Task<GenerationStatus> PollAsync(string jobId, string apiKey, CancellationToken ct) {
        using var http = GenerationHttp.Client(("Authorization", $"Key {apiKey}"));
        string statusJson = await http.GetStringAsync(jobId, ct);
        var state = ParseState(statusJson);
        if (state != GenerationState.Succeeded) {
            return state == GenerationState.Failed
                ? new GenerationStatus(GenerationState.Failed, Error: GenerationHttp.ErrorMessage(statusJson) ?? "Generation failed.")
                : new GenerationStatus(state);
        }
        // COMPLETED: the payload lives at the response URL (status minus "/status").
        string responseUrl = jobId.EndsWith("/status", StringComparison.Ordinal)
            ? jobId[..^"/status".Length]
            : jobId;
        string resultJson = await http.GetStringAsync(responseUrl, ct);
        return ParseResult(resultJson);
    }

    public static GenerationState ParseState(string statusJson) {
        using var doc = JsonDocument.Parse(statusJson);
        return (doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null) switch {
            "COMPLETED" => GenerationState.Succeeded,
            "IN_PROGRESS" => GenerationState.Running,
            "ERROR" or "FAILED" => GenerationState.Failed,
            _ => GenerationState.Queued,
        };
    }

    public static GenerationStatus ParseResult(string resultJson) {
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        // Single `video` object on most models; `videos` array on a few.
        string? url = root.TryGetProperty("video", out var video) ? GenerationHttp.FirstUrl(video) : null;
        url ??= root.TryGetProperty("videos", out var videos) ? GenerationHttp.FirstUrl(videos) : null;
        return url is null
            ? new GenerationStatus(GenerationState.Failed, Error: "fal.ai returned no video URL.")
            : new GenerationStatus(GenerationState.Succeeded, VideoUrl: url);
    }
}
