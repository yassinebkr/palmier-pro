// Windows vertical slice (port-roadmap Option C). Proves the toolchain works
// for a real app surface end-to-end: PalmierCore (portable model + engines) +
// PalmierWin (Windows media bindings) link and run together. Does not decode
// or render video — that's the media-engine spike. It builds a tiny timeline,
// runs the ripple engine + render planner, and initializes Media Foundation.

import Foundation
import CVulkan
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
                        runRenderLoop(dev: dev, instance: instance, win: win, swap: swap)
                        // Exercise the FrameRendering protocol conformance on top
                        // of the simple full-screen quad above. Renders a planned
                        // timeline segment into an offscreen texture via the
                        // portable upstream interface (the same one macOS uses).
                        runFrameRendering(dev: dev)
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
    /// Extracted from main() so the early-exit guards don't have to unwind the
    /// deep `if let` chain.
    static func runRenderLoop(dev: VulkanDevice, instance: VkInstance, win: Win32Window, swap: VulkanSwapchain) {
        guard let pipe = VulkanPipeline(device: dev, renderPass: swap.renderPass, extent: swap.extent) else {
            print("Vulkan pipeline: FAILED to create")
            return
        }
        print("Vulkan pipeline: created OK (shaders loaded, descriptor layout + pipeline built)")

        // Decode one frame from the test clip and upload it to a GPU texture
        // the pipeline will sample. The clip is generated by make-test-media.ps1
        // (not committed). Without it the render loop is skipped — CI has no GPU
        // anyway, so the build still proves linking.
        let clipPath = "PalmierWin/test_media/testsrc.mp4"
        guard FileManager.default.fileExists(atPath: clipPath) else {
            print("Render loop: skipped (no \(clipPath) — run PalmierWin/make-test-media.ps1)")
            return
        }
        guard let texture = decodeAndUpload(dev: dev, clipPath: clipPath) else { return }
        guard let desc = VulkanDescriptor(device: dev, layout: pipe.descriptorSetLayout, texture: texture) else {
            print("Vulkan descriptor: FAILED to create")
            return
        }
        print("Vulkan descriptor: bound texture to set 0")
        guard let renderer = VulkanRenderer(device: dev) else {
            print("Vulkan renderer: FAILED to create")
            return
        }

        win.show()
        print("Vulkan render loop: drawing 240 frames (~4s @ 60fps) or until window close")
        var frame = 0
        let framesToDraw = 240
        while frame < framesToDraw {
            if !win.pollEvents() {
                print("Vulkan render loop: window closed at frame \(frame)")
                break
            }
            _ = renderer.drawFrame(swapchain: swap, pipeline: pipe, descriptorSet: desc.set)
            frame += 1
            // Yield to keep the message pump responsive and cap near 60fps.
            Sleep(16)
        }
        _ = vkQueueWaitIdle(dev.graphicsQueue)
        print("Vulkan render loop: drew \(frame) frame(s)")
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
}
