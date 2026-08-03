namespace PalmierShell.Core.Generation;

/// Runs one generation to completion: submit, poll, download. Progress is
/// reported through `onState`, always on the caller's continuation context.
public static class GenerationService {
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan Deadline = TimeSpan.FromMinutes(20);

    /// Where finished generations land. Kept outside the project until
    /// project packages exist on Windows.
    public static string OutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalmierPro", "Generated");

    /// Returns the downloaded file's path. Throws GenerationException with a
    /// user-facing message on any failure, including timeout and cancellation
    /// of the remote job.
    public static async Task<string> RunAsync(
        IGenerationProvider provider, GenerationRequest request, string apiKey,
        Action<GenerationStatus> onState, CancellationToken ct) {

        onState(new GenerationStatus(GenerationState.Queued));
        string jobId = await provider.SubmitAsync(request, apiKey, ct);

        var started = DateTime.UtcNow;
        GenerationStatus? last = null;
        while (true) {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - started > Deadline)
                throw new GenerationException("The generation did not finish within 20 minutes.");

            await Task.Delay(PollInterval, ct);
            var status = await provider.PollAsync(jobId, apiKey, ct);
            // Progress changes matter as much as state changes: this is the
            // difference between a live job and a counter next to a hang.
            if (status.State != last?.State || status.Progress != last?.Progress) {
                last = status;
                onState(status);
            }
            switch (status.State) {
                case GenerationState.Failed:
                    throw new GenerationException(status.Error ?? "Generation failed.");
                case GenerationState.Succeeded:
                    return await DownloadAsync(status.VideoUrl!, request, ct);
            }
        }
    }

    /// Streams to a temp file and moves it into place, so a partial download
    /// is never visible to the media library.
    static async Task<string> DownloadAsync(string url, GenerationRequest request, CancellationToken ct) {
        Directory.CreateDirectory(OutputDirectory);
        string name = $"{SafeName(request.Prompt)}-{Guid.NewGuid().ToString("N")[..8]}.mp4";
        string destination = Path.Combine(OutputDirectory, name);
        string staging = destination + ".part";

        using (var http = GenerationHttp.Client())
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)) {
            if (!response.IsSuccessStatusCode)
                throw new GenerationException($"Could not download the result (HTTP {(int)response.StatusCode}).");
            await using var file = File.Create(staging);
            await response.Content.CopyToAsync(file, ct);
        }
        File.Move(staging, destination, overwrite: true);
        return destination;
    }

    /// First few prompt words, safe for a filename. Dropping punctuation can
    /// empty a word, so runs of separators collapse to one.
    public static string SafeName(string prompt) {
        var words = prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Take(4)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(word => word.Length > 0);
        string cleaned = string.Join('-', words);
        return cleaned.Length == 0 ? "generated" : cleaned[..Math.Min(40, cleaned.Length)];
    }
}
