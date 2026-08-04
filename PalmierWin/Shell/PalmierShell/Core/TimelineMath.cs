namespace PalmierShell.Core;

/// Pure geometry and playback math for the timeline views and the engine
/// render loop, kept here so the views stay thin and the rules stay testable.
public static class TimelineMath {
    public const double MinPixelsPerFrame = 0.5;
    public const double MaxPixelsPerFrame = 16;

    /// The scroll offset after a zoom change that keeps an anchor frame at
    /// the same screen x: the playhead when it is visible, otherwise whatever
    /// sits at the view's centre.
    public static double AnchoredZoomScroll(double oldPpf, double newPpf, double scroll,
                                            double viewportWidth, int playheadFrame) {
        double anchorX = playheadFrame * oldPpf - scroll;
        double anchorFrame = playheadFrame;
        if (anchorX < 0 || anchorX > viewportWidth) {
            anchorX = viewportWidth / 2;
            anchorFrame = (scroll + anchorX) / oldPpf;
        }
        return Math.Max(0, anchorFrame * newPpf - anchorX);
    }

    /// The scroll offset that centres `frame`, clamped so the view can never
    /// scroll past either end of the content.
    public static double ScrollCentering(int frame, double ppf, double viewportWidth, int totalFrames) {
        double max = Math.Max(0, totalFrames * ppf - viewportWidth);
        return Math.Clamp(frame * ppf - viewportWidth / 2, 0, max);
    }

    /// The main view's visible range mapped into overview-strip coordinates:
    /// strip x 0..stripWidth covers frames 0..totalFrames.
    public static (double X, double Width) OverviewViewport(double scroll, double ppf,
            double viewportWidth, int totalFrames, double stripWidth) {
        if (totalFrames <= 0 || stripWidth <= 0 || ppf <= 0) return (0, Math.Max(0, stripWidth));
        double scale = stripWidth / totalFrames;
        double x = Math.Clamp(scroll / ppf * scale, 0, stripWidth);
        double w = Math.Clamp(viewportWidth / ppf * scale, 0, stripWidth - x);
        return (x, w);
    }

    /// dB-mapped display level (0…1) for a peak magnitude over a fixed 50 dB
    /// window — upstream's macOS mapping. No per-clip boost: loud media keeps
    /// its valleys instead of stretching to a full-height block.
    public static float WaveformLevel(float peak) {
        if (peak <= 0) return 0;
        float db = 20f * MathF.Log10(peak);
        return Math.Clamp((db + 50f) / 50f, 0f, 1f);
    }

    /// Where forward playback should start given a set loop: a playhead
    /// parked outside the loop jumps to its start, one inside stays put.
    public static int LoopEntryFrame(int playhead, int loopStart, int loopEnd) =>
        loopStart >= 0 && loopEnd > loopStart && (playhead < loopStart || playhead >= loopEnd)
            ? loopStart
            : playhead;

    /// Clamps a loop edge drag: start stays below end, end above start, at
    /// least one frame of loop, inside the timeline.
    public static int ClampLoopEdge(int edge, int frame, int? loopStart, int? loopEnd, int totalFrames) {
        if (edge == 0) {
            int max = loopEnd is { } e ? e - 1 : totalFrames;
            return Math.Clamp(frame, 0, Math.Max(0, max));
        }
        int min = loopStart is { } s ? s + 1 : 1;
        return Math.Clamp(frame, min, Math.Max(min, totalFrames));
    }

    /// One playback tick. A set loop range wins when the tick crosses its end;
    /// otherwise normal play wraps to the start at the timeline's end while
    /// the shuttle clamps there and stops.
    public static (int Next, bool Wrapped, bool Stop) AdvancePlayhead(
            int current, int step, int totalFrames, int loopStart, int loopEnd) {
        if (step > 0 && loopStart >= 0 && loopEnd > loopStart &&
            current < loopEnd && current + step >= loopEnd)
            return (loopStart, true, false);
        int next = current + step;
        if (step == 1 && next >= totalFrames) return (0, true, false);
        if (next < 0 || next >= totalFrames)
            return (Math.Clamp(next, 0, Math.Max(0, totalFrames - 1)), false, true);
        return (next, false, false);
    }
}
