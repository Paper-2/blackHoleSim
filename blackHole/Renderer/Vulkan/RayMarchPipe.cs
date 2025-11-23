using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using blackHole.Tools;
using Silk.NET.Maths;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using StbImageSharp;

namespace blackHole.Renderer.Vulkan;

/// <summary>
/// Pipeline for ray marching rendering.
/// </summary>
///
///
public unsafe class RayMarchPipe : PipelineBuilder, IDisposable
{
    private const int MAX_FRAMES_IN_FLIGHT = 2;

    [StructLayout(LayoutKind.Explicit)]
    public struct RayMarchUBO // UBO stands for Uniform Buffer Object
    {
        [FieldOffset(0)]
        public Matrix4X4<float> uInvViewMatrix;

        [FieldOffset(64)]
        public Vector3D<float> uCameraPos;

        [FieldOffset(76)]
        public float _pad0;

        [FieldOffset(80)]
        public Vector4D<float> uFrustumCorners0;

        [FieldOffset(96)]
        public Vector4D<float> uFrustumCorners1;

        [FieldOffset(112)]
        public Vector4D<float> uFrustumCorners2;

        [FieldOffset(128)]
        public Vector4D<float> uFrustumCorners3;

        [FieldOffset(144)]
        public Vector4D<float> uAccretionDiskColor;

        [FieldOffset(160)]
        public Vector4D<float> uBlackHoleColor;

        [FieldOffset(176)]
        public Vector2D<float> uResolution;

        [FieldOffset(184)]
        public Vector2D<float> _pad1;

        [FieldOffset(192)]
        public float uTime;

        [FieldOffset(196)]
        public float uSchwarzschildRadius;

        [FieldOffset(200)]
        public float uSpaceDistortion;

        [FieldOffset(204)]
        public float uAccretionDiskThickness;
    }

    private Silk.NET.Vulkan.Buffer[] uniformBuffers;
    private DeviceMemory[] uniformBuffersMemory;
    private DescriptorPool descriptorPool;
    private DescriptorSet[] descriptorSets;
    private DescriptorSetLayout descriptorSetLayout;

    private Image skyboxImage;
    private DeviceMemory skyboxMemory;
    private ImageView skyboxView;
    private Sampler skyboxSampler;

    private Image noiseImage;
    private DeviceMemory noiseMemory;
    private ImageView noiseView;
    private Sampler noiseSampler;

    public RayMarchPipe(Vulkan vulkan)
        : base(vulkan)
    {
        uniformBuffers = new Silk.NET.Vulkan.Buffer[MAX_FRAMES_IN_FLIGHT];
        uniformBuffersMemory = new DeviceMemory[MAX_FRAMES_IN_FLIGHT];
        descriptorSets = new DescriptorSet[MAX_FRAMES_IN_FLIGHT];
    }

    protected override PrimitiveTopology Topology => PrimitiveTopology.TriangleStrip;
    protected override CullModeFlags CullMode => CullModeFlags.None;

    public override void CreateGraphicsPipeline(
        string vertShader,
        string fragShader //,
    // string compShader
    )
    {
        // Load shaders -- in theory we don't need multiple shaders. We can have just a universal shader and then create the
        // stage info with different entry points.
        string vertPath = Helpers.FindFilePath(vertShader);
        string fragPath = Helpers.FindFilePath(fragShader);
        // string compPath = FindShaderPath(compShader);

        ShaderModule vertShaderModule = _vulkan.CompileShader(
            File.ReadAllText(vertPath),
            ShaderKind.VertexShader,
            vertPath
        );
        ShaderModule fragShaderModule = _vulkan.CompileShader(
            File.ReadAllText(fragPath),
            ShaderKind.FragmentShader,
            fragPath
        );
        // ShaderModule compShaderModule = _vulkan.CompileShader(
        //     File.ReadAllText(compPath),
        //     ShaderKind.ComputeShader,
        //     compPath
        // );

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

        // PipelineShaderStageCreateInfo compShaderStageInfo = new()
        // {
        //     SType = StructureType.PipelineShaderStageCreateInfo,
        //     Stage = ShaderStageFlags.ComputeBit,
        //     Module = compShaderModule,
        //     PName = (byte*)Marshal.StringToHGlobalAnsi("main"),
        // };

        var shaderStages = new[]
        {
            vertShaderStageInfo,
            fragShaderStageInfo,
            /* compShaderStageInfo */
        };

        // No vertex input for ray marching
        PipelineVertexInputStateCreateInfo vertexInputInfo = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 0,
            VertexAttributeDescriptionCount = 0,
        };

        PipelineInputAssemblyStateCreateInfo inputAssembly = new()
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = this.Topology,
            PrimitiveRestartEnable = false,
        };

        uint viewportWidth = _swapChainExtent.Width == 0 ? 1280 : _swapChainExtent.Width;
        uint viewportHeight = _swapChainExtent.Height == 0 ? 720 : _swapChainExtent.Height;

        Viewport viewport = new()
        {
            X = 0.0f,
            Y = 0.0f,
            Width = viewportWidth,
            Height = viewportHeight,
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
            PolygonMode = this.PolygonMode,
            LineWidth = this.LineWidth,
            CullMode = this.CullMode,
            FrontFace = this.FrontFace,
            DepthBiasEnable = false,
        };

        PipelineMultisampleStateCreateInfo multisampling = new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable = false,
            RasterizationSamples = this.RasterizationSamples,
        };

        PipelineDepthStencilStateCreateInfo depthStencil = new()
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = this.EnableDepthWrite,
            DepthCompareOp = this.DepthCompareOp,
            DepthBoundsTestEnable = false,
            StencilTestEnable = false,
        };

        PipelineColorBlendAttachmentState colorBlendAttachment = new()
        {
            ColorWriteMask =
                ColorComponentFlags.RBit
                | ColorComponentFlags.GBit
                | ColorComponentFlags.BBit
                | ColorComponentFlags.ABit,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
        };

        PipelineColorBlendStateCreateInfo colorBlending = new()
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };

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

        AttachmentDescription depthAttachment = new()
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        AttachmentReference depthAttachmentRef = new()
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        AttachmentDescription[] attachments = { colorAttachment, depthAttachment };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PDepthStencilAttachment = &depthAttachmentRef,
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask =
                PipelineStageFlags.ColorAttachmentOutputBit
                | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask =
                PipelineStageFlags.ColorAttachmentOutputBit
                | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask =
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };

        fixed (AttachmentDescription* pAttachments = attachments)
        {
            RenderPassCreateInfo renderPassInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 2,
                PAttachments = pAttachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };

            if (
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass)
                != Result.Success
            )
            {
                throw new Exception("Failed to create render pass!");
            }
        }

        ReadOnlySpan<PushConstantRange> pushConstantRanges = ReadOnlySpan<PushConstantRange>.Empty;
        PipelineLayoutCreateInfo pipelineLayoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = (DescriptorSetLayout*)GetDescriptorSetLayoutPointer(),
            PushConstantRangeCount = (uint)pushConstantRanges.Length,
        };

        if (pushConstantRanges.Length > 0)
        {
            fixed (PushConstantRange* pushPtr = pushConstantRanges)
            {
                pipelineLayoutInfo.PPushConstantRanges = pushPtr;
                if (
                    _vk.CreatePipelineLayout(
                        _device,
                        &pipelineLayoutInfo,
                        null,
                        out _pipelineLayout
                    ) != Result.Success
                )
                {
                    throw new Exception("Failed to create pipeline layout!");
                }
            }
        }
        else
        {
            pipelineLayoutInfo.PPushConstantRanges = null;
            if (
                _vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout)
                != Result.Success
            )
            {
                throw new Exception("Failed to create pipeline layout!");
            }
        }

        DynamicState[] dynamicStates = { DynamicState.Viewport, DynamicState.Scissor };

        PipelineDynamicStateCreateInfo dynamicState = new()
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = (uint)dynamicStates.Length,
            PDynamicStates = (DynamicState*)Unsafe.AsPointer(ref dynamicStates[0]),
        };

        GraphicsPipelineCreateInfo pipelineInfo = new()
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = (uint)shaderStages.Length,
            PStages = null, // will set with fixed
            PVertexInputState = &vertexInputInfo,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlending,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = _renderPass,
            Subpass = 0,
            BasePipelineHandle = default,
            BasePipelineIndex = -1,
        };

        fixed (PipelineShaderStageCreateInfo* pStages = shaderStages)
        {
            pipelineInfo.PStages = pStages;
            if (
                _vk.CreateGraphicsPipelines(
                    _device,
                    default,
                    1,
                    &pipelineInfo,
                    null,
                    out _graphicsPipeline
                ) != Result.Success
            )
            {
                throw new Exception("Failed to create graphics pipeline!");
            }
        }

        _vk.DestroyShaderModule(_device, vertShaderModule, null);
        _vk.DestroyShaderModule(_device, fragShaderModule, null);
    }

    protected override unsafe void* GetDescriptorSetLayoutPointer()
    {
        fixed (DescriptorSetLayout* ptr = &descriptorSetLayout)
        {
            return ptr;
        }
    }

    protected override void CreateVertexBuffer()
    {
        // No vertex buffer for ray marching
    }

    protected override void CreateIndexBuffer()
    {
        // No index buffer
    }

    protected override void CreateUniformBuffers()
    {
        ulong bufferSize = (ulong)sizeof(RayMarchUBO);

        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            _vulkan.CreateBuffer(
                bufferSize,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                ref uniformBuffers[i],
                ref uniformBuffersMemory[i]
            );
        }
    }

    protected override void CreateDescriptorPool()
    {
        DescriptorPoolSize[] poolSizes =
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.UniformBuffer,
                DescriptorCount = (uint)MAX_FRAMES_IN_FLIGHT,
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = (uint)MAX_FRAMES_IN_FLIGHT * 2,
            },
        };

        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = (uint)poolSizes.Length,
            MaxSets = (uint)MAX_FRAMES_IN_FLIGHT,
        };

        fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
        {
            poolInfo.PPoolSizes = poolSizesPtr;
            if (
                _vk.CreateDescriptorPool(_device, &poolInfo, null, out descriptorPool)
                != Result.Success
            )
            {
                throw new Exception("Failed to create descriptor pool!");
            }
        }
    }

    protected override void CreateDescriptorSets()
    {
        fixed (DescriptorSetLayout* setLayoutsPtr = &descriptorSetLayout)
        {
            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = setLayoutsPtr,
            };

            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                fixed (DescriptorSet* setPtr = &descriptorSets[i])
                {
                    if (_vk.AllocateDescriptorSets(_device, &allocInfo, setPtr) != Result.Success)
                    {
                        throw new Exception("Failed to allocate descriptor sets!");
                    }
                }

                DescriptorBufferInfo bufferInfo = new()
                {
                    Buffer = uniformBuffers[i],
                    Offset = 0,
                    Range = (ulong)sizeof(RayMarchUBO),
                };

                DescriptorImageInfo skyboxInfo = new()
                {
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    ImageView = skyboxView,
                    Sampler = skyboxSampler,
                };

                DescriptorImageInfo noiseInfo = new()
                {
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    ImageView = noiseView,
                    Sampler = noiseSampler,
                };

                WriteDescriptorSet[] descriptorWrites =
                {
                    new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = &skyboxInfo,
                    },
                    new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[i],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = &noiseInfo,
                    },
                    new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[i],
                        DstBinding = 2,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.UniformBuffer,
                        PBufferInfo = &bufferInfo,
                    },
                };

                _vk.UpdateDescriptorSets(
                    _device,
                    (uint)descriptorWrites.Length,
                    descriptorWrites,
                    0,
                    ReadOnlySpan<CopyDescriptorSet>.Empty
                );
            }
        }
    }

    protected override void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding[] bindings =
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
        };

        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr,
            };

            if (
                _vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out descriptorSetLayout)
                != Result.Success
            )
            {
                throw new Exception("Failed to create descriptor set layout!");
            }
        }
    }

    protected override void CreateTextureImage()
    {
        // 1. Create Noise Texture (1x1 white)

        uint width = 1;
        uint height = 1;
        byte[] pixels = { 255, 255, 255, 255 };
        ulong imageSize = (ulong)(width * height * 4);

        Silk.NET.Vulkan.Buffer stagingBuffer = default;
        DeviceMemory stagingBufferMemory = default;

        _vulkan.CreateBuffer(
            imageSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            ref stagingBuffer,
            ref stagingBufferMemory
        );

        void* data;
        _vk.MapMemory(_device, stagingBufferMemory, 0, imageSize, 0, &data);
        pixels.CopyTo(new Span<byte>(data, pixels.Length));
        _vk.UnmapMemory(_device, stagingBufferMemory);

        CreateImage(
            width,
            height,
            Format.R8G8B8A8Srgb,
            ImageTiling.Optimal,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            MemoryPropertyFlags.DeviceLocalBit,
            ref noiseImage,
            ref noiseMemory
        );

        TransitionImageLayout(
            noiseImage,
            Format.R8G8B8A8Srgb,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal
        );
        CopyBufferToImage(stagingBuffer, noiseImage, width, height);
        TransitionImageLayout(
            noiseImage,
            Format.R8G8B8A8Srgb,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal
        );

        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingBufferMemory, null);

        // 2. Create Skybox Texture (Load starmap_2020.png)
        string texturePath = Helpers.FindFilePath(
            "blackHole/Resources/Assets/Textures/starmap_2020.png"
        );

        if (!File.Exists(texturePath))
        {
            // Try output directory
            texturePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Textures",
                "starmap_2020.png"
            );
        }

        if (!File.Exists(texturePath))
        {
            // Try source directory relative to bin
            texturePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..",
                "..",
                "..",
                "Resources",
                "Assets",
                "Textures",
                "starmap_2020.png"
            );
        }

        if (!File.Exists(texturePath))
        {
            // Try absolute path (last resort)
            texturePath =
                @"C:\Users\holac\source\repos\blackHoleSim2\blackHole\Resources\Assets\Textures\starmap_2020.png";
        }

        if (!File.Exists(texturePath))
        {
            // Fallback to dummy if not found, but try to find it first
            Console.WriteLine("Warning: starmap_2020.png not found, using fallback.");
            throw new FileNotFoundException(
                $"Texture not found. Tried multiple paths. Last tried: {texturePath}"
            );
        }

        byte[] fileData = File.ReadAllBytes(texturePath);
        ImageResult image = ImageResult.FromMemory(fileData, ColorComponents.RedGreenBlueAlpha);

        width = (uint)image.Width;
        height = (uint)image.Height;
        imageSize = (ulong)(width * height * 4);

        stagingBuffer = default;
        stagingBufferMemory = default;

        _vulkan.CreateBuffer(
            imageSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            ref stagingBuffer,
            ref stagingBufferMemory
        );

        _vk.MapMemory(_device, stagingBufferMemory, 0, imageSize, 0, &data);
        fixed (byte* ptr = image.Data)
        {
            System.Buffer.MemoryCopy(ptr, data, imageSize, imageSize);
        }
        _vk.UnmapMemory(_device, stagingBufferMemory);

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8G8B8A8Srgb,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        if (_vk.CreateImage(_device, &imageInfo, null, out skyboxImage) != Result.Success)
        {
            throw new Exception("Failed to create skybox image!");
        }

        MemoryRequirements memRequirements;
        _vk.GetImageMemoryRequirements(_device, skyboxImage, &memRequirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = Vulkan.FindMemoryType(
                _vk,
                _vulkan.PhysicalDevice,
                memRequirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit
            ),
        };

        if (_vk.AllocateMemory(_device, &allocInfo, null, out skyboxMemory) != Result.Success)
        {
            throw new Exception("Failed to allocate skybox image memory!");
        }

        _vk.BindImageMemory(_device, skyboxImage, skyboxMemory, 0);

        // Manual command buffer for transition and copy
        CommandPool commandPool = _vulkan.CreateCommandPool();

        CommandBufferAllocateInfo cmdAllocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        CommandBuffer commandBuffer;
        if (_vk.AllocateCommandBuffers(_device, &cmdAllocInfo, &commandBuffer) != Result.Success)
        {
            throw new Exception("Failed to allocate command buffer!");
        }

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        _vk.BeginCommandBuffer(commandBuffer, &beginInfo);

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = skyboxImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
        };

        _vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &barrier
        );

        BufferImageCopy region = new()
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };

        _vk.CmdCopyBufferToImage(
            commandBuffer,
            stagingBuffer,
            skyboxImage,
            ImageLayout.TransferDstOptimal,
            1,
            &region
        );

        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.ShaderReadBit;

        _vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &barrier
        );

        _vk.EndCommandBuffer(commandBuffer);

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };

        _vk.QueueSubmit(_vulkan.GraphicsQueue, 1, &submitInfo, default);
        _vk.QueueWaitIdle(_vulkan.GraphicsQueue);

        _vk.FreeCommandBuffers(_device, commandPool, 1, &commandBuffer);
        _vk.DestroyCommandPool(_device, commandPool, null);

        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingBufferMemory, null);
    }

    protected override void CreateTextureImageView()
    {
        ImageViewCreateInfo noiseViewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = noiseImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Srgb,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, &noiseViewInfo, null, out noiseView) != Result.Success)
        {
            throw new Exception("Failed to create noise image view!");
        }

        ImageViewCreateInfo skyboxViewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = skyboxImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Srgb,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, &skyboxViewInfo, null, out skyboxView) != Result.Success)
        {
            throw new Exception("Failed to create skybox image view!");
        }
    }

    protected override void CreateTextureSampler()
    {
        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = true,
            MaxAnisotropy = 16.0f,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MipmapMode = SamplerMipmapMode.Linear,
            MipLodBias = 0.0f,
            MinLod = 0.0f,
            MaxLod = 1.0f,
        };

        if (_vk.CreateSampler(_device, &samplerInfo, null, out noiseSampler) != Result.Success)
        {
            throw new Exception("Failed to create noise sampler!");
        }

        samplerInfo.AddressModeU = SamplerAddressMode.Repeat;
        samplerInfo.AddressModeV = SamplerAddressMode.ClampToEdge;
        samplerInfo.AddressModeW = SamplerAddressMode.ClampToEdge;

        if (_vk.CreateSampler(_device, &samplerInfo, null, out skyboxSampler) != Result.Success)
        {
            throw new Exception("Failed to create skybox sampler!");
        }
    }

    public override void RecordDrawCommands(CommandBuffer commandBuffer, int imageIndex)
    {
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _graphicsPipeline);

        fixed (DescriptorSet* setPtr = descriptorSets)
        {
            _vk.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Graphics,
                _pipelineLayout,
                0,
                1,
                &setPtr[imageIndex],
                0,
                null
            );
        }

        // Draw 4 vertices for full-screen quad
        _vk.CmdDraw(commandBuffer, 4, 1, 0, 0);
    }

    public override void RecordShadowCommands(CommandBuffer commandBuffer, int imageIndex)
    {
        // No shadows for ray marching
    }

    public override void Dispose()
    {
        if (_vk == null || _device.Handle == 0)
            return;

        try
        {
            _vk.DeviceWaitIdle(_device);
        }
        catch
        {
            return;
        }

        if (skyboxSampler.Handle != 0)
            _vk.DestroySampler(_device, skyboxSampler, null);
        if (skyboxView.Handle != 0)
            _vk.DestroyImageView(_device, skyboxView, null);
        if (skyboxImage.Handle != 0)
            _vk.DestroyImage(_device, skyboxImage, null);
        if (skyboxMemory.Handle != 0)
            _vk.FreeMemory(_device, skyboxMemory, null);

        if (noiseSampler.Handle != 0)
            _vk.DestroySampler(_device, noiseSampler, null);
        if (noiseView.Handle != 0)
            _vk.DestroyImageView(_device, noiseView, null);
        if (noiseImage.Handle != 0)
            _vk.DestroyImage(_device, noiseImage, null);
        if (noiseMemory.Handle != 0)
            _vk.FreeMemory(_device, noiseMemory, null);

        if (uniformBuffers != null)
        {
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                if (uniformBuffers[i].Handle != 0)
                    _vk.DestroyBuffer(_device, uniformBuffers[i], null);
            }
        }

        if (uniformBuffersMemory != null)
        {
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                if (uniformBuffersMemory[i].Handle != 0)
                    _vk.FreeMemory(_device, uniformBuffersMemory[i], null);
            }
        }

        if (descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, descriptorPool, null);
        if (descriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, descriptorSetLayout, null);

        CleanupResources();
    }

    public override void UpdateVertexBuffer(Array NewVertexData)
    {
        // No vertex buffer
    }

    public override void UpdateUniformBuffer(int currentImage, object ubo)
    {
        var castedUbo = (RayMarchUBO)ubo;
        void* data;
        _vk.MapMemory(
            _device,
            uniformBuffersMemory[currentImage],
            0,
            (ulong)sizeof(RayMarchUBO),
            0,
            &data
        );
        new Span<RayMarchUBO>(data, 1)[0] = castedUbo;
        _vk.UnmapMemory(_device, uniformBuffersMemory[currentImage]);
    }
}
