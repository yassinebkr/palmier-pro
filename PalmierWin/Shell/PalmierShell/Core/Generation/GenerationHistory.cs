using System.Text.Json;

namespace PalmierShell.Core.Generation;

/// One finished generation still on disk: the take's file plus the provenance
/// its sidecar recorded, for the composer's recent-generations list.
public sealed record RecentGeneration(string MediaPath, string Model, string Prompt,
                                      DateTimeOffset CreatedUtc);

/// Reads the generation sidecars back: newest first, capped, takes whose
/// files have been deleted already forgotten. Synchronous file I/O — call off
/// the UI thread. Fail-soft throughout: a corrupt sidecar is skipped and an
/// unreadable directory reads as empty, never as an error.
public static class GenerationHistory {
    public const int DefaultLimit = 10;

    public static IReadOnlyList<RecentGeneration> Load(string directory,
                                                       int limit = DefaultLimit) {
        try {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "*.generation.json")
                .Select(ReadEntry)
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .OrderByDescending(entry => entry.CreatedUtc)
                .Take(limit)
                .ToList();
        } catch {
            return [];
        }

        static RecentGeneration? ReadEntry(string sidecarPath) {
            const string suffix = ".generation.json";
            string mediaPath = sidecarPath[..^suffix.Length];
            if (!File.Exists(mediaPath)) return null;
            try {
                var record = JsonSerializer.Deserialize<GenerationRecord>(
                    File.ReadAllText(sidecarPath));
                // The positional record parses even "{}" — members just come
                // out null. A prompt-less sidecar is skipped like a corrupt one.
                if (string.IsNullOrEmpty(record?.Prompt)) return null;
                var created = DateTimeOffset.TryParse(record.CreatedUtc, out var when)
                    ? when
                    : File.GetLastWriteTimeUtc(sidecarPath);
                return new RecentGeneration(mediaPath, record.Model ?? "", record.Prompt, created);
            } catch {
                return null;
            }
        }
    }
}
