// Windows vertical slice (port-roadmap Option C). Proves the toolchain works
// for a real app surface end-to-end: PalmierCore (portable model + engines) +
// PalmierWin (Windows media bindings) link and run together. Does not decode
// or render video — that's the media-engine spike. It builds a tiny timeline,
// runs the ripple engine + render planner, and initializes Media Foundation.

import PalmierCore
import PalmierWin

@main
struct VerticalSlice {
    static func main() {
        print("=== Palmier Pro Windows vertical slice ===")

        // 1) Portable core: build a 2-clip timeline on one video track.
        var a = Clip(mediaRef: "media-a", startFrame: 0, durationFrames: 30); a.id = "a"
        var b = Clip(mediaRef: "media-b", startFrame: 30, durationFrames: 30); b.id = "b"
        var track = Track(type: .video)
        track.clips = [a, b]
        var timeline = Timeline()
        timeline.tracks = [track]
        print("timeline: \(timeline.tracks.count) track, \(timeline.tracks[0].clips.count) clips, \(timeline.totalFrames) frames")

        // 2) Ripple engine: shift clips after a removal.
        let shifts = RippleEngine.computeRippleShifts(clips: [a, b], removedIds: ["a"])
        for s in shifts {
            print("ripple: \(s.clipId) -> startFrame \(s.newStartFrame)")
        }

        // 3) Render planner: produce one RenderInstruction per segment.
        let slots: [String: TrackSlot] = [
            "a": TrackSlot(trackID: TrackID(rawValue: 1), natSize: Size2D(width: 1920, height: 1080), transform: .identity),
            "b": TrackSlot(trackID: TrackID(rawValue: 2), natSize: Size2D(width: 1920, height: 1080), transform: .identity),
        ]
        let plan = RenderPlanner.plan(
            timeline: timeline, renderSize: Size2D(width: 1920, height: 1080),
            totalFrames: timeline.totalFrames, trackSlots: slots, resolveTimeline: { _ in nil }
        )
        print("planner: \(plan.count) segment(s)")
        for instr in plan {
            print("  segment [\(instr.frameRange.start), \(instr.frameRange.end)) — \(instr.layers.count) layer(s)")
        }

        // 4) Windows media: initialize Media Foundation (first real Win32 API call).
        let mf = MediaFoundationSession()
        print("Media Foundation: \(mf.isActive ? "started OK" : "FAILED to start")")

        print("=== slice complete ===")
    }
}
