import CVulkan

/// The per-layer composite pipeline: a triangle-strip quad with a push-constant
/// placement matrix (the `Mat3` affine that maps the unit quad into output-pixel
/// space) plus per-layer opacity. One draw call per `LayerPlan`, bottom → top,
/// with premultiplied source-over blending composites the timeline segment.
///
/// The push-constant layout matches `Layer` in Shaders/layer_quad.{vert,frag}:
/// `{ a, b, c, d, tx, ty, opacity }` (7 floats, 28 bytes). The renderer pushes
/// one per draw via `vkCmdPushConstants` before `vkCmdDraw`.
public final class VulkanLayerPipeline: @unchecked Sendable {
    public let device: VkDevice
    public let pipeline: VkPipeline
    public let layout: VkPipelineLayout
    public let descriptorSetLayout: VkDescriptorSetLayout

    public init?(device: VulkanDevice, renderPass: VkRenderPass) {
        let dev = device.device

        guard let vertModule = VulkanLayerPipeline.shaderModule(dev: dev, spirv: Shaders.layer_quad_vertSPIRV),
              let fragModule = VulkanLayerPipeline.shaderModule(dev: dev, spirv: Shaders.layer_quad_fragSPIRV)
        else { return nil }

        // The "main" entry-point C string must outlive vkCreateGraphicsPipelines.
        let (pipeline, layout, descriptorSetLayout): (VkPipeline?, VkPipelineLayout?, VkDescriptorSetLayout?) = "main".withCString { namePtr -> (VkPipeline?, VkPipelineLayout?, VkDescriptorSetLayout?) in
            let stages: [VkPipelineShaderStageCreateInfo] = [
                VulkanLayerPipeline.stageInfo(VK_SHADER_STAGE_VERTEX_BIT, module: vertModule, name: namePtr),
                VulkanLayerPipeline.stageInfo(VK_SHADER_STAGE_FRAGMENT_BIT, module: fragModule, name: namePtr)
            ]

            // Descriptor set layout: one combined image sampler at binding 0.
            var binding = VkDescriptorSetLayoutBinding()
            binding.binding = 0
            binding.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER
            binding.descriptorCount = 1
            binding.stageFlags = UInt32(VK_SHADER_STAGE_FRAGMENT_BIT.rawValue)
            var dslInfo = VkDescriptorSetLayoutCreateInfo()
            dslInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO
            dslInfo.bindingCount = 1
            var dsl: VkDescriptorSetLayout? = nil
            let dslResult: VkResult = withUnsafePointer(to: &binding) { bPtr in
                dslInfo.pBindings = bPtr
                return withUnsafePointer(to: &dslInfo) { iPtr in
                    vkCreateDescriptorSetLayout(dev, iPtr, nil, &dsl)
                }
            }
            guard dslResult == VK_SUCCESS, let dsl else {
                vkDestroyShaderModule(dev, vertModule, nil)
                vkDestroyShaderModule(dev, fragModule, nil)
                return (nil, nil, nil)
            }

            // Pipeline layout with the push-constant range. The range spans
            // vert+frag (both read the same shared block).
            var pushRange = VkPushConstantRange()
            pushRange.stageFlags = UInt32(VK_SHADER_STAGE_VERTEX_BIT.rawValue | VK_SHADER_STAGE_FRAGMENT_BIT.rawValue)
            pushRange.offset = 0
            pushRange.size = UInt32(MemoryLayout<Float>.stride * 7)  // a,b,c,d,tx,ty,opacity

            var plInfo = VkPipelineLayoutCreateInfo()
            plInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO
            plInfo.setLayoutCount = 1
            var setLayouts: [VkDescriptorSetLayout?] = [dsl]
            var pll: VkPipelineLayout? = nil
            let plResult: VkResult = setLayouts.withUnsafeMutableBufferPointer { slBuf in
                plInfo.pSetLayouts = UnsafePointer(slBuf.baseAddress)
                return withUnsafePointer(to: &pushRange) { prPtr in
                    plInfo.pushConstantRangeCount = 1
                    plInfo.pPushConstantRanges = prPtr
                    return withUnsafePointer(to: &plInfo) { iPtr in
                        vkCreatePipelineLayout(dev, iPtr, nil, &pll)
                    }
                }
            }
            guard plResult == VK_SUCCESS, let pll else {
                vkDestroyDescriptorSetLayout(dev, dsl, nil)
                vkDestroyShaderModule(dev, vertModule, nil)
                vkDestroyShaderModule(dev, fragModule, nil)
                return (nil, nil, nil)
            }

            let pipe = VulkanLayerPipeline.createGraphics(
                dev: dev, stages: stages, layout: pll, renderPass: renderPass
            )
            vkDestroyShaderModule(dev, vertModule, nil)
            vkDestroyShaderModule(dev, fragModule, nil)
            if pipe == nil {
                vkDestroyPipelineLayout(dev, pll, nil)
                vkDestroyDescriptorSetLayout(dev, dsl, nil)
            }
            return (pipe, pll, dsl)
        }

        guard let pipeline, let layout, let descriptorSetLayout else {
            print("[VulkanLayerPipeline] FAILED to create")
            return nil
        }
        print("[VulkanLayerPipeline] OK")
        self.device = dev
        self.pipeline = pipeline
        self.layout = layout
        self.descriptorSetLayout = descriptorSetLayout
    }

    private static func stageInfo(_ stage: VkShaderStageFlagBits, module: VkShaderModule, name: UnsafePointer<CChar>) -> VkPipelineShaderStageCreateInfo {
        var s = VkPipelineShaderStageCreateInfo()
        s.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO
        s.stage = stage
        s.module = module
        s.pName = name
        return s
    }

    private static func shaderModule(dev: VkDevice, spirv: [UInt32]) -> VkShaderModule? {
        var info = VkShaderModuleCreateInfo()
        info.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO
        info.codeSize = spirv.count * 4
        var bytes = spirv
        var module: VkShaderModule? = nil
        let r: VkResult = bytes.withUnsafeMutableBufferPointer { buf in
            info.pCode = UnsafePointer(buf.baseAddress)
            return withUnsafePointer(to: &info) { iPtr in
                vkCreateShaderModule(dev, iPtr, nil, &module)
            }
        }
        return r == VK_SUCCESS ? module : nil
    }

    /// Builds the graphics pipeline with triangle-strip topology, premultiplied
    /// source-over blending (so per-layer alpha composites bottom→top), and all
    /// sub-state pointer scopes nested so every pointer is live at call time.
    private static func createGraphics(
        dev: VkDevice,
        stages: [VkPipelineShaderStageCreateInfo],
        layout: VkPipelineLayout,
        renderPass: VkRenderPass
    ) -> VkPipeline? {
        var vertexInput = VkPipelineVertexInputStateCreateInfo()
        vertexInput.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO

        var inputAssembly = VkPipelineInputAssemblyStateCreateInfo()
        inputAssembly.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO
        inputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP
        inputAssembly.primitiveRestartEnable = UInt32(VK_FALSE)

        // No dynamic viewport — the pipeline is built against a specific
        // render-area. Caller records vkCmdSetViewport/Scissor if needed; for
        // the MVP we bind static viewport sized to the swapchain extent at
        // draw time via the renderer. The viewport state struct still requires
        // viewport/scissor counts; the renderer sets them dynamically.
        var viewportState = VkPipelineViewportStateCreateInfo()
        viewportState.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO
        viewportState.viewportCount = 1
        viewportState.scissorCount = 1

        var rasterizer = VkPipelineRasterizationStateCreateInfo()
        rasterizer.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO
        rasterizer.depthClampEnable = UInt32(VK_FALSE)
        rasterizer.rasterizerDiscardEnable = UInt32(VK_FALSE)
        rasterizer.polygonMode = VK_POLYGON_MODE_FILL
        rasterizer.lineWidth = 1
        rasterizer.cullMode = UInt32(VK_CULL_MODE_NONE.rawValue)
        rasterizer.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE

        var multisampling = VkPipelineMultisampleStateCreateInfo()
        multisampling.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO
        multisampling.sampleShadingEnable = UInt32(VK_FALSE)
        multisampling.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT

        // Premultiplied source-over: src already premultiplied in the fragment
        // shader (RGB *= a). Blend: out = src + dst*(1-src.a).
        var colorBlendAttachment = VkPipelineColorBlendAttachmentState()
        colorBlendAttachment.blendEnable = UInt32(VK_TRUE)
        colorBlendAttachment.srcColorBlendFactor = VK_BLEND_FACTOR_ONE
        colorBlendAttachment.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA
        colorBlendAttachment.colorBlendOp = VK_BLEND_OP_ADD
        colorBlendAttachment.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE
        colorBlendAttachment.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA
        colorBlendAttachment.alphaBlendOp = VK_BLEND_OP_ADD
        colorBlendAttachment.colorWriteMask = UInt32(VK_COLOR_COMPONENT_R_BIT.rawValue | VK_COLOR_COMPONENT_G_BIT.rawValue | VK_COLOR_COMPONENT_B_BIT.rawValue | VK_COLOR_COMPONENT_A_BIT.rawValue)

        var colorBlending = VkPipelineColorBlendStateCreateInfo()
        colorBlending.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO
        colorBlending.logicOpEnable = UInt32(VK_FALSE)
        colorBlending.attachmentCount = 1

        // Dynamic viewport + scissor: the renderer sets them per frame from
        // the swapchain extent, so we don't bake a static viewport here.
        var dynStates: [VkDynamicState] = [VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR]
        var dynInfo = VkPipelineDynamicStateCreateInfo()
        dynInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO
        dynInfo.dynamicStateCount = UInt32(dynStates.count)

        var gpInfo = VkGraphicsPipelineCreateInfo()
        gpInfo.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO
        gpInfo.stageCount = 2
        gpInfo.layout = layout
        gpInfo.renderPass = renderPass
        gpInfo.subpass = 0

        var pipe: VkPipeline? = nil
        let result: VkResult = stages.withUnsafeBufferPointer { stagesBuf in
            gpInfo.pStages = UnsafePointer(stagesBuf.baseAddress)
            return dynStates.withUnsafeBufferPointer { dynBuf in
                return withUnsafePointer(to: &vertexInput) { viPtr in
                    gpInfo.pVertexInputState = viPtr
                    return withUnsafePointer(to: &inputAssembly) { iaPtr in
                        gpInfo.pInputAssemblyState = iaPtr
                        return withUnsafePointer(to: &viewportState) { vpsPtr in
                            gpInfo.pViewportState = vpsPtr
                            return withUnsafePointer(to: &rasterizer) { rPtr in
                                gpInfo.pRasterizationState = rPtr
                                return withUnsafePointer(to: &multisampling) { msPtr in
                                    gpInfo.pMultisampleState = msPtr
                                    return withUnsafePointer(to: &colorBlendAttachment) { cbaPtr in
                                        colorBlending.pAttachments = cbaPtr
                                        return withUnsafePointer(to: &colorBlending) { cbPtr in
                                            gpInfo.pColorBlendState = cbPtr
                                            dynInfo.pDynamicStates = UnsafePointer(dynBuf.baseAddress)
                                            return withUnsafePointer(to: &dynInfo) { diPtr in
                                                gpInfo.pDynamicState = diPtr
                                                return withUnsafePointer(to: &gpInfo) { gpPtr in
                                                    vkCreateGraphicsPipelines(dev, nil, 1, gpPtr, nil, &pipe)
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        return result == VK_SUCCESS ? pipe : nil
    }

    deinit {
        vkDestroyPipeline(device, pipeline, nil)
        vkDestroyPipelineLayout(device, layout, nil)
        vkDestroyDescriptorSetLayout(device, descriptorSetLayout, nil)
    }
}
