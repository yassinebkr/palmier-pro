import AVFoundation
import CoreGraphics
import PalmierCore

// `LayerPlan` lives in `PalmierCore` (portable: `TrackID`/`Size2D`/`Mat3`).
// This file adapts the portable segment type to AVFoundation's compositor
// protocol — the only place Apple types appear in the render contract.

/// One timeline segment adapted to `AVVideoCompositionInstructionProtocol`.
/// Layers are the portable `PalmierCore.LayerPlan`; AVFoundation-required
/// fields (`requiredSourceTrackIDs`, `passthroughTrackID`) bridge to
/// `CMPersistentTrackID` here, the only layer that needs to know that
/// `TrackID.rawValue == CMPersistentTrackID`.
final class CompositorInstruction: NSObject, AVVideoCompositionInstructionProtocol, @unchecked Sendable {
    let timeRange: CMTimeRange
    let enablePostProcessing = true
    // Values are sampled per frame; never let AVFoundation cache one frame per instruction.
    let containsTweening = true
    let requiredSourceTrackIDs: [NSValue]?
    let passthroughTrackID = kCMPersistentTrackID_Invalid
    let layers: [LayerPlan]
    let renderSize: CGSize
    let fps: Int

    init(timeRange: CMTimeRange, layers: [LayerPlan], renderSize: CGSize, fps: Int) {
        self.timeRange = timeRange
        self.layers = layers
        self.renderSize = renderSize
        self.fps = fps
        var all: [TrackID] = []
        for layer in layers { layer.collectTrackIDs(into: &all) }
        var seen = Set<TrackID>()
        self.requiredSourceTrackIDs = all.compactMap {
            seen.insert($0).inserted ? NSNumber(value: $0.rawValue) : nil
        }
        super.init()
    }
}

// MARK: - macOS bridges for portable render primitives

extension TrackID {
    /// `TrackID.rawValue` is `CMPersistentTrackID` on macOS.
    var cmPersistentTrackID: CMPersistentTrackID { rawValue }
}

extension Size2D {
    init(_ size: CGSize) { self.init(width: size.width, height: size.height) }
    var cgSize: CGSize { CGSize(width: width, height: height) }
}

extension Mat3 {
    /// Zero-math bridge: `Mat3` and `CGAffineTransform` share the
    /// `(a, b, c, d, tx, ty)` stored-tuple layout (verified empirically; see
    /// `Mat3.concatenating`).
    init(_ t: CGAffineTransform) { self.init(a: t.a, b: t.b, c: t.c, d: t.d, tx: t.tx, ty: t.ty) }
    var cgAffineTransform: CGAffineTransform { CGAffineTransform(a: a, b: b, c: c, d: d, tx: tx, ty: ty) }
}
