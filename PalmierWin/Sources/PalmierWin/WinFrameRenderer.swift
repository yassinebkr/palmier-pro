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
    public let effectPipeline: VulkanEffectPipeline
    /// Lazy text renderer (stb_truetype). Created on first .text layer.
    private var _textRenderer: WinTextRenderer?
    private var textRenderer: WinTextRenderer? {
        if _textRenderer == nil { _textRenderer = try? WinTextRenderer() }
        return _textRenderer
    }

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
              let layerPipeline = VulkanLayerPipeline(device: device, renderPass: templateRP),
              let effectPipeline = VulkanEffectPipeline(device: device, renderPass: templateRP) else {
            return nil
        }
        vkDestroyRenderPass(dev, templateRP, nil)
        self.device = device
        self.layerPipeline = layerPipeline
        self.effectPipeline = effectPipeline
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
        // Pre-resolve all source textures BEFORE allocating the command buffer.
        // Text/group sources may submit their own GPU work (texture upload,
        // child compositing) — that must NOT happen during command buffer
        // recording. The resolved sources are passed into the recording method.
        let sources = prepareSources(
            instruction: instruction, frame: frame, sourceFrame: sourceFrame
        )
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

        render(instruction: instruction, frame: frame, sources: sources, into: output, commandBuffer: cmd)

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
    /// A layer with its source texture resolved + descriptor bound + push
    /// constants computed. Ready to draw — no GPU side effects remain.
    public struct PreparedLayer {
        public let texture: VulkanTexture
        public let descriptor: VulkanDescriptor
        public let pushConstants: LayerPlacement.PushConstants
    }

    /// Resolves all source textures for `instruction.layers` BEFORE any command
    /// buffer recording. Text/group sources create textures and submit GPU work
    /// (uploads, child composites); that must complete before the render pass
    /// command buffer is allocated. Returns the prepared layers (skip zero-
    /// opacity and unresolvable ones, matching macOS FrameRenderer).
    public func prepareSources(
        instruction: RenderInstruction,
        frame: Int,
        sourceFrame: (TrackID) -> VulkanTexture?
    ) -> [PreparedLayer] {
        var prepared: [PreparedLayer] = []
        for layer in instruction.layers {
            let opacity = layer.clip.opacityAt(frame: frame)
            guard opacity > 0 else { continue }
            guard let srcTexture = resolveSourceTexture(for: layer, frame: frame,
                                                        renderSize: instruction.renderSize,
                                                        sourceFrame: sourceFrame) else { continue }
            guard let desc = VulkanDescriptor(device: device, layout: layerPipeline.descriptorSetLayout, texture: srcTexture) else {
                continue
            }
            let pc = LayerPlacement.pushConstants(for: layer, frame: frame, renderSize: instruction.renderSize, opacity: opacity)
            prepared.append(PreparedLayer(texture: srcTexture, descriptor: desc, pushConstants: pc))
        }
        return prepared
    }

    /// Records the composite of `sources` into `output`. No source resolution
    /// happens here — all GPU work that produced the source textures completed
    /// in `prepareSources` before this command buffer was allocated.
    public func render(
        instruction: RenderInstruction,
        frame: Int,
        sources: [PreparedLayer],
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

        // Draw pass: each prepared layer is one bind + push + draw. No source
        // resolution happens here — all GPU work that produced the source
        // textures already completed in prepareSources.
        for layer in sources {
            var setHandle: VkDescriptorSet? = layer.descriptor.set
            withUnsafePointer(to: &setHandle) { s in
                vkCmdBindDescriptorSets(commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, layerPipeline.layout,
                                        0, 1, s, 0, nil)
            }
            var pc = layer.pushConstants
            withUnsafePointer(to: &pc) { pcPtr in
                pcPtr.withMemoryRebound(to: Float.self, capacity: 7) { floats in
                    vkCmdPushConstants(commandBuffer, layerPipeline.layout,
                                       UInt32(VK_SHADER_STAGE_VERTEX_BIT.rawValue | VK_SHADER_STAGE_FRAGMENT_BIT.rawValue),
                                       0, UInt32(MemoryLayout<Float>.stride * 7), floats)
                }
            }
            vkCmdDraw(commandBuffer, 4, 1, 0, 0)
        }

        vkCmdEndRenderPass(commandBuffer)
    }

    /// Applies a chain of effects to the composited frame, ping-ponging between
    /// `source` and `scratch`. Each effect is one full-screen draw: bind the
    /// effect pipeline, push the effect type + params, bind the source texture's
    /// descriptor set, render into the other texture, swap. After all effects,
    /// the final result is in `source` if the count is even, `scratch` if odd —
    /// the return value tells the caller which holds the result.
    ///
    /// Records into `commandBuffer`; caller submits. Both textures must be the
    /// same size and have SHADER_READ_ONLY + COLOR_ATTACHMENT usage.
    @discardableResult
    public func applyEffects(
        _ effects: [Effect],
        frame: Int,
        clipStartFrame: Int,
        source: VulkanTexture,
        scratch: VulkanTexture,
        commandBuffer: VkCommandBuffer
    ) -> VulkanTexture {
        var current = source
        var other = scratch
        for effect in effects where effect.enabled {
            guard let (effectType, params) = resolveEffect(effect, frame: frame, clipStartFrame: clipStartFrame, aspect: Float(Double(source.width) / Double(source.height))) else { continue }
            applyOneEffect(effectType: effectType, params: params, from: current, into: other, commandBuffer: commandBuffer)
            // Swap for the next pass.
            let tmp = current; current = other; other = tmp
        }
        return current
    }

    /// Resolves one effect to its push-constant (type + params[30]). Returns nil
    /// for unsupported effect types (skipped).
    private func resolveEffect(_ effect: Effect, frame: Int, clipStartFrame: Int, aspect: Float) -> (VulkanEffectPipeline.EffectType, [Float])? {
        let offset = frame - clipStartFrame
        let p = effect.params
        func param(_ key: String, _ defaultVal: Double) -> Float {
            Float(p[key]?.resolved(at: offset, default: defaultVal) ?? defaultVal)
        }
        switch effect.type {
        case "stylize.vignette":
            return (.vignette, [
                param("amount", -0.5), param("midpoint", 0.3),
                param("roundness", 0.0), param("feather", 0.5), aspect
            ])
        case "color.wheels":
            return (.wheels, [
                param("lift.r", 0), param("lift.g", 0), param("lift.b", 0),
                param("gain.r", 1), param("gain.g", 1), param("gain.b", 1),
                1.0 / max(0.01, param("gamma.r", 1)),
                1.0 / max(0.01, param("gamma.g", 1)),
                1.0 / max(0.01, param("gamma.b", 1))
            ])
        case "color.blacksWhites":
            return (.levels, [param("blacks", 0), param("whites", 0)])
        case "stylize.grain":
            return (.grain, [param("amount", 0.5), param("size", 1.0), Float(frame)])
        case "key.chroma":
            return (.chromaKey, [
                param("keyColor.r", 0), param("keyColor.g", 1), param("keyColor.b", 0),
                param("threshold", 0.4), param("spill", 0.5)
            ])
        case "color.highlightsShadows":
            return (.highlightsShadows, [param("highlights", 0), param("shadows", 0)])
        default:
            return nil  // Unsupported effect type for the MVP
        }
    }

    /// Records one effect pass: begin render pass into `dst`, set viewport,
    /// bind effect pipeline, bind source texture's descriptor set, push constants, draw, end.
    private func applyOneEffect(
        effectType: VulkanEffectPipeline.EffectType,
        params: [Float],
        from src: VulkanTexture,
        into dst: VulkanTexture,
        commandBuffer: VkCommandBuffer
    ) {
        let dev = device.device
        let target = renderTarget(for: dst, device: dev)
        guard let desc = VulkanDescriptor(device: device, layout: effectPipeline.descriptorSetLayout, texture: src) else { return }

        var rpBegin = VkRenderPassBeginInfo()
        rpBegin.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO
        rpBegin.renderPass = target.renderPass
        rpBegin.framebuffer = target.framebuffer
        rpBegin.renderArea = VkRect2D(offset: VkOffset2D(x: 0, y: 0),
                                      extent: VkExtent2D(width: dst.width, height: dst.height))
        rpBegin.clearValueCount = 0  // load-op is LOAD (not clear) — preserve? Actually effect pass overwrites fully
        withUnsafePointer(to: &rpBegin) { rpb in
            vkCmdBeginRenderPass(commandBuffer, rpb, VK_SUBPASS_CONTENTS_INLINE)
        }

        var viewport = VkViewport()
        viewport.x = 0; viewport.y = 0
        viewport.width = Float(dst.width); viewport.height = Float(dst.height)
        viewport.minDepth = 0; viewport.maxDepth = 1
        var scissor = VkRect2D(offset: VkOffset2D(x: 0, y: 0),
                               extent: VkExtent2D(width: dst.width, height: dst.height))
        withUnsafePointer(to: &viewport) { vp in
            withUnsafePointer(to: &scissor) { sc in
                vkCmdSetViewport(commandBuffer, 0, 1, vp)
                vkCmdSetScissor(commandBuffer, 0, 1, sc)
            }
        }

        vkCmdBindPipeline(commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, effectPipeline.pipeline)
        var setHandle: VkDescriptorSet? = desc.set
        withUnsafePointer(to: &setHandle) { s in
            vkCmdBindDescriptorSets(commandBuffer, VK_PIPELINE_BIND_POINT_GRAPHICS, effectPipeline.layout,
                                    0, 1, s, 0, nil)
        }

        // Push constants: uint effectType + 30 floats. Pack into one [UInt32]
        // buffer; the shader reads effectType as the first uint and params[0..29]
        // as the rest (float and uint32 have the same bit width).
        var pushData = [UInt32](repeating: 0, count: 31)
        pushData[0] = effectType.rawValue
        for i in 0..<min(params.count, 30) {
            pushData[i + 1] = params[i].bitPattern
        }
        pushData.withUnsafeBufferPointer { buf in
            vkCmdPushConstants(commandBuffer, effectPipeline.layout,
                               UInt32(VK_SHADER_STAGE_FRAGMENT_BIT.rawValue),
                               0, UInt32(MemoryLayout<UInt32>.stride * 31), buf.baseAddress)
        }

        vkCmdDraw(commandBuffer, 3, 1, 0, 0)
        vkCmdEndRenderPass(commandBuffer)
        _ = desc  // released; safe because command buffer isn't submitted until caller does
    }

    /// Resolves a layer's source into a GPU texture to sample, handling all
    /// three LayerPlan.Source cases:
    /// - `.track(id)` — from the caller's sourceFrame closure (decoded video).
    /// - `.text` — rasterize the clip's textContent via WinTextRenderer, upload.
    /// - `.group(children, canvas)` — recurse: composite children into a canvas-
    ///   sized texture, then return it as this layer's source.
    private func resolveSourceTexture(
        for layer: LayerPlan,
        frame: Int,
        renderSize: Size2D,
        sourceFrame: (TrackID) -> VulkanTexture?
    ) -> VulkanTexture? {
        switch layer.source {
        case .track(let id):
            return sourceFrame(id)
        case .text:
            return resolveTextTexture(for: layer, frame: frame, renderSize: renderSize)
        case .group(let children, let canvas):
            return resolveGroupTexture(children: children, canvas: canvas, frame: frame, sourceFrame: sourceFrame)
        }
    }

    /// Renders a text clip into a canvas-sized texture via WinTextRenderer.
    /// MVP: single-line, white glyphs; TextStyle color/tracking/layout come
    /// later. The text fills the clip's placement region (handled by
    /// LayerPlacement like any other layer).
    private func resolveTextTexture(for layer: LayerPlan, frame: Int, renderSize: Size2D) -> VulkanTexture? {
        guard let tr = textRenderer else { return nil }
        let content = layer.clip.textContent ?? ""
        guard !content.isEmpty else { return nil }
        // Font size scales with canvas height (matches macOS's reference-canvas scaling).
        let canvasW = Int(renderSize.width)
        let canvasH = Int(renderSize.height)
        let fontSize = Float(renderSize.height) * Float(layer.clip.textStyle?.fontSize ?? 96) / 1080.0
        guard let bgra = tr.render(content, fontSize: fontSize, canvasWidth: canvasW, canvasHeight: canvasH) else { return nil }
        guard let tex = VulkanTexture(device: device, width: UInt32(canvasW), height: UInt32(canvasH)) else { return nil }
        guard tex.upload(bgra: bgra) else { return nil }
        return tex
    }

    /// Composites a group's children bottom→top into a canvas-sized texture.
    /// Builds a one-segment RenderInstruction from the children and renders it
    /// into a fresh offscreen texture, which becomes this group layer's source.
    private func resolveGroupTexture(
        children: [LayerPlan],
        canvas: Size2D,
        frame: Int,
        sourceFrame: (TrackID) -> VulkanTexture?
    ) -> VulkanTexture? {
        guard !children.isEmpty, canvas.width > 0, canvas.height > 0 else { return nil }
        guard let offscreen = VulkanTexture(device: device, width: UInt32(canvas.width), height: UInt32(canvas.height)) else { return nil }
        // Render the children as a one-frame instruction into the offscreen.
        let instr = RenderInstruction(
            frameRange: FrameRange(start: frame, end: frame + 1),
            layers: children, renderSize: canvas, fps: 30
        )
        // Use the recording-core render variant (no submit) into the offscreen.
        // For the MVP group path, allocate a command buffer, render, submit,
        // wait — mirrors the protocol method but with the children instruction.
        let dev = device.device
        var cbInfo = VkCommandBufferAllocateInfo()
        cbInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO
        cbInfo.commandPool = device.commandPool
        cbInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY
        cbInfo.commandBufferCount = 1
        var cmd: VkCommandBuffer? = nil
        guard withUnsafePointer(to: &cbInfo, { vkAllocateCommandBuffers(dev, $0, &cmd) }) == VK_SUCCESS,
              let cmd else { return nil }
        var beginInfo = VkCommandBufferBeginInfo()
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO
        beginInfo.flags = UInt32(VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.rawValue)
        guard withUnsafePointer(to: &beginInfo, { vkBeginCommandBuffer(cmd, $0) }) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return nil
        }
        let childSources = prepareSources(instruction: instr, frame: frame, sourceFrame: sourceFrame)
        render(instruction: instr, frame: frame, sources: childSources, into: offscreen, commandBuffer: cmd)
        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return nil
        }
        var fenceInfo = VkFenceCreateInfo()
        fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO
        var fence: VkFence? = nil
        guard withUnsafePointer(to: &fenceInfo, { vkCreateFence(dev, $0, nil, &fence) }) == VK_SUCCESS, let fence else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return nil
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
        return offscreen
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
