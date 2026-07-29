import CVulkan

/// Draws the textured-quad pipeline sampling one texture into the swapchain.
/// Owns one persistent command buffer (reset+recorded per frame) and runs the
/// acquire → record → submit → present loop synchronously against the
/// swapchain's in-flight fence.
///
/// MVP: one frame in flight, synchronous fence wait each frame. The playback
/// loop will move to double-buffered frames-in-flight with per-frame command
/// buffers and semaphores — see docs/windows-media-engine-design.md.
public final class VulkanRenderer: @unchecked Sendable {
    public let device: VkDevice
    public let queue: VkQueue
    public let commandPool: VkCommandPool
    public let commandBuffer: VkCommandBuffer

    public init?(device: VulkanDevice) {
        let dev = device.device
        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
        cbInfo.commandPool = device.commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
        cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(dev, $0, &cmd) }) == VK_SUCCESS,
              let cmd else { return nil }
        self.device = dev
        self.queue = device.graphicsQueue
        self.commandPool = device.commandPool
        self.commandBuffer = cmd
    }

    deinit {
        var cmdHandle: VkCommandBuffer? = commandBuffer
        withUnsafePointer(to: &cmdHandle) { cPtr in
            vkFreeCommandBuffers(device, commandPool, 1, cPtr)
        }
    }

    /// Acquires the next swapchain image, records a draw of `pipeline` sampling
    /// `descriptorSet` into it (clearing to `clearColor`), submits, and presents.
    /// Blocks on the swapchain's in-flight fence. Returns false on any Vulkan
    /// error or if the window/surface became unavailable (suboptimality at
    /// present is tolerated for the MVP).
    @discardableResult
    public func drawFrame(
        swapchain: VulkanSwapchain,
        pipeline: VulkanPipeline,
        descriptorSet: VkDescriptorSet,
        clearColor: (Float, Float, Float, Float) = (0.05, 0.05, 0.07, 1.0)
    ) -> Bool {
        let dev = device

        // Wait for the previous frame before reusing the command buffer.
        var inFlightHandle: VkFence? = swapchain.inFlight
        withUnsafePointer(to: &inFlightHandle) { f in
            _ = vkWaitForFences(dev, 1, f, UInt32(VK_TRUE), UInt64.max)
        }
        _ = withUnsafePointer(to: &inFlightHandle) { f in
            vkResetFences(dev, 1, f)
        }

        // Acquire the next image. The imageAvailable semaphore is signaled when
        // the image is ready; we wait on it before the color-attachment stage.
        var imageIndex: UInt32 = 0
        let acquireResult = vkAcquireNextImageKHR(
            dev, swapchain.swapchain, UInt64.max, swapchain.imageAvailable, nil, &imageIndex
        )
        guard acquireResult == VK_SUCCESS || acquireResult == VK_SUBOPTIMAL_KHR else { return false }

        // Record: clear → bind pipeline → bind descriptor set → draw 3 → end.
        _ = vkResetCommandBuffer(commandBuffer, 0)
        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(commandBuffer, $0) }) == VK_SUCCESS
        else { return false }

        var clear = VkClearValue()
        clear.color.float32.0 = clearColor.0
        clear.color.float32.1 = clearColor.1
        clear.color.float32.2 = clearColor.2
        clear.color.float32.3 = clearColor.3

        var rpBegin = VkRenderPassBeginInfo()
        rpBegin.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO
        rpBegin.renderPass = swapchain.renderPass
        rpBegin.framebuffer = swapchain.framebuffers[Int(imageIndex)]
        rpBegin.renderArea = VkRect2D(offset: VkOffset2D(x: 0, y: 0), extent: swapchain.extent)
        rpBegin.clearValueCount = 1
        withUnsafePointer(to: &clear) { c in
            rpBegin.pClearValues = c
            withUnsafePointer(to: &rpBegin) { rpb in
                vkCmdBeginRenderPass(commandBuffer, rpb, VK_SUBPASS_CONTENTS_INLINE)
            }
        }

        vkCmdBindPipeline(commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline.pipeline)
        var setHandle: VkDescriptorSet? = descriptorSet
        withUnsafePointer(to: &setHandle) { s in
            vkCmdBindDescriptorSets(
                commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline.layout,
                0, 1, s, 0, nil
            )
        }
        vkCmdDraw(commandBuffer, 3, 1, 0, 0)
        vkCmdEndRenderPass(commandBuffer)
        guard vkEndCommandBuffer(commandBuffer) == VK_SUCCESS else { return false }

        // Submit: wait on imageAvailable at color-attachment-output stage, signal
        // renderFinished, signal the in-flight fence when done.
        var waitSemaphore: VkSemaphore? = swapchain.imageAvailable
        var signalSemaphore: VkSemaphore? = swapchain.renderFinished
        var cmdHandle: VkCommandBuffer? = commandBuffer
        var waitStageMask: UInt32 = UInt32(VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT.rawValue)
        var submitInfo = VkSubmitInfo()
        submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO
        submitInfo.commandBufferCount = 1
        let submitResult: VkResult = withUnsafePointer(to: &waitSemaphore) { ws in
            submitInfo.waitSemaphoreCount = 1
            submitInfo.pWaitSemaphores = ws
            submitInfo.pCommandBuffers = withUnsafePointer(to: &cmdHandle) { $0 }
            return withUnsafePointer(to: &waitStageMask) { wsm in
                submitInfo.pWaitDstStageMask = wsm
                return withUnsafePointer(to: &signalSemaphore) { ss in
                    submitInfo.signalSemaphoreCount = 1
                    submitInfo.pSignalSemaphores = ss
                    return withUnsafePointer(to: &submitInfo) { si in
                        vkQueueSubmit(queue, 1, si, swapchain.inFlight)
                    }
                }
            }
        }
        guard submitResult == VK_SUCCESS else { return false }

        // Present: wait on renderFinished before the present engine reads the image.
        var swapchainHandle: VkSwapchainKHR? = swapchain.swapchain
        var presentInfo = VkPresentInfoKHR()
        presentInfo.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR
        let presentResult: VkResult = withUnsafePointer(to: &signalSemaphore) { ss in
            presentInfo.waitSemaphoreCount = 1
            presentInfo.pWaitSemaphores = ss
            return withUnsafePointer(to: &swapchainHandle) { sw in
                presentInfo.pSwapchains = sw
                presentInfo.swapchainCount = 1
                return withUnsafePointer(to: &imageIndex) { ii in
                    presentInfo.pImageIndices = ii
                    return withUnsafePointer(to: &presentInfo) { pi in
                        vkQueuePresentKHR(queue, pi)
                    }
                }
            }
        }
        return presentResult == VK_SUCCESS || presentResult == VK_SUBOPTIMAL_KHR
    }
}
