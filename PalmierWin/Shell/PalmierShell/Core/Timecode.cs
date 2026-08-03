namespace PalmierShell.Core;

/// HH:MM:SS:FF — the single formatting rule for the ruler, the transport, and
/// every other timecode surface.
public static class Timecode {
    public static string Format(int frame, int fps) {
        if (fps <= 0) return "00:00:00:00";
        int f = Math.Max(0, frame);
        int seconds = f / fps;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{f % fps:00}";
    }
}
