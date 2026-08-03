using System.Text.Json;

namespace PalmierShell.Core;

/// One detected silent span in source-media time (milliseconds).
public readonly record struct SilentRange(int StartMs, int EndMs) {
    public int DurationMs => EndMs - StartMs;
}

/// Silence removal domain logic: parsing the core's detection result,
/// mapping source-time spans onto the clip's timeline frames, and applying
/// the cuts through the existing range-delete intent.
public static class SilenceRemoval {
    public static List<SilentRange> ParseRanges(string json) {
        using var doc = JsonDocument.Parse(json);
        var ranges = new List<SilentRange>();
        foreach (var e in doc.RootElement.EnumerateArray())
            ranges.Add(new SilentRange(e.GetProperty("startMs").GetInt32(),
                                       e.GetProperty("endMs").GetInt32()));
        return ranges;
    }

    /// Converts source-ms spans into timeline frame ranges [start, end),
    /// respecting the clip's head trim and speed, clipped to its visible
    /// span. Spans landing fully outside the clip are dropped.
    public static List<(int Start, int End)> TimelineRanges(
        ClipState clip, IReadOnlyList<SilentRange> ranges, double fps) {
        var spans = new List<(int Start, int End)>();
        foreach (var r in ranges) {
            double sourceStart = r.StartMs * fps / 1000.0;
            double sourceEnd = r.EndMs * fps / 1000.0;
            int start = clip.StartFrame + (int)Math.Round((sourceStart - clip.TrimStartFrame) / clip.Speed);
            int end = clip.StartFrame + (int)Math.Round((sourceEnd - clip.TrimStartFrame) / clip.Speed);
            start = Math.Max(start, clip.StartFrame);
            end = Math.Min(end, clip.EndFrame);
            if (end > start) spans.Add((start, end));
        }
        return spans;
    }

    /// Ripple-deletes every span across all tracks — linked partners are cut
    /// at the same times by the range delete itself. Latest span first, so
    /// the earlier cuts stay valid as the timeline closes up. Returns how
    /// many clips the cuts touched.
    public static int Apply(IntPtr project, IReadOnlyList<(int Start, int End)> spans) {
        int touched = 0;
        for (int i = spans.Count - 1; i >= 0; i--)
            touched += CoreApi.palmier_timeline_delete_range(project, spans[i].Start, spans[i].End, 1);
        return touched;
    }
}
