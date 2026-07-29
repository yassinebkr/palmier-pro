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

        // Apply effects (ping-pong between offscreen and scratch). The MVP
        // applies the union of enabled effects from all clips to the composited
        // frame; per-layer effects come later. Effect passes record into a
        // one-shot command buffer (the render's protocol method already
        // submitted + waited on its own).
        var finalTexture = offscreen
        let allEffects = instruction.layers.flatMap { $0.clip.effects ?? [] }
        if !allEffects.isEmpty {
            let firstLayerStart = instruction.layers.first?.clip.startFrame ?? 0
            finalTexture = recordAndApplyEffects(
                allEffects, frame: frame, clipStartFrame: firstLayerStart
            ) ?? offscreen
        }

        // Read the result back to CPU BGRA and encode it.
        if let bgra = finalTexture.download() {
            _ = encoder.writeFrame(bgra)
        }
    }

    /// Allocates a one-shot command buffer, records the effect chain via
    /// WinFrameRenderer.applyEffects, submits, and waits. Returns the texture
    /// holding the final result (offscreen or scratch depending on pass count).
    private func recordAndApplyEffects(_ effects: [Effect], frame: Int, clipStartFrame: Int) -> VulkanTexture? {
        let dev = device.device
        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
        cbInfo.commandPool = device.commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
        cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(dev, $0, &cmd) }) == VK_SUCCESS,
              let cmd else { return nil }
        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return nil
        }
        let result = renderer.applyEffects(
            effects, frame: frame, clipStartFrame: clipStartFrame,
            source: offscreen, scratch: scratch, commandBuffer: cmd
        )
        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return nil
        }

        var fenceInfo = VkFenceCreateInfo()
        fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO
        var fence: VkFence? = nil
        guard withUnsafePointer(to: &fenceInfo, { vkCreateFence(dev, $0, nil, &fence) }) == VK_SUCCESS, let fence else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return nil
        }
        var submitInfo = VkSubmitInfo()
        submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO
        submitInfo.commandBufferCount = 1
        var cmdHandle: VkCommandBuffer? = cmd
        let submitResult: VkResult = withUnsafePointer(to: &cmdHandle) { ch in
            submitInfo.pCommandBuffers = ch
            return withUnsafePointer(to: &submitInfo) { si in
                vkQueueSubmit(device.graphicsQueue, 1, si, fence)
            }
        }
        if submitResult == VK_SUCCESS {
            var fenceHandle: VkFence? = fence
            withUnsafePointer(to: &fenceHandle) { f in
                _ = vkWaitForFences(dev, 1, f, UInt32(VK_TRUE), UInt64.max)
            }
        }
        vkDestroyFence(dev, fence, nil)
        var toFree: VkCommandBuffer? = cmd
        withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
        return result
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
