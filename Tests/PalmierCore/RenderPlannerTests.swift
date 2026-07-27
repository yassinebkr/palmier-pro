import Testing
@testable import PalmierCore

@Suite("RenderPlanner")
struct RenderPlannerTests {

    private func mediaClip(_ id: String, start: Int, duration: Int) -> Clip {
        var c = Clip(mediaRef: "media-\(id)", startFrame: start, durationFrames: duration)
        c.id = id
        return c
    }

    private func slot(_ rawID: Int32) -> TrackSlot {
        TrackSlot(trackID: TrackID(rawValue: rawID), natSize: Size2D(width: 1920, height: 1080), transform: .identity)
    }

    private func timeline(_ tracks: [Track]) -> Timeline {
        var t = Timeline()
        t.tracks = tracks
        return t
    }

    @Test func singleClipProducesOneSegmentCoveringItsRange() {
        let clip = mediaClip("a", start: 0, duration: 60)
        let tl = timeline([Track(type: .video, clips: [clip])])
        let planned = RenderPlanner.plan(
            timeline: tl, renderSize: Size2D(width: 1920, height: 1080),
            totalFrames: 60, trackSlots: ["a": slot(1)], resolveTimeline: { _ in nil }
        )
        #expect(planned.count == 1)
        #expect(planned[0].frameRange == FrameRange(start: 0, end: 60))
        #expect(planned[0].layers.count == 1)
    }

    @Test func twoClipsOnOneTrackProduceSegmentPerCut() {
        // [0,30) and [30,60) on one track → two segments, one cut at 30.
        let a = mediaClip("a", start: 0, duration: 30)
        let b = mediaClip("b", start: 30, duration: 30)
        let tl = timeline([Track(type: .video, clips: [a, b])])
        let planned = RenderPlanner.plan(
            timeline: tl, renderSize: Size2D(width: 1920, height: 1080),
            totalFrames: 60, trackSlots: ["a": slot(1), "b": slot(2)], resolveTimeline: { _ in nil }
        )
        #expect(planned.count == 2)
        #expect(planned[0].frameRange == FrameRange(start: 0, end: 30))
        #expect(planned[1].frameRange == FrameRange(start: 30, end: 60))
    }

    @Test func overlappingClipsAcrossTracksSplitAtOverlapBoundaries() {
        // Top track [0,40), bottom track [20,60): three segments at 0,20,40,60.
        let top = mediaClip("top", start: 0, duration: 40)
        let bottom = mediaClip("bottom", start: 20, duration: 40)
        let tl = timeline([Track(type: .video, clips: [bottom]), Track(type: .video, clips: [top])])
        let planned = RenderPlanner.plan(
            timeline: tl, renderSize: Size2D(width: 1920, height: 1080),
            totalFrames: 60,
            trackSlots: ["top": slot(10), "bottom": slot(20)],
            resolveTimeline: { _ in nil }
        )
        #expect(planned.count == 3)
        #expect(planned.map(\.frameRange.start) == [0, 20, 40])
        #expect(planned.map(\.frameRange.end) == [20, 40, 60])
    }

    @Test func layersOrderedBottomToTopAcrossTracks() {
        // The planner walks timeline.tracks.reversed(), so the FIRST track in
        // the array becomes the TOP layer. With two overlapping clips, the
        // segment's layers read [first-track clip, second-track clip] = bottom→top.
        let firstTrackClip = mediaClip("first", start: 0, duration: 60)
        let secondTrackClip = mediaClip("second", start: 0, duration: 60)
        let tl = timeline([Track(type: .video, clips: [firstTrackClip]), Track(type: .video, clips: [secondTrackClip])])
        let planned = RenderPlanner.plan(
            timeline: tl, renderSize: Size2D(width: 1920, height: 1080),
            totalFrames: 60,
            trackSlots: ["first": slot(10), "second": slot(20)],
            resolveTimeline: { _ in nil }
        )
        #expect(planned.count == 1)
        // Reversed walk: second-track clip is composited first (bottom of stack).
        let layerIDs = planned[0].layers.compactMap { $0.clip.id }
        #expect(layerIDs == ["second", "first"])
    }

    @Test func clipWithoutTrackSlotYieldsEmptyLayersSegment() {
        // An offline clip (no slot) is skipped at LayerPlan construction, so it
        // contributes no layer. The segment scaffolding still covers its range
        // with an empty layer list — matches the AVFoundation planner's behavior
        // (an all-offline timeline renders black, not nothing).
        let offline = mediaClip("offline", start: 0, duration: 30)
        let tl = timeline([Track(type: .video, clips: [offline])])
        let planned = RenderPlanner.plan(
            timeline: tl, renderSize: Size2D(width: 1920, height: 1080),
            totalFrames: 30, trackSlots: [:], resolveTimeline: { _ in nil }
        )
        #expect(planned.count == 1)
        #expect(planned[0].frameRange == FrameRange(start: 0, end: 30))
        #expect(planned[0].layers.isEmpty)
    }
}
