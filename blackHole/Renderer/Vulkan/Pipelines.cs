namespace blackHole.Renderer.Vulkan;

using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using Silk.NET.Maths;

public abstract unsafe class PipelineBuilder : IDisposable
{
    protected readonly Vk _vk;
    protected readonly Device _device;
    protected readonly Format _swapChainImageFormat;
    protected readonly Vulkan _vulkan;
    public PipelineLayout _pipelineLayout;
    public RenderPass _renderPass;
    protected Pipeline _graphicsPipeline;
    public Extent2D _swapChainExtent; // RenderWorker will update this on window resize

    // MSAA resources
    protected Image msaaColorImage;
    protected DeviceMemory msaaColorMemory;
    protected ImageView msaaColorView;

    protected Image msaaDepthImage;
    protected DeviceMemory msaaDepthMemory;
    protected ImageView msaaDepthView;

    // Shadow map resources
    protected Image shadowImage;
    protected DeviceMemory shadowMemory;
    protected ImageView shadowView;

    // Texture resources
    protected Image textureImage;
    protected DeviceMemory textureMemory;
    protected ImageView textureView;
    protected Sampler textureSampler;

    public PipelineBuilder(Vulkan vulkan)
    {
        _vulkan = vulkan;
        _vk = vulkan.VkAPI!;
        _device = vulkan.Device;
        _swapChainExtent = vulkan._swapChainExtent;
        _swapChainImageFormat = vulkan._swapChainImageFormat;
    }

    protected virtual PrimitiveTopology Topology => PrimitiveTopology.TriangleList;
    protected virtual PolygonMode PolygonMode => PolygonMode.Fill;
    protected virtual float LineWidth => 1.0f;
    protected virtual CullModeFlags CullMode => CullModeFlags.BackBit;
    protected virtual FrontFace FrontFace => FrontFace.CounterClockwise;
    protected virtual SampleCountFlags RasterizationSamples => SampleCountFlags.Count1Bit;
    protected virtual bool EnableDepthWrite => true;
    protected virtual CompareOp DepthCompareOp => CompareOp.Less;

    private static string FindShaderPath(string shaderPath)
    {
        // Try the provided path first
        if (File.Exists(shaderPath))
            return shaderPath;

        // Try without the 'blackHole/' prefix (for when running from build output)
        string withoutPrefix = shaderPath.Replace("blackHole/", "");
        if (File.Exists(withoutPrefix))
            return withoutPrefix;

        // Try with '../../../' to go up from bin/Debug/net9.0
        string relativePath = Path.Combine("..", "..", "..", withoutPrefix);
        if (File.Exists(relativePath))
            return relativePath;

        // If nothing works, return original and let it fail with a clear error
        throw new FileNotFoundException($"Shader not found. Tried paths: '{shaderPath}', '{withoutPrefix}', '{relativePath}'");
    }

    public virtual void CreateGraphicsPipeline(string vertShader, string fragShader)
    {
        // Load shaders with fallback paths
        string vertPath = FindShaderPath(vertShader);
        string fragPath = FindShaderPath(fragShader);
        
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

        PipelineShaderStageCreateInfo vertShaderStageInfo = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertShaderModule,
            PName = (byte*)Marshal.StringToHGlobalAnsi("main"), // sets the entry point of the shader to the "main" function
        };

        PipelineShaderStageCreateInfo fragShaderStageInfo = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragShaderModule,
            PName = (byte*)Marshal.StringToHGlobalAnsi("main"), // sets the entry point of the shader to the "main" function
        };

        var shaderStages = new[] { vertShaderStageInfo, fragShaderStageInfo };

        // Define vertex input binding and attributes
        VertexInputBindingDescription bindingDescription = new()
        {
            Binding = 0,
            Stride = (uint)sizeof(float) * 11, // 3 (pos) + 3 (normal) + 3 (color) + 2 (texcoord) = 11 floats
            InputRate = VertexInputRate.Vertex,
        };

        VertexInputAttributeDescription[] attributeDescriptions = new[]
        {
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 0,
                Format = Format.R32G32B32Sfloat, // Position (vec3)
                Offset = 0,
            },
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 1,
                Format = Format.R32G32B32Sfloat, // Normal (vec3)
                Offset = (uint)sizeof(float) * 3,
            },
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 2,
                Format = Format.R32G32B32Sfloat, // Color (vec3)
                Offset = (uint)sizeof(float) * 6,
            },
            new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = 3,
                Format = Format.R32G32Sfloat, // TexCoord (vec2)
                Offset = (uint)sizeof(float) * 9,
            },
        };

        PipelineVertexInputStateCreateInfo vertexInputInfo;
        fixed (VertexInputAttributeDescription* pAttributes = attributeDescriptions)
        {
            vertexInputInfo = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &bindingDescription,
                VertexAttributeDescriptionCount = (uint)attributeDescriptions.Length,
                PVertexAttributeDescriptions = pAttributes,
            };
        }

        PipelineInputAssemblyStateCreateInfo inputAssembly = new()
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = this.Topology,
            PrimitiveRestartEnable = false,
        };

        // Ensure viewport has valid dimensions (Vulkan requires non-zero dimensions)
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

        Rect2D scissor = new() 
        { 
            Offset = { X = 0, Y = 0 }, 
            Extent = new Extent2D(viewportWidth, viewportHeight)
        };

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

        PipelineColorBlendAttachmentState colorBlendAttachment = new()
        {
            ColorWriteMask =
                ColorComponentFlags.RBit
                | ColorComponentFlags.GBit
                | ColorComponentFlags.BBit
                | ColorComponentFlags.ABit,
            // BlendEnable = this.blendEnable,
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

        // Depth stencil state for proper depth testing
        PipelineDepthStencilStateCreateInfo depthStencil = new()
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = this.EnableDepthWrite,
            DepthCompareOp = this.DepthCompareOp,
            DepthBoundsTestEnable = false,
            StencilTestEnable = false,
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
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
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

            if (_vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass) != Result.Success)
            {
                throw new Exception("Failed to create render pass!");
            }
        }

        PipelineLayoutCreateInfo pipelineLayoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = (DescriptorSetLayout*)GetDescriptorSetLayoutPointer(),
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
                PDepthStencilState = &depthStencil,
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

    // Functions for for buffer creation, memory allocation, and data transfer would go here

    protected abstract void CreateDescriptorSetLayout();
    protected abstract void* GetDescriptorSetLayoutPointer(); // Returns pointer to the descriptor set layout
    protected abstract void CreateVertexBuffer();
    protected abstract void CreateIndexBuffer();
    protected abstract void CreateUniformBuffers();
    protected abstract void CreateDescriptorPool();
    protected abstract void CreateDescriptorSets();
    protected abstract void CreateTextureImage();

    protected virtual void CreateImage(
        uint width,
        uint height,
        Format format,
        ImageTiling tiling,
        ImageUsageFlags usage,
        MemoryPropertyFlags properties,
        ref Image image,
        ref DeviceMemory imageMemory
    )
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = format,
            Tiling = tiling,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Image* imagePtr = &image)
        {
            if (_vk.CreateImage(_device, &imageInfo, null, imagePtr) != Result.Success)
            {
                throw new Exception("Failed to create image!");
            }
        }

        MemoryRequirements memRequirements;
        _vk.GetImageMemoryRequirements(_device, image, &memRequirements);

        uint memoryTypeIndex = Vulkan.FindMemoryType(
            _vk,
            _vulkan.PhysicalDevice,
            memRequirements.MemoryTypeBits,
            properties
        );

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };

        fixed (DeviceMemory* memoryPtr = &imageMemory)
        {
            if (_vk.AllocateMemory(_device, &allocInfo, null, memoryPtr) != Result.Success)
            {
                throw new Exception("Failed to allocate image memory!");
            }
        }

        _vk.BindImageMemory(_device, image, imageMemory, 0);
    }

    protected virtual void CreateImageWithSamples(
        uint width,
        uint height,
        Format format,
        ImageTiling tiling,
        ImageUsageFlags usage,
        MemoryPropertyFlags properties,
        SampleCountFlags samples,
        ref Image image,
        ref DeviceMemory imageMemory
    )
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = format,
            Tiling = tiling,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = samples,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Image* imagePtr = &image)
        {
            if (_vk.CreateImage(_device, &imageInfo, null, imagePtr) != Result.Success)
            {
                throw new Exception("Failed to create image with samples!");
            }
        }

        MemoryRequirements memRequirements;
        _vk.GetImageMemoryRequirements(_device, image, &memRequirements);

        uint memoryTypeIndex = Vulkan.FindMemoryType(
            _vk,
            _vulkan.PhysicalDevice,
            memRequirements.MemoryTypeBits,
            properties
        );

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };

        fixed (DeviceMemory* memoryPtr = &imageMemory)
        {
            if (_vk.AllocateMemory(_device, &allocInfo, null, memoryPtr) != Result.Success)
            {
                throw new Exception("Failed to allocate image memory!");
            }
        }

        _vk.BindImageMemory(_device, image, imageMemory, 0);
    }

    protected virtual void CreateMsaaColorResources(uint width, uint height)
    {
        CreateImageWithSamples(
            width,
            height,
            _swapChainImageFormat,
            ImageTiling.Optimal,
            ImageUsageFlags.TransientAttachmentBit | ImageUsageFlags.ColorAttachmentBit,
            MemoryPropertyFlags.DeviceLocalBit,
            RasterizationSamples,
            ref msaaColorImage,
            ref msaaColorMemory
        );

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = msaaColorImage,
            ViewType = ImageViewType.Type2D,
            Format = _swapChainImageFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, &viewInfo, null, out msaaColorView) != Result.Success)
        {
            throw new Exception("Failed to create MSAA color image view!");
        }
    }

    protected virtual void CreateMsaaDepthResources(uint width, uint height)
    {
        Format depthFormat = Format.D32Sfloat;

        CreateImageWithSamples(
            width,
            height,
            depthFormat,
            ImageTiling.Optimal,
            ImageUsageFlags.DepthStencilAttachmentBit,
            MemoryPropertyFlags.DeviceLocalBit,
            RasterizationSamples,
            ref msaaDepthImage,
            ref msaaDepthMemory
        );

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = msaaDepthImage,
            ViewType = ImageViewType.Type2D,
            Format = depthFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, &viewInfo, null, out msaaDepthView) != Result.Success)
        {
            throw new Exception("Failed to create MSAA depth image view!");
        }
    }

    protected virtual void CreateShadowMap()
    {
        uint shadowMapSize = 1024; // Default size, can be overridden
        Format depthFormat = Format.D32Sfloat;

        CreateImage(
            shadowMapSize,
            shadowMapSize,
            depthFormat,
            ImageTiling.Optimal,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            MemoryPropertyFlags.DeviceLocalBit,
            ref shadowImage,
            ref shadowMemory
        );

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = shadowImage,
            ViewType = ImageViewType.Type2D,
            Format = depthFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, &viewInfo, null, out shadowView) != Result.Success)
        {
            throw new Exception("Failed to create shadow map image view!");
        }
    }

    protected virtual void TransitionImageLayout(
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout
    )
    {
        CommandPool commandPool = _vulkan.CreateCommandPool();

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        CommandBuffer commandBuffer;
        if (_vk.AllocateCommandBuffers(_device, &allocInfo, &commandBuffer) != Result.Success)
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
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        PipelineStageFlags sourceStage;
        PipelineStageFlags destinationStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (
            oldLayout == ImageLayout.TransferDstOptimal
            && newLayout == ImageLayout.ShaderReadOnlyOptimal
        )
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new Exception("Unsupported layout transition!");
        }

        _vk.CmdPipelineBarrier(
            commandBuffer,
            sourceStage,
            destinationStage,
            DependencyFlags.None,
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
    }

    protected virtual void CopyBufferToImage(Buffer buffer, Image image, uint width, uint height)
    {
        CommandPool commandPool = _vulkan.CreateCommandPool();

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        CommandBuffer commandBuffer;
        if (_vk.AllocateCommandBuffers(_device, &allocInfo, &commandBuffer) != Result.Success)
        {
            throw new Exception("Failed to allocate command buffer!");
        }

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        _vk.BeginCommandBuffer(commandBuffer, &beginInfo);

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
            buffer,
            image,
            ImageLayout.TransferDstOptimal,
            1,
            &region
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
    }

    protected virtual void CreateTextureImageView()
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = textureImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Srgb, // Assume this format
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, &viewInfo, null, out textureView) != Result.Success)
        {
            throw new Exception("Failed to create texture image view!");
        }
    }

    protected virtual void CreateTextureSampler()
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

        if (_vk.CreateSampler(_device, &samplerInfo, null, out textureSampler) != Result.Success)
        {
            throw new Exception("Failed to create texture sampler!");
        }
    }

    public abstract void RecordDrawCommands(CommandBuffer commandBuffer, int imageIndex);
    public abstract void RecordShadowCommands(CommandBuffer commandBuffer, int imageIndex);
    public abstract void Dispose();

    protected virtual void CleanupResources()
    {
        // Cleanup MSAA resources
        if (msaaColorView.Handle != 0)
            _vk.DestroyImageView(_device, msaaColorView, null);
        if (msaaColorImage.Handle != 0)
            _vk.DestroyImage(_device, msaaColorImage, null);
        if (msaaColorMemory.Handle != 0)
            _vk.FreeMemory(_device, msaaColorMemory, null);

        if (msaaDepthView.Handle != 0)
            _vk.DestroyImageView(_device, msaaDepthView, null);
        if (msaaDepthImage.Handle != 0)
            _vk.DestroyImage(_device, msaaDepthImage, null);
        if (msaaDepthMemory.Handle != 0)
            _vk.FreeMemory(_device, msaaDepthMemory, null);

        // Cleanup shadow map
        if (shadowView.Handle != 0)
            _vk.DestroyImageView(_device, shadowView, null);
        if (shadowImage.Handle != 0)
            _vk.DestroyImage(_device, shadowImage, null);
        if (shadowMemory.Handle != 0)
            _vk.FreeMemory(_device, shadowMemory, null);

        // Cleanup texture
        if (textureSampler.Handle != 0)
            _vk.DestroySampler(_device, textureSampler, null);
        if (textureView.Handle != 0)
            _vk.DestroyImageView(_device, textureView, null);
        if (textureImage.Handle != 0)
            _vk.DestroyImage(_device, textureImage, null);
        if (textureMemory.Handle != 0)
            _vk.FreeMemory(_device, textureMemory, null);

        // Cleanup pipeline resources
        if (_graphicsPipeline.Handle != 0)
            _vk.DestroyPipeline(_device, _graphicsPipeline, null);
        if (_pipelineLayout.Handle != 0)
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        if (_renderPass.Handle != 0)
            _vk.DestroyRenderPass(_device, _renderPass, null);
    }

    public abstract void UpdateVertexBuffer(Array NewVertexData);

    public abstract void UpdateUniformBuffer(int currentImage, UniformBufferObject ubo);

    // public abstract void UpdateIndexBuffer(Array NewIndexData); I don't think this needs to be updated

    // END of buffer-related functions

    public void build()
    {
        CreateVertexBuffer();
        CreateIndexBuffer();
        CreateUniformBuffers();
        CreateTextureImage();
        CreateTextureImageView();
        CreateTextureSampler();
        CreateDescriptorSetLayout();
        CreateDescriptorPool();
        CreateDescriptorSets();
    }

    public Pipeline GetPipeline()
    {
        return _graphicsPipeline;
    }

    public PipelineLayout GetPipelineLayout()
    {
        return _pipelineLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UniformBufferObject
    {
        public Matrix4X4<float> model;
        public Matrix4X4<float> view;
        public Matrix4X4<float> proj;
    }
}
