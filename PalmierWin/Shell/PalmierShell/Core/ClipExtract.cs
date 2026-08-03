using System.Diagnostics;
using System.Globalization;

namespace PalmierShell.Core;

/// Extracts a short piece of a clip's source media to its own file — the
/// video context a reference-to-video generation feeds on. Source-range math
/// comes from the clip's own trim/speed mapping (ClipState.SourceFrameAt),
/// the same mapping the engine reads frames with.
public static class ClipExtract {
    /// The last `frames` timeline frames of the clip. Blocking process work —
    /// call off the UI thread.
    public static string? SaveTail(ClipState clip, int frames, int fps) =>
        SaveRange(clip, Math.Max(clip.StartFrame, clip.EndFrame - frames), clip.EndFrame,
                  fps, "context-before");

    /// The first `frames` timeline frames of the clip.
    public static string? SaveHead(ClipState clip, int frames, int fps) =>
        SaveRange(clip, clip.StartFrame, Math.Min(clip.EndFrame, clip.StartFrame + frames),
                  fps, "context-after");

    static string? SaveRange(ClipState clip, int fromFrame, int toFrame, int fps, string hint) {
        if (toFrame <= fromFrame) return null;
        if (clip.MediaRef is not { Length: > 0 } media || !File.Exists(media)) return null;

        double start = clip.SourceFrameAt(fromFrame) / (double)fps;
        double end = (clip.SourceFrameAt(Math.Max(fromFrame, toFrame - 1)) + 1) / (double)fps;
        double duration = Math.Max(0.2, end - start);

        Directory.CreateDirectory(FrameCapture.OutputDirectory);
        string output = Path.Combine(FrameCapture.OutputDirectory,
            $"{hint}-{Guid.NewGuid().ToString("N")[..8]}.mp4");

        // Re-encode rather than stream-copy: -c copy can only cut on
        // keyframes, and a reference that starts seconds early defeats the
        // point of choosing the span.
        string args = string.Create(CultureInfo.InvariantCulture,
            $"-y -ss {start:0.###} -i \"{media}\" -t {duration:0.###} " +
            $"-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac \"{output}\"");
        try {
            using var process = Process.Start(new ProcessStartInfo(FfmpegPath(), args) {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            });
            if (process is null) return null;
            process.StandardError.ReadToEnd();   // drain, or a full pipe deadlocks
            if (!process.WaitForExit(60_000)) {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 && File.Exists(output) ? output : null;
        } catch (Exception) {
            return null;
        }
    }

    /// run-shell puts the bundled ffmpeg on PATH; a packaged or test process
    /// may not have it, so probe upward for the ThirdParty copy as well.
    static string FfmpegPath() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) {
            string candidate = Path.Combine(dir.FullName, "ThirdParty", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "PalmierWin", "ThirdParty", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return "ffmpeg";
    }
}
