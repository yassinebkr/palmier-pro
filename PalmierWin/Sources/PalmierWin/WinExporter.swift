import CVulkan
import PalmierCore
import Foundation

/// Exports a planned timeline to a video file, end-to-end. Per timeline frame:
/// looks up the active `RenderInstruction`, decodes the right source frame per
/// track (cached), composites via `WinFrameRenderer` into an offscreen texture,
/// reads the offscreen back to CPU BGRA, and encodes it via `FFmpegEncoder`.
///
/// The mirror of `WinPlayback` (which presents each frame instead of encoding
/// it). MVP: single-threaded, synchronous per-frame. The production exporter
/// will move decode off-thread and batch frames-in-flight — see
/// docs/windows-media-engine-design.md.
public final class WinExporter {
    public let device: VulkanDevice
    public let renderer: WinFrameRenderer
    public let offscreen: VulkanTexture
    public let scratch: VulkanTexture  // ping-pong target for effect passes
    public let encoder: FFmpegEncoder
    public let renderSize: Size2D
    private let instructions: [RenderInstruction]
    private let totalFrames: Int
    private var caches: [TrackID: DecodedFrameCache] = [:]
    private let mediaPaths: [TrackID: String]
    /// Timeline frame rate; source frame indices are expressed in it.
    private let fps: Int

    /// Builds the exporter for a planned timeline. The encoder writes to `path`.
    public init?(
        device: VulkanDevice,
        timeline: Timeline,
        renderSize: Size2D,
        trackSlots: [String: TrackSlot],
        mediaPaths: [TrackID: String],
        outputPath: String,
        encoderConfig: FFmpegEncoder.Config
    ) {
        guard let renderer = WinFrameRenderer(device: device) else { return nil }
        guard let offscreen = VulkanTexture(
            device: device,
            width: UInt32(renderSize.width),
            height: UInt32(renderSize.height)
        ) else { return nil }
        guard let scratch = VulkanTexture(
            device: device,
            width: UInt32(renderSize.width),
            height: UInt32(renderSize.height)
        ) else { return nil }
        guard let encoder = try? FFmpegEncoder(path: outputPath, config: encoderConfig) else { return nil }
        self.device = device
        self.renderer = renderer
        self.offscreen = offscreen
        self.scratch = scratch
        self.encoder = encoder
        self.renderSize = renderSize
        self.instructions = RenderPlanner.plan(
            timeline: timeline, renderSize: renderSize,
            totalFrames: timeline.totalFrames, trackSlots: trackSlots,
            resolveTimeline: { _ in nil }
        )
        self.totalFrames = timeline.totalFrames
        self.fps = max(1, timeline.fps)
        self.mediaPaths = mediaPaths
    }

    /// Per-frame progress hook: (framesEncoded, totalFrames). Called on the
    /// exporting thread after each frame.
    public var onFrame: ((Int, Int) -> Void)?

    /// Exports every timeline frame to the encoder, then drains + closes the
    /// encoder (writing the trailer). Returns the number of frames encoded.
    @discardableResult
    public func export() throws -> Int {
        var encoded = 0
        for frame in 0..<totalFrames {
            try Task.checkCancellation()
            drawAndEncodeFrame(frame: frame)
            encoded += 1
            onFrame?(encoded, totalFrames)
        }
        try encoder.close()
        return encoded
    }

    /// Renders one timeline frame into the offscreen texture and encodes it.
    private func drawAndEncodeFrame(frame: Int) {
        guard let instruction = segment(for: frame) else {
            // Gap: encode a black frame (the offscreen render pass clears to
            // black, but with no segment there's no render — clear explicitly
            // by encoding whatever the offscreen already holds).
            if let bgra = offscreen.download() { _ = encoder.writeFrame(bgra) }
            return
        }

        var sources: [TrackID: VulkanTexture] = [:]
        for layer in instruction.layers {
            guard let trackID = layer.trackID else { continue }
            if sources[trackID] != nil { continue }  // already resolved this frame
            let sourceFrame = sourceFrameIndex(for: layer, timelineFrame: frame)
            if let tex = texture(for: trackID, frame: sourceFrame, natSize: layer.natSize) {
                sources[trackID] = tex
            }
        }

        // Composite into offscreen via the FrameRendering entry point.
        renderer.render(
            instruction: instruction,
            frame: frame,
            sourceFrame: { id in sources[id] },
            into: offscreen
        )

        // Apply effects (ping-pong between offscreen and scratch). The MVP
        // applies the union of enabled effects from all clips to the composited
        // frame; per-layer effects come later.
        var finalTexture = offscreen
        let allEffects = instruction.layers.flatMap { $0.clip.effects ?? [] }
        if !allEffects.isEmpty {
            let firstLayerStart = instruction.layers.first?.clip.startFrame ?? 0
            finalTexture = renderer.applyEffectsOneShot(
                allEffects, frame: frame, clipStartFrame: firstLayerStart,
                source: offscreen, scratch: scratch
            ) ?? offscreen
        }

        // Read the result back to CPU BGRA and encode it.
        if let bgra = finalTexture.download() {
            _ = encoder.writeFrame(bgra)
        }
    }

    private func segment(for frame: Int) -> RenderInstruction? {
        TimelineLookup.segment(instructions, frame: frame)
    }

    private func sourceFrameIndex(for layer: LayerPlan, timelineFrame: Int) -> Int {
        TimelineLookup.sourceFrame(for: layer, timelineFrame: timelineFrame)
    }

    /// The decoded texture for `trackID` at `frame`. Shares DecodedFrameCache
    /// with playback: this used to be a copy of the same twenty lines, and the
    /// copy still took whatever keyframe a seek landed on and called it the
    /// requested frame — so every clip after the first exported from the wrong
    /// place, and passed its own colour bars off as the second clip's picture.
    private func texture(for trackID: TrackID, frame: Int, natSize: Size2D) -> VulkanTexture? {
        let cache: DecodedFrameCache
        if let existing = caches[trackID] {
            cache = existing
        } else {
            guard let path = mediaPaths[trackID],
                  let made = DecodedFrameCache(path: path, device: device, fps: fps) else { return nil }
            cache = made
            caches[trackID] = cache
        }
        return cache.texture(at: frame)
    }
}
