import Foundation

/// Immutable per-clip snapshot read on the render queue — never the live
/// timeline. This is the authoritative render-contract layer type, portable
/// across macOS and Windows. macOS bridges to AVFoundation at the
/// `CompositorInstruction`/`FrameRenderer` boundary: `preferredTransform: Mat3`
/// is byte-identical to `CGAffineTransform` under the layout documented in
/// `Mat3.concatenating`, and `TrackID.rawValue == CMPersistentTrackID`.
public struct LayerPlan: Sendable, Equatable {
    public enum Source: Sendable, Equatable {
        case track(TrackID)
        case text
        /// Nested timeline: children composite into a `canvas`-sized unit, then
        /// the nest clip's pipeline applies.
        case group(children: [LayerPlan], canvas: Size2D)
    }
    public let source: Source
    public let clip: Clip
    public let natSize: Size2D
    public let preferredTransform: Mat3

    public init(source: Source, clip: Clip, natSize: Size2D, preferredTransform: Mat3) {
        self.source = source
        self.clip = clip
        self.natSize = natSize
        self.preferredTransform = preferredTransform
    }

    public var trackID: TrackID? {
        if case .track(let id) = source { return id }
        return nil
    }

    public func collectTrackIDs(into ids: inout [TrackID]) {
        switch source {
        case .track(let id): ids.append(id)
        case .text: break
        case .group(let children, _):
            for child in children { child.collectTrackIDs(into: &ids) }
        }
    }
}

/// One timeline segment between clip boundaries, in portable form. Layers are
/// ordered bottom → top. The macOS `CompositorInstruction` adapts this to
/// AVFoundation's `AVVideoCompositionInstructionProtocol`; Windows consumes it
/// directly without that protocol layer.
public struct RenderInstruction: Sendable, Equatable {
    public let frameRange: FrameRange
    public let layers: [LayerPlan]
    public let renderSize: Size2D
    public let fps: Int

    public init(frameRange: FrameRange, layers: [LayerPlan], renderSize: Size2D, fps: Int) {
        self.frameRange = frameRange
        self.layers = layers
        self.renderSize = renderSize
        self.fps = fps
    }

    public var requiredTrackIDs: [TrackID] {
        var all: [TrackID] = []
        var seen = Set<TrackID]()
        for layer in layers {
            layer.collectTrackIDs(into: &all)
        }
        return all.filter { seen.insert($0).inserted }
    }
}

