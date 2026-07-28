import CVulkan

/// The textured-quad graphics pipeline: SPIR-V shader modules + descriptor set
/// layout (one combined image sampler) + pipeline layout + the pipeline itself.
///
/// Drawn as a full-screen triangle (no vertex buffer; the vertex shader emits
/// positions from gl_VertexIndex). The fragment shader samples a BGRA texture.
/// This is the simplest pipeline that puts a decoded frame on screen; per-layer
/// crop/transform/opacity composite is layered on top later.
public final class VulkanPipeline: @unchecked Sendable {
    public let device: VkDevice
    public let pipeline: VkPipeline
    public let layout: VkPipelineLayout
    public let descriptorSetLayout: VkDescriptorSetLayout

    public init?(device: VulkanDevice, renderPass: VkRenderPass, extent: VkExtent2D) {
        let dev = device.device

        // 1) Shader modules from the embedded SPIR-V.
        guard let vertModule = VulkanPipeline.shaderModule(dev: dev, spirv: Shaders.textured_quad_vertSPIRV),
              let fragModule = VulkanPipeline.shaderModule(dev: dev, spirv: Shaders.textured_quad_fragSPIRV)
        else { return nil }

        // The "main" entry-point C string must outlive vkCreateGraphicsPipelines,
        // because each VkPipelineShaderStageCreateInfo carries pName as a raw
        // pointer. withCString's pointer is only valid inside its closure, so
        // the whole pipeline build runs nested inside one stable scope.
        let (pipeline, layout, descriptorSetLayout): (VkPipeline?, VkPipelineLayout?, VkDescriptorSetLayout?) = "main".withCString { namePtr -> (VkPipeline?, VkPipelineLayout?, VkDescriptorSetLayout?) in
            var stages: [VkPipelineShaderStageCreateInfo] = [
                VulkanPipeline.stageInfo(VK_SHADER_STAGE_VERTEX_BIT, module: vertModule, name: namePtr),
                VulkanPipeline.stageInfo(VK_SHADER_STAGE_FRAGMENT_BIT, module: fragModule, name: namePtr)
            ]

            // 2) Descriptor set layout: one combined image sampler at binding 0.
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

            // 3) Pipeline layout.
            var plInfo = VkPipelineLayoutCreateInfo()
            plInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO
            plInfo.setLayoutCount = 1
            var setLayouts: [VkDescriptorSetLayout?] = [dsl]
            var pll: VkPipelineLayout? = nil
            let plResult: VkResult = setLayouts.withUnsafeMutableBufferPointer { buf in
                plInfo.pSetLayouts = UnsafePointer(buf.baseAddress)
                return withUnsafePointer(to: &plInfo) { iPtr in
                    vkCreatePipelineLayout(dev, iPtr, nil, &pll)
                }
            }
            guard plResult == VK_SUCCESS, let pll else {
                vkDestroyDescriptorSetLayout(dev, dsl, nil)
                vkDestroyShaderModule(dev, vertModule, nil)
                vkDestroyShaderModule(dev, fragModule, nil)
                return (nil, nil, nil)
            }

            // 4) Graphics pipeline.
            let pipe = VulkanPipeline.createGraphics(
                dev: dev, stages: stages, layout: pll, renderPass: renderPass, extent: extent
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
            print("[VulkanPipeline] FAILED to create")
            return nil
        }
        print("[VulkanPipeline] OK")
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
        print("[shaderModule] codeSize=\(info.codeSize), words=\(spirv.count), result=\(r.rawValue), module=\(module != nil)")
        return r == VK_SUCCESS ? module : nil
    }

    /// Builds the graphics pipeline with all sub-state pointer scopes nested so
    /// every pointer is live when vkCreateGraphicsPipelines reads gpInfo.
    private static func createGraphics(
        dev: VkDevice,
        stages: [VkPipelineShaderStageCreateInfo],
        layout: VkPipelineLayout,
        renderPass: VkRenderPass,
        extent: VkExtent2D
    ) -> VkPipeline? {
        var vertexInput = VkPipelineVertexInputStateCreateInfo()
        vertexInput.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO

        var inputAssembly = VkPipelineInputAssemblyStateCreateInfo()
        inputAssembly.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO
        inputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST
        inputAssembly.primitiveRestartEnable = UInt32(VK_FALSE)

        var viewport = VkViewport()
        viewport.x = 0; viewport.y = 0
        viewport.width = Float(extent.width); viewport.height = Float(extent.height)
        viewport.minDepth = 0; viewport.maxDepth = 1
        var scissor = VkRect2D()
        scissor.offset = VkOffset2D(x: 0, y: 0)
        scissor.extent = extent
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
        colorBlendAttachment.blendEnable = UInt32(VK_FALSE)
        colorBlendAttachment.colorWriteMask = UInt32(VK_COLOR_COMPONENT_R_BIT.rawValue | VK_COLOR_COMPONENT_G_BIT.rawValue | VK_COLOR_COMPONENT_B_BIT.rawValue | VK_COLOR_COMPONENT_A_BIT.rawValue)

        var colorBlending = VkPipelineColorBlendStateCreateInfo()
        colorBlending.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO
        colorBlending.logicOpEnable = UInt32(VK_FALSE)
        colorBlending.attachmentCount = 1

        var gpInfo = VkGraphicsPipelineCreateInfo()
        gpInfo.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO
        gpInfo.stageCount = 2
        gpInfo.layout = layout
        gpInfo.renderPass = renderPass
        gpInfo.subpass = 0

        var pipe: VkPipeline? = nil
        let result: VkResult = stages.withUnsafeBufferPointer { stagesBuf in
            gpInfo.pStages = UnsafePointer(stagesBuf.baseAddress)
            return withUnsafePointer(to: &viewport) { vpPtr in
                viewportState.pViewports = vpPtr
                return withUnsafePointer(to: &scissor) { scPtr in
                    viewportState.pScissors = scPtr
                    return withUnsafePointer(to: &colorBlendAttachment) { cbaPtr in
                        colorBlending.pAttachments = cbaPtr
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
                                            return withUnsafePointer(to: &colorBlending) { cbPtr in
                                                gpInfo.pColorBlendState = cbPtr
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
        print("[VulkanPipeline.createGraphics] result = \(result.rawValue) (0 = VK_SUCCESS)")
        return result == VK_SUCCESS ? pipe : nil
    }

    deinit {
        vkDestroyPipeline(device, pipeline, nil)
        vkDestroyPipelineLayout(device, layout, nil)
        vkDestroyDescriptorSetLayout(device, descriptorSetLayout, nil)
    }
}
