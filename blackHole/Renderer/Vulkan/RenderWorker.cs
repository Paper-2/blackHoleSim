using System.Collections.Concurrent;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace blackHole.Renderer.Vulkan;

public unsafe class RenderWorker : IDisposable
{
    // Thread management
    private Thread? _renderThread;
    private volatile bool _isRunning;
    private readonly ManualResetEventSlim _renderSignal = new(false);

    // Message queue for commands from main thread
    private readonly BlockingCollection<RenderCommand> _commandQueue = new();

    // Vulkan resources (moved from SimulationWindow)
    private Vulkan? _vulkan; // Made by SimulationWindow
    private Vk? _vk;
    private Instance _instance;
    private Device _device;
    private Queue _graphicsQueue;
    private SwapchainKHR _swapchain;
    private uint _graphicsQueueFamily;
    private KhrSwapchain? _khrSwapchain;
    private SurfaceKHR _surface;

    private CommandPool _commandPool; // Owned by this thread!
    private CommandBuffer[]? _commandBuffers; // Per-frame

    // Synchronization primitives (critical for multi-frame)
    private Semaphore[]? _imageAvailableSemaphores;
    private Semaphore[]? _renderFinishedSemaphores;
    private Fence[]? _inFlightFences;
    private int _currentFrame = 0;
    private const int MAX_FRAMES_IN_FLIGHT = 2;

    // Lock for queue operations
    private readonly object _queueLock = new();

    // Staging buffer and rendering data
    private Silk.NET.Vulkan.Buffer _stagingBuffer;
    private DeviceMemory _stagingBufferMemory;
    private Image[] _swapchainImages = Array.Empty<Image>();
    private PhysicalDevice _physicalDevice;
    private Random _random = new Random();
    private int _width;
    private int _height;
    private int _frameSkip = 0;

    private StatsConsole? _statsConsole;

    private int _pendingWidth = -1;
    private int _pendingHeight = -1;
    private DateTime _lastResizeTime = DateTime.MinValue;
    private const int RESIZE_DEBOUNCE_MS = 200; // Wait 200ms after last resize
    private int _lastRecreatedWidth = 0;
    private int _lastRecreatedHeight = 0;

    private const int RESIZE_THRESHOLD = 50; // Only recreate if size changed by 50+ pixels

    public void Start(Vulkan vulkan)
    {
        _vulkan = vulkan;
        _vk = vulkan.VkAPI;
        _instance = vulkan.VulkanInstance;
        _physicalDevice = vulkan.PhysicalDevice;
        _device = vulkan.Device;
        _graphicsQueue = vulkan.GraphicsQueue;
        _swapchain = vulkan.Swapchain;
        _graphicsQueueFamily = vulkan.GraphicsQueueFamily;
        _khrSwapchain = vulkan.KhrSwapchain;
        _width = 1280; // default or from somewhere
        _height = 720;

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
        InitializeStagingBuffer();

        _statsConsole = new StatsConsole();
        _statsConsole.Start();
        _statsConsole.WriteLine("Render worker started");

        // Start render thread
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
        var frameTimer = System.Diagnostics.Stopwatch.StartNew();
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
                    _statsConsole?.WriteLine(
                        $"FPS: {fps:F2} | Frame: {_currentFrame} | Size: {_width}x{_height}"
                    );
                    frameCount = 0;
                    frameTimer.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            _statsConsole?.WriteLine($"ERROR: {ex.Message}");
            _isRunning = false;
        }
    }

    private void ProcessCommands()
    {
        while (_commandQueue.TryTake(out var command, 0)) // Non-blocking
        {
            switch (command)
            {
                case ResizeCommand resize:
                    _pendingWidth = resize.Width;
                    _pendingHeight = resize.Height;
                    _lastResizeTime = DateTime.Now;

                    // Recreate immediately if size change is large
                    int widthDiff = Math.Abs(resize.Width - _lastRecreatedWidth);
                    int heightDiff = Math.Abs(resize.Height - _lastRecreatedHeight);

                    if (widthDiff > RESIZE_THRESHOLD || heightDiff > RESIZE_THRESHOLD)
                    {
                        Vulkan.Instance.RecreateSwapchain((uint)resize.Width, (uint)resize.Height);

                        // Update RenderWorker's state
                        _width = resize.Width;
                        _height = resize.Height;
                        _swapchain = Vulkan.Instance.Swapchain;
                        _khrSwapchain = Vulkan.Instance.KhrSwapchain;

                // Recreate staging buffer
                _vk!.DestroyBuffer(_device, _stagingBuffer, null);
                _vk.FreeMemory(_device, _stagingBufferMemory, null);
                _stagingBuffer = default;
                _stagingBufferMemory = default;
                InitializeStagingBuffer();                        // Update swapchain images
                        uint imageCount = 0;
                        _khrSwapchain!.GetSwapchainImages(_device, _swapchain, &imageCount, null);
                        _swapchainImages = new Image[imageCount];
                        fixed (Image* imagesPtr = _swapchainImages)
                        {
                            _khrSwapchain.GetSwapchainImages(
                                _device,
                                _swapchain,
                                &imageCount,
                                imagesPtr
                            );
                        }

                        _lastRecreatedWidth = resize.Width;
                        _lastRecreatedHeight = resize.Height;
                        _pendingWidth = -1;
                        _pendingHeight = -1;
                    }
                    break;
                case StopCommand:
                    _isRunning = false;
                    break;
                case UpdateSimulationData update:
                    // TODO: Update uniform buffers
                    break;
            }
        }

        // Handle debounced resize (final adjustment)
        if (_pendingWidth > 0 && _pendingHeight > 0)
        {
            var timeSinceResize = (DateTime.Now - _lastResizeTime).TotalMilliseconds;
            if (timeSinceResize >= RESIZE_DEBOUNCE_MS)
            {
                Vulkan.Instance.RecreateSwapchain((uint)_pendingWidth, (uint)_pendingHeight);

                // Update RenderWorker's state
                _width = _pendingWidth;
                _height = _pendingHeight;
                _swapchain = Vulkan.Instance.Swapchain;
                _khrSwapchain = Vulkan.Instance.KhrSwapchain;

                // Recreate staging buffer
                _vk!.DestroyBuffer(_device, _stagingBuffer, null);
                _vk.FreeMemory(_device, _stagingBufferMemory, null);
                _stagingBuffer = default;
                _stagingBufferMemory = default;
                InitializeStagingBuffer();

                // Update swapchain images
                uint imageCount = 0;
                _khrSwapchain!.GetSwapchainImages(_device, _swapchain, &imageCount, null);
                _swapchainImages = new Image[imageCount];
                fixed (Image* imagesPtr = _swapchainImages)
                {
                    _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, imagesPtr);
                }

                _lastRecreatedWidth = _pendingWidth;
                _lastRecreatedHeight = _pendingHeight;
                _pendingWidth = -1;
                _pendingHeight = -1;
            }
        }
    }

    private void RenderFrame()
    {
        // Wait for previous frame
        _vk!.WaitForFences(_device, 1, in _inFlightFences![_currentFrame], true, ulong.MaxValue);

        // Acquire image
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
        {
            return; // Need swapchain recreation
        }

        _vk.ResetFences(_device, 1, in _inFlightFences[_currentFrame]);

        // Generate static - but ONLY update once per 1 frames to reduce CPU load
        if (_frameSkip++ % 1 == 0)
        {
            void* data;
            _vk.MapMemory(
                _device,
                _stagingBufferMemory,
                0,
                (ulong)(_width * _height * 4),
                0,
                &data
            );
            var pixelData = new Span<byte>(data, _width * _height * 4);

            int seed = Environment.TickCount;
            unsafe
            {
                ulong* ptr = (ulong*)data;
                int count = pixelData.Length / 8;
                for (int i = 0; i < count; i++)
                {
                    // Generate 8 bytes at once
                    ptr[i] = (ulong)i * 2654435761u + (ulong)seed;
                }
            }

            _vk.UnmapMemory(_device, _stagingBufferMemory);
        }

        // Record command buffer
        CommandBuffer cmd = _commandBuffers![_currentFrame];

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        _vk.BeginCommandBuffer(cmd, &beginInfo);

        // Transition image to transfer dst
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
                LayerCount = 1,
            },
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
        };

        _vk.CmdPipelineBarrier(
            cmd,
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
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)_width, (uint)_height, 1),
        };

        _vk.CmdCopyBufferToImage(
            cmd,
            _stagingBuffer,
            _swapchainImages[imageIndex],
            ImageLayout.TransferDstOptimal,
            1,
            &region
        );

        // Transition to present
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.PresentSrcKhr;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = 0;

        _vk.CmdPipelineBarrier(
            cmd,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.BottomOfPipeBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &barrier
        );

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

    private void InitializeStagingBuffer()
    {
        ulong bufferSize = (ulong)(_width * _height * 4);

        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
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
            MemoryTypeIndex = Vulkan.FindMemoryType(
                _vk,
                _physicalDevice,
                memRequirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            ),
        };

        if (
            _vk.AllocateMemory(_device, &allocInfo, null, out _stagingBufferMemory)
            != Result.Success
        )
        {
            throw new Exception("Failed to allocate staging buffer memory!");
        }

        _vk.BindBufferMemory(_device, _stagingBuffer, _stagingBufferMemory, 0);
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
                throw new Exception("Failed make sync objects");
            }
        }
    }

    public void Dispose()
    {
        Stop(); // Stop render thread first

        if (_vk != null && _device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device); // Ensure GPU is idle

            // Destroy staging buffer
            if (_stagingBuffer.Handle != 0)
                _vk.DestroyBuffer(_device, _stagingBuffer, null);
            if (_stagingBufferMemory.Handle != 0)
                _vk.FreeMemory(_device, _stagingBufferMemory, null);

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
                    _vk.FreeCommandBuffers(_device, _commandPool, (uint)_commandBuffers.Length, cmdPtr);
                }
            }

            // Destroy command pool
            if (_commandPool.Handle != 0)
                _vk.DestroyCommandPool(_device, _commandPool, null);
        }
    }
}

public abstract record RenderCommand;

public record ResizeCommand(int Width, int Height) : RenderCommand;

public record StopCommand() : RenderCommand;

public record UpdateSimulationData( /* your data */
) : RenderCommand;

public class StatsConsole : IDisposable
{
    private Process? _consoleProcess;
    private StreamWriter? _logWriter;
    private string _logFile = string.Empty;
    private volatile bool _isReady = false;
    private Queue<string> _pendingMessages = new();

    public void Start()
    {
        _logFile = Path.Combine(Path.GetTempPath(), $"blackhole_stats_{Guid.NewGuid()}.log");
        _logWriter = new StreamWriter(_logFile, false) { AutoFlush = true };

        WriteLine("Black Hole Sim - Stats Monitor");
        WriteLine("================================");
        WriteLine("");

        // Start PowerShell with Get-Content -Wait
        _consoleProcess = Process.Start(
            new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoExit -Command \"Write-Host 'Stats Monitor Started' -ForegroundColor Green; Get-Content -Path '{_logFile}' -Wait\"",
                UseShellExecute = true,
                CreateNoWindow = false,
            }
        );

        _isReady = true;

        // Write any pending messages
        lock (_pendingMessages)
        {
            while (_pendingMessages.Count > 0)
            {
                var msg = _pendingMessages.Dequeue();
                _logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            }
        }
    }

    public void WriteLine(string message)
    {
        if (_isReady && _logWriter != null)
        {
            _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
        else
        {
            lock (_pendingMessages)
            {
                _pendingMessages.Enqueue(message);
            }
        }
    }

    public void Dispose()
    {
        _logWriter?.Dispose();
        _consoleProcess?.Kill();
        _consoleProcess?.Dispose();

        // Clean up log file after a delay
        Task.Delay(1000)
            .ContinueWith(_ =>
            {
                try
                {
                    if (File.Exists(_logFile))
                        File.Delete(_logFile);
                }
                catch { }
            });
    }
}
