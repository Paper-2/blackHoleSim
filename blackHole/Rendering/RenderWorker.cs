using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Microsoft.Extensions.ObjectPool;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using blackHole;
using blackHole.Models;
using Microsoft.AspNetCore.Routing.Tree;
using Silk.NET.Core;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Collections.Concurrent;
using Semaphore = Silk.NET.Vulkan.Semaphore;


namespace blackHole.Rendering;


public unsafe class RenderWorker : IDisposable
{
    // Thread management
    private Thread? _renderThread;
    private volatile bool _isRunning;
    private readonly ManualResetEventSlim _renderSignal = new(false);

    // Message queue for commands from main thread
    private readonly BlockingCollection<RenderCommand> _commandQueue = new();

    // Vulkan resources (moved from SimulationWindow)
    private Vk? _vk; // Made by SimulationWindow
    private Instance _instance; // Made by SimulationWindow
    private Device _device; // Made by SimulationWindow
    private Queue _graphicsQueue; // Made by SimulationWindow
    private SwapchainKHR _swapchain; // Made by SimulationWindow
    private uint _graphicsQueueFamily;
    private KhrSwapchain? _khrSwapchain;

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

    public void Start(
        Vk vk, Instance instance, Device device, Queue graphicsQueue, SwapchainKHR swapchain, uint graphicsQueueFamily)
    {
        _vk = vk;
        _instance = instance;
        _device = device;
        _graphicsQueue = graphicsQueue;
        _swapchain = swapchain;
        _graphicsQueueFamily = graphicsQueueFamily;

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
        {
            throw new Exception("Failed to get KHR_swapchain extension");
        }

        InitializeCommandPool();
        InitializeSyncObjects();
        AllocateCommandBuffers();

        // Start the render thread
        _isRunning = true;
        _renderThread = new Thread(RenderLoop) { IsBackground = true };
        _renderThread.Start();
    }
    public void Stop()
    {
        if (!_isRunning) return;

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
        try
        {
            while (_isRunning)
            {
                ProcessCommands();
                RenderFrame();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Render thread error: {ex}");
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
                    RecreateSwapchain(resize.Width, resize.Height);
                    break;
                case StopCommand:
                    _isRunning = false;
                    break;
                case UpdateSimulationData update:
                    // TODO: Update uniform buffers
                    break;
            }
        }
    }
    private void RenderFrame()
    {
        _vk!.WaitForFences(_device, 1, in _inFlightFences![_currentFrame], true, ulong.MaxValue);
        _vk.ResetFences(_device, 1, in _inFlightFences[_currentFrame]);

        uint imageIndex;
        Result result = _khrSwapchain!.AcquireNextImage(
            _device, _swapchain,
            ulong.MaxValue,
            _imageAvailableSemaphores![_currentFrame],
            default,
            &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            // Need to recreate swapchain
            return;
        }
        // 3. Record command buffer
        CommandBuffer cmd = _commandBuffers![_currentFrame];

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        _vk!.BeginCommandBuffer(cmd, &beginInfo);

        // TODO: add render pass whatever that means.

        _vk.EndCommandBuffer(cmd);

        // 4. Submit to GPU queue
        Semaphore waitSemaphore = _imageAvailableSemaphores[_currentFrame];
        Semaphore signalSemaphore = _renderFinishedSemaphores![_currentFrame];
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,        // Wait for image available
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore     // Signal when rendering done
        };

        lock (_queueLock)
        {
            if (_vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, _inFlightFences[_currentFrame]) != Result.Success)
            {
                throw new Exception("Failed to submit draw command buffer!");
            }
        }

        // 5. Present to screen
        SwapchainKHR swapchain = _swapchain;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,      // Wait for rendering finished
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };

        lock (_queueLock)
        {
            _khrSwapchain.QueuePresent(_graphicsQueue, &presentInfo);
        }
        _currentFrame = (_currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

    }
    private void RecreateSwapchain(int width, int height)
    {
        _vk!.DeviceWaitIdle(_device); // Wait for GPU to finish

        // TODO: Destroy old swapchain image views/framebuffers
        // TODO: Recreate swapchain with new dimensions
        // TODO: Recreate image views/framebuffers

        // For now, just acknowledge the resize
        Console.WriteLine($"Swapchain recreation needed: {width}x{height}");
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

    private void AllocateCommandBuffers()
    {
        _commandBuffers = new CommandBuffer[MAX_FRAMES_IN_FLIGHT];

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MAX_FRAMES_IN_FLIGHT
        };

        fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
        {
            if (_vk!.AllocateCommandBuffers(_device, &allocInfo, commandBuffersPtr) != Result.Success)
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

        SemaphoreCreateInfo semaphoreInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
        };

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            if (_vk!.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailableSemaphores[i]) != Result.Success ||
                _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinishedSemaphores[i]) != Result.Success ||
                _vk.CreateFence(_device, &fenceInfo, null, out _inFlightFences[i]) != Result.Success)
            {
                throw new Exception("Failed make sync objects");
            }
        }
    }


    public void Dispose()
    {
        Stop(); // Stop render thread first

        _vk?.DeviceWaitIdle(_device); // Wait for GPU to finish everything

        // Destroy sync objects
        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            if (_imageAvailableSemaphores != null)
                _vk?.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
            if (_renderFinishedSemaphores != null)
                _vk?.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
            if (_inFlightFences != null)
                _vk?.DestroyFence(_device, _inFlightFences[i], null);
        }

        // Free command buffers
        if (_commandBuffers != null)
        {
            fixed (CommandBuffer* cmdPtr = _commandBuffers)
            {
                _vk?.FreeCommandBuffers(_device, _commandPool, (uint)_commandBuffers.Length, cmdPtr);
            }
        }

        // Destroy command pool
        _vk?.DestroyCommandPool(_device, _commandPool, null);
    }
}
public abstract record RenderCommand;
public record ResizeCommand(int Width, int Height) : RenderCommand;
public record StopCommand() : RenderCommand;
public record UpdateSimulationData(/* your data */) : RenderCommand;

