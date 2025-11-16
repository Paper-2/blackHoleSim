using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace blackHole.Renderer.Vulkan;

public unsafe class SimulationWindow
{
    private IWindow? _window;

    public int Width = 1280;
    public int Height = 720;

    private Vulkan _vulkan = Vulkan.Instance;

    private RenderWorker _renderWorker = new();

    public void Initialize()
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(Width, Height),
            Title = "Simulation of a Black Hole",
        };

        _window = Window.Create(options);

        _window.Load += OnLoad;
        // OnRender is now handled by RenderWorker
        // _window.Render += OnRender;
        _window.Update += OnUpdate;
        _window.Resize += OnResize;
        _window.Closing += OnClosing;
    }

    public void Run()
    {
        _window?.Run();
    }

    private void OnLoad()
    {
        // Initialize Vulkan through IVulkan interface
        _vulkan.Initialize(_window!, (uint)Width, (uint)Height);
        // Start rendering
        _vulkan.Render(_renderWorker);
    }

    private void OnUpdate(double deltaTime)
    {
        // Simulation logic here
    }

    private void OnResize(Vector2D<int> newSize)
    {
        if (newSize.X == 0 || newSize.Y == 0)
            return;

        Width = newSize.X;
        Height = newSize.Y;

        // Send resize command to RenderWorker
        _renderWorker.SendCommand(new ResizeCommand(newSize.X, newSize.Y));
    }

    private void OnClosing()
    {
        Console.WriteLine("Closing Window");

        // Cleanup Vulkan resources through IVulkan
        Vulkan.Instance.Cleanup();

        // Note: Staging buffer cleanup is handled by RenderWorker
        // as it's part of the rendering pipeline
    }
}
