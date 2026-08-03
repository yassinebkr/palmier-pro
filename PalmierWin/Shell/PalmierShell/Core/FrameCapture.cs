using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PalmierShell.Core;

/// Pulls a full-resolution frame out of a media file and writes it as a PNG.
/// Backs "capture frame to media" and the first/last stills a generated
/// transition is built from.
public static class FrameCapture {
    /// Where captured stills live until projects can hold their own media.
    public static string OutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalmierPro", "Frames");

    /// Decodes `timelineFrame` of `mediaPath` and saves it as a PNG, returning
    /// the file path. Null when the frame cannot be decoded. Blocking work —
    /// call it off the UI thread.
    ///
    /// The frame index is in the timeline's domain at `timelineFps`, matching
    /// how clip trims are stored, not the media file's own rate.
    public static string? SaveFrame(string mediaPath, int timelineFrame, int timelineFps,
                                    string? nameHint = null) {
        if (CoreApi.ProbeMedia(mediaPath) is not { } probe || probe.Width <= 0 || probe.Height <= 0)
            return null;

        var pixels = new byte[probe.Width * probe.Height * 4];
        if (CoreApi.palmier_extract_frame(mediaPath, Math.Max(0, timelineFrame), timelineFps,
                                          pixels, pixels.Length) != 1)
            return null;

        Directory.CreateDirectory(OutputDirectory);
        string stem = nameHint ?? Path.GetFileNameWithoutExtension(mediaPath);
        string path = Path.Combine(OutputDirectory,
            $"{Sanitise(stem)}-{timelineFrame:D6}-{Guid.NewGuid().ToString("N")[..6]}.png");

        var bitmap = new WriteableBitmap(
            new PixelSize(probe.Width, probe.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var locked = bitmap.Lock()) {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
        }
        bitmap.Save(path);
        return path;
    }

    /// Saves timeline `frame` as the preview composites it — layers, trims,
    /// speeds and transforms included. This is what transition and shot stills
    /// are cut from: upstream captures the composite for exactly this, so the
    /// still can never disagree with what the user sees at that frame.
    /// Blocking GPU + decode work — call off the UI thread.
    public static string? SaveTimelineFrame(IntPtr project, int frame, string? nameHint = null) {
        int result = CoreApi.palmier_project_capture_frame(project, Math.Max(0, frame),
                                                           [], 0, out _, out _);
        if (result >= 0) return null;   // only a required-size reply is expected

        var pixels = new byte[-result];
        if (CoreApi.palmier_project_capture_frame(project, Math.Max(0, frame),
                                                  pixels, pixels.Length,
                                                  out int width, out int height) != 1)
            return null;
        if (width <= 0 || height <= 0) return null;

        Directory.CreateDirectory(OutputDirectory);
        string path = Path.Combine(OutputDirectory,
            $"{Sanitise(nameHint ?? "frame")}-{frame:D6}-{Guid.NewGuid().ToString("N")[..6]}.png");
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var locked = bitmap.Lock()) {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
        }
        bitmap.Save(path);
        return path;
    }

    static string Sanitise(string name) {
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length == 0 ? "frame" : cleaned[..Math.Min(40, cleaned.Length)];
    }
}
