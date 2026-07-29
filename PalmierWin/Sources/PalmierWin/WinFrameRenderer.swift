import CVulkan
import PalmierCore

/// Windows `FrameRendering` conformer: composites one `RenderInstruction`
/// (bottom → top) into an offscreen `VulkanTexture` via the layer pipeline.
///
/// Each layer's source texture comes from the `sourceFrame` closure (keyed by
/// `TrackID`); each draw pushes that layer's placement matrix + opacity as
/// push constants. Layers with `opacityAt(frame) <= 0` or with no source frame
/// are skipped, matching the macOS `FrameRenderer` semantics.
///
/// The output texture is an offscreen BGRA8 image sized to `renderSize`; the
/// presenter blits it to the swapchain. This mirrors macOS, where
/// `FrameRendering.render(into: CVPixelBuffer)` targets an offscreen pixel
/// buffer that AVFoundation then presents.
public final class WinFrameRenderer: FrameRendering, @unchecked Sendable {
    public typealias PixelBuffer = VulkanTexture

    public let device: VulkanDevice
    public let layerPipeline: VulkanLayerPipeline

    /// Lazily-created per-output-texture render pass + framebuffer. The output
    /// texture's lifetime bounds the framebuffer's; cleared on deinit.
    private final class RenderTarget {
        let renderPass: VkRenderPass
        let framebuffer: VkFramebuffer
        let device: VkDevice
        init(renderPass: VkRenderPass, framebuffer: VkFramebuffer, device: VkDevice) {
            self.renderPass = renderPass
            self.framebuffer = framebuffer
            self.device = device
        }
        deinit {
            vkDestroyFramebuffer(device, framebuffer, nil)
            vkDestroyRenderPass(device, renderPass, nil)
        }
    }

    private var targets: [ObjectIdentifier: RenderTarget] = [:]

    public init?(device: VulkanDevice, renderPassFormat: VkFormat = VK_FORMAT_B8G8R8A8_UNORM) {
        let dev = device.device
        // The pipeline is built against a throwaway render pass; pipelines are
        // compatible with any render pass that has a matching attachment
        // format, so the per-output render passes created lazily below work
        // fine. vkCreateGraphicsPipelines doesn't retain the render pass.
        guard let templateRP = WinFrameRenderer.makeRenderPass(device: dev, format: renderPassFormat),
              let layerPipeline = VulkanLayerPipeline(device: device, renderPass: templateRP) else {
            return nil
        }
        vkDestroyRenderPass(dev, templateRP, nil)
        self.device = device
        self.layerPipeline = layerPipeline
    }

    deinit {
        // RenderTargets tear down their own VkRenderPass/Framebuffer.
    }

    /// `FrameRendering` conformance: composites `instruction` into `output`.
    /// Allocates + submits its own one-shot command buffer and waits on a fence
    /// (MVP single-frame synchronous). The `render(into:commandBuffer:)` variant
    /// below is the recording core for callers that batch multiple renders.
    public func render(
        instruction: RenderInstruction,
        frame: Int,
        sourceFrame: (TrackID) -> VulkanTexture?,
        into output: VulkanTexture
    ) {
        let dev = device.device
        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
        cbInfo.commandPool = device.commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
        cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(dev, $0, &cmd) }) == VK_SUCCESS,
              let cmd else { return }
        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return
        }

        render(instruction: instruction, frame: frame, sourceFrame: sourceFrame, into: output, commandBuffer: cmd)

        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return
        }

        var fenceInfo = VkFenceCreateInfo()
        fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO
        var fence: VkFence? = nil
        guard withUnsafePointer(to: &fenceInfo, { vkCreateFence(dev, $0, nil, &fence) }) == VK_SUCCESS, let fence else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return
        }

        var submitInfo = VkSubmitInfo()
        submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO
        submitInfo.commandBufferCount = 1
        var cmdHandle: VkCommandBuffer? = cmd
        let submitResult: VkResult = withUnsafePointer(to: &cmdHandle) { ch in
            submitInfo.pCommandBuffers = ch
            return withUnsafePointer(to: &submitInfo) { si in
                vkQueueSubmit(device.graphicsQueue, 1, si, fence)
            }
        }
        if submitResult == VK_SUCCESS {
            var fenceHandle: VkFence? = fence
            withUnsafePointer(to: &fenceHandle) { f in
                _ = vkWaitForFences(dev, 1, f, UInt32(VK_TRUE), UInt64.max)
            }
        }
        vkDestroyFence(dev, fence, nil)
        var toFree: VkCommandBuffer? = cmd
        withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
    }

    /// Composites `instruction` into `output`. Records into `commandBuffer`
    /// (the caller's recording command buffer — no submit). The caller submits
    /// after all rendering is recorded.
    ///
    /// Per AGENTS.md the protocol's `into:` is the offscreen pixel buffer;
    /// here it's a VulkanTexture. The `sourceFrame` closure is called per
    /// layer to fetch that layer's decoded texture.
    public func render(
        instruction: RenderInstruction,
        frame: Int,
        sourceFrame: (TrackID) -> VulkanTexture?,
        into output: VulkanTexture,
        commandBuffer: VkCommandBuffer
    ) {
        let dev = device.device
        let target = renderTarget(for: output, device: dev)

        // Clear to opaque black, then composite each visible layer.
        var clear = VkClearValue()
        clear.color.float32.0 = 0
        clear.color.float32.1 = 0
        clear.color.float32.2 = 0
        clear.color.float32.3 = 0

        var rpBegin = VkRenderPassBeginInfo()
        rpBegin.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO
        rpBegin.renderPass = target.renderPass
        rpBegin.framebuffer = target.framebuffer
        rpBegin.renderArea = VkRect2D(offset: VkOffset2D(x: 0, y: 0),
                                      extent: VkExtent2D(width: output.width, height: output.height))
        rpBegin.clearValueCount = 1
        withUnsafePointer(to: &clear) { c in
            rpBegin.pClearValues = c
            withUnsafePointer(to: &rpBegin) { rpb in
                vkCmdBeginRenderPass(commandBuffer, rpb, VK_SUBPASS_CONTENTS_INLINE)
            }
        }

        // Dynamic viewport + scissor matching the output texture.
        var viewport = VkViewport()
        viewport.x = 0; viewport.y = 0
        viewport.width = Float(output.width); viewport.height = Float(output.height)
        viewport.minDepth = 0; viewport.maxDepth = 1
        var scissor = VkRect2D(offset: VkOffset2D(x: 0, y: 0),
                               extent: VkExtent2D(width: output.width, height: output.height))
        withUnsafePointer(to: &viewport) { vp in
            withUnsafePointer(to: &scissor) { sc in
                vkCmdSetViewport(commandBuffer, 0, 1, vp)
                vkCmdSetScissor(commandBuffer, 0, 1, sc)
            }
        }

        vkCmdBindPipeline(commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, layerPipeline.pipeline)

        // Layers are bottom → top; draw in array order. Skip layers with zero
        // opacity or no source frame (matches macOS FrameRenderer).
        for layer in instruction.layers {
            // Text and group sources aren't supported in the MVP layer pipeline.
            guard let trackID = layer.trackID else { continue }
            let opacity = layer.clip.opacityAt(frame: frame)
            guard opacity > 0 else { continue }
            guard let srcTexture = sourceFrame(trackID) else { continue }

            // One descriptor set per source texture would be ideal, but for the
            // MVP we allocate+update+free a single descriptor set inline per
            // layer. A descriptor pool/cache will replace this once multiple
            // layers per frame are common.
            guard let desc = VulkanDescriptor(device: device, layout: layerPipeline.descriptorSetLayout, texture: srcTexture) else {
                continue
            }

            var setHandle: VkDescriptorSet? = desc.set
            withUnsafePointer(to: &setHandle) { s in
                vkCmdBindDescriptorSets(commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, layerPipeline.layout,
                                        0, 1, s, 0, nil)
            }

            var pc = LayerPlacement.pushConstants(for: layer, frame: frame, renderSize: instruction.renderSize, opacity: opacity)
            withUnsafePointer(to: &pc) { pcPtr in
                pcPtr.withMemoryRebound(to: Float.self, capacity: 7) { floats in
                    vkCmdPushConstants(commandBuffer, layerPipeline.layout,
                                       UInt32(VK_SHADER_STAGE_VERTEX_BIT.rawValue | VK_SHADER_STAGE_FRAGMENT_BIT.rawValue),
                                       0, UInt32(MemoryLayout<Float>.stride * 7), floats)
                }
            }

            vkCmdDraw(commandBuffer, 4, 1, 0, 0)
            // desc is released here; its deinit destroys the descriptor pool.
            // For the MVP single-frame-in-flight this is safe because the
            // command buffer isn't submitted until after this method returns.
            _ = desc
        }

        vkCmdEndRenderPass(commandBuffer)
    }

    /// Returns (creating once) the render pass + framebuffer for `output`.
    private func renderTarget(for output: VulkanTexture, device: VkDevice) -> RenderTarget {
        let key = ObjectIdentifier(output)
        if let existing = targets[key] { return existing }
        let rp = WinFrameRenderer.makeRenderPass(device: device, format: VK_FORMAT_B8G8R8A8_UNORM)!
        let fb = WinFrameRenderer.makeFramebuffer(device: device, renderPass: rp, view: output.view, extent: VkExtent2D(width: output.width, height: output.height))!
        let target = RenderTarget(renderPass: rp, framebuffer: fb, device: device)
        targets[key] = target
        return target
    }

    private static func makeRenderPass(device: VkDevice, format: VkFormat) -> VkRenderPass? {
        var attachment = VkAttachmentDescription()
        attachment.format = format
        attachment.samples = VK_SAMPLE_COUNT_1_BIT
        attachment.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR
        attachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE
        attachment.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE
        attachment.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE
        attachment.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED
        attachment.finalLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
        var colorRef = VkAttachmentReference()
        colorRef.attachment = 0
        colorRef.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL
        var subpass = VkSubpassDescription()
        subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS
        subpass.colorAttachmentCount = 1
        var rpInfo = VkRenderPassCreateInfo()
        rpInfo.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO
        rpInfo.attachmentCount = 1
        rpInfo.subpassCount = 1
        var rp: VkRenderPass? = nil
        let result: VkResult = withUnsafePointer(to: &attachment) { aPtr in
            rpInfo.pAttachments = aPtr
            return withUnsafePointer(to: &colorRef) { crPtr in
                subpass.pColorAttachments = crPtr
                return withUnsafePointer(to: &subpass) { spPtr in
                    rpInfo.pSubpasses = spPtr
                    return withUnsafePointer(to: &rpInfo) { iPtr in
                        vkCreateRenderPass(device, iPtr, nil, &rp)
                    }
                }
            }
        }
        return result == VK_SUCCESS ? rp : nil
    }

    private static func makeFramebuffer(device: VkDevice, renderPass: VkRenderPass, view: VkImageView, extent: VkExtent2D) -> VkFramebuffer? {
        var fbInfo = VkFramebufferCreateInfo()
        fbInfo.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO
        fbInfo.renderPass = renderPass
        fbInfo.attachmentCount = 1
        var viewHandle: VkImageView? = view
        fbInfo.width = extent.width
        fbInfo.height = extent.height
        fbInfo.layers = 1
        var fb: VkFramebuffer? = nil
        let result: VkResult = withUnsafePointer(to: &viewHandle) { vPtr in
            fbInfo.pAttachments = vPtr
            return withUnsafePointer(to: &fbInfo) { iPtr in
                vkCreateFramebuffer(device, iPtr, nil, &fb)
            }
        }
        return result == VK_SUCCESS ? fb : nil
    }
}
