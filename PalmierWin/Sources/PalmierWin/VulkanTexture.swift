import CVulkan
import Foundation

/// A GPU image that a fragment shader can sample. Owns one VkImage + its
/// VkDeviceMemory + a VkImageView + a VkSampler, sized to a fixed width/height
/// in BGRA8. Frames are uploaded by copying flat tightly-packed BGRA bytes
/// into a transient host-visible staging buffer and then queueing a
/// buffer→image copy with the proper layout transitions.
///
/// For the MVP, uploads use one-shot command buffers and vkQueueWaitIdle.
/// The future playback loop will switch to a per-frame ring of staging
/// buffers with fences (see docs/windows-media-engine-design.md).
public final class VulkanTexture: @unchecked Sendable {
    public let device: VkDevice
    public let image: VkImage
    public let view: VkImageView
    public let sampler: VkSampler
    public let width: UInt32
    public let height: UInt32
    private let physical: VkPhysicalDevice
    private let memory: VkDeviceMemory
    private let queue: VkQueue
    private let commandPool: VkCommandPool

    /// Creates a BGRA8 sampled image of `width`x`height`. The image starts
    /// in UNDEFINED layout; the first `upload` transitions it to SHADER_READ_ONLY.
    public init?(device: VulkanDevice, width: UInt32, height: UInt32) {
        guard width > 0, height > 0 else { return nil }
        let dev = device.device

        var imageInfo = VkImageCreateInfo()
        imageInfo.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO
        imageInfo.imageType = VK_IMAGE_TYPE_2D
        imageInfo.format = VK_FORMAT_B8G8R8A8_UNORM
        imageInfo.extent = VkExtent3D(width: width, height: height, depth: 1)
        imageInfo.mipLevels = 1
        imageInfo.arrayLayers = 1
        imageInfo.samples = VK_SAMPLE_COUNT_1_BIT
        imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL
        imageInfo.usage = UInt32(VK_IMAGE_USAGE_TRANSFER_DST_BIT.rawValue | VK_IMAGE_USAGE_SAMPLED_BIT.rawValue)
        imageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE
        imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED

        var img: VkImage? = nil
        guard withUnsafePointer(to: &imageInfo, { vkCreateImage(dev, $0, nil, &img) }) == VK_SUCCESS,
              let img else { return nil }

        // Allocate memory in the first memory type that satisfies both the
        // image's requirements and DEVICE_LOCAL (GPU-optimal for sampling).
        var memReqs = VkMemoryRequirements()
        vkGetImageMemoryRequirements(dev, img, &memReqs)
        guard let memTypeIndex = VulkanTexture.findMemoryType(
            physical: device.physical, typeBits: memReqs.memoryTypeBits,
            properties: UInt32(VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT.rawValue)
        ) else { vkDestroyImage(dev, img, nil); return nil }

        var allocInfo = VkMemoryAllocateInfo()
        allocInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO
        allocInfo.allocationSize = memReqs.size
        allocInfo.memoryTypeIndex = memTypeIndex
        var mem: VkDeviceMemory? = nil
        guard withUnsafePointer(to: &allocInfo, { vkAllocateMemory(dev, $0, nil, &mem) }) == VK_SUCCESS,
              let mem, vkBindImageMemory(dev, img, mem, 0) == VK_SUCCESS
        else { vkDestroyImage(dev, img, nil); return nil }

        // Image view (COLOR aspect, 2D, matching format).
        var viewInfo = VkImageViewCreateInfo()
        viewInfo.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO
        viewInfo.image = img
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D
        viewInfo.format = VK_FORMAT_B8G8R8A8_UNORM
        viewInfo.subresourceRange.aspectMask = UInt32(VK_IMAGE_ASPECT_COLOR_BIT.rawValue)
        viewInfo.subresourceRange.baseMipLevel = 0
        viewInfo.subresourceRange.levelCount = 1
        viewInfo.subresourceRange.baseArrayLayer = 0
        viewInfo.subresourceRange.layerCount = 1
        var view: VkImageView? = nil
        guard withUnsafePointer(to: &viewInfo, { vkCreateImageView(dev, $0, nil, &view) }) == VK_SUCCESS,
              let view else {
            vkFreeMemory(dev, mem, nil); vkDestroyImage(dev, img, nil); return nil
        }

        // Sampler: linear filtering, clamp-to-edge, no anisotropy (1 mip level).
        var samplerInfo = VkSamplerCreateInfo()
        samplerInfo.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO
        samplerInfo.magFilter = VK_FILTER_LINEAR
        samplerInfo.minFilter = VK_FILTER_LINEAR
        samplerInfo.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE
        samplerInfo.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE
        samplerInfo.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE
        samplerInfo.anisotropyEnable = UInt32(VK_FALSE)
        samplerInfo.borderColor = VK_BORDER_COLOR_INT_OPAQUE_BLACK
        samplerInfo.unnormalizedCoordinates = UInt32(VK_FALSE)
        samplerInfo.compareEnable = UInt32(VK_FALSE)
        samplerInfo.compareOp = VK_COMPARE_OP_ALWAYS
        samplerInfo.mipmapMode = VK_SAMPLER_MIPMAP_MODE_LINEAR
        samplerInfo.mipLodBias = 0
        samplerInfo.minLod = 0
        samplerInfo.maxLod = 0
        var sampler: VkSampler? = nil
        guard withUnsafePointer(to: &samplerInfo, { vkCreateSampler(dev, $0, nil, &sampler) }) == VK_SUCCESS,
              let sampler else {
            vkDestroyImageView(dev, view, nil); vkFreeMemory(dev, mem, nil); vkDestroyImage(dev, img, nil); return nil
        }

        self.device = dev
        self.image = img
        self.view = view
        self.sampler = sampler
        self.physical = device.physical
        self.memory = mem
        self.queue = device.graphicsQueue
        self.commandPool = device.commandPool
        self.width = width
        self.height = height
    }

    deinit {
        vkDestroySampler(device, sampler, nil)
        vkDestroyImageView(device, view, nil)
        vkFreeMemory(device, memory, nil)
        vkDestroyImage(device, image, nil)
    }

    /// Uploads `bytes` (tightly-packed BGRA, width*height*4) into the image.
    /// Blocks until the copy completes (vkQueueWaitIdle). Returns false on any
    /// Vulkan error.
    @discardableResult
    public func upload(bgra bytes: Data) -> Bool {
        let expected = Int(width) * Int(height) * 4
        guard bytes.count >= expected else { return false }

        let dev = device
        let size = VkDeviceSize(expected)

        // Staging buffer: host-visible + host-coherent so the memcpy lands
        // without an explicit flush.
        var bufInfo = VkBufferCreateInfo()
        bufInfo.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO
        bufInfo.size = size
        bufInfo.usage = UInt32(VK_BUFFER_USAGE_TRANSFER_SRC_BIT.rawValue)
        bufInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE
        var staging: VkBuffer? = nil
        guard withUnsafePointer(to: &bufInfo, { vkCreateBuffer(dev, $0, nil, &staging) }) == VK_SUCCESS,
              let staging else { return false }

        var bufReqs = VkMemoryRequirements()
        vkGetBufferMemoryRequirements(dev, staging, &bufReqs)
        guard let memTypeIndex = VulkanTexture.findMemoryType(
            physical: physical, typeBits: bufReqs.memoryTypeBits,
            properties: UInt32(VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT.rawValue | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT.rawValue)
        ) else {
            vkDestroyBuffer(dev, staging, nil); return false
        }
        var allocInfo = VkMemoryAllocateInfo()
        allocInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO
        allocInfo.allocationSize = bufReqs.size
        allocInfo.memoryTypeIndex = memTypeIndex
        var stagingMem: VkDeviceMemory? = nil
        guard withUnsafePointer(to: &allocInfo, { vkAllocateMemory(dev, $0, nil, &stagingMem) }) == VK_SUCCESS,
              let stagingMem, vkBindBufferMemory(dev, staging, stagingMem, 0) == VK_SUCCESS
        else { vkDestroyBuffer(dev, staging, nil); return false }

        var mapped: UnsafeMutableRawPointer? = nil
        guard vkMapMemory(dev, stagingMem, 0, size, 0, &mapped) == VK_SUCCESS, let mapped else {
            vkFreeMemory(dev, stagingMem, nil); vkDestroyBuffer(dev, staging, nil); return false
        }
        bytes.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            if let src = raw.baseAddress, raw.count >= expected {
                memcpy(mapped, src, expected)
            }
        }
        vkUnmapMemory(dev, stagingMem)

        let ok = recordAndSubmitCopy(staging: staging)
        vkQueueWaitIdle(queue)
        vkFreeMemory(dev, stagingMem, nil)
        vkDestroyBuffer(dev, staging, nil)
        return ok
    }

    /// Records buffer→image copy + layout transitions into a one-shot command
    /// buffer and submits it. Uses sync2 (Vulkan 1.3+ core) for clarity.
    private func recordAndSubmitCopy(staging: VkBuffer) -> Bool {
        let dev = device

        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
        cbInfo.commandPool = commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
        cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(dev, $0, &cmd) }) == VK_SUCCESS,
              let cmd else { return false }

        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else {
            var cmdToFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &cmdToFree) { cPtr in vkFreeCommandBuffers(dev, commandPool, 1, cPtr) }
            return false
        }

        // Barrier 1: UNDEFINED → TRANSFER_DST_OPTIMAL.
        var range = VkImageSubresourceRange()
        range.aspectMask = UInt32(VK_IMAGE_ASPECT_COLOR_BIT.rawValue)
        range.baseMipLevel = 0
        range.levelCount = 1
        range.baseArrayLayer = 0
        range.layerCount = 1
        var b1 = VkImageMemoryBarrier2()
        b1.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2
        b1.srcStageMask = VK_PIPELINE_STAGE_2_NONE
        b1.srcAccessMask = VK_ACCESS_2_NONE
        b1.dstStageMask = VK_PIPELINE_STAGE_2_TRANSFER_BIT
        b1.dstAccessMask = VK_ACCESS_2_TRANSFER_WRITE_BIT
        b1.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED
        b1.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL
        b1.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        b1.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        b1.image = image
        b1.subresourceRange = range
        applyBarrier(cmd, b1)

        // Copy: tightly-packed src buffer → whole image.
        var region = VkBufferImageCopy()
        region.bufferOffset = 0
        region.bufferRowLength = 0
        region.bufferImageHeight = 0
        region.imageSubresource.aspectMask = UInt32(VK_IMAGE_ASPECT_COLOR_BIT.rawValue)
        region.imageSubresource.mipLevel = 0
        region.imageSubresource.baseArrayLayer = 0
        region.imageSubresource.layerCount = 1
        region.imageOffset = VkOffset3D(x: 0, y: 0, z: 0)
        region.imageExtent = VkExtent3D(width: width, height: height, depth: 1)
        withUnsafePointer(to: &region) { rPtr in
            vkCmdCopyBufferToImage(cmd, staging, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, rPtr)
        }

        // Barrier 2: TRANSFER_DST_OPTIMAL → SHADER_READ_ONLY_OPTIMAL.
        var b2 = VkImageMemoryBarrier2()
        b2.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2
        b2.srcStageMask = VK_PIPELINE_STAGE_2_TRANSFER_BIT
        b2.srcAccessMask = VK_ACCESS_2_TRANSFER_WRITE_BIT
        b2.dstStageMask = VK_PIPELINE_STAGE_2_FRAGMENT_SHADER_BIT
        b2.dstAccessMask = VK_ACCESS_2_SHADER_SAMPLED_READ_BIT
        b2.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL
        b2.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
        b2.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        b2.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED
        b2.image = image
        b2.subresourceRange = range
        applyBarrier(cmd, b2)

        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            var cmdToFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &cmdToFree) { cPtr in vkFreeCommandBuffers(dev, commandPool, 1, cPtr) }
            return false
        }

        var submitInfo = VkSubmitInfo()
        submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO
        submitInfo.commandBufferCount = 1
        var cmdHandle: VkCommandBuffer? = cmd
        let submitResult: VkResult = withUnsafePointer(to: &cmdHandle) { ptr in
            submitInfo.pCommandBuffers = ptr
            return withUnsafePointer(to: &submitInfo) { sPtr in
                vkQueueSubmit(queue, 1, sPtr, nil)
            }
        }
        var cmdToFree: VkCommandBuffer? = cmd
        withUnsafePointer(to: &cmdToFree) { cPtr in vkFreeCommandBuffers(dev, commandPool, 1, cPtr) }
        return submitResult == VK_SUCCESS
    }

    private func applyBarrier(_ cmd: VkCommandBuffer, _ barrier: VkImageMemoryBarrier2) {
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

    /// Find the first memory type matching `typeBits` and `properties` on the
    /// physical device. Returns nil if none.
    private static func findMemoryType(physical: VkPhysicalDevice, typeBits: UInt32, properties: UInt32) -> UInt32? {
        var props = VkPhysicalDeviceMemoryProperties()
        vkGetPhysicalDeviceMemoryProperties(physical, &props)
        let typeCount = props.memoryTypeCount
        // Rebind the address of the memoryTypes FIELD (not the struct base —
        // the struct begins with two uint32 count fields, so rebinding from
        // &props would misindex every VkMemoryType by 8 bytes).
        return withUnsafePointer(to: &props.memoryTypes) { mtPtr -> UInt32? in
            mtPtr.withMemoryRebound(to: VkMemoryType.self, capacity: Int(VK_MAX_MEMORY_TYPES)) { types in
                for i in 0..<Int(typeCount) {
                    let flags = types[i].propertyFlags
                    if typeBits & (1 << i) != 0, flags & properties == properties {
                        return UInt32(i)
                    }
                }
                return nil
            }
        }
    }
}
