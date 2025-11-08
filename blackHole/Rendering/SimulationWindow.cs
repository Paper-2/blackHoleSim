using Microsoft.Extensions.ObjectPool;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using blackHole;
using blackHole.Models;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Routing.Tree;
using Silk.NET.Core;
using Silk.NET.Vulkan.Extensions.KHR;

namespace blackHole.Rendering;

unsafe public class SimulationWindow
{
    private IWindow? _window;
    private bool _isRunning;
    private Vk? _vk;
    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private KhrSwapchain? _khrSwapchain;
    private SwapchainKHR _swapchain;
    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private uint _graphicsQueueFamily;

    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;

    // Staging buffer for pixel data
    private Silk.NET.Vulkan.Buffer _stagingBuffer;
    private DeviceMemory _stagingBufferMemory;

    private Random _random = new Random();

    public int Width = 1280;
    public int Height = 720;

    public void Initialize()
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(Width, Height),
            Title = "Simulation of a Black Hole"
        };

        _window = Window.Create(options);

        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Update += OnUpdate;
        _window.Resize += OnResize;
        _window.Closing += OnClosing;
    }

    public void Run()
    {
        _isRunning = true;
        _window?.Run();
    }

    private void OnLoad()
    {
        InitializeVulkanInstance();
        InitializeSurfaceInstance();
        InitializeDevice();
        InitializeLogicalDevice();
        InitializeSwapChain();
        InitializeImageView();
        InitializeCommandPool();
        InitializeCommandBuffer();
        InitializeStagingBuffer();
    }

    private void InitializeVulkanInstance()
    {
        _vk = Vk.GetApi();

        var appName = Marshal.StringToHGlobalAnsi("Black Hole Simulation");
        var engineName = Marshal.StringToHGlobalAnsi("No Engine");

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)appName,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)engineName,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version13
        };

        var extensions = _window!.VkSurface!.GetRequiredExtensions(out var extensionCount);

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = extensionCount,
            PpEnabledExtensionNames = extensions,
            EnabledLayerCount = 0
        };

        if (_vk!.CreateInstance(&createInfo, null, out _instance) != Result.Success)
        {
            throw new Exception("Failed to create Vulkan instance!");
        }

        Marshal.FreeHGlobal(appName);
        Marshal.FreeHGlobal(engineName);
    }

    private void InitializeSurfaceInstance()
    {
        _surface = _window!.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
    }

    private void InitializeDevice()
    {
        uint deviceCount = 0;
        _vk!.EnumeratePhysicalDevices(_instance, &deviceCount, null);

        if (deviceCount == 0) throw new Exception("no devices");

        var devices = new PhysicalDevice[deviceCount];

        fixed (PhysicalDevice* devicesPtr = devices)
        {
            _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicesPtr);
        }

        _physicalDevice = devices[0];

        PhysicalDeviceProperties properties;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, &properties);

        Console.WriteLine($"Using GPU: {Marshal.PtrToStringAnsi((nint)properties.DeviceName)}");
    }

    private void InitializeLogicalDevice()
    {
        uint queueFamilyCount = 0;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, queueFamiliesPtr);
        }

        _graphicsQueueFamily = uint.MaxValue;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if ((queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                _graphicsQueueFamily = i;
                break;
            }
        }

        float queuePriority = 1.0f;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        PhysicalDeviceFeatures deviceFeatures = new();

        var extensionName = Marshal.StringToHGlobalAnsi(KhrSwapchain.ExtensionName);
        byte* extensionNamePtr = (byte*)extensionName;

        DeviceCreateInfo deviceCreateInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo,
            PEnabledFeatures = &deviceFeatures,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = &extensionNamePtr
        };

        if (_vk!.CreateDevice(_physicalDevice, &deviceCreateInfo, null, out _device) != Result.Success)
        {
            throw new Exception("Failed to create logical device!");
        }

        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        Marshal.FreeHGlobal(extensionName);
    }

    private void InitializeSwapChain()
    {
        if (!_vk!.TryGetDeviceExtension(_instance, _device, out _khrSwapchain)) throw new Exception("no swap machine");

        SwapchainCreateInfoKHR createInfoKHR = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = 2,
            ImageFormat = Format.B8G8R8A8Srgb,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = new Extent2D((uint)Width, (uint)Height),
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = SurfaceTransformFlagsKHR.IdentityBitKhr,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
        };

        if (_khrSwapchain!.CreateSwapchain(_device, &createInfoKHR, null, out _swapchain) != Result.Success)
            throw new Exception("Failed to create swapchain!");

        uint imageCount = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* imagesPtr = _swapchainImages)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, imagesPtr);
        }
    }

    private void InitializeImageView()
    {
        _swapchainImageViews = new ImageView[_swapchainImages.Length];

        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = Format.B8G8R8A8Srgb,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            if (_vk!.CreateImageView(_device, &createInfo, null, out _swapchainImageViews[i]) != Result.Success)
            {
                throw new Exception($"Failed to create image view {i}!");
            }
        }
    }

    private void InitializeCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };

        if (_vk!.CreateCommandPool(_device, &poolInfo, null, out _commandPool) != Result.Success)
        {
            throw new Exception("Failed to create command pool!");
        }
    }

    private void InitializeCommandBuffer()
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        fixed (CommandBuffer* commandBufferPtr = &_commandBuffer)
        {
            if (_vk!.AllocateCommandBuffers(_device, &allocInfo, commandBufferPtr) != Result.Success)
            {
                throw new Exception("Failed to allocate command buffer!");
            }
        }
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProperties;
        _vk!.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1 << (int)i)) != 0 &&
                (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
            {
                return i;
            }
        }

        throw new Exception("Failed to find suitable memory type!");
    }

    private void InitializeStagingBuffer()
    {
        ulong bufferSize = (ulong)(Width * Height * 4); // RGBA = 4 bytes per pixel

        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };

        if (_vk!.CreateBuffer(_device, &bufferInfo, null, out _stagingBuffer) != Result.Success)
        {
            throw new Exception("Failed to create staging buffer!");
        }

        MemoryRequirements memRequirements;
        _vk.GetBufferMemoryRequirements(_device, _stagingBuffer, &memRequirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };

        if (_vk.AllocateMemory(_device, &allocInfo, null, out _stagingBufferMemory) != Result.Success)
        {
            throw new Exception("Failed to allocate staging buffer memory!");
        }

        _vk.BindBufferMemory(_device, _stagingBuffer, _stagingBufferMemory, 0);
    }

    private void CleanupSwapChain()
    {
        foreach (var imageView in _swapchainImageViews)
        {
            _vk!.DestroyImageView(_device, imageView, null);
        }

        _khrSwapchain?.DestroySwapchain(_device, _swapchain, null);
    }

    private void OnRender(double deltaTime)
    {
        uint imageIndex = 0;
        _khrSwapchain!.AcquireNextImage(_device, _swapchain, ulong.MaxValue, default, default, &imageIndex);

        // Generate random pixel data
        int pixelCount = Width * Height;
        byte* data;
        _vk!.MapMemory(_device, _stagingBufferMemory, 0, (ulong)(pixelCount * 4), 0, (void**)&data);

        for (int i = 0; i < pixelCount; i++)
        {
            var brightness = (byte)_random.Next(256);
            data[i * 4 + 0] = brightness; // B
            data[i * 4 + 1] = brightness; // G
            data[i * 4 + 2] = brightness; // R
            data[i * 4 + 3] = 255;        // A
        }

        _vk.UnmapMemory(_device, _stagingBufferMemory);

        // Record command buffer
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo);

        // Transition image to transfer destination
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _swapchainImages[imageIndex],
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit
        };

        _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &barrier);

        // Copy buffer to image
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
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)Width, (uint)Height, 1)
        };

        _vk.CmdCopyBufferToImage(_commandBuffer, _stagingBuffer, _swapchainImages[imageIndex],
            ImageLayout.TransferDstOptimal, 1, &region);

        // Transition to present
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.PresentSrcKhr;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = 0;

        _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit,
            0, 0, null, 0, null, 1, &barrier);

        _vk.EndCommandBuffer(_commandBuffer);

        // Submit command buffer
        var commandBuffer = _commandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };

        _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, default);
        _vk.QueueWaitIdle(_graphicsQueue);

        // Present
        var swapchain = _swapchain;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };

        _khrSwapchain.QueuePresent(_graphicsQueue, &presentInfo);

        Console.Write($"\rFPS: {1.0 / deltaTime:F1}");
    }

    private void OnUpdate(double deltaTime)
    {
        // Simulation logic here
    }

    private void OnResize(Vector2D<int> newSize)
    {
        if (newSize.X == 0 || newSize.Y == 0) return;

        Width = newSize.X;
        Height = newSize.Y;

        _vk!.DeviceWaitIdle(_device);

        CleanupSwapChain();

        InitializeSwapChain();
        InitializeImageView();

        _vk.DestroyBuffer(_device, _stagingBuffer, null);
        _vk.FreeMemory(_device, _stagingBufferMemory, null);
        InitializeStagingBuffer();

    }

    private void OnClosing()
    {
        Console.WriteLine("Closing Window");
        _isRunning = false;

        _vk!.DestroyBuffer(_device, _stagingBuffer, null);
        _vk.FreeMemory(_device, _stagingBufferMemory, null);
        _vk.DestroyCommandPool(_device, _commandPool, null);

        foreach (var imageView in _swapchainImageViews)
        {
            _vk!.DestroyImageView(_device, imageView, null);
        }

        _khrSwapchain?.DestroySwapchain(_device, _swapchain, null);
        _vk!.DestroyDevice(_device, null);

        if (_surface.Handle != 0)
        {
            var khrSurface = new KhrSurface(_vk!.Context);
            khrSurface.DestroySurface(_instance, _surface, null);
        }

        if (_instance.Handle != 0)
        {
            _vk!.DestroyInstance(_instance, null);
        }

        _vk!.Dispose();
    }
}