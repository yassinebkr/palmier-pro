import PalmierCore

/// Where the playhead lands in a render plan, and where that lands inside a
/// source file.
///
/// Playback and export must answer both identically. They each carried their
/// own copy and drifted: export dropped `trimStartFrame`, so every trimmed or
/// blade-split clip encoded from the wrong place, and both treated a playhead
/// with nothing under it as "keep showing the last clip".
public enum TimelineLookup {
    /// The instruction covering `frame`, or nil when none does — a gap between
    /// clips, or past the end of the plan. Both present black; falling back to
    /// the last instruction instead left the previous clip on screen and, past
    /// the end, asked its decoder for a frame beyond the media on every tick.
    public static func segment(_ instructions: [RenderInstruction], frame: Int) -> RenderInstruction? {
        for instr in instructions where frame >= instr.frameRange.start && frame < instr.frameRange.end {
            return instr
        }
        return nil
    }

    /// Source-frame index for a layer at a timeline frame: the head trim plus
    /// the speed-scaled local offset. A blade split encodes the right half's
    /// start as `trimStartFrame`, so dropping it silently plays the left half.
    public static func sourceFrame(for layer: LayerPlan, timelineFrame: Int) -> Int {
        let local = timelineFrame - layer.clip.startFrame
        let scaled = Double(local) * layer.clip.speed
        return max(0, layer.clip.trimStartFrame + Int(scaled.rounded()))
    }
}
