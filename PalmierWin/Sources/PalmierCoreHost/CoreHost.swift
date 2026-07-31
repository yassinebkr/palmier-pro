import CVulkan
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

/// Retained engine state behind the opaque handle.
private final class EngineContext {
    let instance: VkInstance
    var device: VulkanDevice?
    let window: Win32Window
    var swapchain: VulkanSwapchain?
    init(instance: VkInstance, device: VulkanDevice, window: Win32Window, swapchain: VulkanSwapchain) {
        self.instance = instance
        self.device = device
        self.window = window
        self.swapchain = swapchain
    }
    // Teardown order is dependency order: swapchain → device → (caller does
    // instance). Optionals let deinit force that sequence.
    deinit {
        if let device {
            vkDeviceWaitIdle(device.device)
        }
        swapchain = nil
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
    guard let swapchain = VulkanSwapchain(device: device, instance: instance, window: window) else { return nil }
    let ctx = EngineContext(instance: instance, device: device, window: window, swapchain: swapchain)
    return Unmanaged.passRetained(ctx).toOpaque()
}

/// Renders one frame (animated clear color) and presents. Returns 1 on
/// success, 0 on failure. Message pumping is the caller's job.
@_cdecl("palmier_engine_render_frame")
public func palmierEngineRenderFrame(_ handle: UnsafeMutableRawPointer?, _ frame: Int32) -> Int32 {
    guard let handle else { return 0 }
    let ctx = Unmanaged<EngineContext>.fromOpaque(handle).takeUnretainedValue()
    guard let dev = ctx.device, let swap = ctx.swapchain else { return 0 }
    let vkDev = dev.device

    var fence: VkFence? = swap.inFlight
    withUnsafePointer(to: &fence) { f in _ = vkWaitForFences(vkDev, 1, f, UInt32(VK_TRUE), UInt64.max) }
    _ = withUnsafePointer(to: &fence) { f in vkResetFences(vkDev, 1, f) }

    var imageIndex: UInt32 = 0
    let acquire = vkAcquireNextImageKHR(vkDev, swap.swapchain, UInt64.max, swap.imageAvailable, nil, &imageIndex)
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
@_cdecl("palmier_engine_destroy")
public func palmierEngineDestroy(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    let instance = Unmanaged<EngineContext>.fromOpaque(handle).takeUnretainedValue().instance
    Unmanaged<EngineContext>.fromOpaque(handle).release()
    Vulkan.destroyInstance(instance)
}
