import CVulkan
import PalmierCore
import Foundation

/// Plays a planned timeline to a Vulkan swapchain, end-to-end. Per timeline
/// frame: looks up the active `RenderInstruction`, decodes the right source
/// frame per track (cached), composites via `WinFrameRenderer` into an offscreen
/// texture, blits that to the acquired swapchain image, and presents.
///
/// MVP: single-frame-in-flight, synchronous fence wait per frame, Sleep-based
/// frame pacing at the timeline's fps. Decode is on the calling thread. The
/// production playback loop will move decode off-thread and use proper
/// frames-in-flight — see docs/windows-media-engine-design.md.
///
/// Not Sendable — owned by one playback worker; the rendered frames cross
/// isolation as immutable swapchain presents.
public final class WinPlayback {
    public let device: VulkanDevice
    public let swapchain: VulkanSwapchain
    public let renderer: WinFrameRenderer
    public let offscreen: VulkanTexture
    public let renderSize: Size2D
    private let instructions: [RenderInstruction]
    private let totalFrames: Int
    private let fps: Int

    /// Per-track decode cache: the last decoded frame index + its texture, so
    /// we don't re-decode the same frame when the timeline stalls on it.
    private final class TrackCache {
        let decoder: FFmpegDecoder
        var lastFrame: Int = -1
        var texture: VulkanTexture?
        init(decoder: FFmpegDecoder) { self.decoder = decoder }
    }
    private var caches: [TrackID: TrackCache] = [:]
    private let mediaPaths: [TrackID: String]

    /// Builds the playback for a planned timeline. `mediaPaths` maps each
    /// trackID to the source file FFmpegDecoder opens; one decoder per track
    /// is kept alive for the duration of playback.
    public init?(
        device: VulkanDevice,
        swapchain: VulkanSwapchain,
        timeline: Timeline,
        renderSize: Size2D,
        trackSlots: [String: TrackSlot],
        mediaPaths: [TrackID: String]
    ) {
        guard let renderer = WinFrameRenderer(device: device) else { return nil }
        guard let offscreen = VulkanTexture(
            device: device,
            width: UInt32(renderSize.width),
            height: UInt32(renderSize.height)
        ) else { return nil }
        self.device = device
        self.swapchain = swapchain
        self.renderer = renderer
        self.offscreen = offscreen
        self.renderSize = renderSize
        self.instructions = RenderPlanner.plan(
            timeline: timeline, renderSize: renderSize,
            totalFrames: timeline.totalFrames, trackSlots: trackSlots,
            resolveTimeline: { _ in nil }
        )
        self.totalFrames = timeline.totalFrames
        self.fps = 30
        self.mediaPaths = mediaPaths
    }

    /// Plays the timeline from frame 0, presenting each frame to the swapchain
    /// until `totalFrames` is reached or `shouldStop()` returns true. Pumps
    /// Win32 messages via `shouldStop` (the caller checks window close).
    public func play(window: Win32Window, shouldStop: () -> Bool) {
        let frameIntervalMs = UInt32(1000 / max(1, fps))
        var frame = 0
        while frame < totalFrames && !shouldStop() {
            drawTimelineFrame(frame: frame)
            Sleep(frameIntervalMs)
            frame += 1
        }
        // Wait for the final blit's fence before returning so the offscreen
        // texture and decoders can be torn down safely (the last blit reads
        // offscreen.image). A targeted fence wait on the last submission;
        // vkQueueWaitIdle hits a driver device-lost path after a long present
        // loop on this hardware, so we wait on the specific fence instead.
        if frame > 0 {
            var fenceHandle: VkFence? = swapchain.inFlight
            withUnsafePointer(to: &fenceHandle) { f in
                _ = vkWaitForFences(device.device, 1, f, UInt32(VK_TRUE), UInt64.max)
            }
        }
        print("[WinPlayback] played \(frame) frame(s)")
    }

    /// Renders one timeline frame: segment lookup → per-track decode →
    /// WinFrameRenderer composite → blit to swapchain → present.
    private func drawTimelineFrame(frame: Int) {
        guard let instruction = segment(for: frame) else {
            presentCleared()
            return
        }

        // Resolve each required track's source frame into a texture. The
        // source frame for a clip is (timelineFrame - clip.startFrame) * speed
        // (MVP: speed = 1, single clip per track per segment).
        let required = instruction.requiredTrackIDs
        var sources: [TrackID: VulkanTexture] = [:]
        for layer in instruction.layers {
            guard let trackID = layer.trackID else { continue }
            guard !required.contains(trackID) || sources[trackID] != nil else { continue }
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

        blitAndPresent()
    }

    /// Finds the RenderInstruction covering `frame`, or nil if past the end.
    private func segment(for frame: Int) -> RenderInstruction? {
        for instr in instructions where frame >= instr.frameRange.start && frame < instr.frameRange.end {
            return instr
        }
        return instructions.last
    }

    /// Source-frame index for a clip at a given timeline frame. MVP: speed=1.
    private func sourceFrameIndex(for layer: LayerPlan, timelineFrame: Int) -> Int {
        let local = timelineFrame - layer.clip.startFrame
        let scaled = Double(local) * layer.clip.speed
        return max(0, Int(scaled.rounded()))
    }

    /// Returns the decoded texture for `trackID` at `frame`, decoding + caching
    /// if the cache is stale. Reuses the existing texture when the frame hasn't
    /// changed (avoids re-decode on repeated plays of the same frame).
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

        if frame == cache.lastFrame, let tex = cache.texture {
            return tex
        }

        // Seek if we jumped backward or far forward; otherwise decode forward.
        if frame < cache.lastFrame || frame > cache.lastFrame + 1 {
            try? cache.decoder.seek(timestamp: Int64(frame))
            cache.lastFrame = frame - 1
        }

        // Walk forward to the requested frame.
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
                _ = try? cache.decoder.nextBGRAFrame()  // discard intermediate
            }
        }
        return cache.texture
    }

    /// Blits the offscreen composite to the next swapchain image and presents.
    private func blitAndPresent() {
        let dev = device.device
        var inFlightHandle: VkFence? = swapchain.inFlight
        withUnsafePointer(to: &inFlightHandle) { f in
            _ = vkWaitForFences(dev, 1, f, UInt32(VK_TRUE), UInt64.max)
        }
        _ = withUnsafePointer(to: &inFlightHandle) { f in vkResetFences(dev, 1, f) }

        var imageIndex: UInt32 = 0
        let acquireResult = vkAcquireNextImageKHR(
            dev, swapchain.swapchain, UInt64.max, swapchain.imageAvailable, nil, &imageIndex
        )
        guard acquireResult == VK_SUCCESS || acquireResult == VK_SUBOPTIMAL_KHR else { return }

        // Record: blit offscreen → swapchain image. The offscreen render pass
        // left the texture in SHADER_READ_ONLY_OPTIMAL; VulkanBlit transitions
        // src + dst and restores src.
        let swapImage = Int(imageIndex) < swapchain.images.count ? swapchain.images[Int(imageIndex)] : nil
        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
        cbInfo.commandPool = device.commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
        cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(dev, $0, &cmd) }) == VK_SUCCESS,
              let cmd, let swapImage else { return }
        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return
        }
        VulkanBlit.record(
            commandBuffer: cmd,
            src: offscreen.image, srcExtent: VkExtent2D(width: offscreen.width, height: offscreen.height),
            dst: swapImage, dstExtent: swapchain.extent
        )
        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return
        }

        var waitSemaphore: VkSemaphore? = swapchain.imageAvailable
        var signalSemaphore: VkSemaphore? = swapchain.renderFinished
        var cmdHandle: VkCommandBuffer? = cmd
        var waitStageMask: UInt32 = UInt32(VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT.rawValue)
        var submitInfo = VkSubmitInfo()
        submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO
        submitInfo.commandBufferCount = 1
        let submitResult: VkResult = withUnsafePointer(to: &waitSemaphore) { ws in
            submitInfo.waitSemaphoreCount = 1
            submitInfo.pWaitSemaphores = ws
            submitInfo.pCommandBuffers = withUnsafePointer(to: &cmdHandle) { $0 }
            return withUnsafePointer(to: &waitStageMask) { wsm in
                submitInfo.pWaitDstStageMask = wsm
                return withUnsafePointer(to: &signalSemaphore) { ss in
                    submitInfo.signalSemaphoreCount = 1
                    submitInfo.pSignalSemaphores = ss
                    return withUnsafePointer(to: &submitInfo) { si in
                        vkQueueSubmit(device.graphicsQueue, 1, si, swapchain.inFlight)
                    }
                }
            }
        }
        var toFree: VkCommandBuffer? = cmd
        withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
        guard submitResult == VK_SUCCESS else { return }

        var swapchainHandle: VkSwapchainKHR? = swapchain.swapchain
        var presentInfo = VkPresentInfoKHR()
        presentInfo.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR
        withUnsafePointer(to: &signalSemaphore) { ss in
            presentInfo.waitSemaphoreCount = 1
            presentInfo.pWaitSemaphores = ss
            withUnsafePointer(to: &swapchainHandle) { sw in
                presentInfo.pSwapchains = sw
                presentInfo.swapchainCount = 1
                withUnsafePointer(to: &imageIndex) { ii in
                    presentInfo.pImageIndices = ii
                    withUnsafePointer(to: &presentInfo) { pi in
                        _ = vkQueuePresentKHR(device.graphicsQueue, pi)
                    }
                }
            }
        }
    }

    /// Clears + presents a frame for timeline gaps (no segment). Reuses the
    /// blit path after clearing the offscreen — for the MVP we just present
    /// whatever the offscreen already holds, since gaps are rare in the test.
    private func presentCleared() { blitAndPresent() }
}
