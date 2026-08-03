namespace PalmierShell.Core;

/// Maps a clip's on-timeline tiles into its window of the media's filmstrip.
///
/// The strip is 8 tiles sampled across the *whole* file, but a clip usually
/// shows only a slice of it — a split's right half starts mid-file, a trimmed
/// clip starts past the head. Spreading the full strip across every clip made
/// each half of a cut show the entire movie squeezed into its own width, which
/// reads as the thumbnails breaking whenever a clip is cut.
public static class FilmstripWindow {
    /// Strip index for tile `tile` of `tileCount` drawn across a clip that
    /// shows `[trimStart, trimStart + sourceFramesConsumed)` of a media that is
    /// `sourceTotalFrames` long (all in timeline-fps frames). A media of
    /// unknown length falls back to spreading the whole strip.
    public static int TileIndex(int tile, int tileCount, int stripLength,
                                int trimStart, int sourceFramesConsumed, int sourceTotalFrames) {
        if (stripLength <= 0) return 0;
        double centre = (tile + 0.5) / Math.Max(1, tileCount);
        double fraction;
        if (sourceTotalFrames > 0 && sourceTotalFrames != int.MaxValue) {
            double start = Math.Clamp((double)trimStart / sourceTotalFrames, 0, 1);
            double end = Math.Clamp((double)(trimStart + sourceFramesConsumed) / sourceTotalFrames, start, 1);
            fraction = start + centre * (end - start);
        } else {
            fraction = centre;
        }
        return Math.Clamp((int)(fraction * stripLength), 0, stripLength - 1);
    }
}
