using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using blackHole.Simulation.Entities;
using blackHole.Tools;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace blackHole.Renderer.Vulkan;

public unsafe class RenderWorker : IDisposable
{
    // Thread management
    private Thread? _renderThread;
    private volatile bool _isRunning;
    private readonly BlockingCollection<RenderCommand> _commandQueue = new();

    // Vulkan resources
    private Vulkan? _vulkan;
    private Vk? _vk;
    private Device _device;
    private Queue _graphicsQueue;
    private SwapchainKHR _swapchain;
    private KhrSwapchain? _khrSwapchain;

    private CommandPool _commandPool;
    private CommandBuffer[]? _commandBuffers;

    // Synchronization primitives
    private Semaphore[]? _imageAvailableSemaphores;
    private Semaphore[]? _renderFinishedSemaphores;
    private Fence[]? _inFlightFences;
    private int _currentFrame = 0;
    private const int MAX_FRAMES_IN_FLIGHT = 2;

    private readonly object _queueLock = new();
    private Vertex[]? _latestDustVertices;

    // Rendering pipelines
    private DustPipeline? _dustPipeline;
    private BlackHolePipeline? _blackHolePipeline;
    private SkyboxPipeline? _skyboxPipeline;

    // Framebuffers and image views
    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();

    // Depth buffer
    private Image _depthImage;
    private DeviceMemory _depthImageMemory;
    private ImageView _depthImageView;

    private int _width;
    private int _height;

    public int GetCurrentFrame() => _currentFrame;

    public void Start(Vulkan vulkan)
    {
        _vulkan = vulkan;
        _vk = vulkan.VkAPI;
        _device = vulkan.Device;
        _graphicsQueue = vulkan.GraphicsQueue;
        _swapchain = vulkan.Swapchain;
        _khrSwapchain = vulkan.KhrSwapchain;

        _width = (int)Math.Max(1, vulkan._swapChainExtent.Width);
        _height = (int)Math.Max(1, vulkan._swapChainExtent.Height);

        // Get swapchain images
        uint imageCount = 0;
        _khrSwapchain!.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* imagesPtr = _swapchainImages)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, imagesPtr);
        }

        _commandPool = _vulkan!.CreateCommandPool();
        InitializeSyncObjects();
        AllocateCommandBuffers();

        _isRunning = true;
        _renderThread = new Thread(RenderLoop) { IsBackground = true };
        _renderThread.Start();
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _commandQueue.Add(new StopCommand());
        _renderThread?.Join(5000);
    }

    public void SendCommand(RenderCommand command)
    {
        _commandQueue.Add(command);
    }

    private void RenderLoop()
    {
        var frameTimer = Stopwatch.StartNew();
        int frameCount = 0;

        try
        {
            while (_isRunning)
            {
                ProcessCommands();
                RenderFrame();

                frameCount++;

                if (frameTimer.ElapsedMilliseconds >= 1000)
                {
                    double fps = frameCount / frameTimer.Elapsed.TotalSeconds;
                    Console.WriteLine(
                        $"FPS: {fps:F2} | Frame: {_currentFrame} | Size: {_width}x{_height}"
                    );
                    frameCount = 0;
                    frameTimer.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}\n{ex.StackTrace}");
            _isRunning = false;
        }
    }

    private void ProcessCommands()
    {
        while (_commandQueue.TryTake(out var command, 0))
        {
            switch (command)
            {
                case StopCommand:
                    _isRunning = false;
                    break;
                case UpdateSimulationDataCommand update:
                    UpdatePipelineUniforms(update);
                    break;
                case SetPipelineDataCommand setPipeline:
                    _dustPipeline = setPipeline.DustPipeline;
                    _blackHolePipeline = setPipeline.BlackHolePipeline;
                    _skyboxPipeline = setPipeline.SkyboxPipeline;
                    CreateFramebuffers();
                    break;
                case UpdateDustVerticesCommand updateDust:
                    _latestDustVertices = updateDust.Vertices;
                    break;
            }
        }
    }

    private void UpdatePipelineUniforms(UpdateSimulationDataCommand update)
    {
        if (_dustPipeline != null)
        {
            var ubo = new PipelineBuilder.UniformBufferObject
            {
                model = Matrix4X4<float>.Identity,
                view = update.ViewMatrix,
                proj = update.ProjectionMatrix,
            };
            _dustPipeline.UpdateUniformBuffer(_currentFrame, ubo);
        }

        if (_blackHolePipeline != null)
        {
            var ubo = new PipelineBuilder.UniformBufferObject
            {
                model = Matrix4X4.CreateTranslation(
                    update.SimState.CentralBlackHole.Position.X,
                    update.SimState.CentralBlackHole.Position.Y,
                    update.SimState.CentralBlackHole.Position.Z
                ),
                view = update.ViewMatrix,
                proj = update.ProjectionMatrix,
            };
            _blackHolePipeline.UpdateUniformBuffer(_currentFrame, ubo);
        }

        if (_skyboxPipeline != null)
        {
            var ubo = new PipelineBuilder.UniformBufferObject
            {
                model = Matrix4X4<float>.Identity,
                view = update.ViewMatrix,
                proj = update.ProjectionMatrix,
            };
            _skyboxPipeline.UpdateUniformBuffer(_currentFrame, ubo);
        }
    }

    private void RenderFrame()
    {
        if (_width <= 0 || _height <= 0 || _blackHolePipeline == null || _framebuffers.Length == 0)
        {
            Thread.Sleep(10);
            return;
        }

        _vk!.WaitForFences(_device, 1, in _inFlightFences![_currentFrame], true, ulong.MaxValue);

        // After fence wait, this frame's resources are safe to modify
        // Update dust vertex buffer for the current frame (now safe because GPU finished with it)
        if (_dustPipeline != null && _latestDustVertices != null)
        {
            _dustPipeline.UpdateVertexBuffer(_latestDustVertices, _currentFrame);
        }

        uint imageIndex;
        Result result = _khrSwapchain!.AcquireNextImage(
            _device,
            _swapchain,
            ulong.MaxValue,
            _imageAvailableSemaphores![_currentFrame],
            default,
            &imageIndex
        );

        if (result == Result.ErrorOutOfDateKhr)
            return;

        _vk.ResetFences(_device, 1, in _inFlightFences[_currentFrame]);

        CommandBuffer cmd = _commandBuffers![_currentFrame];

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        _vk.BeginCommandBuffer(cmd, &beginInfo);

        if (_blackHolePipeline != null && _framebuffers.Length > imageIndex)
        {
            ClearValue[] clearValues =
            {
                new ClearValue
                {
                    Color = new ClearColorValue
                    {
                        Float32_0 = 0.0f,
                        Float32_1 = 0.0f,
                        Float32_2 = 0.0f,
                        Float32_3 = 1.0f,
                    },
                },
                new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 },
                },
            };

            fixed (ClearValue* clearValuesPtr = clearValues)
            {
                RenderPassBeginInfo renderPassInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _blackHolePipeline._renderPass,
                    Framebuffer = _framebuffers[imageIndex],
                    RenderArea = new Rect2D
                    {
                        Offset = new Offset2D(0, 0),
                        Extent = new Extent2D((uint)_width, (uint)_height),
                    },
                    ClearValueCount = 2,
                    PClearValues = clearValuesPtr,
                };

                _vk.CmdBeginRenderPass(cmd, &renderPassInfo, SubpassContents.Inline);
            }

            // Set viewport and scissor
            Viewport viewport = new()
            {
                X = 0.0f,
                Y = 0.0f,
                Width = (uint)_width,
                Height = (uint)_height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f,
            };
            _vk.CmdSetViewport(cmd, 0, 1, &viewport);

            Rect2D scissor = new()
            {
                Offset = new Offset2D(0, 0),
                Extent = new Extent2D((uint)_width, (uint)_height),
            };
            _vk.CmdSetScissor(cmd, 0, 1, &scissor);

            // Render in order: Black Hole -> Dust -> Skybox (background rendered last with depth test)
            _blackHolePipeline.RecordDrawCommands(cmd, _currentFrame);

            if (_dustPipeline != null)
            {
                _dustPipeline.RecordDrawCommands(cmd, _currentFrame);
            }

            if (_skyboxPipeline != null)
            {
                _skyboxPipeline.RecordDrawCommands(cmd, _currentFrame);
            }

            _vk.CmdEndRenderPass(cmd);
        }

        _vk.EndCommandBuffer(cmd);

        // Submit
        Semaphore waitSemaphore = _imageAvailableSemaphores[_currentFrame];
        Semaphore signalSemaphore = _renderFinishedSemaphores![_currentFrame];
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        lock (_queueLock)
        {
            _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, _inFlightFences[_currentFrame]);
        }

        // Present
        SwapchainKHR swapchain = _swapchain;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        lock (_queueLock)
        {
            _khrSwapchain.QueuePresent(_graphicsQueue, &presentInfo);
        }

        _currentFrame = (_currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
    }

    private void AllocateCommandBuffers()
    {
        _commandBuffers = new CommandBuffer[MAX_FRAMES_IN_FLIGHT];

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MAX_FRAMES_IN_FLIGHT,
        };

        fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
        {
            if (
                _vk!.AllocateCommandBuffers(_device, &allocInfo, commandBuffersPtr)
                != Result.Success
            )
            {
                throw new Exception("Failed to allocate command buffers!");
            }
        }
    }

    private void InitializeSyncObjects()
    {
        _imageAvailableSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        _renderFinishedSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        _inFlightFences = new Fence[MAX_FRAMES_IN_FLIGHT];

        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            if (
                _vk!.CreateSemaphore(
                    _device,
                    &semaphoreInfo,
                    null,
                    out _imageAvailableSemaphores[i]
                ) != Result.Success
                || _vk.CreateSemaphore(
                    _device,
                    &semaphoreInfo,
                    null,
                    out _renderFinishedSemaphores[i]
                ) != Result.Success
                || _vk.CreateFence(_device, &fenceInfo, null, out _inFlightFences[i])
                    != Result.Success
            )
            {
                throw new Exception($"Failed to create synchronization objects for frame {i}!");
            }
        }
    }

    private void CreateFramebuffers()
    {
        if (
            _blackHolePipeline == null
            || _swapchainImages.Length == 0
            || _width <= 0
            || _height <= 0
        )
            return;

        // Create depth image using Vulkan API directly
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)_width, (uint)_height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.D32Sfloat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        if (_vk!.CreateImage(_device, &imageInfo, null, out _depthImage) != Result.Success)
        {
            throw new Exception("Failed to create depth image!");
        }

        // Allocate memory for depth image
        MemoryRequirements memRequirements;
        _vk.GetImageMemoryRequirements(_device, _depthImage, &memRequirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = Vulkan.FindMemoryType(
                _vk,
                _vulkan!.PhysicalDevice,
                memRequirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit
            ),
        };

        if (_vk.AllocateMemory(_device, &allocInfo, null, out _depthImageMemory) != Result.Success)
        {
            throw new Exception("Failed to allocate depth image memory!");
        }

        _vk.BindImageMemory(_device, _depthImage, _depthImageMemory, 0);

        // Create depth image view
        ImageViewCreateInfo depthViewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _depthImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.D32Sfloat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (
            _vk!.CreateImageView(_device, &depthViewInfo, null, out _depthImageView)
            != Result.Success
        )
        {
            throw new Exception("Failed to create depth image view!");
        }

        _swapchainImageViews = new ImageView[_swapchainImages.Length];
        _framebuffers = new Framebuffer[_swapchainImages.Length];

        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _vulkan!._swapChainImageFormat,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            if (
                _vk!.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[i])
                != Result.Success
            )
            {
                throw new Exception($"Failed to create image view {i}!");
            }

            ImageView[] attachments = { _swapchainImageViews[i], _depthImageView };
            fixed (ImageView* attachmentsPtr = attachments)
            {
                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _blackHolePipeline._renderPass,
                    AttachmentCount = 2,
                    PAttachments = attachmentsPtr,
                    Width = (uint)_width,
                    Height = (uint)_height,
                    Layers = 1,
                };

                if (
                    _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[i])
                    != Result.Success
                )
                {
                    throw new Exception($"Failed to create framebuffer {i}!");
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();

        if (_vk != null && _device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);

            // Destroy framebuffers and image views
            for (int i = 0; i < _framebuffers.Length; i++)
            {
                if (_framebuffers[i].Handle != 0)
                    _vk.DestroyFramebuffer(_device, _framebuffers[i], null);
                if (_swapchainImageViews.Length > i && _swapchainImageViews[i].Handle != 0)
                    _vk.DestroyImageView(_device, _swapchainImageViews[i], null);
            }

            // Destroy depth resources
            if (_depthImageView.Handle != 0)
                _vk.DestroyImageView(_device, _depthImageView, null);
            if (_depthImage.Handle != 0)
                _vk.DestroyImage(_device, _depthImage, null);
            if (_depthImageMemory.Handle != 0)
                _vk.FreeMemory(_device, _depthImageMemory, null);

            // Destroy pipeline data
            _dustPipeline?.Dispose();
            _blackHolePipeline?.Dispose();
            _skyboxPipeline?.Dispose();

            // Destroy sync objects
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                if (_imageAvailableSemaphores != null && _imageAvailableSemaphores[i].Handle != 0)
                    _vk.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                if (_renderFinishedSemaphores != null && _renderFinishedSemaphores[i].Handle != 0)
                    _vk.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                if (_inFlightFences != null && _inFlightFences[i].Handle != 0)
                    _vk.DestroyFence(_device, _inFlightFences[i], null);
            }

            // Free command buffers
            if (_commandBuffers != null && _commandPool.Handle != 0)
            {
                fixed (CommandBuffer* cmdPtr = _commandBuffers)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        (uint)_commandBuffers.Length,
                        cmdPtr
                    );
                }
            }

            // Destroy command pool
            if (_commandPool.Handle != 0)
                _vk.DestroyCommandPool(_device, _commandPool, null);
        }
    }
}

// Command patterns for render thread communication
public abstract record RenderCommand;

public record StopCommand() : RenderCommand;

public record UpdateSimulationDataCommand(
    SimState SimState,
    Matrix4X4<float> ViewMatrix,
    Matrix4X4<float> ProjectionMatrix
) : RenderCommand;

public record SetPipelineDataCommand(
    DustPipeline DustPipeline,
    BlackHolePipeline BlackHolePipeline,
    SkyboxPipeline SkyboxPipeline
) : RenderCommand;

public record UpdateDustVerticesCommand(Vertex[] Vertices) : RenderCommand;
