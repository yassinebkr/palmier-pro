import CVulkan
import CImGui
import Foundation

/// Initializes and manages Dear ImGui with our existing Vulkan + Win32 context.
/// Renders UI on top of the video frame in the same render pass. NO COM,
/// NO WinUI3 — ImGui draws directly via the Vulkan backend.
///
/// Usage:
///   let ui = try WinUI(device: dev, instance: instance, window: win, swapchain: swap)
///   // per frame:
///   ui.newFrame()
///   ui.buildUI { ... }   // widgets via the flat-C API
///   ui.render(commandBuffer: cmd)  // records into the current render pass
public final class WinUI: @unchecked Sendable {
    public let device: VkDevice
    private let ctx: OpaquePointer
    private let descriptorPool: VkDescriptorPool

    public init(device: VulkanDevice, instance: VkInstance, window: Win32Window, swapchain: VulkanSwapchain) throws {
        let dev = device.device

        // ImGui needs its own descriptor pool (separate from the renderer's).
        var poolSizes: [VkDescriptorPoolSize] = [
            VkDescriptorPoolSize(type: VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, descriptorCount: 1000)
        ]
        var poolInfo = VkDescriptorPoolCreateInfo()
        poolInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO
        poolInfo.flags = UInt32(VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT.rawValue)
        poolInfo.maxSets = 1000
        poolInfo.poolSizeCount = 1
        var pool: VkDescriptorPool? = nil
        let poolResult: VkResult = poolSizes.withUnsafeBufferPointer { ps in
            poolInfo.pPoolSizes = UnsafePointer(ps.baseAddress)
            return withUnsafePointer(to: &poolInfo) { pi in
                vkCreateDescriptorPool(dev, pi, nil, &pool)
            }
        }
        guard poolResult == VK_SUCCESS, let pool else {
            throw NSError(domain: "WinUI", code: 1, userInfo: [NSLocalizedDescriptionKey: "vkCreateDescriptorPool failed"])
        }
        self.descriptorPool = pool
        self.device = dev

        // Init ImGui with our Vulkan handles + the swapchain's render pass.
        // The render pass must match what we render into (the swapchain's
        // BGRA8 render pass from VulkanSwapchain).
        guard let cctx = cimgui_init(
            UnsafeMutableRawPointer(instance),       // VkInstance
            UnsafeMutableRawPointer(window.hwnd),    // HWND
            UnsafeMutableRawPointer(device.physical),// VkPhysicalDevice
            UnsafeMutableRawPointer(dev),            // VkDevice
            device.graphicsFamily,                  // queue family
            UnsafeMutableRawPointer(device.graphicsQueue),  // VkQueue
            UInt32(swapchain.imageViews.count),     // image count
            UInt32(swapchain.imageViews.count),     // min image count
            UInt32(VK_FORMAT_B8G8R8A8_UNORM.rawValue),  // color format
            UnsafeMutableRawPointer(swapchain.renderPass), // render pass
            UnsafeMutableRawPointer(pool)            // descriptor pool
        ) else {
            vkDestroyDescriptorPool(dev, pool, nil)
            throw NSError(domain: "WinUI", code: 2, userInfo: [NSLocalizedDescriptionKey: "cimgui_init failed"])
        }
        self.ctx = cctx
    }

    deinit {
        cimgui_shutdown(ctx)
        vkDestroyDescriptorPool(device, descriptorPool, nil)
    }

    /// Begins a new ImGui frame. Call after the Win32 message pump, before
    /// building widgets or recording the render pass.
    public func newFrame() {
        cimgui_new_frame(ctx)
    }

    /// Records ImGui's draw data into the command buffer. Call INSIDE a
    /// vkCmdBeginRenderPass / vkCmdEndRenderPass block (the swapchain's
    /// render pass).
    public func render(commandBuffer: VkCommandBuffer) {
        cimgui_render(ctx, UnsafeMutableRawPointer(commandBuffer))
    }
}

// Flat-C pointer casts — ImGui's opaque handles need raw-pointer bridging.
// VkInstance/VkDevice/etc. are OpaquePointer in Swift; cimgui_init takes void*.
fileprivate extension VkInstance { var raw: UnsafeRawPointer { UnsafeRawPointer(self) } }
