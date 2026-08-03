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
    public private(set) var swapchain: VulkanSwapchain
    public let renderer: WinFrameRenderer
    public let offscreen: VulkanTexture
    /// Ping-pong target for the effect passes, like the exporter's.
    public let scratch: VulkanTexture
    public let renderSize: Size2D
    private let instructions: [RenderInstruction]
    private let totalFrames: Int
    private let fps: Int

    /// Decode caches, owned outside the presenter so an edit does not reopen
    /// every clip's decoder on the render thread.
    private let caches: DecodeCachePool
    private let mediaPaths: [TrackID: String]
    /// Clip that owns each track slot, so a cache survives a presenter rebuild.
    private let clipIds: [TrackID: String]

    /// Transform of the clip the user has selected, or nil for no selection.
    /// Set by the shell each time selection changes; drawn as a manipulation
    /// frame over the composite. Preview only — the exporter never sees it.
    public var selection: Transform?

    /// Builds the playback for a planned timeline. `mediaPaths` maps each
    /// trackID to the source file FFmpegDecoder opens; one decoder per track
    /// is kept alive for the duration of playback.
    public init?(
        device: VulkanDevice,
        swapchain: VulkanSwapchain,
        timeline: Timeline,
        renderSize: Size2D,
        trackSlots: [String: TrackSlot],
        mediaPaths: [TrackID: String],
        clipIds: [TrackID: String] = [:],
        caches: DecodeCachePool = DecodeCachePool()
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
        self.scratch = scratch
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
        self.fps = max(1, timeline.fps)
        self.mediaPaths = mediaPaths
        self.clipIds = clipIds
        self.caches = caches
        caches.keepOnly(clipIds: Set(clipIds.values))
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

    /// Swaps in a freshly created swapchain after a window resize. The caller
    /// must have waited for device idle; decoders and the offscreen survive.
    public func replaceSwapchain(_ next: VulkanSwapchain) {
        // The device is idle, so the last submit has finished and its buffer
        // can go — the fence that gated it belongs to the outgoing swapchain.
        releasePendingCommandBuffer()
        swapchain = next
    }

    deinit {
        // Owners wait for device idle before releasing the presenter, so the
        // last submit has completed and its buffer is safe to free.
        releasePendingCommandBuffer()
    }

    /// Renders one timeline frame: segment lookup → per-track decode →
    /// WinFrameRenderer composite → blit to swapchain → present. Public so an
    /// external clock (the shell's render loop) can drive frames directly.
    /// Returns false when the swapchain rejected the frame (stale after a
    /// resize) — the owner should recreate the swapchain and retry.
    @discardableResult
    public func drawTimelineFrame(frame: Int) -> Bool {
        // Before the composite, not after: `offscreen` is the blit's source,
        // so rendering into it while the previous frame's blit is still
        // reading overwrites the picture being presented.
        guard awaitPreviousFrame() else { return true }
        guard let instruction = segment(for: frame) else {
            return clearAndPresent()
        }

        // Resolve each required track's source frame into a texture. The
        // source frame for a clip is (timelineFrame - clip.startFrame) * speed
        // (MVP: speed = 1, single clip per track per segment).
        var sources: [TrackID: VulkanTexture] = [:]
        for layer in instruction.layers {
            guard let trackID = layer.trackID else { continue }
            if sources[trackID] != nil { continue }  // already resolved this frame
            let sourceFrame = sourceFrameIndex(for: layer, timelineFrame: frame)
            if let tex = texture(for: trackID, frame: sourceFrame, natSize: layer.natSize) {
                sources[trackID] = tex
            }
        }

        // Handle sizes are in presented pixels, so they stay constant however
        // the canvas is scaled into the window.
        let overlay = selection.map {
            SelectionOverlay.quads(for: $0, surface: Size2D(
                width: Double(swapchain.extent.width),
                height: Double(swapchain.extent.height)))
        } ?? []

        // Composite into offscreen via the FrameRendering entry point.
        renderer.render(
            instruction: instruction,
            frame: frame,
            sourceFrame: { id in sources[id] },
            overlay: overlay,
            into: offscreen
        )

        // The same effect pass the exporter runs — the preview showing an
        // ungraded frame the export would grade is silent drift.
        var presented = offscreen
        let allEffects = instruction.layers.flatMap { $0.clip.effects ?? [] }
        if !allEffects.isEmpty {
            let firstLayerStart = instruction.layers.first?.clip.startFrame ?? 0
            presented = renderer.applyEffectsOneShot(
                allEffects, frame: frame, clipStartFrame: firstLayerStart,
                source: offscreen, scratch: scratch
            ) ?? offscreen
        }

        return blitAndPresent(from: presented)
    }

    private func segment(for frame: Int) -> RenderInstruction? {
        TimelineLookup.segment(instructions, frame: frame)
    }

    private func sourceFrameIndex(for layer: LayerPlan, timelineFrame: Int) -> Int {
        TimelineLookup.sourceFrame(for: layer, timelineFrame: timelineFrame)
    }

    /// The decoded texture for `trackID` at `frame`. The seek-and-walk rule
    /// lives in DecodedFrameCache so playback and export cannot drift apart.
    private func texture(for trackID: TrackID, frame: Int, natSize: Size2D) -> VulkanTexture? {
        guard let path = mediaPaths[trackID] else {
            engineLog("[playback] no media path for track \(trackID)")
            return nil
        }
        let clipId = clipIds[trackID] ?? "track-\(trackID.rawValue)"
        return caches.cache(clipId: clipId, path: path, device: device, fps: fps)?.texture(at: frame)
    }

    /// How long a frame waits on the GPU before giving up and trying again.
    /// An unbounded wait turns one driver hiccup into a preview that never
    /// updates again while the rest of the app keeps responding — the worst
    /// kind of failure, because nothing about it looks broken.
    private static let gpuTimeoutNanoseconds: UInt64 = 1_000_000_000

    /// Frames skipped in a row because the GPU did not come back in time.
    private var stalledFrames = 0

    /// The command buffer the last submit is still executing. Freeing a
    /// pending buffer lets the pool hand the same memory to the next frame
    /// while the GPU is reading it, so it is released only once `inFlight`
    /// signals.
    private var pendingCommandBuffer: VkCommandBuffer?

    /// Waits for the previous frame's blit and releases its command buffer.
    /// False means the GPU did not come back inside the timeout: skip this
    /// frame and try again — the swapchain is sound, so rebuilding it would
    /// not help.
    private func awaitPreviousFrame() -> Bool {
        var inFlightHandle: VkFence? = swapchain.inFlight
        let waited = withUnsafePointer(to: &inFlightHandle) { f in
            vkWaitForFences(device.device, 1, f, UInt32(VK_TRUE), Self.gpuTimeoutNanoseconds)
        }
        guard waited == VK_SUCCESS else {
            stalledFrames += 1
            PreviewStats.shared.recordStall()
            if stalledFrames == 1 || stalledFrames % 60 == 0 {
                engineLog("[WinPlayback] GPU busy past \(Self.gpuTimeoutNanoseconds / 1_000_000) ms; " +
                          "skipped \(stalledFrames) frame(s)")
            }
            return false
        }
        if stalledFrames > 0 {
            engineLog("[WinPlayback] recovered after \(stalledFrames) skipped frame(s)")
            stalledFrames = 0
        }
        releasePendingCommandBuffer()
        return true
    }

    /// Frees the last submitted command buffer. Only call once its fence has
    /// signalled, or the device is idle.
    private func releasePendingCommandBuffer() {
        guard let cmd = pendingCommandBuffer else { return }
        pendingCommandBuffer = nil
        free(commandBuffer: cmd)
    }

    private func free(commandBuffer: VkCommandBuffer) {
        var toFree: VkCommandBuffer? = commandBuffer
        withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(device.device, device.commandPool, 1, $0) }
    }

    private func clearAndPresent() -> Bool {
        renderer.renderEmpty(size: renderSize, fps: fps, into: offscreen)
        return blitAndPresent()
    }

    /// Blits the offscreen composite to the next swapchain image and presents.
    /// The caller must have called `awaitPreviousFrame()` first.
    ///
    /// Returns false when the swapchain must be recreated. Every failure after
    /// the image is acquired reports itself that way on purpose: the acquire
    /// signalled `imageAvailable`, only a submit can consume it, and rebuilding
    /// the swapchain is what clears a semaphore left signalled.
    ///
    /// The in-flight fence is reset only once a submit is certain to follow.
    /// Resetting it earlier — as this did — means any bail-out between the
    /// reset and the submit leaves it unsignalled forever, and the next frame
    /// blocks on it for good.
    @discardableResult
    private func blitAndPresent(from source: VulkanTexture? = nil) -> Bool {
        let presented = source ?? offscreen
        let dev = device.device
        var inFlightHandle: VkFence? = swapchain.inFlight
        var imageIndex: UInt32 = 0
        let acquireResult = vkAcquireNextImageKHR(
            dev, swapchain.swapchain, Self.gpuTimeoutNanoseconds,
            swapchain.imageAvailable, nil, &imageIndex
        )
        guard acquireResult == VK_SUCCESS || acquireResult == VK_SUBOPTIMAL_KHR else { return false }

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
              let cmd, let swapImage else { return false }
        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else {
            free(commandBuffer: cmd)   // never submitted, so never pending
            return false
        }
        VulkanBlit.record(
            commandBuffer: cmd,
            src: presented.image, srcExtent: VkExtent2D(width: presented.width, height: presented.height),
            dst: swapImage, dstExtent: swapchain.extent
        )
        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            free(commandBuffer: cmd)
            return false
        }

        // Certain of a submit now: reset the fence it will signal.
        _ = withUnsafePointer(to: &inFlightHandle) { f in vkResetFences(dev, 1, f) }

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
        // A failed submit leaves the fence reset and nothing to signal it, so
        // the swapchain has to be rebuilt rather than waited on again.
        guard submitResult == VK_SUCCESS else {
            free(commandBuffer: cmd)
            return false
        }
        // Executing now. It is freed after the next frame's fence wait.
        pendingCommandBuffer = cmd

        var swapchainHandle: VkSwapchainKHR? = swapchain.swapchain
        var presentInfo = VkPresentInfoKHR()
        presentInfo.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR
        let presentResult: VkResult = withUnsafePointer(to: &signalSemaphore) { ss in
            presentInfo.waitSemaphoreCount = 1
            presentInfo.pWaitSemaphores = ss
            return withUnsafePointer(to: &swapchainHandle) { sw in
                presentInfo.pSwapchains = sw
                presentInfo.swapchainCount = 1
                return withUnsafePointer(to: &imageIndex) { ii in
                    presentInfo.pImageIndices = ii
                    return withUnsafePointer(to: &presentInfo) { pi in
                        vkQueuePresentKHR(device.graphicsQueue, pi)
                    }
                }
            }
        }
        return presentResult == VK_SUCCESS || presentResult == VK_SUBOPTIMAL_KHR
    }

    /// Presents black, for a playhead with nothing under it. It has to
    /// actually clear: leaving the last composited frame up reads as the
    /// preview being stuck on a clip that is no longer there.
    @discardableResult
    public func presentCleared() -> Bool {
        guard awaitPreviousFrame() else { return true }
        return clearAndPresent()
    }
}
