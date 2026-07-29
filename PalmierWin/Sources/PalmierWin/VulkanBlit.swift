import CVulkan

/// Copies `src` (an offscreen sampled texture in SHADER_READ_ONLY) into `dst`
/// (a swapchain image being transitioned to PRESENT_SRC), scaling with a
/// linear filter. Records the blit + the surrounding layout transitions into
/// `commandBuffer` — no submit. The caller submits after recording.
///
/// This is the bridge between the offscreen `WinFrameRenderer` output and the
/// presentation path. The swapchain image must have TRANSFER_DST usage (the
/// swapchain sets it). Both images must be BGRA8_UNORM, so no format
/// conversion is needed.
public enum VulkanBlit {
    /// Records the offscreen → swapchain blit into `commandBuffer`. `src` is
    /// the WinFrameRenderer output (currently SHADER_READ_ONLY_OPTIMAL); `dst`
    /// is the acquired swapchain image (currently UNDEFINED on first frame or
    /// PRESENT_SRC from the previous frame).
    public static func record(
        commandBuffer: VkCommandBuffer,
        src: VkImage, srcExtent: VkExtent2D,
        dst: VkImage, dstExtent: VkExtent2D
    ) {
        var range = VkImageSubresourceRange()
        range.aspectMask = UInt32(VK_IMAGE_ASPECT_COLOR_BIT.rawValue)
        range.baseMipLevel = 0
        range.levelCount = 1
        range.baseArrayLayer = 0
        range.layerCount = 1

        // src: SHADER_READ_ONLY → TRANSFER_SRC_OPTIMAL.
        var srcBarrier = VkImageMemoryBarrier2()
        srcBarrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2
        srcBarrier.srcStageMask = VK_PIPELINE_STAGE_2_COLOR_ATTACHMENT_OUTPUT_BIT
        srcBarrier.srcAccessMask = VK_ACCESS_2_COLOR_ATTACHMENT_WRITE_BIT
        srcBarrier.dstStageMask = VK_PIPELINE_STAGE_2_TRANSFER_BIT
        srcBarrier.dstAccessMask = VK_ACCESS_2_TRANSFER_READ_BIT
        srcBarrier.oldLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
        srcBarrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL
        srcBarrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        srcBarrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        srcBarrier.image = src
        srcBarrier.subresourceRange = range
        applyBarrier(commandBuffer, srcBarrier)

        // dst: (UNDEFINED|PRESENT_SRC) → TRANSFER_DST_OPTIMAL.
        var dstBarrier = VkImageMemoryBarrier2()
        dstBarrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2
        dstBarrier.srcStageMask = VK_PIPELINE_STAGE_2_NONE
        dstBarrier.srcAccessMask = VK_ACCESS_2_NONE
        dstBarrier.dstStageMask = VK_PIPELINE_STAGE_2_TRANSFER_BIT
        dstBarrier.dstAccessMask = VK_ACCESS_2_TRANSFER_WRITE_BIT
        dstBarrier.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED
        dstBarrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL
        dstBarrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        dstBarrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        dstBarrier.image = dst
        dstBarrier.subresourceRange = range
        applyBarrier(commandBuffer, dstBarrier)

        // Blit (scaling). Both offscreen and swapchain share Vulkan's top-left
        // origin (the renderer's clip-space Y is already flipped in the vert
        // shader), so no Y flip here — straight 1:1 offset mapping with scaling.
        var region = VkImageBlit()
        region.srcSubresource.aspectMask = UInt32(VK_IMAGE_ASPECT_COLOR_BIT.rawValue)
        region.srcSubresource.mipLevel = 0
        region.srcSubresource.baseArrayLayer = 0
        region.srcSubresource.layerCount = 1
        region.dstSubresource.aspectMask = UInt32(VK_IMAGE_ASPECT_COLOR_BIT.rawValue)
        region.dstSubresource.mipLevel = 0
        region.dstSubresource.baseArrayLayer = 0
        region.dstSubresource.layerCount = 1
        region.srcOffsets.0 = VkOffset3D(x: 0, y: 0, z: 0)
        region.srcOffsets.1 = VkOffset3D(x: Int32(srcExtent.width), y: Int32(srcExtent.height), z: 0)
        region.dstOffsets.0 = VkOffset3D(x: 0, y: 0, z: 0)
        region.dstOffsets.1 = VkOffset3D(x: Int32(dstExtent.width), y: Int32(dstExtent.height), z: 0)
        withUnsafePointer(to: &region) { r in
            vkCmdBlitImage(commandBuffer,
                           src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                           dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                           1, r, VK_FILTER_LINEAR)
        }

        // dst: TRANSFER_DST_OPTIMAL → PRESENT_SRC_KHR.
        var presentBarrier = VkImageMemoryBarrier2()
        presentBarrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2
        presentBarrier.srcStageMask = VK_PIPELINE_STAGE_2_TRANSFER_BIT
        presentBarrier.srcAccessMask = VK_ACCESS_2_TRANSFER_WRITE_BIT
        presentBarrier.dstStageMask = VK_PIPELINE_STAGE_2_NONE
        presentBarrier.dstAccessMask = VK_ACCESS_2_NONE
        presentBarrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL
        presentBarrier.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR
        presentBarrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        presentBarrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        presentBarrier.image = dst
        presentBarrier.subresourceRange = range
        applyBarrier(commandBuffer, presentBarrier)

        // Restore src to SHADER_READ_ONLY_OPTIMAL so the next render pass can
        // sample from it (the WinFrameRenderer's render pass finalLayout is
        // SHADER_READ_ONLY, but a subsequent render transitions it to
        // COLOR_ATTACHMENT_OPTIMAL as input — keep it consistent).
        var restoreBarrier = VkImageMemoryBarrier2()
        restoreBarrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2
        restoreBarrier.srcStageMask = VK_PIPELINE_STAGE_2_TRANSFER_BIT
        restoreBarrier.srcAccessMask = VK_ACCESS_2_TRANSFER_READ_BIT
        restoreBarrier.dstStageMask = VK_PIPELINE_STAGE_2_FRAGMENT_SHADER_BIT
        restoreBarrier.dstAccessMask = VK_ACCESS_2_SHADER_SAMPLED_READ_BIT
        restoreBarrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL
        restoreBarrier.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
        restoreBarrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        restoreBarrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        restoreBarrier.image = src
        restoreBarrier.subresourceRange = range
        applyBarrier(commandBuffer, restoreBarrier)
    }

    private static func applyBarrier(_ cmd: VkCommandBuffer, _ barrier: VkImageMemoryBarrier2) {
        var b = barrier
        var dep = VkDependencyInfo()
        dep.sType = VK_STRUCTURE_TYPE_DEPENDENCY_INFO
        dep.imageMemoryBarrierCount = 1
        withUnsafePointer(to: &b) { bPtr in
            dep.pImageMemoryBarriers = bPtr
            withUnsafePointer(to: &dep) { depPtr in
                vkCmdPipelineBarrier2(cmd, depPtr)
            }
        }
    }
}
