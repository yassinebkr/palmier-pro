import CVulkan
import WinSDK

/// Owns the presentation surface + swapchain + per-frame render targets.
///
/// Pipeline: HWND → `VK_KHR_win32_surface` → swapchain (BGRA8_UNORM, FIFO) →
/// `imageViews` → render pass → `framebuffers`. Plus the semaphores/fences one
/// frame-in-flight needs to acquire/present safely.
///
/// All `vk*` functions used here are statically exported by vulkan-1.lib (no
/// loader). The Win32 surface needs the `VK_KHR_win32_surface` instance
/// extension and `VK_KHR_swapchain` device extension (both enabled upstream).
/// The presentation surface for one HWND, owned independently of the
/// swapchains built on it.
///
/// A window may have only one live swapchain. Creating a second surface and
/// swapchain for an HWND that still has one — which is what recreating on
/// resize used to do, because the presenter held the old swapchain alive —
/// fails with `VK_ERROR_NATIVE_WINDOW_IN_USE_KHR`, and the retry fails the
/// same way forever. Keeping one surface for the window's lifetime and
/// retiring only the swapchain removes that failure entirely.
public final class VulkanSurface: @unchecked Sendable {
    public let instance: VkInstance
    public let surface: VkSurfaceKHR

    public init?(instance: VkInstance, window: Win32Window) {
        var info = VkWin32SurfaceCreateInfoKHR()
        info.sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR
        info.hinstance = window.instance
        info.hwnd = window.hwnd
        var made: VkSurfaceKHR? = nil
        guard vkCreateWin32SurfaceKHR(instance, &info, nil, &made) == VK_SUCCESS, let made else { return nil }
        self.instance = instance
        self.surface = made
    }

    deinit { vkDestroySurfaceKHR(instance, surface, nil) }
}

public final class VulkanSwapchain: @unchecked Sendable {
    public let device: VulkanDevice
    public let instance: VkInstance
    /// Keeps the surface alive for as long as any swapchain uses it.
    public let surfaceOwner: VulkanSurface
    public let surface: VkSurfaceKHR
    public let swapchain: VkSwapchainKHR
    public let extent: VkExtent2D
    public let format: VkFormat
    public let renderPass: VkRenderPass

    public let imageViews: [VkImageView]
    /// Raw swapchain `VkImage` handles — needed for offscreen→swapchain blits.
    /// Pair with `imageViews[i]` by index.
    public let images: [VkImage]
    public let framebuffers: [VkFramebuffer]

    // One frame in flight: acquire/transfer semaphore + submit/present semaphore
    // + a fence the CPU waits on before reusing the command buffer.
    public let imageAvailable: VkSemaphore
    public let renderFinished: VkSemaphore
    public let inFlight: VkFence

    /// Creates the surface + swapchain + render pass + framebuffers + sync.
    /// Returns nil if the device can't present to the surface (no graphics+present
    /// queue overlap) or any Vulkan call fails.
    /// Builds a swapchain for `window`. Pass the window's existing
    /// `VulkanSurface` and the swapchain being replaced when recreating after
    /// a resize; both default to nil for a first-time create.
    public init?(device: VulkanDevice, instance: VkInstance, window: Win32Window,
                 surface sharedSurface: VulkanSurface? = nil,
                 oldSwapchain: VkSwapchainKHR? = nil) {
        self.device = device
        self.instance = instance

        // 1) Win32 surface — reused across recreations when the caller owns one.
        guard let owner = sharedSurface ?? VulkanSurface(instance: instance, window: window) else { return nil }
        self.surfaceOwner = owner
        let surf = owner.surface
        self.surface = surf

        // 2) Capabilities + format + extent.
        guard let (caps, chosenFormat, chosenExtent) = VulkanSwapchain.querySurface(
            physical: device.physical, surface: surf, window: window
        ) else { return nil }
        self.extent = chosenExtent
        self.format = chosenFormat

        // 3) Swapchain (FIFO mailbox — vsync on).
        var scc = VkSwapchainCreateInfoKHR()
        scc.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR
        scc.surface = surf
        scc.minImageCount = max(2, caps.minImageCount)
        scc.imageFormat = chosenFormat
        scc.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR
        scc.imageExtent = chosenExtent
        scc.imageArrayLayers = 1
        scc.imageUsage = UInt32(VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT.rawValue) | UInt32(VK_IMAGE_USAGE_TRANSFER_DST_BIT.rawValue)
        scc.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE
        scc.preTransform = caps.currentTransform
        scc.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR
        scc.presentMode = VK_PRESENT_MODE_FIFO_KHR
        scc.clipped = UInt32(VK_TRUE)
        // Retires the previous swapchain in place rather than leaving the
        // window with two, which is what the driver refuses.
        scc.oldSwapchain = oldSwapchain
        var swp: VkSwapchainKHR? = nil
        guard vkCreateSwapchainKHR(device.device, &scc, nil, &swp) == VK_SUCCESS, let swp else { return nil }
        self.swapchain = swp

        // 4) Swapchain images → image views.
        var imgCount: UInt32 = 0
        vkGetSwapchainImagesKHR(device.device, swp, &imgCount, nil)
        var images = [VkImage?](repeating: nil, count: Int(imgCount))
        guard vkGetSwapchainImagesKHR(device.device, swp, &imgCount, images.withUnsafeMutableBufferPointer { $0.baseAddress }) == VK_SUCCESS
        else { vkDestroySwapchainKHR(device.device, swp, nil); return nil }

        self.imageViews = images.compactMap { image -> VkImageView? in
            guard let image else { return nil }
            var viewInfo = VkImageViewCreateInfo()
            viewInfo.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO
            viewInfo.image = image
            viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D
            viewInfo.format = chosenFormat
            viewInfo.components = VkComponentMapping(r: VK_COMPONENT_SWIZZLE_IDENTITY, g: VK_COMPONENT_SWIZZLE_IDENTITY, b: VK_COMPONENT_SWIZZLE_IDENTITY, a: VK_COMPONENT_SWIZZLE_IDENTITY)
            viewInfo.subresourceRange = VkImageSubresourceRange(
                aspectMask: UInt32(VK_IMAGE_ASPECT_COLOR_BIT.rawValue),
                baseMipLevel: 0, levelCount: 1, baseArrayLayer: 0, layerCount: 1
            )
            var view: VkImageView? = nil
            return vkCreateImageView(device.device, &viewInfo, nil, &view) == VK_SUCCESS ? view : nil
        }
        guard imageViews.count == images.count else { return nil }
        self.images = images.compactMap { $0 }

        // 5) Render pass (one color attachment, BGRA8, load-clear/store-store).
        var attachment = VkAttachmentDescription()
        attachment.format = chosenFormat
        attachment.samples = VK_SAMPLE_COUNT_1_BIT
        attachment.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR
        attachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE
        attachment.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE
        attachment.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE
        attachment.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED
        attachment.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR
        var colorRef = VkAttachmentReference()
        colorRef.attachment = 0
        colorRef.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL
        var subpass = VkSubpassDescription()
        subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS
        subpass.colorAttachmentCount = 1
        subpass.pColorAttachments = withUnsafePointer(to: &colorRef) { $0 }
        var rpInfo = VkRenderPassCreateInfo()
        rpInfo.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO
        rpInfo.attachmentCount = 1
        rpInfo.pAttachments = withUnsafePointer(to: &attachment) { $0 }
        rpInfo.subpassCount = 1
        rpInfo.pSubpasses = withUnsafePointer(to: &subpass) { $0 }
        var rp: VkRenderPass? = nil
        guard vkCreateRenderPass(device.device, &rpInfo, nil, &rp) == VK_SUCCESS, let rp else { return nil }
        self.renderPass = rp

        // 6) One framebuffer per swapchain image view.
        var builtFbs: [VkFramebuffer] = []
        for view in imageViews {
            var viewPtr: [VkImageView?] = [view]
            var fbInfo = VkFramebufferCreateInfo()
            fbInfo.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO
            fbInfo.renderPass = rp
            fbInfo.attachmentCount = 1
            fbInfo.pAttachments = viewPtr.withUnsafeMutableBufferPointer { UnsafePointer($0.baseAddress) }
            fbInfo.width = chosenExtent.width
            fbInfo.height = chosenExtent.height
            fbInfo.layers = 1
            var fb: VkFramebuffer? = nil
            if vkCreateFramebuffer(device.device, &fbInfo, nil, &fb) == VK_SUCCESS, let fb {
                builtFbs.append(fb)
            }
        }
        self.framebuffers = builtFbs
        guard framebuffers.count == imageViews.count else { return nil }

        // 7) Sync primitives.
        var semInfo = VkSemaphoreCreateInfo()
        semInfo.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO
        var fenceInfo = VkFenceCreateInfo()
        fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO
        fenceInfo.flags = UInt32(VK_FENCE_CREATE_SIGNALED_BIT.rawValue)
        var ia: VkSemaphore? = nil, rf: VkSemaphore? = nil, fl: VkFence? = nil
        guard vkCreateSemaphore(device.device, &semInfo, nil, &ia) == VK_SUCCESS,
              vkCreateSemaphore(device.device, &semInfo, nil, &rf) == VK_SUCCESS,
              vkCreateFence(device.device, &fenceInfo, nil, &fl) == VK_SUCCESS,
              let ia, let rf, let fl else { return nil }
        self.imageAvailable = ia
        self.renderFinished = rf
        self.inFlight = fl
    }

    deinit {
        vkDestroySemaphore(device.device, imageAvailable, nil)
        vkDestroySemaphore(device.device, renderFinished, nil)
        vkDestroyFence(device.device, inFlight, nil)
        for fb in framebuffers { vkDestroyFramebuffer(device.device, fb, nil) }
        vkDestroyRenderPass(device.device, renderPass, nil)
        for v in imageViews { vkDestroyImageView(device.device, v, nil) }
        vkDestroySwapchainKHR(device.device, swapchain, nil)
        // The surface outlives this swapchain; `surfaceOwner` releases it once
        // no swapchain for the window is left.
    }

    // MARK: - Surface query

    private static func querySurface(physical: VkPhysicalDevice, surface: VkSurfaceKHR, window: Win32Window)
        -> (caps: VkSurfaceCapabilitiesKHR, format: VkFormat, extent: VkExtent2D)?
    {
        var caps = VkSurfaceCapabilitiesKHR()
        guard vkGetPhysicalDeviceSurfaceCapabilitiesKHR(physical, surface, &caps) == VK_SUCCESS else { return nil }

        // Format: prefer BGRA8_UNORM, else the first available.
        var fmtCount: UInt32 = 0
        vkGetPhysicalDeviceSurfaceFormatsKHR(physical, surface, &fmtCount, nil)
        var fmts = [VkSurfaceFormatKHR](repeating: VkSurfaceFormatKHR(), count: Int(fmtCount))
        guard fmtCount > 0,
              vkGetPhysicalDeviceSurfaceFormatsKHR(physical, surface, &fmtCount, fmts.withUnsafeMutableBufferPointer { $0.baseAddress }) == VK_SUCCESS
        else { return nil }
        let chosenFormat = fmts.first { $0.format == VK_FORMAT_B8G8R8A8_UNORM }?.format ?? fmts.first!.format

        // Extent: if currentExtent is the sentinel (max), use the window's client size.
        let extent: VkExtent2D
        let maxUInt32 = UInt32.max
        if caps.currentExtent.width != maxUInt32 {
            extent = caps.currentExtent
        } else {
            var rect = RECT()
            GetClientRect(window.hwnd, &rect)
            let w = max(caps.minImageExtent.width, min(caps.maxImageExtent.width, UInt32(rect.right - rect.left)))
            let h = max(caps.minImageExtent.height, min(caps.maxImageExtent.height, UInt32(rect.bottom - rect.top)))
            extent = VkExtent2D(width: w, height: h)
        }
        return (caps, chosenFormat, extent)
    }
}
