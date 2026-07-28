import CVulkan

/// Swift-friendly Vulkan lifecycle + version helpers. The full device/swapchain/
/// render surface is added incrementally; this is the seed that proves the
/// binding links and the runtime driver responds.
public enum Vulkan {
    /// `VK_MAKE_VERSION(major, minor, patch)` is a function-like macro (not
    /// imported) — compute it here. Layout: (major<<22)|(minor<<12)|patch.
    public static func makeVersion(_ major: UInt32, _ minor: UInt32, _ patch: UInt32) -> UInt32 {
        (major << 22) | (minor << 12) | patch
    }

    /// Creates a VkInstance with the given extension names. Returns the instance
    /// or nil on failure. Caller must `vkDestroyInstance` when done.
    public static func createInstance(appName: String, extensions: [String] = []) -> VkInstance? {
        var appInfo = VkApplicationInfo()
        appInfo.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO
        appInfo.apiVersion = makeVersion(1, 0, 0)

        let cstrings = extensions.map { strdup($0) }
        defer { cstrings.forEach { free($0) } }
        let extPtrs = cstrings.map { UnsafePointer($0) }

        var instance: VkInstance? = nil
        let result: VkResult = appName.withCString { namePtr in
            extPtrs.withUnsafeBufferPointer { buf in
                var createInfo = VkInstanceCreateInfo()
                createInfo.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
                createInfo.pApplicationInfo = withUnsafePointer(to: &appInfo) { $0 }
                createInfo.enabledExtensionCount = UInt32(buf.count)
                createInfo.ppEnabledExtensionNames = buf.baseAddress
                appInfo.pApplicationName = UnsafePointer(namePtr)
                return vkCreateInstance(&createInfo, nil, &instance)
            }
        }
        return result == VK_SUCCESS ? instance : nil
    }

    /// Balances `createInstance`.
    public static func destroyInstance(_ instance: VkInstance?) {
        vkDestroyInstance(instance, nil)
    }
}
