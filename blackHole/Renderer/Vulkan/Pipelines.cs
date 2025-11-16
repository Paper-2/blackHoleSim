namespace blackHole.Renderer.Vulkan;

using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

public unsafe class Pipelines
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Format _swapChainImageFormat;

    public PipelineLayout _pipelineLayout;
    public RenderPass _renderPass;
    private Pipeline _graphicsPipeline;
    public Extent2D _swapChainExtent; // RenderWorker will update this on window resize

    public Pipelines(Vk vk, Device device, Extent2D swapChainExtent, Format swapChainImageFormat)
    {
        _vk = vk;
        _device = device;
        _swapChainExtent = swapChainExtent;
        _swapChainImageFormat = swapChainImageFormat;
    }

    public void CreateGraphicsPipeline(string vertShader, string fragShader)
    {
        // Load shaders
        var vertShaderCode = File.ReadAllBytes(vertShader);
        var fragShaderCode = File.ReadAllBytes(fragShader);
        ShaderModule vertShaderModule = CreateShaderModule(vertShaderCode);
        ShaderModule fragShaderModule = CreateShaderModule(fragShaderCode);

        PipelineShaderStageCreateInfo vertShaderStageInfo = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertShaderModule,
            PName = (byte*)Marshal.StringToHGlobalAnsi("main"),
        };

        PipelineShaderStageCreateInfo fragShaderStageInfo = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragShaderModule,
            PName = (byte*)Marshal.StringToHGlobalAnsi("main"),
        };

        var shaderStages = new[] { vertShaderStageInfo, fragShaderStageInfo };

        PipelineVertexInputStateCreateInfo vertexInputInfo = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 0,
            VertexAttributeDescriptionCount = 0,
        };

        PipelineInputAssemblyStateCreateInfo inputAssembly = new() 
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
            PrimitiveRestartEnable = false,
        };

        Viewport viewport = new()
        {
            X = 0.0f,
            Y = 0.0f,
            Width = _swapChainExtent.Width,
            Height = _swapChainExtent.Height,
            MinDepth = 0.0f,
            MaxDepth = 1.0f,
        };

        Rect2D scissor = new() { Offset = { X = 0, Y = 0 }, Extent = _swapChainExtent };

        PipelineViewportStateCreateInfo viewportState = new()
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            PViewports = &viewport,
            ScissorCount = 1,
            PScissors = &scissor,
        };

        PipelineRasterizationStateCreateInfo rasterizer = new()
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = PolygonMode.Fill,
            LineWidth = 1.0f,
            CullMode = CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
            DepthBiasEnable = false,
        };

        PipelineMultisampleStateCreateInfo multisampling = new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable = false,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        PipelineColorBlendAttachmentState colorBlendAttachment = new()
        {
            ColorWriteMask =
                ColorComponentFlags.RBit
                | ColorComponentFlags.GBit
                | ColorComponentFlags.BBit
                | ColorComponentFlags.ABit,
            BlendEnable = false,
        };

        PipelineColorBlendStateCreateInfo colorBlending = new()
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            LogicOp = LogicOp.Copy,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };
        colorBlending.BlendConstants[0] = 0.0f;
        colorBlending.BlendConstants[1] = 0.0f;
        colorBlending.BlendConstants[2] = 0.0f;
        colorBlending.BlendConstants[3] = 0.0f;

        AttachmentDescription colorAttachment = new()
        {
            Format = _swapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        AttachmentReference colorAttachmentRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        if (_vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass) != Result.Success)
        {
            throw new Exception("Failed to create render pass!");
        }

        PipelineLayoutCreateInfo pipelineLayoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 0,
            PushConstantRangeCount = 0,
        };

        if (
            _vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout)
            != Result.Success
        )
        {
            throw new Exception("Failed to create pipeline layout!");
        }

        fixed (PipelineShaderStageCreateInfo* pStages = shaderStages)
        {
            GraphicsPipelineCreateInfo pipelineInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = pStages,
                PVertexInputState = &vertexInputInfo,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PColorBlendState = &colorBlending,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
                BasePipelineHandle = default,
                BasePipelineIndex = -1,
            };

            fixed (Pipeline* graphicsPipelinePtr = &_graphicsPipeline)
            {
                if (
                    _vk.CreateGraphicsPipelines(
                        _device,
                        default,
                        1,
                        &pipelineInfo,
                        null,
                        graphicsPipelinePtr
                    ) != Result.Success
                )
                {
                    throw new Exception("Failed to create graphics pipeline!");
                }
            }
        }



        _vk.DestroyShaderModule(_device, fragShaderModule, null);
        _vk.DestroyShaderModule(_device, vertShaderModule, null);

        Marshal.FreeHGlobal((IntPtr)vertShaderStageInfo.PName);
        Marshal.FreeHGlobal((IntPtr)fragShaderStageInfo.PName);
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        ShaderModuleCreateInfo createInfo = new()
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
        };

        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;

            if (
                _vk.CreateShaderModule(_device, &createInfo, null, out ShaderModule shaderModule)
                != Result.Success
            )
            {
                throw new Exception("Failed to create shader module!");
            }

            return shaderModule;
        }
    }
}
