import CVulkan

/// Owns the Vulkan logical device + graphics queue + command pool, selected
/// from a physical device that supports graphics. Foundation for the swapchain
/// and the renderer's per-frame command buffers.
///
/// All device-level Vulkan functions used here are exported directly by
/// vulkan-1.lib (verified: vkCreateDevice, vkGetDeviceQueue, vkCreateCommandPool,
/// etc. are all in the loader's exports) — no vkGetInstanceProcAddr dance.
public final class VulkanDevice: @unchecked Sendable {
    public let physical: VkPhysicalDevice
    public let device: VkDevice
    public let graphicsQueue: VkQueue
    public let graphicsFamily: UInt32
    public let commandPool: VkCommandPool

    /// Properties of the chosen physical device (filled on init for diagnostics).
    public let deviceName: String
    public let deviceType: VkPhysicalDeviceType

    private init(physical: VkPhysicalDevice, device: VkDevice, queue: VkQueue,
                 family: UInt32, pool: VkCommandPool, name: String, type: VkPhysicalDeviceType) {
        self.physical = physical
        self.device = device
        self.graphicsQueue = queue
        self.graphicsFamily = family
        self.commandPool = pool
        self.deviceName = name
        self.deviceType = type
    }

    /// Picks the first physical device exposing a graphics queue family and
    /// creates the logical device + command pool. Returns nil if none found.
    public static func create(instance: VkInstance) -> VulkanDevice? {
        guard let physical = firstGraphicsPhysicalDevice(instance: instance) else { return nil }
        guard let family = firstGraphicsQueueFamily(physical: physical) else { return nil }

        var priority: Float = 1.0
        var qInfo = VkDeviceQueueCreateInfo()
        qInfo.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO
        qInfo.queueFamilyIndex = family
        qInfo.queueCount = 1
        qInfo.pQueuePriorities = withUnsafePointer(to: &priority) { $0 }

        // Swapchain is required to present; enable it explicitly.
        let swapchainExt = "VK_KHR_swapchain"
        var devInfo = VkDeviceCreateInfo()
        devInfo.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO
        devInfo.pQueueCreateInfos = withUnsafePointer(to: &qInfo) { $0 }
        devInfo.queueCreateInfoCount = 1

        var dev: VkDevice? = nil
        let result = swapchainExt.withCString { extPtr in
            var exts: [UnsafePointer<CChar>?] = [UnsafePointer(extPtr)]
            return exts.withUnsafeMutableBufferPointer { buf in
                devInfo.enabledExtensionCount = UInt32(buf.count)
                devInfo.ppEnabledExtensionNames = UnsafePointer(buf.baseAddress)
                return vkCreateDevice(physical, &devInfo, nil, &dev)
            }
        }
        guard result == VK_SUCCESS, let device = dev else { return nil }

        var queue: VkQueue? = nil
        vkGetDeviceQueue(device, family, 0, &queue)
        guard let queue else { vkDestroyDevice(device, nil); return nil }

        var poolInfo = VkCommandPoolCreateInfo()
        poolInfo.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO
        poolInfo.flags = UInt32(VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT.rawValue)
        poolInfo.queueFamilyIndex = family
        var pool: VkCommandPool? = nil
        guard vkCreateCommandPool(device, &poolInfo, nil, &pool) == VK_SUCCESS, let pool else {
            vkDestroyDevice(device, nil); return nil
        }

        let props = physicalDeviceProperties(physical: physical)
        return VulkanDevice(physical: physical, device: device, queue: queue,
                            family: family, pool: pool, name: props.name, type: props.type)
    }

    deinit {
        vkDestroyCommandPool(device, commandPool, nil)
        vkDestroyDevice(device, nil)
    }

    // MARK: - Selection helpers

    private static func firstGraphicsPhysicalDevice(instance: VkInstance) -> VkPhysicalDevice? {
        var count: UInt32 = 0
        vkEnumeratePhysicalDevices(instance, &count, nil)
        guard count > 0 else { return nil }
        var devices = [VkPhysicalDevice?](repeating: nil, count: Int(count))
        let result = devices.withUnsafeMutableBufferPointer { buf in
            vkEnumeratePhysicalDevices(instance, &count, buf.baseAddress)
        }
        guard result == VK_SUCCESS else { return nil }
        // Prefer a discrete/integrated GPU that has a graphics queue.
        for d in devices.compactMap({ $0 }) {
            if firstGraphicsQueueFamily(physical: d) != nil {
                return d
            }
        }
        return nil
    }

    private static func firstGraphicsQueueFamily(physical: VkPhysicalDevice) -> UInt32? {
        var count: UInt32 = 0
        vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, nil)
        guard count > 0 else { return nil }
        var props = [VkQueueFamilyProperties](repeating: VkQueueFamilyProperties(), count: Int(count))
        props.withUnsafeMutableBufferPointer { buf in
            vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, buf.baseAddress)
        }
        for (i, p) in props.enumerated() where p.queueFlags & UInt32(VK_QUEUE_GRAPHICS_BIT.rawValue) != 0 {
            return UInt32(i)
        }
        return nil
    }

    private static func physicalDeviceProperties(physical: VkPhysicalDevice) -> (name: String, type: VkPhysicalDeviceType) {
        var props = VkPhysicalDeviceProperties()
        vkGetPhysicalDeviceProperties(physical, &props)
        return (String(cString: withUnsafePointer(to: &props.deviceName) {
            $0.withMemoryRebound(to: CChar.self, capacity: 256) { $0 }
        }), props.deviceType)
    }
}
