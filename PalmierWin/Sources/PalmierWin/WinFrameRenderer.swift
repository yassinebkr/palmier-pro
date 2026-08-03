import CVulkan
import Foundation
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

    /// Lazily-created per-output-texture render pass + framebuffer. Retains the
    /// output texture — the framebuffer references its image view, so the view
    /// must outlive the cache entry (an `ObjectIdentifier` key can otherwise be
    /// reused by a new texture after the old one dies). Cleared on deinit.
    private final class RenderTarget {
        let renderPass: VkRenderPass
        let framebuffer: VkFramebuffer
        let device: VkDevice
        let output: VulkanTexture
        init(renderPass: VkRenderPass, framebuffer: VkFramebuffer, device: VkDevice, output: VulkanTexture) {
            self.renderPass = renderPass
            self.framebuffer = framebuffer
            self.device = device
            self.output = output
        }
        deinit {
            vkDestroyFramebuffer(device, framebuffer, nil)
            vkDestroyRenderPass(device, renderPass, nil)
        }
    }

    private var targets: [ObjectIdentifier: RenderTarget] = [:]

    /// Descriptors recorded into a not-yet-submitted command buffer. Freed at
    /// the start of the next submitting `render` — every caller submits and
    /// waits on that buffer before rendering again (exporter, playback, spike).
    private var inFlightDescriptors: [VulkanDescriptor] = []

    /// Lazily-created 1×1 white stand-in for unused aux sampler bindings —
    /// descriptor sets must always have all 3 bindings valid.
    private var _fallbackTexture: VulkanTexture?
    private var fallbackTexture: VulkanTexture? {
        if _fallbackTexture == nil {
            let tex = VulkanTexture(device: device, width: 1, height: 1)
            if let tex, tex.upload(bgra: Data([255, 255, 255, 255])) { _fallbackTexture = tex }
        }
        return _fallbackTexture
    }

    /// Full-size scratch pair for blur pre-passes (clarity, glow). Reallocated
    /// when the source size changes.
    private var blurScratchA: VulkanTexture?
    private var blurScratchB: VulkanTexture?
    private func blurScratch(width: UInt32, height: UInt32) -> (VulkanTexture, VulkanTexture)? {
        if blurScratchA?.width != width || blurScratchA?.height != height {
            blurScratchA = VulkanTexture(device: device, width: width, height: height)
            blurScratchB = VulkanTexture(device: device, width: width, height: height)
        }
        guard let a = blurScratchA, let b = blurScratchB else { return nil }
        return (a, b)
    }

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
        render(instruction: instruction, frame: frame, sourceFrame: sourceFrame,
               overlay: [], into: output)
    }

    /// Clears `output` to black. The render pass clears on load, so an
    /// instruction with no layers is exactly an empty frame.
    public func renderEmpty(size: Size2D, fps: Int, into output: VulkanTexture) {
        render(instruction: RenderInstruction(frameRange: FrameRange(start: 0, end: 1),
                                              layers: [], renderSize: size, fps: fps),
               frame: 0, sourceFrame: { _ in nil }, overlay: [], into: output)
    }

    /// As `render`, plus overlay quads composited above every layer. Only the
    /// preview passes these; the exporter uses the protocol entry point.
    public func render(
        instruction: RenderInstruction,
        frame: Int,
        sourceFrame: (TrackID) -> VulkanTexture?,
        overlay: [SelectionOverlay.Quad],
        into output: VulkanTexture
    ) {
        // Pre-resolve all source textures BEFORE allocating the command buffer.
        // Text/group sources may submit their own GPU work (texture upload,
        // child compositing) — that must NOT happen during command buffer
        // recording. The resolved sources are passed into the recording method.
        var sources = prepareSources(
            instruction: instruction, frame: frame, sourceFrame: sourceFrame
        )
        // Overlay quads draw last so the selection frame sits above every layer.
        sources.append(contentsOf: prepareOverlay(overlay))
        // Every caller submits + waits on the previous frame's command buffer
        // before calling render again, so its descriptors are safe to free now.
        inFlightDescriptors.removeAll()
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
        _ = finish(submitResult, fence: fence, commandBuffer: cmd)
    }

    /// Applies a clip's effect stack in its own one-shot submission: records
    /// the ping-pong passes between `source` and `scratch`, submits, and waits
    /// (bounded). Returns the texture holding the result, or nil when nothing
    /// ran. Shared by playback and export — the preview showing ungraded
    /// frames while the export grades them is the same class of drift as the
    /// old duplicated decode rule.
    public func applyEffectsOneShot(_ effects: [Effect], frame: Int, clipStartFrame: Int,
                                    source: VulkanTexture, scratch: VulkanTexture) -> VulkanTexture? {
        guard effects.contains(where: \.enabled) else { return nil }
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
        let result = applyEffects(effects, frame: frame, clipStartFrame: clipStartFrame,
                                  source: source, scratch: scratch, commandBuffer: cmd)
        guard vkEndCommandBuffer(cmd) == VK_SUCCESS else {
            var toFree: VkCommandBuffer? = cmd
            withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
            return nil
        }
        var fenceInfo = VkFenceCreateInfo()
        fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO
        var fence: VkFence? = nil
        guard withUnsafePointer(to: &fenceInfo, { vkCreateFence(dev, $0, nil, &fence) }) == VK_SUCCESS,
              let fence else {
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
        return finish(submitResult, fence: fence, commandBuffer: cmd) ? result : nil
    }

    /// Waits for a one-shot submission, then releases its fence and command
    /// buffer. Bounded: every other GPU wait on the render thread is capped,
    /// and an unbounded one here turned a single driver hiccup into a preview
    /// frozen for the session and an app that hung on the way out.
    ///
    /// Returns whether the work completed. On timeout the fence and buffer are
    /// deliberately leaked — destroying objects a running submission still
    /// owns is undefined, and one frame's worth of handles is the cheaper side
    /// of that trade.
    private func finish(_ submitResult: VkResult, fence: VkFence, commandBuffer: VkCommandBuffer) -> Bool {
        let dev = device.device
        var completed = false
        if submitResult == VK_SUCCESS {
            var fenceHandle: VkFence? = fence
            let waited = withUnsafePointer(to: &fenceHandle) { f in
                vkWaitForFences(dev, 1, f, UInt32(VK_TRUE), gpuTimeoutNanoseconds)
            }
            completed = waited == VK_SUCCESS
            if !completed {
                engineLog("[WinFrameRenderer] composite did not complete in " +
                          "\(gpuTimeoutNanoseconds / 1_000_000) ms")
                return false
            }
        }
        vkDestroyFence(dev, fence, nil)
        var toFree: VkCommandBuffer? = commandBuffer
        withUnsafePointer(to: &toFree) { vkFreeCommandBuffers(dev, device.commandPool, 1, $0) }
        return completed
    }

    /// Ceiling on any GPU wait the compositor makes.
    private let gpuTimeoutNanoseconds: UInt64 = 1_000_000_000

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

    /// Turns overlay quads into draws. They all sample the same 1×1 white
    /// texture, so one descriptor serves the whole set; only the placement and
    /// alpha differ. Empty when the texture or descriptor cannot be made —
    /// a missing selection frame must never cost the frame itself.
    public func prepareOverlay(_ quads: [SelectionOverlay.Quad]) -> [PreparedLayer] {
        guard !quads.isEmpty, let white = fallbackTexture,
              let descriptor = VulkanDescriptor(device: device,
                                                layout: layerPipeline.descriptorSetLayout,
                                                texture: white) else { return [] }
        return quads.map { quad in
            PreparedLayer(
                texture: white,
                descriptor: descriptor,
                pushConstants: LayerPlacement.PushConstants(
                    a: Float(quad.matrix.a), b: Float(quad.matrix.b),
                    c: Float(quad.matrix.c), d: Float(quad.matrix.d),
                    tx: Float(quad.matrix.tx), ty: Float(quad.matrix.ty),
                    opacity: Float(min(1, max(0, quad.opacity)))))
        }
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
    /// `source` and `scratch`. Each effect resolves to a plan: optional blur
    /// pre-passes into scratch textures, then one main pass — bind the effect
    /// pipeline, push the effect type + params, bind the source texture's
    /// descriptor set (plus aux samplers), render into the other texture, swap.
    /// After all effects, the final result is in `source` if the count is even,
    /// `scratch` if odd — the return value tells the caller which holds it.
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
            guard let plan = resolveEffect(effect, frame: frame, clipStartFrame: clipStartFrame, src: current) else { continue }
            for prep in plan.prep {
                applyOneEffect(effectType: prep.type, params: prep.params, from: prep.src, into: prep.dst, commandBuffer: commandBuffer)
            }
            applyOneEffect(effectType: plan.type, params: plan.params, from: current, into: other,
                           aux: plan.aux, aux2: plan.aux2, commandBuffer: commandBuffer)
            // Swap for the next pass.
            let tmp = current; current = other; other = tmp
        }
        return current
    }

    /// One resolved effect: blur pre-passes (explicit src→dst scratch textures,
    /// run before the main pass reads `src`), then the main ping-pong pass with
    /// its aux samplers (blurred copy, LUTs, 3D-LUT strip).
    private struct EffectPlan {
        struct Prep {
            let type: VulkanEffectPipeline.EffectType
            let params: [Float]
            let src: VulkanTexture
            let dst: VulkanTexture
        }
        var prep: [Prep] = []
        let type: VulkanEffectPipeline.EffectType
        let params: [Float]
        var aux: VulkanTexture? = nil
        var aux2: VulkanTexture? = nil
    }

    /// Resolves one effect to its passes. Returns nil for unsupported effect
    /// types and for no-op/unresolvable effects (skipped).
    private func resolveEffect(_ effect: Effect, frame: Int, clipStartFrame: Int, src: VulkanTexture) -> EffectPlan? {
        let offset = frame - clipStartFrame
        let p = effect.params
        let w = Float(src.width), h = Float(src.height)
        let texel: [Float] = [1 / w, 1 / h]
        func param(_ key: String, _ defaultVal: Double) -> Float {
            Float(p[key]?.resolved(at: offset, default: defaultVal) ?? defaultVal)
        }
        switch effect.type {
        case "stylize.vignette":
            return EffectPlan(type: .vignette, params: [
                param("amount", -0.5), param("midpoint", 0.3),
                param("roundness", 0.0), param("feather", 0.5), w / h
            ])
        case "color.wheels":
            return EffectPlan(type: .wheels, params: [
                param("lift.r", 0), param("lift.g", 0), param("lift.b", 0),
                param("gain.r", 1), param("gain.g", 1), param("gain.b", 1),
                1.0 / max(0.01, param("gamma.r", 1)),
                1.0 / max(0.01, param("gamma.g", 1)),
                1.0 / max(0.01, param("gamma.b", 1))
            ])
        case "color.blacksWhites":
            return EffectPlan(type: .levels, params: [param("blacks", 0), param("whites", 0)])
        case "stylize.invert":
            return EffectPlan(type: .invert, params: [])
        case "stylize.grain":
            return EffectPlan(type: .grain, params: [param("amount", 0.5), param("size", 1.0), Float(frame)])
        case "key.chroma":
            return EffectPlan(type: .chromaKey, params: [
                param("keyColor.r", 0), param("keyColor.g", 1), param("keyColor.b", 0),
                param("threshold", 0.4), param("spill", 0.5)
            ])
        case "color.highlightsShadows":
            return EffectPlan(type: .highlightsShadows, params: [param("highlights", 0), param("shadows", 0)])
        case "detail.clarity":
            let clarity = param("clarity", 0), dehaze = param("dehaze", 0)
            guard clarity != 0 || dehaze != 0,
                  let (bA, bB) = blurScratch(width: src.width, height: src.height) else { return nil }
            let radius = max(w, h) / 40  // low-frequency local-contrast scale
            var plan = EffectPlan(type: .clarity, params: [clarity, dehaze], aux: bB)
            plan.prep = [
                EffectPlan.Prep(type: .blur, params: [1, 0, radius] + texel, src: src, dst: bA),
                EffectPlan.Prep(type: .blur, params: [0, 1, radius] + texel, src: bA, dst: bB)
            ]
            return plan
        case "stylize.glow":
            let intensity = param("intensity", 0)
            guard intensity > 0,
                  let (bA, bB) = blurScratch(width: src.width, height: src.height) else { return nil }
            let radius = param("radius", 20)
            var plan = EffectPlan(type: .glowComposite, params: [intensity], aux: bA)
            plan.prep = [
                EffectPlan.Prep(type: .glowBright, params: [param("threshold", 0.6), param("warmth", 0)], src: src, dst: bA),
                EffectPlan.Prep(type: .blur, params: [1, 0, radius] + texel, src: bA, dst: bB),
                EffectPlan.Prep(type: .blur, params: [0, 1, radius] + texel, src: bB, dst: bA)
            ]
            return plan
        case "color.curves":
            guard let json = p["curve"]?.string, let curve = GradeCurve(json: json), !curve.isIdentity,
                  let luts = curveLUTs(for: curve, key: effect.id + json) else { return nil }
            return EffectPlan(type: .gradeCurves, params: [], aux: luts.channels, aux2: luts.master)
        case "color.hueCurves":
            guard let json = p["curves"]?.string, let curves = HueCurves(json: json), !curves.isIdentity,
                  let lut = hueLUT(for: curves, key: effect.id + json) else { return nil }
            return EffectPlan(type: .hueCurves, params: [], aux: lut)
        case "color.lut":
            guard let path = p["path"]?.string,
                  let (strip, n) = lutStrip(path: path) else { return nil }
            return EffectPlan(type: .lutTetra, params: [Float(n), param("intensity", 1)], aux: strip)
        default:
            return nil  // Unsupported effect type for the MVP
        }
    }

    /// 256×1 per-channel + master curve LUTs, cached per curve JSON (mirrors
    /// macOS GradeCurveKernel.buildLUTs, quantized to BGRA8).
    private var curveLUTCache: [String: (channels: VulkanTexture, master: VulkanTexture)] = [:]
    private func curveLUTs(for curve: GradeCurve, key: String) -> (channels: VulkanTexture, master: VulkanTexture)? {
        if let hit = curveLUTCache[key] { return hit }
        if curveLUTCache.count > 64 { curveLUTCache.removeAll() }
        let w = 256
        func cl(_ v: Double) -> UInt8 { UInt8((min(1, max(0, v)) * 255).rounded()) }
        var ch = [UInt8](repeating: 255, count: w * 4)
        var ms = [UInt8](repeating: 255, count: w * 4)
        for x in 0..<w {
            let t = Double(x) / Double(w - 1)
            ch[x * 4] = cl(GradeCurve.eval(curve.blue, t))
            ch[x * 4 + 1] = cl(GradeCurve.eval(curve.green, t))
            ch[x * 4 + 2] = cl(GradeCurve.eval(curve.red, t))
            let m = cl(GradeCurve.eval(curve.master, t))
            ms[x * 4] = m; ms[x * 4 + 1] = m; ms[x * 4 + 2] = m
        }
        guard let chTex = VulkanTexture(device: device, width: UInt32(w), height: 1),
              let msTex = VulkanTexture(device: device, width: UInt32(w), height: 1),
              chTex.upload(bgra: Data(ch)), msTex.upload(bgra: Data(ms)) else { return nil }
        let luts = (channels: chTex, master: msTex)
        curveLUTCache[key] = luts
        return luts
    }

    /// 256×1 hue-curve LUT (R=Δhue, G=satScale, B=Δlum), cached per curve JSON.
    /// Mirrors HueCurveKernel.buildLUT; signed values are normalized to their
    /// ±max range and encoded 0..1, decoded in the shader (BGRA8 has no signed
    /// channels).
    private var hueLUTCache: [String: VulkanTexture] = [:]
    private func hueLUT(for curves: HueCurves, key: String) -> VulkanTexture? {
        if let hit = hueLUTCache[key] { return hit }
        if hueLUTCache.count > 64 { hueLUTCache.removeAll() }
        let maxHueShift = 1.0 / 12, maxLumShift = 0.5
        let w = 256
        func enc(_ v: Double) -> UInt8 { UInt8(((min(1, max(-1, v)) * 0.5 + 0.5) * 255).rounded()) }
        var px = [UInt8](repeating: 255, count: w * 4)
        for i in 0..<w {
            let hue = (Double(i) + 0.5) / Double(w)
            let dHue = (HueCurves.eval(curves.hueVsHue, hue) - 0.5) * 2 * maxHueShift
            let satScale = (HueCurves.eval(curves.hueVsSat, hue) - 0.5) * 2
            let dLum = (HueCurves.eval(curves.hueVsLum, hue) - 0.5) * 2 * maxLumShift
            px[i * 4] = enc(dLum / maxLumShift)
            px[i * 4 + 1] = enc(satScale)
            px[i * 4 + 2] = enc(dHue / maxHueShift)
        }
        guard let tex = VulkanTexture(device: device, width: UInt32(w), height: 1),
              tex.upload(bgra: Data(px)) else { return nil }
        hueLUTCache[key] = tex
        return tex
    }

    /// 3D-LUT strip texture (n wide, n² tall), cached per resolved file path.
    private var lutStripCache: [String: (texture: VulkanTexture, dimension: Int)] = [:]
    private func lutStrip(path: String) -> (texture: VulkanTexture, dimension: Int)? {
        if let hit = lutStripCache[path] { return hit }
        if lutStripCache.count > 16 { lutStripCache.removeAll() }
        guard let lut = CubeLUTLoader.load(path: path),
              let tex = VulkanTexture(device: device, width: UInt32(lut.dimension),
                                      height: UInt32(lut.dimension * lut.dimension)),
              tex.upload(bgra: lut.bgra) else { return nil }
        let strip = (texture: tex, dimension: lut.dimension)
        lutStripCache[path] = strip
        return strip
    }

    /// Records one effect pass: begin render pass into `dst`, set viewport,
    /// bind effect pipeline, bind the descriptor set (src + aux samplers),
    /// push constants, draw, end. The descriptor is retained in
    /// `inFlightDescriptors` until the caller has submitted + waited.
    private func applyOneEffect(
        effectType: VulkanEffectPipeline.EffectType,
        params: [Float],
        from src: VulkanTexture,
        into dst: VulkanTexture,
        aux: VulkanTexture? = nil,
        aux2: VulkanTexture? = nil,
        commandBuffer: VkCommandBuffer
    ) {
        let dev = device.device
        let target = renderTarget(for: dst, device: dev)
        guard let fallback = fallbackTexture,
              let desc = VulkanDescriptor(device: device, layout: effectPipeline.descriptorSetLayout,
                                          texture: src, aux: aux, aux2: aux2, fallback: fallback) else { return }
        inFlightDescriptors.append(desc)

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
    /// Single-line; honours the style's size, color, and alignment (tracking,
    /// shadow, border come later). The text fills the clip's placement region
    /// (handled by LayerPlacement like any other layer).
    private func resolveTextTexture(for layer: LayerPlan, frame: Int, renderSize: Size2D) -> VulkanTexture? {
        guard let tr = textRenderer else { return nil }
        let content = layer.clip.textContent ?? ""
        guard !content.isEmpty else { return nil }
        // Font size scales with canvas height (matches macOS's reference-canvas scaling).
        let canvasW = Int(renderSize.width)
        let canvasH = Int(renderSize.height)
        let style = layer.clip.textStyle
        let fontSize = Float(renderSize.height) * Float(style?.fontSize ?? 96) / 1080.0
        guard let bgra = tr.render(content, fontSize: fontSize,
                                   color: style?.color ?? TextStyle.RGBA(),
                                   alignment: style?.alignment ?? .center,
                                   canvasWidth: canvasW, canvasHeight: canvasH) else { return nil }
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
        guard finish(submitResult, fence: fence, commandBuffer: cmd) else { return nil }
        return offscreen
    }

    /// Returns (creating once) the render pass + framebuffer for `output`.
    private func renderTarget(for output: VulkanTexture, device: VkDevice) -> RenderTarget {
        let key = ObjectIdentifier(output)
        if let existing = targets[key] { return existing }
        let rp = WinFrameRenderer.makeRenderPass(device: device, format: VK_FORMAT_B8G8R8A8_UNORM)!
        let fb = WinFrameRenderer.makeFramebuffer(device: device, renderPass: rp, view: output.view, extent: VkExtent2D(width: output.width, height: output.height))!
        let target = RenderTarget(renderPass: rp, framebuffer: fb, device: device, output: output)
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
