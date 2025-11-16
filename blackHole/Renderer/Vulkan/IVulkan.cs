using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace blackHole.Renderer.Vulkan;

public interface IVulkan
{
    void Initialize(IWindow window, uint width, uint height);
    void CreateInstance();
    void CreateDevice();
    void CreateSwapchain(uint width, uint height);

    void RecreateSwapchain(uint width, uint height);

    void Render(RenderWorker renderWorker);

    void setupDebugMessenger();

    void createSurface();

    void pickPhysicalDevice();

    void createLogicalDevice();

    void createImageViews();

    void createGraphicsPipeline();

    void Cleanup();

    Instance GetInstance();
    CommandPool CreateCommandPool(); // Exposed for RenderWorker and company
}

public unsafe class Vulkan : IVulkan
{
    private static readonly Lazy<Vulkan> _instance = new Lazy<Vulkan>(() => new Vulkan());
    public KhrSwapchain? KhrSwapchain => _khrSwapchain;

    public static Vulkan Instance => _instance.Value;

    private Vk? _vk;
    public Vk? VkAPI => _vk;
    private Instance _vulkanInstance;
    public Instance VulkanInstance => _vulkanInstance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    private Device _device;
    public Device Device => _device;
    private Queue _graphicsQueue;
    public Queue GraphicsQueue => _graphicsQueue;
    private KhrSwapchain? _khrSwapchain;
    private SwapchainKHR _swapchain;
    public SwapchainKHR Swapchain => _swapchain;

    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Extent2D _swapChainExtent;
    private Format _swapChainImageFormat;
    private PipelineLayout _pipelineLayout;
    private RenderPass _renderPass;
    private Pipeline _graphicsPipeline;
    private uint _graphicsQueueFamily;
    public uint GraphicsQueueFamily => _graphicsQueueFamily;

    private IWindow? _window;
    private RenderWorker? _renderWorker;

    private Vulkan()
    {
        // singleton class
        // leave empty as to not initialize
        // more than one vulkan context for now.
        // I read somewhere that multiple instances
        // are extremely rare in practice.
    }

    public void Initialize(IWindow window, uint width, uint height)
    {
        _window = window;

        // Initialize Vulkan API
        _vk = Vk.GetApi();
        CreateInstance();
        CreateDevice();

        CreateSwapchain(width, height);

        InitializeSwapchainImages();

        //start rendering
    }

    public void CreateInstance()
    {
        // TODO: move this part below somewhere else to have more flexibiltuy
        var appName = Marshal.StringToHGlobalAnsi("Black Hole Simulation");
        var engineName = Marshal.StringToHGlobalAnsi("No Engine");

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)appName,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)engineName,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version13,
        };

        var extensions = _window!.VkSurface!.GetRequiredExtensions(out var extensionCount);

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = extensionCount,
            PpEnabledExtensionNames = extensions,
            EnabledLayerCount = 0,
        };

        if (_vk!.CreateInstance(&createInfo, null, out _vulkanInstance) != Result.Success)
        {
            throw new Exception("Failed to create Vulkan instance!");
        }

        Marshal.FreeHGlobal(appName);
        Marshal.FreeHGlobal(engineName);

        // Create surface for the window after instance creation other wise it will fail
        _surface = _window!
            .VkSurface!.Create<AllocationCallbacks>(_vulkanInstance.ToHandle(), null)
            .ToSurface();
    }

    public void CreateDevice()
    {
        // Initialize physical device
        uint deviceCount = 0;
        _vk!.EnumeratePhysicalDevices(_vulkanInstance, &deviceCount, null);

        if (deviceCount == 0)
            throw new Exception("No Vulkan devices found");

        var devices = new PhysicalDevice[deviceCount];

        fixed (PhysicalDevice* devicesPtr = devices)
        {
            _vk.EnumeratePhysicalDevices(_vulkanInstance, &deviceCount, devicesPtr);
        }

        _physicalDevice = devices[0];

        PhysicalDeviceProperties properties;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, &properties);

        Console.WriteLine($"Using GPU: {Marshal.PtrToStringAnsi((nint)properties.DeviceName)}");

        // Initialize logical device
        uint queueFamilyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(
                _physicalDevice,
                &queueFamilyCount,
                queueFamiliesPtr
            );
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

        if (_graphicsQueueFamily == uint.MaxValue)
        {
            throw new Exception("No graphics queue family found!");
        }

        float queuePriority = 1.0f;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority,
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
            PpEnabledExtensionNames = &extensionNamePtr,
        };

        if (
            _vk!.CreateDevice(_physicalDevice, &deviceCreateInfo, null, out _device)
            != Result.Success
        )
        {
            throw new Exception("Failed to create logical device!");
        }

        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        Marshal.FreeHGlobal(extensionName);
    }

    public void CreateSwapchain(uint width, uint height)
    {
        // Create swapchain
        if (!_vk!.TryGetDeviceExtension(_vulkanInstance, _device, out _khrSwapchain))
            throw new Exception("KHR_swapchain extension not available");

        SwapchainCreateInfoKHR createInfoKHR = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = 2,
            ImageFormat = Format.B8G8R8A8Srgb,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = new Extent2D(width, height),
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = SurfaceTransformFlagsKHR.IdentityBitKhr,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
        };

        if (
            _khrSwapchain!.CreateSwapchain(_device, &createInfoKHR, null, out _swapchain)
            != Result.Success
        )
            throw new Exception("Failed to create swapchain!");

        // Get swapchain images
        InitializeSwapchainImages();

        // Create image views
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
                _vk!.CreateImageView(_device, &createInfo, null, out _swapchainImageViews[i])
                != Result.Success
            )
            {
                throw new Exception($"Failed to create image view {i}!");
            }
        }
    }

    private void InitializeSwapchainImages()
    {
        uint imageCount = 0;
        _khrSwapchain!.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* imagesPtr = _swapchainImages)
        {
            _khrSwapchain!.GetSwapchainImages(_device, _swapchain, &imageCount, imagesPtr);
        }
    }

    public void Render(RenderWorker renderWorker)
    {
        _renderWorker = renderWorker;

        // Start render worker with Vulkan resources
        if (_renderWorker != null && _vk != null)
        {
            _renderWorker.Start(this);
        }
    }

    public Instance GetInstance()
    {
        return _vulkanInstance;
    }

    public void Cleanup()
    {
        // Cleanup resources
        if (_renderWorker != null)
        {
            _renderWorker.Stop();
            _renderWorker.Dispose();
        }

        foreach (var imageView in _swapchainImageViews)
        {
            if (_vk != null && _device.Handle != 0)
                _vk.DestroyImageView(_device, imageView, null);
        }

        CleanupSwapchain();

        if (_graphicsPipeline.Handle != 0)
            _vk!.DestroyPipeline(_device, _graphicsPipeline, null);
        if (_pipelineLayout.Handle != 0)
            _vk!.DestroyPipelineLayout(_device, _pipelineLayout, null);
        if (_renderPass.Handle != 0)
            _vk!.DestroyRenderPass(_device, _renderPass, null);

        if (_vk != null && _device.Handle != 0)
            _vk.DestroyDevice(_device, null);

        if (_surface.Handle != 0)
        {
            var khrSurface = new KhrSurface(_vk!.Context);
            khrSurface.DestroySurface(_vulkanInstance, _surface, null);
        }

        if (_vk != null && _vulkanInstance.Handle != 0)
            _vk.DestroyInstance(_vulkanInstance, null);

        _vk?.Dispose();
    }

    private void CleanupSwapchain()
    {
        if (_khrSwapchain != null && _device.Handle != 0)
            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
    }

    /// <summary>
    /// Recreates the swapchain with the given width and height.
    /// Meant to be called on window resize.
    /// </summary>
    /// <param name="width"></param>
    /// <param name="height"></param>
    public void RecreateSwapchain(uint width, uint height)
    {
        CleanupSwapchain();
        CreateSwapchain(width, height);
    }

    public CommandPool CreateCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        if (_vk!.CreateCommandPool(_device, &poolInfo, null, out var commandPool) != Result.Success)
        {
            throw new Exception("Failed to create command pool!");
        }

        return commandPool;
    }

    public static uint FindMemoryType(
        Vk vk,
        PhysicalDevice physicalDevice,
        uint typeFilter,
        MemoryPropertyFlags properties
    )
    {
        PhysicalDeviceMemoryProperties memProperties;
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, &memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if (
                (typeFilter & (1 << (int)i)) != 0
                && (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties
            )
            {
                return i;
            }
        }

        throw new Exception("Failed to find suitable memory type!");
    }

    public void setupDebugMessenger()
    {
        // Debug messenger setup can be added here if validation layers are enabled
    }

    public void createSurface()
    {
        _surface = _window!
            .VkSurface!.Create<AllocationCallbacks>(_vulkanInstance.ToHandle(), null)
            .ToSurface();
    }

    public void pickPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk!.EnumeratePhysicalDevices(_vulkanInstance, &deviceCount, null);

        if (deviceCount == 0)
            throw new Exception("No Vulkan devices found");

        var devices = new PhysicalDevice[deviceCount];

        fixed (PhysicalDevice* devicesPtr = devices)
        {
            _vk.EnumeratePhysicalDevices(_vulkanInstance, &deviceCount, devicesPtr);
        }

        _physicalDevice = devices[0];

        PhysicalDeviceProperties properties;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, &properties);

        // Console.WriteLine($"Using GPU: {Marshal.PtrToStringAnsi((nint)properties.DeviceName)}");
    }

    public void createLogicalDevice()
    {
        uint queueFamilyCount = 0;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            _vk!.GetPhysicalDeviceQueueFamilyProperties(
                _physicalDevice,
                &queueFamilyCount,
                queueFamiliesPtr
            );
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

        if (_graphicsQueueFamily == uint.MaxValue)
        {
            throw new Exception("No graphics queue family found!");
        }

        float queuePriority = 1.0f;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority,
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
            PpEnabledExtensionNames = &extensionNamePtr,
        };

        if (
            _vk!.CreateDevice(_physicalDevice, &deviceCreateInfo, null, out _device)
            != Result.Success
        )
        {
            throw new Exception("Failed to create logical device!");
        }

        _vk!.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        Marshal.FreeHGlobal(extensionName);
    }

    public void createSwapChain()
    {
        if (!_vk!.TryGetDeviceExtension(_vulkanInstance, _device, out _khrSwapchain))
            throw new Exception("KHR_swapchain extension not available");

        SwapchainCreateInfoKHR createInfoKHR = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = 2,
            ImageFormat = Format.B8G8R8A8Srgb,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = new Extent2D((uint)_window!.Size.X, (uint)_window!.Size.Y),
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = SurfaceTransformFlagsKHR.IdentityBitKhr,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
        };

        if (
            _khrSwapchain!.CreateSwapchain(_device, &createInfoKHR, null, out _swapchain)
            != Result.Success
        )
            throw new Exception("Failed to create swapchain!");

        _swapChainExtent = createInfoKHR.ImageExtent;
        _swapChainImageFormat = createInfoKHR.ImageFormat;
    }

    public void createImageViews()
    {
        _swapchainImageViews = new ImageView[_swapchainImages.Length];

        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapChainImageFormat,
                Components =
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            if (
                _vk!.CreateImageView(_device, &createInfo, null, out _swapchainImageViews[i])
                != Result.Success
            )
            {
                throw new Exception("Failed to create image views!");
            }
        }
    }

    public void createGraphicsPipeline()
    {
        Pipelines pipelines = new Pipelines(_vk!, _device, _swapChainExtent, _swapChainImageFormat);
        pipelines.CreateGraphicsPipeline("shaders/shader.vert", "shaders/shader.frag");
    }
}
