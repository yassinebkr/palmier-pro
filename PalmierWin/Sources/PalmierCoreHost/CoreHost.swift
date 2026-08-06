import CCrashGuard
import CVulkan
import Foundation
import PalmierCore
import PalmierWin
import WinSDK

// C ABI surface for the .NET shell. Every function is freestanding, uses only
// C-compatible types (integers, doubles, pointers), and is documented with the
// exact contract the P/Invoke side relies on. Handles are opaque pointers.

/// Sanity check for the interop path: returns a + b.
@_cdecl("palmier_add")
public func palmierAdd(_ a: Int32, _ b: Int32) -> Int32 { a + b }

/// Builds a small timeline (2 clips, 1 track) and returns its total frames.
/// Proves PalmierCore model code runs behind the ABI.
@_cdecl("palmier_demo_total_frames")
public func palmierDemoTotalFrames() -> Int32 {
    var a = Clip(mediaRef: "a", startFrame: 0, durationFrames: 30)
    a.id = "a"
    var b = Clip(mediaRef: "b", startFrame: 30, durationFrames: 30)
    b.id = "b"
    var track = Track(type: .video)
    track.clips = [a, b]
    var timeline = Timeline()
    timeline.tracks = [track]
    return Int32(timeline.totalFrames)
}

/// Runs the ripple engine on the demo timeline and returns clip b's shifted
/// start frame after removing a. Proves the portable editing engines work.
@_cdecl("palmier_demo_ripple_shift")
public func palmierDemoRippleShift() -> Int32 {
    var a = Clip(mediaRef: "a", startFrame: 0, durationFrames: 30)
    a.id = "a"
    var b = Clip(mediaRef: "b", startFrame: 30, durationFrames: 30)
    b.id = "b"
    let shifts = RippleEngine.computeRippleShifts(clips: [a, b], removedIds: ["a"])
    return Int32(shifts.first { $0.clipId == "b" }?.newStartFrame ?? -1)
}

// MARK: - Engine host (interop spike 2: Swift renders into a shell-owned HWND)

/// Ceiling on any GPU wait the render thread makes. Waiting forever means one
/// hiccup freezes the preview for the rest of the session.
let engineGpuTimeoutNanoseconds: UInt64 = 1_000_000_000

/// Retained engine state behind the opaque handle.
final class EngineContext {
    let instance: VkInstance
    var device: VulkanDevice?
    let window: Win32Window
    /// One surface for the window's lifetime; swapchains are retired against it.
    var surface: VulkanSurface?
    var swapchain: VulkanSwapchain?
    /// Client size the last swapchain rebuild was attempted at. A rebuild the
    /// driver refused is retried when the target size changes, not every frame.
    var lastSwapchainAttempt: (width: Int, height: Int)?

    // Everything below `inbox` is owned by the render thread. The shell runs on
    // its own thread, so what it sets goes through the lock and is picked up at
    // the top of a frame — a Vulkan presenter must never be released out from
    // under the thread drawing with it.
    private let inbox = NSLock()
    private var pendingProject: ProjectContext??
    private var pendingSelection: String?

    /// Timeline playback (attached via palmier_engine_set_project).
    var project: ProjectContext?
    var presenter: WinPlayback?
    var presenterGeneration: Int = -1
    /// The timeline has no video to plan; the presenter only clears the screen.
    var presenterEmpty = false
    /// Revision whose planning already failed, so it is not retried per frame.
    var plannedGeneration: Int?
    var natSizeCache: [String: Size2D] = [:]
    /// Decoders survive presenter rebuilds; an edit must not reopen every clip.
    let decodeCaches = DecodeCachePool()
    /// Clip the shell has selected, drawn with a manipulation frame.
    var selectedClipId: String?

    /// Called from the shell's thread.
    func post(project: ProjectContext?) {
        inbox.lock()
        pendingProject = .some(project)
        inbox.unlock()
    }

    /// Called from the shell's thread.
    func post(selection: String?) {
        inbox.lock()
        pendingSelection = selection
        inbox.unlock()
    }

    /// Called from the render thread, once per frame, before anything reads
    /// the fields it updates.
    func drainInbox() {
        inbox.lock()
        let nextProject = pendingProject
        let nextSelection = pendingSelection
        pendingProject = nil
        inbox.unlock()

        selectedClipId = nextSelection
        guard let nextProject else { return }
        project = nextProject
        presenter = nil
        presenterEmpty = false
        presenterGeneration = -1
    }

    init(instance: VkInstance, device: VulkanDevice, window: Win32Window,
         surface: VulkanSurface, swapchain: VulkanSwapchain) {
        self.instance = instance
        self.device = device
        self.window = window
        self.surface = surface
        self.swapchain = swapchain
    }
    // Teardown order is dependency order: presenter (offscreen/decoders) →
    // swapchain → surface → device → (caller does instance). Optionals let
    // deinit force that sequence.
    deinit {
        if let device {
            vkDeviceWaitIdle(device.device)
        }
        presenter = nil
        swapchain = nil
        surface = nil
        device = nil
    }
}

/// Creates the Vulkan engine on a window owned by the caller (the .NET shell).
/// `hwnd` is a Win32 HWND as an opaque pointer. Returns an opaque engine
/// handle, or NULL on failure. Call palmier_engine_destroy to release.
@_cdecl("palmier_engine_create")
public func palmierEngineCreate(_ hwnd: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let hwnd, let nativeHwnd = HWND(bitPattern: UInt(bitPattern: hwnd)) else { return nil }
    guard let instance = Vulkan.createInstance(appName: "palmier-shell",
                                               extensions: ["VK_KHR_surface", "VK_KHR_win32_surface"]),
          let device = VulkanDevice.create(instance: instance) else { return nil }
    let window = Win32Window(foreignHwnd: nativeHwnd)
    guard let surface = VulkanSurface(instance: instance, window: window),
          let swapchain = VulkanSwapchain(device: device, instance: instance,
                                          window: window, surface: surface) else { return nil }
    let ctx = EngineContext(instance: instance, device: device, window: window,
                            surface: surface, swapchain: swapchain)
    let handle = Unmanaged.passRetained(ctx).toOpaque()
    HandleRegistry.shared.register(handle)
    return handle
}

/// Renders one frame and presents. With a project attached this composites
/// the timeline frame through WinFrameRenderer; otherwise it renders the
/// animated clear (interop diagnostic). Returns 1 on success, 0 on failure.
/// Message pumping is the caller's job.
@_cdecl("palmier_engine_render_frame")
public func palmierEngineRenderFrame(_ handle: UnsafeMutableRawPointer?, _ frame: Int32) -> Int32 {
    guard let handle else { return 0 }
    let ctx = Unmanaged<EngineContext>.fromOpaque(handle).takeUnretainedValue()
    // Apply anything the shell posted, on this thread, before deciding what to
    // draw — otherwise a project attached a moment ago is not seen until the
    // next frame, or never on the diagnostic path.
    ctx.drainInbox()
    if ctx.project != nil { return renderProjectFrame(ctx, frame: Int(frame)) }
    guard let dev = ctx.device else { return 0 }
    // Same rule as the project path: a missing swapchain is a state to retry
    // out of, not a permanent failure.
    guard let swap = ctx.swapchain else { return 1 }
    let vkDev = dev.device

    // Bounded wait, and the fence is reset only once a submit is certain: the
    // same rule as WinPlayback.blitAndPresent, and for the same reason — a
    // fence reset without a submit to signal it wedges every later frame.
    var fence: VkFence? = swap.inFlight
    let waited = withUnsafePointer(to: &fence) { f in
        vkWaitForFences(vkDev, 1, f, UInt32(VK_TRUE), engineGpuTimeoutNanoseconds)
    }
    guard waited == VK_SUCCESS else { return 1 }   // busy, not broken: try again

    var imageIndex: UInt32 = 0
    let acquire = vkAcquireNextImageKHR(vkDev, swap.swapchain, engineGpuTimeoutNanoseconds,
                                        swap.imageAvailable, nil, &imageIndex)
    guard acquire == VK_SUCCESS || acquire == VK_SUBOPTIMAL_KHR else { return 0 }

    var cbInfo = VkCommandBufferAllocateInfo()
    cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
    cbInfo.commandPool = dev.commandPool
    cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
    cbInfo.commandBufferCount = 1
    var cmd: VkCommandBuffer? = nil
    guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(vkDev, $0, &cmd) }) == VK_SUCCESS, let cmd else { return 0 }
    var beginInfo = VkCommandBufferBeginInfo()
    beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
    beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
    guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else { return 0 }

    // Animated clear: cycles through a dark hue ramp to prove presentation.
    let t = Float(frame % 240) / 240.0
    var clear = VkClearValue()
    clear.color.float32.0 = 0.05 + 0.35 * t
    clear.color.float32.1 = 0.05 + 0.2 * (1.0 - t)
    clear.color.float32.2 = 0.15 + 0.4 * t
    clear.color.float32.3 = 1.0
    var rpBegin = VkRenderPassBeginInfo()
    rpBegin.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO
    rpBegin.renderPass = swap.renderPass
    rpBegin.framebuffer = swap.framebuffers[Int(imageIndex)]
    rpBegin.renderArea = VkRect2D(offset: VkOffset2D(x: 0, y: 0), extent: swap.extent)
    rpBegin.clearValueCount = 1
    withUnsafePointer(to: &clear) { c in
        rpBegin.pClearValues = c
        withUnsafePointer(to: &rpBegin) { rpb in vkCmdBeginRenderPass(cmd, rpb, VK_SUBPASS_CONTENTS_INLINE) }
    }
    vkCmdEndRenderPass(cmd)
    guard vkEndCommandBuffer(cmd) == VK_SUCCESS else { return 0 }

    _ = withUnsafePointer(to: &fence) { f in vkResetFences(vkDev, 1, f) }

    var waitSem: VkSemaphore? = swap.imageAvailable
    var signalSem: VkSemaphore? = swap.renderFinished
    var cmdHandle: VkCommandBuffer? = cmd
    var waitStage: UInt32 = UInt32(VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT.rawValue)
    var submitInfo = VkSubmitInfo()
    submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO
    submitInfo.commandBufferCount = 1
    let submitResult: VkResult = withUnsafePointer(to: &waitSem) { ws in
        submitInfo.waitSemaphoreCount = 1
        submitInfo.pWaitSemaphores = ws
        submitInfo.pCommandBuffers = withUnsafePointer(to: &cmdHandle) { $0 }
        return withUnsafePointer(to: &waitStage) { wsm in
            submitInfo.pWaitDstStageMask = wsm
            return withUnsafePointer(to: &signalSem) { ss in
                submitInfo.signalSemaphoreCount = 1
                submitInfo.pSignalSemaphores = ss
                return withUnsafePointer(to: &submitInfo) { si in vkQueueSubmit(dev.graphicsQueue, 1, si, swap.inFlight) }
            }
        }
    }
    var toFree: VkCommandBuffer? = cmd
    withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(vkDev, dev.commandPool, 1, $0) }
    guard submitResult == VK_SUCCESS else { return 0 }

    var swapHandle: VkSwapchainKHR? = swap.swapchain
    var presentInfo = VkPresentInfoKHR()
    presentInfo.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR
    withUnsafePointer(to: &signalSem) { ss in
        presentInfo.waitSemaphoreCount = 1
        presentInfo.pWaitSemaphores = ss
        withUnsafePointer(to: &swapHandle) { sw in
            presentInfo.pSwapchains = sw
            presentInfo.swapchainCount = 1
            withUnsafePointer(to: &imageIndex) { ii in
                presentInfo.pImageIndices = ii
                withUnsafePointer(to: &presentInfo) { pi in _ = vkQueuePresentKHR(dev.graphicsQueue, pi) }
            }
        }
    }
    return 1
}

/// Releases the engine handle created by palmier_engine_create. The context's
/// deinit tears down swapchain → device; the instance goes last, here.
/// A second destroy on the same handle is a no-op, not a use-after-free.
@_cdecl("palmier_engine_destroy")
public func palmierEngineDestroy(_ handle: UnsafeMutableRawPointer?) {
    guard let handle, HandleRegistry.shared.unregister(handle) else { return }
    let instance = Unmanaged<EngineContext>.fromOpaque(handle).takeUnretainedValue().instance
    Unmanaged<EngineContext>.fromOpaque(handle).release()
    Vulkan.destroyInstance(instance)
}

/// Dev-only: forces a genuine native access violation to verify the crash
/// guard end to end. Never called in production paths. (Address 1, not 0:
/// bitPattern 0 makes a nil pointer and the unwrap trap fires instead.)
@_cdecl("palmier_crash_test")
public func palmierCrashTest() {
    UnsafeMutablePointer<UInt8>(bitPattern: 1)!.pointee = 0
}

/// Installs the native vectored crash handler, passing the reporter exe
/// (this shell) the handler spawns on a fatal fault.
@_cdecl("palmier_install_crash_guard")
public func palmierInstallCrashGuard(_ reporterPath: UnsafePointer<UInt16>?) {
    crashguard_install(reporterPath)
}
