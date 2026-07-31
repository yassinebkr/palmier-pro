import CVulkan

/// Allocates a descriptor pool + one descriptor set matching a pipeline's
/// set-layout (combined image samplers starting at binding 0), and binds a
/// texture's view+sampler into it. The renderer binds this set per draw.
///
/// One pool / one set per draw. The per-layer composite pools many sets (one
/// per source frame) — see docs/windows-media-engine-design.md.
public final class VulkanDescriptor: @unchecked Sendable {
    public let device: VkDevice
    public let pool: VkDescriptorPool
    public let set: VkDescriptorSet

    /// Creates the pool, allocates one set from `layout`, and writes `texture`'s
    /// view+sampler into binding 0. `poolSamplers` sizes the pool for layouts
    /// with multiple sampler bindings (the caller writes the rest itself).
    /// Returns nil if any Vulkan call fails.
    public init?(device: VulkanDevice, layout: VkDescriptorSetLayout, texture: VulkanTexture, poolSamplers: Int = 1) {
        let dev = device.device

        var poolSize = VkDescriptorPoolSize()
        poolSize.type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER
        poolSize.descriptorCount = UInt32(poolSamplers)
        var poolInfo = VkDescriptorPoolCreateInfo()
        poolInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO
        poolInfo.maxSets = 1
        poolInfo.poolSizeCount = 1
        var p: VkDescriptorPool? = nil
        guard withUnsafePointer(to: &poolSize) { ps in
            poolInfo.pPoolSizes = ps
            return withUnsafePointer(to: &poolInfo) { pi in vkCreateDescriptorPool(dev, pi, nil, &p) }
        } == VK_SUCCESS, let p else { return nil }
        self.pool = p

        var allocInfo = VkDescriptorSetAllocateInfo()
        allocInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO
        allocInfo.descriptorPool = p
        allocInfo.descriptorSetCount = 1
        var layoutHandle: VkDescriptorSetLayout? = layout
        var s: VkDescriptorSet? = nil
        guard withUnsafePointer(to: &layoutHandle) { lp in
            allocInfo.pSetLayouts = lp
            return withUnsafePointer(to: &allocInfo) { ai in vkAllocateDescriptorSets(dev, ai, &s) }
        } == VK_SUCCESS, let s else {
            vkDestroyDescriptorPool(dev, p, nil); return nil
        }
        self.set = s

        // Write the texture's view+sampler into binding 0.
        var imageInfo = VkDescriptorImageInfo()
        imageInfo.sampler = texture.sampler
        imageInfo.imageView = texture.view
        imageInfo.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL

        var write = VkWriteDescriptorSet()
        write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET
        write.dstSet = s
        write.dstBinding = 0
        write.dstArrayElement = 0
        write.descriptorCount = 1
        write.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER
        withUnsafePointer(to: &imageInfo) { ii in
            write.pImageInfo = ii
            withUnsafePointer(to: &write) { w in vkUpdateDescriptorSets(dev, 1, w, 0, nil) }
        }
        self.device = dev
    }

    deinit { vkDestroyDescriptorPool(device, pool, nil) }
}

extension VulkanDescriptor {
    /// Multi-sampler variant for the effect pipeline's 3-binding layout: writes
    /// `texture` into binding 0 and `aux`/`aux2` into bindings 1/2 (falling back
    /// to `fallback` for any nil slot — every binding must be valid). Pool is
    /// sized for 3 samplers.
    public convenience init?(device: VulkanDevice, layout: VkDescriptorSetLayout,
                             texture: VulkanTexture, aux: VulkanTexture?, aux2: VulkanTexture?,
                             fallback: VulkanTexture) {
        self.init(device: device, layout: layout, texture: texture, poolSamplers: 3)
        var writes: [VkWriteDescriptorSet] = []
        var imageInfos: [VkDescriptorImageInfo] = []
        let slots: [VulkanTexture] = [texture, aux ?? fallback, aux2 ?? fallback]
        for (i, tex) in slots.enumerated() {
            var imageInfo = VkDescriptorImageInfo()
            imageInfo.sampler = tex.sampler
            imageInfo.imageView = tex.view
            imageInfo.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
            imageInfos.append(imageInfo)
            var write = VkWriteDescriptorSet()
            write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET
            write.dstSet = set
            write.dstBinding = UInt32(i)
            write.dstArrayElement = 0
            write.descriptorCount = 1
            write.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER
            writes.append(write)
        }
        writes.withUnsafeMutableBufferPointer { wBuf in
            imageInfos.withUnsafeBufferPointer { iiBuf in
                for i in 0..<wBuf.count {
                    wBuf[i].pImageInfo = iiBuf.baseAddress! + i
                }
                vkUpdateDescriptorSets(device.device, UInt32(wBuf.count), UnsafePointer(wBuf.baseAddress), 0, nil)
            }
        }
    }
}
