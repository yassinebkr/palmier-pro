// Windows vertical slice (port-roadmap Option C). Proves the toolchain works
// for a real app surface end-to-end: PalmierCore (portable model + engines) +
// PalmierWin (Windows media bindings) link and run together. Does not decode
// or render video — that's the media-engine spike. It builds a tiny timeline,
// runs the ripple engine + render planner, and initializes Media Foundation.

import Foundation
import CVulkan
import CImGui
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

        // 5) Vulkan: create an instance against the GPU driver (FFmpeg+Vulkan is
        //    the chosen render path; MF/D3D deferred — see
        //    docs/windows-media-engine-design.md).
        print("FFmpeg: \(FFmpeg.versionInfo)")
        if let instance = Vulkan.createInstance(appName: "palmier-spike",
                                                extensions: ["VK_KHR_surface", "VK_KHR_win32_surface"]) {
            print("Vulkan: instance created OK")
            if let dev = VulkanDevice.create(instance: instance) {
                print("Vulkan device: \(dev.deviceName) (graphics family \(dev.graphicsFamily), pool OK)")

                // 6) Win32 window (flat-C via WinSDK) — backs the Vulkan surface.
                if let win = Win32Window(title: "Palmier Pro Windows", width: 1280, height: 720) {
                    print("Win32 window: created OK (HWND present)")

                    // 7) Swapchain: surface + swapchain + render pass + framebuffers.
                    if let swap = VulkanSwapchain(device: dev, instance: instance, window: win) {
                        print("Vulkan swapchain: \(swap.extent.width)x\(swap.extent.height), \(swap.imageViews.count) image(s), render pass OK")
                        runUI(dev: dev, instance: instance, win: win, swap: swap)
                        // Exercise the FrameRendering protocol conformance on top
                        // of the simple full-screen quad above. Renders a planned
                        // timeline segment into an offscreen texture via the
                        // portable upstream interface (the same one macOS uses).
                        runFrameRendering(dev: dev)
                        // Full timeline playback: decode→composite→blit→present
                        // per frame, end-to-end through the portable contract.
                        runPlayback(dev: dev, win: win, swap: swap)
                        // Encode round-trip: decode the test clip frame-by-frame
                        // and re-encode to a new MP4 via FFmpegEncoder. Proves
                        // the encode path works before wiring the full export
                        // pipeline (decode → composite → encode).
                        runExport(dev: dev)
                    } else {
                        print("Vulkan swapchain: FAILED to create")
                    }
                } else {
                    print("Win32 window: FAILED to create")
                }
            } else {
                print("Vulkan device: FAILED to create")
            }
            Vulkan.destroyInstance(instance)
        } else {
            print("Vulkan: FAILED to create instance")
        }

        print("=== slice complete ===")
    }

    /// Builds the pipeline + texture + descriptor + renderer and runs the
    /// acquire→draw→present loop for a few seconds (or until the window closes).
    /// Full editor UI: video preview via the textured-quad pipeline + an ImGui
    /// overlay with the editor chrome (title bar, inspector, timeline info).
    /// Runs a render loop that draws both the video frame and the UI into the
    /// same swapchain render pass. NO COM, NO WinUI3 — ImGui via Vulkan backend.
    static func runUI(dev: VulkanDevice, instance: VkInstance, win: Win32Window, swap: VulkanSwapchain) {
        guard let pipe = VulkanPipeline(device: dev, renderPass: swap.renderPass, extent: swap.extent) else {
            print("Vulkan pipeline: FAILED to create")
            return
        }

        let clipPath = "PalmierWin/test_media/testsrc.mp4"
        let hasClip = FileManager.default.fileExists(atPath: clipPath)
        var decoder: FFmpegDecoder? = nil
        var texture: VulkanTexture? = nil
        var videoDescriptor: VulkanDescriptor? = nil  // retained: deinit destroys the pool
        if hasClip, let dec = try? FFmpegDecoder(path: clipPath) {
            print("FFmpeg decoder: opened \(dec.info.width)x\(dec.info.height) (\(dec.info.codecName))")
            decoder = dec
            texture = VulkanTexture(device: dev, width: UInt32(dec.info.width), height: UInt32(dec.info.height))
            if let texture { videoDescriptor = VulkanDescriptor(device: dev, layout: pipe.descriptorSetLayout, texture: texture) }
        }

        // ImGui init — our Vulkan device + the swapchain's render pass.
        guard let ui = try? WinUI(device: dev, instance: instance, window: win, swapchain: swap) else {
            print("ImGui: FAILED to init")
            return
        }
        print("ImGui: init OK (Vulkan + Win32 backends, no COM)")

        // Build the real editor timeline for the UI to display.
        var a = Clip(mediaRef: "a", startFrame: 0, durationFrames: 60); a.id = "Clip A"
        var track1 = Track(type: .video)
        track1.clips = [a]
        var b = Clip(mediaRef: "b", startFrame: 15, durationFrames: 30); b.id = "PiP"
        b.transform.width = 0.35
        b.transform.height = 0.35
        b.transform.centerX = 0.78
        b.transform.centerY = 0.22
        b.transform.rotation = 8
        var track2 = Track(type: .video)
        track2.clips = [b]
        var title = Clip(mediaRef: "", startFrame: 5, durationFrames: 40)
        title.id = "Title"
        title.mediaType = .text
        title.textContent = "PALMIER PRO"
        title.textStyle = TextStyle()
        var track3 = Track(type: .video)
        track3.clips = [title]
        var editorTimeline = Timeline()
        editorTimeline.tracks = [track3, track2, track1]  // bottom→top

        let editor = WinEditorUI(timeline: editorTimeline)
        editor.selectedClipID = "PiP"
        if let texture {
            editor.videoAspect = Float(texture.width) / Float(texture.height)
            // ImGui draws textures with its own pipeline layout — register the
            // view/sampler with the backend instead of reusing the quad's set.
            editor.videoTextureID = cimgui_add_texture(UnsafeMutableRawPointer(texture.sampler), UnsafeMutableRawPointer(texture.view), 5)
        }
        print("Editor UI: initialized with \(editorTimeline.tracks.count) tracks, \(editorTimeline.totalFrames) frames")

        win.show()
        print("Editor UI: running (styled Palmier Pro editor) — close window to exit")
        var frame = 0
        var lastDecodedFrame = -1
        let framesToDraw = 600  // ~10s @ 60fps
        while frame < framesToDraw {
            if !win.pollEvents() { break }

            // Advance the playhead for a live preview effect, unless the user
            // is scrubbing the timeline.
            if !editor.isScrubbing {
                editor.playheadFrame = frame % max(1, editorTimeline.totalFrames)
            }

            // Decode the source frame under the playhead into the preview
            // texture. Sequential play decodes forward; scrubs seek.
            if let decoder, let texture {
                let target = min(editor.playheadFrame, 59)  // clip A = 60 frames of testsrc
                if target != lastDecodedFrame {
                    // Wait for the previous present to stop sampling the texture.
                    var waitFence: VkFence? = swap.inFlight
                    withUnsafePointer(to: &waitFence) { f in
                        _ = vkWaitForFences(dev.device, 1, f, UInt32(VK_TRUE), UInt64.max)
                    }
                    if let bgra = decodeVideoFrame(decoder: decoder, frame: target, lastDecoded: lastDecodedFrame),
                       texture.upload(bgra: bgra) {
                        lastDecodedFrame = target
                    }
                }
            }

            // Build the ImGui UI for this frame.
            ui.newFrame()
            editor.buildFrame()

            // Draw: video quad + ImGui into the swapchain. The quad path
            // samples the texture — only valid once an upload transitioned it
            // out of UNDEFINED layout, so gate on a successful first decode.
            if let videoDescriptor, lastDecodedFrame >= 0 {
                drawFrameWithUI(swap: swap, pipe: pipe, descSet: videoDescriptor.set, ui: ui, dev: dev)
            } else {
                drawClearWithUI(swap: swap, ui: ui, dev: dev)
            }
            frame += 1
            Sleep(16)
        }
        var fenceHandle: VkFence? = swap.inFlight
        withUnsafePointer(to: &fenceHandle) { f in
            _ = vkWaitForFences(dev.device, 1, f, UInt32(VK_TRUE), UInt64.max)
        }
        print("Editor UI: drew \(frame) frame(s)")
    }

    /// Draws the video frame + ImGui overlay into the swapchain. The video
    /// quad renders first, then ImGui draws on top in the same render pass.
    private static func drawFrameWithUI(swap: VulkanSwapchain, pipe: VulkanPipeline, descSet: VkDescriptorSet, ui: WinUI, dev: VulkanDevice) {
        let vkDev = dev.device
        var inFlightHandle: VkFence? = swap.inFlight
        withUnsafePointer(to: &inFlightHandle) { f in
            _ = vkWaitForFences(vkDev, 1, f, UInt32(VK_TRUE), UInt64.max)
        }
        _ = withUnsafePointer(to: &inFlightHandle) { f in vkResetFences(vkDev, 1, f) }

        var imageIndex: UInt32 = 0
        let acquire = vkAcquireNextImageKHR(vkDev, swap.swapchain, UInt64.max, swap.imageAvailable, nil, &imageIndex)
        guard acquire == VK_SUCCESS || acquire == VK_SUBOPTIMAL_KHR else { return }

        // Record: clear → video quad → ImGui → end.
        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
        cbInfo.commandPool = dev.commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
        cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(vkDev, $0, &cmd) }) == VK_SUCCESS, let cmd else { return }
        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else {
            var f: VkCommandBuffer? = cmd; withUnsafePointer(to: &f) { vkFreeCommandBuffers(vkDev, dev.commandPool, 1, $0) }
            return
        }

        // Begin the swapchain's render pass (clears to dark).
        var clear = VkClearValue()
        clear.color.float32.0 = 0.05; clear.color.float32.1 = 0.05
        clear.color.float32.2 = 0.07; clear.color.float32.3 = 1.0
        var rpBegin = VkRenderPassBeginInfo()
        rpBegin.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO
        rpBegin.renderPass = swap.renderPass
        rpBegin.framebuffer = swap.framebuffers[Int(imageIndex)]
        rpBegin.renderArea = VkRect2D(offset: VkOffset2D(x: 0, y: 0), extent: swap.extent)
        rpBegin.clearValueCount = 1
        withUnsafePointer(to: &clear) { c in
            rpBegin.pClearValues = c
            withUnsafePointer(to: &rpBegin) { rpb in
                vkCmdBeginRenderPass(cmd, rpb, VK_SUBPASS_CONTENTS_INLINE)
            }
        }

        // Video quad.
        var vp = VkViewport(x: 0, y: 0, width: Float(swap.extent.width), height: Float(swap.extent.height), minDepth: 0, maxDepth: 1)
        var sc = VkRect2D(offset: VkOffset2D(x: 0, y: 0), extent: swap.extent)
        withUnsafePointer(to: &vp) { v in withUnsafePointer(to: &sc) { s in
            vkCmdSetViewport(cmd, 0, 1, v); vkCmdSetScissor(cmd, 0, 1, s)
        }}
        vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe.pipeline)
        var setHandle: VkDescriptorSet? = descSet
        withUnsafePointer(to: &setHandle) { s in
            vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe.layout, 0, 1, s, 0, nil)
        }
        vkCmdDraw(cmd, 3, 1, 0, 0)

        // ImGui on top.
        ui.render(commandBuffer: cmd)

        vkCmdEndRenderPass(cmd)
        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            var f: VkCommandBuffer? = cmd; withUnsafePointer(to: &f) { vkFreeCommandBuffers(vkDev, dev.commandPool, 1, $0) }
            return
        }

        // Submit + present.
        var waitSem: VkSemaphore? = swap.imageAvailable
        var signalSem: VkSemaphore? = swap.renderFinished
        var cmdHandle: VkCommandBuffer? = cmd
        var waitStage: UInt32 = UInt32(VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT.rawValue)
        var submitInfo = VkSubmitInfo()
        submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO
        submitInfo.commandBufferCount = 1
        let submitResult: VkResult = withUnsafePointer(to: &waitSem) { ws in
            submitInfo.waitSemaphoreCount = 1; submitInfo.pWaitSemaphores = ws
            submitInfo.pCommandBuffers = withUnsafePointer(to: &cmdHandle) { $0 }
            return withUnsafePointer(to: &waitStage) { wsm in
                submitInfo.pWaitDstStageMask = wsm
                return withUnsafePointer(to: &signalSem) { ss in
                    submitInfo.signalSemaphoreCount = 1; submitInfo.pSignalSemaphores = ss
                    return withUnsafePointer(to: &submitInfo) { si in
                        vkQueueSubmit(dev.graphicsQueue, 1, si, swap.inFlight)
                    }
                }
            }
        }
        var toFree: VkCommandBuffer? = cmd
        withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(vkDev, dev.commandPool, 1, $0) }
        guard submitResult == VK_SUCCESS else { return }

        var swapHandle: VkSwapchainKHR? = swap.swapchain
        var presentInfo = VkPresentInfoKHR()
        presentInfo.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR
        withUnsafePointer(to: &signalSem) { ss in
            presentInfo.waitSemaphoreCount = 1; presentInfo.pWaitSemaphores = ss
            withUnsafePointer(to: &swapHandle) { sw in
                presentInfo.pSwapchains = sw; presentInfo.swapchainCount = 1
                withUnsafePointer(to: &imageIndex) { ii in
                    presentInfo.pImageIndices = ii
                    withUnsafePointer(to: &presentInfo) { pi in _ = vkQueuePresentKHR(dev.graphicsQueue, pi) }
                }
            }
        }
    }

    /// Clears + draws ImGui only (no video).
    private static func drawClearWithUI(swap: VulkanSwapchain, ui: WinUI, dev: VulkanDevice) {
        // Reuse drawFrameWithUI but without binding a texture — just clear + ImGui.
        // For simplicity, allocate a dummy no-op; the ImGui panels still show.
        let vkDev = dev.device
        var inFlightHandle: VkFence? = swap.inFlight
        withUnsafePointer(to: &inFlightHandle) { f in _ = vkWaitForFences(vkDev, 1, f, UInt32(VK_TRUE), UInt64.max) }
        _ = withUnsafePointer(to: &inFlightHandle) { f in vkResetFences(vkDev, 1, f) }
        var imageIndex: UInt32 = 0
        let acquire = vkAcquireNextImageKHR(vkDev, swap.swapchain, UInt64.max, swap.imageAvailable, nil, &imageIndex)
        guard acquire == VK_SUCCESS || acquire == VK_SUBOPTIMAL_KHR else { return }
        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO; cbInfo.commandPool = dev.commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(vkDev, $0, &cmd) }) == VK_SUCCESS, let cmd else { return }
        var bi = VkCommandBufferBeginInfo()
        bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        bi.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &bi, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else { return }
        var clear = VkClearValue()
        clear.color.float32.0 = 0.05; clear.color.float32.1 = 0.05; clear.color.float32.2 = 0.07; clear.color.float32.3 = 1.0
        var rpBegin = VkRenderPassBeginInfo()
        rpBegin.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO; rpBegin.renderPass = swap.renderPass
        rpBegin.framebuffer = swap.framebuffers[Int(imageIndex)]
        rpBegin.renderArea = VkRect2D(offset: VkOffset2D(x: 0, y: 0), extent: swap.extent)
        rpBegin.clearValueCount = 1
        withUnsafePointer(to: &clear) { c in rpBegin.pClearValues = c; withUnsafePointer(to: &rpBegin) { rpb in vkCmdBeginRenderPass(cmd, rpb, VK_SUBPASS_CONTENTS_INLINE) } }
        ui.render(commandBuffer: cmd)
        vkCmdEndRenderPass(cmd)
        _ = vkEndCommandBuffer(cmd)
        var waitSem: VkSemaphore? = swap.imageAvailable
        var signalSem: VkSemaphore? = swap.renderFinished
        var cmdHandle: VkCommandBuffer? = cmd
        var waitStage: UInt32 = UInt32(VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT.rawValue)
        var si = VkSubmitInfo(); si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO; si.commandBufferCount = 1
        _ = withUnsafePointer(to: &waitSem) { ws in si.waitSemaphoreCount = 1; si.pWaitSemaphores = ws; si.pCommandBuffers = withUnsafePointer(to: &cmdHandle) { $0 }; return withUnsafePointer(to: &waitStage) { wsm in si.pWaitDstStageMask = wsm; return withUnsafePointer(to: &signalSem) { ss in si.signalSemaphoreCount = 1; si.pSignalSemaphores = ss; return withUnsafePointer(to: &si) { s in vkQueueSubmit(dev.graphicsQueue, 1, s, swap.inFlight) } } } }
        var toFree: VkCommandBuffer? = cmd; withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(vkDev, dev.commandPool, 1, $0) }
        var swapHandle: VkSwapchainKHR? = swap.swapchain
        var pi = VkPresentInfoKHR(); pi.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR
        withUnsafePointer(to: &signalSem) { ss in pi.waitSemaphoreCount = 1; pi.pWaitSemaphores = ss; withUnsafePointer(to: &swapHandle) { sw in pi.pSwapchains = sw; pi.swapchainCount = 1; withUnsafePointer(to: &imageIndex) { ii in pi.pImageIndices = ii; withUnsafePointer(to: &pi) { p in _ = vkQueuePresentKHR(dev.graphicsQueue, p) } } } }
    }

    /// Decodes one frame from `clipPath` and uploads it to a GPU texture.
    private static func decodeAndUpload(dev: VulkanDevice, clipPath: String) -> VulkanTexture? {
        do {
            let decoder = try FFmpegDecoder(path: clipPath)
            print("FFmpeg decoder: opened \(decoder.info.width)x\(decoder.info.height) (\(decoder.info.codecName))")
            guard let bgra = try decoder.nextBGRAFrame() else {
                print("FFmpeg decoder: EOF on first frame")
                return nil
            }
            print("FFmpeg decoder: decoded frame, \(bgra.count) bytes (expected \(decoder.info.width * decoder.info.height * 4))")
            guard let tex = VulkanTexture(device: dev, width: UInt32(decoder.info.width), height: UInt32(decoder.info.height)) else {
                print("Vulkan texture: FAILED to create")
                return nil
            }
            let ok = tex.upload(bgra: bgra)
            print("Vulkan texture: \(ok ? "uploaded OK" : "FAILED to upload") (\(tex.width)x\(tex.height))")
            return ok ? tex : nil
        } catch {
            print("FFmpeg decoder: error \(error)")
            return nil
        }
    }

    /// Decodes the source frame for a playhead position. Sequential +1 steps
    /// decode forward; anything else seeks (keyframe-accurate) then decodes.
    /// On EOF the decoder wraps by seeking back to the requested frame.
    private static func decodeVideoFrame(decoder: FFmpegDecoder, frame: Int, lastDecoded: Int) -> Data? {
        do {
            if frame != lastDecoded + 1 {
                try decoder.seek(toFrame: frame, fps: 30)
            }
            if let bgra = try decoder.nextBGRAFrame() { return bgra }
            try decoder.seek(toFrame: frame, fps: 30)
            return try decoder.nextBGRAFrame()
        } catch {
            print("Preview decode: \(error)")
            return nil
        }
    }

    /// Exports the same 2-layer timeline the playback step plays, but to a
    /// file via WinExporter — per frame: decode → WinFrameRenderer composite →
    /// GPU readback → FFmpegEncoder. Proves the full export pipeline
    /// (decode → composite → readback → encode) end-to-end. Skipped on CI.
    static func runExport(dev: VulkanDevice) {
        let clipPath = "PalmierWin/test_media/testsrc.mp4"
        guard FileManager.default.fileExists(atPath: clipPath) else {
            print("Export: skipped (no \(clipPath))")
            return
        }
        guard let probe = try? FFmpegDecoder(path: clipPath) else {
            print("Export: couldn't open test clip")
            return
        }
        let renderSize = Size2D(width: Double(probe.info.width), height: Double(probe.info.height))

        // Same 2-layer timeline as runPlayback (base + rotated PiP), plus a
        // vignette effect on the base clip to exercise the SPIR-V effect pipeline.
        var a = Clip(mediaRef: "a", startFrame: 0, durationFrames: 60); a.id = "a"
        a.effects = [Effect(id: "v1", type: "stylize.vignette", enabled: true, params: [
            "amount": EffectParam(value: -0.8),
            "midpoint": EffectParam(value: 0.2),
            "feather": EffectParam(value: 0.6)
        ])]
        var track1 = Track(type: .video)
        track1.clips = [a]
        var b = Clip(mediaRef: "b", startFrame: 0, durationFrames: 60); b.id = "b"
        b.transform.width = 0.35
        b.transform.height = 0.35
        b.transform.centerX = 0.78
        b.transform.centerY = 0.22
        b.transform.rotation = 8
        var track2 = Track(type: .video)
        track2.clips = [b]
        // Text layer (top): a title overlay rendered via stb_truetype (no COM).
        var title = Clip(mediaRef: "", startFrame: 0, durationFrames: 60)
        title.id = "title"
        title.mediaType = .text
        title.textContent = "PALMIER PRO"
        title.textStyle = TextStyle()
        var track3 = Track(type: .video)
        track3.clips = [title]
        var timeline = Timeline()
        timeline.tracks = [track3, track2, track1]  // bottom→top: video base, PiP, text

        let trackSlots: [String: TrackSlot] = [
            "a": TrackSlot(trackID: TrackID(rawValue: 1), natSize: renderSize, transform: .identity),
            "b": TrackSlot(trackID: TrackID(rawValue: 2), natSize: renderSize, transform: .identity)
        ]
        let media: [TrackID: String] = [
            TrackID(rawValue: 1): clipPath,
            TrackID(rawValue: 2): clipPath
        ]
        let outPath = "PalmierWin/test_media/exported.mp4"
        let config = FFmpegEncoder.Config(
            width: probe.info.width, height: probe.info.height, fps: 30
        )
        print("Export: exporting 3-layer timeline (video+PiP+text) → \(outPath)")
        guard let exporter = WinExporter(
            device: dev, timeline: timeline, renderSize: renderSize,
            trackSlots: trackSlots, mediaPaths: media,
            outputPath: outPath, encoderConfig: config
        ) else {
            print("Export: WinExporter FAILED to create")
            return
        }
        do {
            let frames = try exporter.export()
            let size = (try? FileManager.default.attributesOfItem(atPath: outPath)[.size] as? Int) ?? 0
            print("Export: encoded \(frames) frame(s), \(size) bytes → \(outPath)")
        } catch {
            print("Export: error \(error)")
        }
    }

    /// Exercises the FrameRendering protocol conformance (WinFrameRenderer):
    /// decode a frame, build a single-clip timeline, plan it, and render one
    /// segment into an offscreen texture via the portable upstream interface.
    /// Proves the Windows port speaks the same render contract as macOS.
    static func runFrameRendering(dev: VulkanDevice) {
        let clipPath = "PalmierWin/test_media/testsrc.mp4"
        guard FileManager.default.fileExists(atPath: clipPath) else {
            print("FrameRendering: skipped (no \(clipPath))")
            return
        }
        guard let sourceTexture = decodeAndUpload(dev: dev, clipPath: clipPath) else { return }
        guard let renderer = WinFrameRenderer(device: dev) else {
            print("WinFrameRenderer: FAILED to create")
            return
        }
        print("WinFrameRenderer: created OK (FrameRendering conformer, layer pipeline + push constants)")

        // Build a single-clip timeline matching the decoded source dimensions.
        let renderSize = Size2D(width: Double(sourceTexture.width), height: Double(sourceTexture.height))
        let trackID = TrackID(rawValue: 1)
        var clip = Clip(mediaRef: "a", startFrame: 0, durationFrames: 30)
        clip.id = "a"
        var track = Track(type: .video)
        track.clips = [clip]
        var timeline = Timeline()
        timeline.tracks = [track]

        let slots: [String: TrackSlot] = [
            "a": TrackSlot(trackID: trackID, natSize: renderSize, transform: .identity)
        ]
        let plan = RenderPlanner.plan(
            timeline: timeline, renderSize: renderSize,
            totalFrames: timeline.totalFrames, trackSlots: slots, resolveTimeline: { _ in nil }
        )
        guard let instruction = plan.first else {
            print("FrameRendering: planner produced no segments")
            return
        }
        print("FrameRendering: planner produced \(plan.count) segment(s); rendering segment 0 (\(instruction.layers.count) layer(s))")

        guard let offscreen = VulkanTexture(device: dev, width: sourceTexture.width, height: sourceTexture.height) else {
            print("FrameRendering: offscreen texture FAILED to create")
            return
        }

        // The sourceFrame closure returns the decoded texture for trackID.
        // frame=0, default identity transform → the source fills the canvas.
        renderer.render(
            instruction: instruction,
            frame: 0,
            sourceFrame: { id in id == trackID ? sourceTexture : nil },
            into: offscreen
        )
        print("FrameRendering: rendered segment 0 into offscreen texture via WinFrameRenderer (protocol conformance OK)")
    }

    /// Full timeline playback end-to-end: builds a 2-clip timeline sourcing the
    /// test clip twice (track 1 + track 2 as a picture-in-picture), plans it,
    /// and plays it via WinPlayback — per frame: decode → WinFrameRenderer
    /// composite → blit offscreen → swapchain → present. Window shows the
    /// timeline playing. Skipped on CI (no GPU, no test clip).
    static func runPlayback(dev: VulkanDevice, win: Win32Window, swap: VulkanSwapchain) {
        let clipPath = "PalmierWin/test_media/testsrc.mp4"
        guard FileManager.default.fileExists(atPath: clipPath) else {
            print("Playback: skipped (no \(clipPath))")
            return
        }
        guard let probe = try? FFmpegDecoder(path: clipPath) else {
            print("Playback: couldn't open test clip")
            return
        }
        let renderSize = Size2D(width: Double(probe.info.width), height: Double(probe.info.height))

        // Two clips on two tracks. Track 1 fills the canvas (the base layer);
        // track 2 is a smaller picture-in-picture in the corner (exercises the
        // per-layer placement + composite path, not just a full-screen blit).
        var a = Clip(mediaRef: "a", startFrame: 0, durationFrames: 60); a.id = "a"
        var track1 = Track(type: .video)
        track1.clips = [a]
        var b = Clip(mediaRef: "b", startFrame: 0, durationFrames: 60); b.id = "b"
        b.transform.width = 0.35
        b.transform.height = 0.35
        b.transform.centerX = 0.78
        b.transform.centerY = 0.22
        b.transform.rotation = 8
        var track2 = Track(type: .video)
        track2.clips = [b]
        var timeline = Timeline()
        timeline.tracks = [track2, track1]  // bottom→top: track1 is the base

        let trackSlots: [String: TrackSlot] = [
            "a": TrackSlot(trackID: TrackID(rawValue: 1), natSize: renderSize, transform: .identity),
            "b": TrackSlot(trackID: TrackID(rawValue: 2), natSize: renderSize, transform: .identity)
        ]
        let media: [TrackID: String] = [
            TrackID(rawValue: 1): clipPath,
            TrackID(rawValue: 2): clipPath
        ]
        guard let playback = WinPlayback(
            device: dev, swapchain: swap, timeline: timeline,
            renderSize: renderSize, trackSlots: trackSlots, mediaPaths: media
        ) else {
            print("Playback: WinPlayback FAILED to create")
            return
        }
        print("Playback: playing 2-layer timeline (base + PiP) end-to-end")
        win.show()
        playback.play(window: win, shouldStop: { !win.pollEvents() })
        print("Playback: done")
    }
}
