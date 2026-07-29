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
    public let encoder: FFmpegEncoder
    public let renderSize: Size2D
    private let instructions: [RenderInstruction]
    private let totalFrames: Int
    private var caches: [TrackID: TrackCache] = [:]
    private let mediaPaths: [TrackID: String]

    private final class TrackCache {
        let decoder: FFmpegDecoder
        var lastFrame: Int = -1
        var texture: VulkanTexture?
        init(decoder: FFmpegDecoder) { self.decoder = decoder }
    }

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
        guard let encoder = try? FFmpegEncoder(path: outputPath, config: encoderConfig) else { return nil }
        self.device = device
        self.renderer = renderer
        self.offscreen = offscreen
        self.encoder = encoder
        self.renderSize = renderSize
        self.instructions = RenderPlanner.plan(
            timeline: timeline, renderSize: renderSize,
            totalFrames: timeline.totalFrames, trackSlots: trackSlots,
            resolveTimeline: { _ in nil }
        )
        self.totalFrames = timeline.totalFrames
        self.mediaPaths = mediaPaths
    }

    /// Exports every timeline frame to the encoder, then drains + closes the
    /// encoder (writing the trailer). Returns the number of frames encoded.
    @discardableResult
    public func export() throws -> Int {
        var encoded = 0
        for frame in 0..<totalFrames {
            try Task.checkCancellation()
            drawAndEncodeFrame(frame: frame)
            encoded += 1
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

        // Read the offscreen back to CPU BGRA and encode it.
        if let bgra = offscreen.download() {
            _ = encoder.writeFrame(bgra)
        }
    }

    private func segment(for frame: Int) -> RenderInstruction? {
        for instr in instructions where frame >= instr.frameRange.start && frame < instr.frameRange.end {
            return instr
        }
        return instructions.last
    }

    private func sourceFrameIndex(for layer: LayerPlan, timelineFrame: Int) -> Int {
        let local = timelineFrame - layer.clip.startFrame
        let scaled = Double(local) * layer.clip.speed
        return max(0, Int(scaled.rounded()))
    }

    /// Decode cache (mirrors WinPlayback). Returns the decoded texture for
    /// `trackID` at `frame`, decoding forward or seeking as needed.
    private func texture(for trackID: TrackID, frame: Int, natSize: Size2D) -> VulkanTexture? {
        let cache: TrackCache
        if let existing = caches[trackID] {
            cache = existing
        } else {
            guard let path = mediaPaths[trackID],
                  let decoder = try? FFmpegDecoder(path: path) else {
                return nil
            }
            cache = TrackCache(decoder: decoder)
            caches[trackID] = cache
        }
        if frame == cache.lastFrame, let tex = cache.texture { return tex }

        if frame < cache.lastFrame || frame > cache.lastFrame + 1 {
            try? cache.decoder.seek(timestamp: Int64(frame))
            cache.lastFrame = frame - 1
        }
        while cache.lastFrame < frame {
            cache.lastFrame += 1
            if cache.lastFrame == frame {
                if let bgra = try? cache.decoder.nextBGRAFrame() {
                    let w = cache.decoder.info.width
                    let h = cache.decoder.info.height
                    if cache.texture == nil {
                        cache.texture = VulkanTexture(device: device, width: UInt32(w), height: UInt32(h))
                    }
                    if cache.texture?.width == UInt32(w), cache.texture?.height == UInt32(h) {
                        _ = cache.texture?.upload(bgra: bgra)
                    }
                } else {
                    return cache.texture  // EOF — hold last frame
                }
            } else {
                _ = try? cache.decoder.nextBGRAFrame()
            }
        }
        return cache.texture
    }
}
