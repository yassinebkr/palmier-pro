import Foundation

/// The frame-domain scheduling core of the renderer: turns a `Timeline` plus a
/// map of where each inserted clip lives on the decode tracks into one
/// `RenderInstruction` per segment between clip boundaries, layers ordered
/// bottom → top. Pure `Timeline`/`Clip`/frame math — no platform types.
///
/// macOS wraps each result in `CompositorInstruction` (AVFoundation's
/// `AVVideoCompositionInstructionProtocol`); Windows consumes `RenderInstruction`
/// directly. Both get identical scheduling because they share this planner.
public enum RenderPlanner {

    /// Produce one `RenderInstruction` per segment between clip boundaries.
    /// - Parameters:
    ///   - timeline: the timeline being rendered.
    ///   - renderSize: output canvas size.
    ///   - totalFrames: composition length in frames (segment bounds clamp to it).
    ///   - trackSlots: clip-id → where its decoded frames come from. Only clips
    ///     present here render as media layers; others are skipped (offline).
    ///   - resolveTimeline: resolves a `.sequence` (nest) carrier's child
    ///     timeline by mediaRef. Returns nil for unresolvable nests.
    public static func plan(
        timeline: Timeline,
        renderSize: Size2D,
        totalFrames: Int,
        trackSlots: [String: TrackSlot],
        resolveTimeline: @Sendable (String) -> Timeline?
    ) -> [RenderInstruction] {
        // Flatten is pure per carrier — memoize; segments reuse one result.
        var flattenCache: [String: NestFlattener.Flattened] = [:]
        func flattened(for carrier: Clip, depth: Int) -> NestFlattener.Flattened? {
            guard depth < NestFlattener.maxDepth else { return nil }
            if let cached = flattenCache[carrier.id] { return cached }
            guard let child = resolveTimeline(carrier.mediaRef) else { return nil }
            let flat = NestFlattener.flatten(carrier: carrier, child: child, visual: true)
            flattenCache[carrier.id] = flat
            return flat
        }

        // Group layer for one segment window; empty children still render (nest gaps are opaque black).
        func nestGroupPlan(carrier: Clip, depth: Int, window: Range<Int>) -> LayerPlan? {
            guard let flat = flattened(for: carrier, depth: depth) else { return nil }
            let childCanvas = flat.childCanvas
            var children: [LayerPlan] = []
            for childClips in flat.videoTracks.reversed() {
                var prevEnd = Int.min
                for clip in childClips where clip.durationFrames > 0 {
                    let overlapsWindow = clip.startFrame < window.upperBound && clip.endFrame > window.lowerBound
                    if clip.mediaType == .text {
                        guard overlapsWindow, !(clip.textContent ?? "").isEmpty else { continue }
                        children.append(LayerPlan(source: .text, clip: clip, natSize: childCanvas, preferredTransform: .identity))
                    } else if clip.mediaType == .sequence {
                        guard clip.startFrame >= prevEnd else { continue }
                        prevEnd = clip.endFrame
                        guard overlapsWindow, let plan = nestGroupPlan(carrier: clip, depth: depth + 1, window: window) else { continue }
                        children.append(plan)
                    } else {
                        guard clip.startFrame >= prevEnd, let slot = trackSlots[clip.id] else { continue }
                        prevEnd = clip.endFrame
                        guard overlapsWindow else { continue }
                        children.append(LayerPlan(source: .track(slot.trackID), clip: clip, natSize: slot.natSize, preferredTransform: slot.transform))
                    }
                }
            }
            return LayerPlan(source: .group(children: children, canvas: childCanvas),
                             clip: carrier, natSize: childCanvas, preferredTransform: .identity)
        }

        // Child clip boundaries: segments scope decoder demand to what's visible.
        func nestCutFrames(carrier: Clip, depth: Int) -> [Int] {
            guard let flat = flattened(for: carrier, depth: depth) else { return [] }
            var frames: [Int] = []
            for childClips in flat.videoTracks {
                for clip in childClips {
                    frames.append(clip.startFrame)
                    frames.append(clip.endFrame)
                    if clip.mediaType == .sequence {
                        frames.append(contentsOf: nestCutFrames(carrier: clip, depth: depth + 1))
                    }
                }
            }
            return frames.filter { $0 > carrier.startFrame && $0 < carrier.endFrame }
        }

        struct Entry { let start: Int; let end: Int; let plan: LayerPlan }

        // Walk tracks in reverse to produce bottom→top entries. Text layers follow track order.
        var entries: [Entry] = []
        for track in timeline.tracks.reversed() where !track.hidden {
            var prevEndFrame = Int.min
            for clip in track.clips.sorted(by: { $0.startFrame < $1.startFrame }) where clip.durationFrames > 0 {
                let plan: LayerPlan
                if clip.mediaType == .text {
                    guard !(clip.textContent ?? "").isEmpty else { continue }
                    plan = LayerPlan(source: .text, clip: clip, natSize: renderSize, preferredTransform: .identity)
                } else if clip.mediaType == .sequence {
                    guard clip.startFrame >= prevEndFrame else { continue }
                    prevEndFrame = clip.endFrame
                    // One entry per child-boundary segment: each requires only the
                    // source tracks visible in that segment.
                    let bounds = ([clip.startFrame, clip.endFrame] + nestCutFrames(carrier: clip, depth: 0))
                        .reduce(into: Set<Int>()) { $0.insert($1) }
                        .sorted()
                    for i in 0..<(bounds.count - 1) {
                        let window = bounds[i]..<bounds[i + 1]
                        guard window.count > 0,
                              let group = nestGroupPlan(carrier: clip, depth: 0, window: window) else { continue }
                        entries.append(Entry(start: window.lowerBound, end: window.upperBound, plan: group))
                    }
                    continue
                } else {
                    guard clip.startFrame >= prevEndFrame, let slot = trackSlots[clip.id] else { continue }
                    plan = LayerPlan(source: .track(slot.trackID), clip: clip, natSize: slot.natSize, preferredTransform: slot.transform)
                    prevEndFrame = clip.endFrame
                }
                entries.append(Entry(start: clip.startFrame, end: clip.endFrame, plan: plan))
            }
        }

        var cutSet = Set<Int>()
        for e in entries {
            cutSet.insert(e.start)
            cutSet.insert(e.end)
        }
        let cuts = cutSet.filter { $0 > 0 && $0 < totalFrames }.sorted()
        let bounds = [0] + cuts + [totalFrames]

        var startsByFrame: [Int: [Int]] = [:]
        var endsByFrame: [Int: [Int]] = [:]
        for (index, entry) in entries.enumerated() {
            startsByFrame[entry.start, default: []].append(index)
            endsByFrame[entry.end, default: []].append(index)
        }

        var active: [Int] = []
        var activeSet = Set<Int>()

        func insertActive(_ index: Int) {
            guard activeSet.insert(index).inserted else { return }
            var low = 0
            var high = active.count
            while low < high {
                let mid = (low + high) / 2
                if active[mid] < index {
                    low = mid + 1
                } else {
                    high = mid
                }
            }
            active.insert(index, at: low)
        }

        func removeActive(_ index: Int) {
            guard activeSet.remove(index) != nil else { return }
            var low = 0
            var high = active.count
            while low < high {
                let mid = (low + high) / 2
                if active[mid] < index {
                    low = mid + 1
                } else {
                    high = mid
                }
            }
            if low < active.count, active[low] == index {
                active.remove(at: low)
            }
        }

        for (index, entry) in entries.enumerated() where entry.start < 0 && entry.end > 0 {
            insertActive(index)
        }

        var instructions: [RenderInstruction] = []
        instructions.reserveCapacity(max(0, bounds.count - 1))
        for i in 0..<(bounds.count - 1) {
            let start = bounds[i]
            for index in endsByFrame[start] ?? [] { removeActive(index) }
            for index in startsByFrame[start] ?? [] { insertActive(index) }

            let range = FrameRange(start: bounds[i], end: bounds[i + 1])
            guard range.length > 0 else { continue }
            let layers = active.map { entries[$0].plan }
            instructions.append(RenderInstruction(
                frameRange: range, layers: layers, renderSize: renderSize, fps: timeline.fps
            ))
        }
        return instructions
    }
}
