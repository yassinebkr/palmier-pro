using System.Net.Http.Json;
using System.Text.Json;

namespace PalmierShell.Core.Generation;

/// Replicate's predictions API: POST to the model's predictions endpoint,
/// then poll the prediction until `status` is terminal.
/// https://replicate.com/docs/reference/http
public sealed class ReplicateProvider : IGenerationProvider {
    public string Id => "replicate";
    public string Name => "Replicate";
    public string KeyHint => "replicate.com/account/api-tokens";

    /// Replicate's Seedance 2.0 takes a first and last frame directly, in
    /// `image` and `last_frame_image`. Its `reference_images` array is a
    /// different mode — "character consistency, style guidance, and scene
    /// composition", the identity-transfer path — and the schema says the two
    /// are mutually exclusive.
    ///
    /// Sending endpoint frames as reference images was wrong: it asks the
    /// model to carry a likeness rather than travel between two frames.
    /// Face rejection is a conditional input classifier ("may contain a real
    /// person"), not a blanket ban — identifiable faces in stills can be
    /// flagged on any mode, and the flag surfaces as the opaque "(E005)".
    public IReadOnlyList<GenerationModel> Models => ModelManifest.For("replicate");

    static GenerationModel? Curated(string modelId) =>
        ModelManifest.For("replicate").FirstOrDefault(m => m.Id == modelId);

    /// How the endpoint takes stills; None for any id we do not curate.
    static FrameInput Frames(string modelId) => Curated(modelId)?.Frames ?? FrameInput.None;

    public async Task<string> SubmitAsync(GenerationRequest request, string apiKey, CancellationToken ct) {
        using var http = GenerationHttp.Client(("Authorization", $"Bearer {apiKey}"));
        // Videos are far past the size a data URI tolerates, so they go up
        // through Replicate's files API first and travel as hosted URLs.
        var videoUrls = new List<string>();
        foreach (string path in request.ReferenceVideos)
            videoUrls.Add(await UploadFileAsync(http, path, ct));
        var input = BuildInput(request, videoUrls);
        var body = new Dictionary<string, object> { ["input"] = input };
        LogShape(request, input);
        using var response = await http.PostAsJsonAsync(
            $"https://api.replicate.com/v1/models/{request.Model}/predictions", body, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GenerationException(GenerationHttp.ErrorMessage(json) ?? $"HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } jobId) {
            // The id is what makes a failure investigable: the prediction's
            // page on Replicate shows the exact input the model received.
            Console.Error.WriteLine($"[generate] prediction {jobId}");
            return jobId;
        }
        throw new GenerationException("Replicate did not return a prediction id.");
    }

    /// Uploads one local file to Replicate's files API and returns its hosted
    /// URL. Reference videos cannot ride inline: multi-megabyte data URIs are
    /// past what the predictions endpoint accepts.
    static async Task<string> UploadFileAsync(HttpClient http, string path, CancellationToken ct) {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        form.Add(bytes, "content", Path.GetFileName(path));
        using var response = await http.PostAsync("https://api.replicate.com/v1/files", form, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GenerationException(
                $"Reference upload failed: {GenerationHttp.ErrorMessage(json) ?? $"HTTP {(int)response.StatusCode}"}");
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("urls", out var urls)
            && urls.TryGetProperty("get", out var get)
            && get.GetString() is { Length: > 0 } url)
            return url;
        throw new GenerationException("Reference upload returned no URL.");
    }

    /// Assembles the model input. Split out so the request shape is testable
    /// without a network call — `referenceVideoUrls` are the hosted URLs the
    /// upload step produced for `request.ReferenceVideos`, in order.
    /// Replicate rejects input keys a model's schema does not declare, so
    /// every field goes under the name — and in the vocabulary — of the model
    /// family actually selected.
    public static Dictionary<string, object> BuildInput(GenerationRequest request,
                                                        IReadOnlyList<string>? referenceVideoUrls = null) {
        var input = new Dictionary<string, object> {
            ["prompt"] = request.Prompt,
            ["duration"] = request.Seconds,
        };
        bool kling = request.Model.Contains("kling-v3", StringComparison.OrdinalIgnoreCase);
        if (request.Model.Contains("seedance-2", StringComparison.OrdinalIgnoreCase)
            || request.Model.Contains("veo-3", StringComparison.OrdinalIgnoreCase))
            input["resolution"] = request.Resolution;
        else if (kling)
            input["mode"] = request.Resolution switch {
                "1080p" => "pro", "4k" => "4k", _ => "standard",
            };
        // Its schema default is true, so every run was also synthesising
        // dialogue, effects and music into a clip that drops onto a timeline
        // with its own sound — unasked for, and paid for.
        if (Curated(request.Model)?.SynthesisesAudio == true)
            input["generate_audio"] = false;
        // Kling's negative channel is a dedicated field; other families take
        // exclusions inside the prompt, where the style already puts them.
        if (kling && request.NegativePrompt is { Length: > 0 } negative)
            input["negative_prompt"] = negative;

        // Reference-to-video (Seedance 2.0): user-attached images inline as
        // data URIs, videos as the hosted URLs the upload step produced. The
        // schema forbids mixing these with first/last frames — the composer
        // enforces that, and this layer honours the refs when both slip in,
        // because refs were the explicit attachment.
        bool hasReferences = request.ReferenceImages.Count > 0 || request.ReferenceVideos.Count > 0;
        if (hasReferences && Curated(request.Model) is { AcceptsReferences: true }) {
            var images = request.ReferenceImages
                .Select(path => GenerationHttp.DataUri(path)).Where(uri => uri is not null).Cast<object>().ToList();
            if (images.Count > 0) input["reference_images"] = images;
            if (referenceVideoUrls is { Count: > 0 })
                input["reference_videos"] = referenceVideoUrls.Cast<object>().ToList();
            return input;
        }

        // FLUX.3 is one endpoint whose optional inputs pick the workflow:
        // `images` (one opens the clip, two start and end it), `start_video`
        // to continue from a clip's final frames — the schema forbids
        // combining them — and neither for text-to-video. Its duration field
        // is a string ("auto" or the seconds), unlike the families above.
        if (Curated(request.Model) is { Family: "flux" } flux) {
            input["duration"] = request.Seconds.ToString();
            input["resolution"] = request.Resolution;
            if (request.Draft && flux.Capabilities.Contains("draft"))
                input["draft"] = true;
            if (referenceVideoUrls is { Count: > 0 } videos)
                input["start_video"] = videos[0];
            else {
                // Same rule as the other families: a lone end frame is never
                // sent — as images[0] it would OPEN the clip, the exact
                // opposite of the intent, and the run would be paid for.
                var images = new List<object>();
                if (GenerationHttp.DataUri(request.FirstFrame) is { } opens) {
                    images.Add(opens);
                    if (GenerationHttp.DataUri(request.LastFrame) is { } ends)
                        images.Add(ends);
                }
                if (images.Count > 0) input["images"] = images;
            }
            return input;
        }

        (string firstKey, string lastKey) = kling
            ? ("start_image", "end_image") : ("image", "last_frame_image");
        switch (Frames(request.Model)) {
            case FrameInput.FirstLast:
                // The end-frame field only works alongside a first frame, so a
                // lone end frame is not sent — the model would ignore it and
                // the run would be paid for either way.
                if (GenerationHttp.DataUri(request.FirstFrame) is { } start) {
                    input[firstKey] = start;
                    if (GenerationHttp.DataUri(request.LastFrame) is { } end)
                        input[lastKey] = end;
                }
                break;
            case FrameInput.FirstOnly:
                // No last-frame field exists; the composer says so up front
                // rather than this code dropping the still silently.
                if (GenerationHttp.DataUri(request.FirstFrame) is { } opening)
                    input[firstKey] = opening;
                break;
            case FrameInput.References:
                // Reference order is the contract: [Image1] is the first
                // image, [Image2] the second. The prompt style names them the
                // same way.
                var references = new List<string>();
                if (GenerationHttp.DataUri(request.FirstFrame) is { } a) references.Add(a);
                if (GenerationHttp.DataUri(request.LastFrame) is { } b) references.Add(b);
                if (references.Count > 0) input["reference_images"] = references;
                break;
        }
        return input;
    }

    /// Records which input keys a run actually used, and how big the inlined
    /// stills were. Never the image bytes and never the key — just enough to
    /// tell, after a failure, whether the request was the shape intended. A
    /// provider error that says nothing about the request is otherwise
    /// impossible to tell apart from sending the wrong request.
    static void LogShape(GenerationRequest request, Dictionary<string, object> input) {
        static string Size(string? path) =>
            path is not null && File.Exists(path) ? $"{new FileInfo(path).Length / 1024} KB" : "none";
        Console.Error.WriteLine(
            $"[generate] {request.Model} keys=[{string.Join(", ", input.Keys.Order())}] " +
            $"first={Size(request.FirstFrame)} last={Size(request.LastFrame)} " +
            $"refs={request.ReferenceImages.Count}i/{request.ReferenceVideos.Count}v " +
            $"resolution={request.Resolution} seconds={request.Seconds}");
    }

    public async Task<GenerationStatus> PollAsync(string jobId, string apiKey, CancellationToken ct) {
        using var http = GenerationHttp.Client(("Authorization", $"Bearer {apiKey}"));
        string json = await http.GetStringAsync($"https://api.replicate.com/v1/predictions/{jobId}", ct);
        var status = ParseStatus(json);
        // Carry the id into the message. Replicate collapses upstream failures
        // into an opaque string — "(E005)" is not even its own error code — so
        // the prediction's own page, which shows the exact input the model
        // received, is the only way to tell a rejected request from a wrong one.
        return status is { State: GenerationState.Failed, Error: { } error }
            ? status with { Error = $"{error}\nreplicate.com/p/{jobId}" }
            : status;
    }

    /// Split out so the response mapping is testable without a network call.
    public static GenerationStatus ParseStatus(string json) {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        switch (status) {
            case "succeeded":
                string? url = GenerationHttp.FirstUrl(root.TryGetProperty("output", out var output) ? output : default);
                return url is null
                    ? new GenerationStatus(GenerationState.Failed, Error: "Replicate returned no video URL.")
                    : new GenerationStatus(GenerationState.Succeeded, VideoUrl: url);
            case "failed":
            case "canceled":
                string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                return new GenerationStatus(GenerationState.Failed,
                    Error: error.Length > 0 ? error : $"Generation {status}.");
            case "processing":
                return new GenerationStatus(GenerationState.Running) {
                    Progress = ProgressFromLogs(
                        root.TryGetProperty("logs", out var logs) ? logs.GetString() : null),
                };
            default:
                return new GenerationStatus(GenerationState.Queued);
        }
    }

    /// The prediction's log stream is the only live progress Replicate has —
    /// models emit "NN%" lines as they render. The last percentage wins; a
    /// model that logs nothing reports null rather than a fake number.
    public static int? ProgressFromLogs(string? logs) {
        if (string.IsNullOrEmpty(logs)) return null;
        var matches = PercentPattern.Matches(logs);
        if (matches.Count == 0) return null;
        int value = int.Parse(matches[^1].Groups[1].Value);
        return value is >= 0 and <= 100 ? value : null;
    }

    static readonly System.Text.RegularExpressions.Regex PercentPattern =
        new(@"(\d{1,3})\s?%", System.Text.RegularExpressions.RegexOptions.Compiled);
}
