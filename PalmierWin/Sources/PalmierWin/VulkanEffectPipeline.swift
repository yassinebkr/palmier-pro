import CVulkan

/// One-pipeline-per-effect-pass graphics pipeline: a full-screen triangle that
/// samples a source texture, applies one effect kernel (selected via push
/// constant `effectType` + `params[]`), and writes the result to the bound
/// render target. The renderer ping-pongs between two offscreen textures to
/// chain multiple effects per clip.
///
/// The dispatching fragment shader (Shaders/effect.frag) covers all effect
/// types — one SPIR-V module, one pipeline, one draw per pass.
public final class VulkanEffectPipeline: @unchecked Sendable {
    public let device: VkDevice
    public let pipeline: VkPipeline
    public let layout: VkPipelineLayout
    public let descriptorSetLayout: VkDescriptorSetLayout

    /// Effect type constants — must match the `switch` in Shaders/effect.frag.
    public enum EffectType: UInt32 {
        case vignette = 1
        case wheels = 2
        case levels = 3
        case grain = 4
        case chromaKey = 5
        case highlightsShadows = 6
        case edgeRounding = 7
        case clarity = 8
        case glowBright = 9
        case glowComposite = 10
        case gradeCurves = 11
        case hueCurves = 12
        case lutTetra = 13
        case blur = 14
        case invert = 15
    }

    public init?(device: VulkanDevice, renderPass: VkRenderPass) {
        let dev = device.device

        guard let vertModule = VulkanEffectPipeline.shaderModule(dev: dev, spirv: Shaders.effect_vertSPIRV),
              let fragModule = VulkanEffectPipeline.shaderModule(dev: dev, spirv: Shaders.effect_fragSPIRV)
        else { return nil }

        let (pipeline, layout, descriptorSetLayout): (VkPipeline?, VkPipelineLayout?, VkDescriptorSetLayout?) = "main".withCString { namePtr -> (VkPipeline?, VkPipelineLayout?, VkDescriptorSetLayout?) in
            let stages: [VkPipelineShaderStageCreateInfo] = [
                VulkanEffectPipeline.stageInfo(VK_SHADER_STAGE_VERTEX_BIT, module: vertModule, name: namePtr),
                VulkanEffectPipeline.stageInfo(VK_SHADER_STAGE_FRAGMENT_BIT, module: fragModule, name: namePtr)
            ]

            // Three combined image samplers: src (0), aux (1), aux2 (2).
            // Single-texture effects bind a 1×1 white dummy to the unused slots.
            var bindings: [VkDescriptorSetLayoutBinding] = (0..<3).map { i in
                var b = VkDescriptorSetLayoutBinding()
                b.binding = UInt32(i)
                b.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER
                b.descriptorCount = 1
                b.stageFlags = UInt32(VK_SHADER_STAGE_FRAGMENT_BIT.rawValue)
                return b
            }
            var dslInfo = VkDescriptorSetLayoutCreateInfo()
            dslInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO
            dslInfo.bindingCount = 3
            var dsl: VkDescriptorSetLayout? = nil
            let dslResult: VkResult = bindings.withUnsafeMutableBufferPointer { bBuf in
                dslInfo.pBindings = UnsafePointer(bBuf.baseAddress)
                return withUnsafePointer(to: &dslInfo) { iPtr in
                    vkCreateDescriptorSetLayout(dev, iPtr, nil, &dsl)
                }
            }
            guard dslResult == VK_SUCCESS, let dsl else {
                vkDestroyShaderModule(dev, vertModule, nil)
                vkDestroyShaderModule(dev, fragModule, nil)
                return (nil, nil, nil)
            }

            // Push constants: effectType (uint) + 30 floats = 124 bytes.
            var pushRange = VkPushConstantRange()
            pushRange.stageFlags = UInt32(VK_SHADER_STAGE_FRAGMENT_BIT.rawValue)
            pushRange.offset = 0
            pushRange.size = UInt32(MemoryLayout<UInt32>.stride + MemoryLayout<Float>.stride * 30)

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

            let pipe = VulkanEffectPipeline.createGraphics(dev: dev, stages: stages, layout: pll, renderPass: renderPass)
            vkDestroyShaderModule(dev, vertModule, nil)
            vkDestroyShaderModule(dev, fragModule, nil)
            if pipe == nil {
                vkDestroyPipelineLayout(dev, pll, nil)
                vkDestroyDescriptorSetLayout(dev, dsl, nil)
            }
            return (pipe, pll, dsl)
        }

        guard let pipeline, let layout, let descriptorSetLayout else { return nil }
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

    /// Full-screen triangle, no vertex input, no blending (overwrite — effects
    /// chain by ping-ponging whole textures), dynamic viewport/scissor.
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
        inputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST
        inputAssembly.primitiveRestartEnable = UInt32(VK_FALSE)

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

        var colorBlendAttachment = VkPipelineColorBlendAttachmentState()
        colorBlendAttachment.blendEnable = UInt32(VK_FALSE)  // overwrite (no blend)
        colorBlendAttachment.colorWriteMask = UInt32(VK_COLOR_COMPONENT_R_BIT.rawValue | VK_COLOR_COMPONENT_G_BIT.rawValue | VK_COLOR_COMPONENT_B_BIT.rawValue | VK_COLOR_COMPONENT_A_BIT.rawValue)

        var colorBlending = VkPipelineColorBlendStateCreateInfo()
        colorBlending.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO
        colorBlending.logicOpEnable = UInt32(VK_FALSE)
        colorBlending.attachmentCount = 1

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
