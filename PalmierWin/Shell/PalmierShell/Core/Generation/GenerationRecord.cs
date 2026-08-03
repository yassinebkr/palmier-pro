using System.Text.Json;

namespace PalmierShell.Core.Generation;

/// What produced a generated clip. Written as a sidecar next to the media so
/// a clip can still explain itself months later — which model, which prompt,
/// which stills it travelled between.
public sealed record GenerationRecord(
    string Provider, string Model, string Prompt, int Seconds, string CreatedUtc) {
    public string? FirstFrame { get; init; }
    public string? LastFrame { get; init; }

    /// Sidecar path for a media file: "clip.mp4" → "clip.mp4.generation.json".
    public static string SidecarFor(string mediaPath) => mediaPath + ".generation.json";

    public static void Write(string mediaPath, GenerationRecord record) {
        try {
            File.WriteAllText(SidecarFor(mediaPath), JsonSerializer.Serialize(record));
        } catch {
            // Provenance is a bonus; never fail a finished generation over it.
        }
    }

    /// Null when the file was not generated here, or the sidecar is unreadable.
    public static GenerationRecord? Read(string mediaPath) {
        try {
            string path = SidecarFor(mediaPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<GenerationRecord>(File.ReadAllText(path))
                : null;
        } catch {
            return null;
        }
    }
}
