using System;
using System.Diagnostics;
using System.Numerics;
using blackHole.Core.Math;
using blackHole.Simulation.Entities;
using blackHole.Tools;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace blackHole.Renderer.Vulkan;

public unsafe class SimulationWindow
{
    private IWindow? _window;
    private Vulkan? _vulkan;
    private RenderWorker? _renderWorker;
    private SimState _simState = new();

    private RayMarchPipe? _rayMarchPipeline;

    // Camera state. I should probably make a Camera class for this...
    private Vector3D<float> _cameraPosition = new(0, 300, 600);
    private Vector3D<float> _cameraFront = new(0, -0.316f, -0.948f); // Normalized vector pointing roughly to origin
    private Vector3D<float> _cameraUp = new(0, 1, 0);
    private float _cameraYaw = -90.0f; // Start facing -Z
    private float _cameraPitch = -18.0f; // Look down at the black hole
    private float _cameraSpeed = 50.0f;
    private float _mouseSensitivity = 0.1f;

    private Stopwatch _simulationTimer = new();
    private DateTime _lastInputTime = DateTime.MinValue;
    private const double InputCooldown = 0.2; // 200ms

    // Input state
    private IInputContext? _inputContext;
    private IMouse? _mouse;
    private IKeyboard? _keyboard;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMove = true;

    // Event for testing
    public event Action? OnLoadComplete;

    public void Initialize()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = "Black Hole Simulation - Vulkan";
        options.VSync = true;
        options.API = new GraphicsAPI(
            ContextAPI.Vulkan,
            ContextProfile.Core,
            ContextFlags.Default,
            new APIVersion(1, 3)
        );

        _window = Window.Create(options);

        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        // _window.Resize += OnResize; // TODO: future me fix this
        _window.Render += OnRender;
        _window.Closing += OnClosing;
    }

    public void Run()
    {
        _window?.Run();
    }

    public void RequestClose()
    {
        _window?.Close();
    }

    private void OnLoad()
    {
        Console.WriteLine("SimulationWindow: Initializing Vulkan...");

        _vulkan = Vulkan.Instance;
        _vulkan.Initialize(_window!, (uint)_window!.Size.X, (uint)_window.Size.Y);

        _renderWorker = new RenderWorker();

        InitializeSimulation();
        InitializePipelines();
        InitializeInput();

        _renderWorker.Start(_vulkan);
        _simulationTimer.Start();

        Console.WriteLine("Controls:");
        Console.WriteLine("Mouse: Look around");
        Console.WriteLine("WASD: Move camera");
        Console.WriteLine("Space/Shift: Move up/down");
        Console.WriteLine("Mouse Wheel: Adjust speed"); // more keys please
        Console.WriteLine("ESC: Exit");

        OnLoadComplete?.Invoke();
    }

    private void InitializeInput()
    {
        _inputContext = _window!.CreateInput();

        foreach (var mouse in _inputContext.Mice)
        {
            _mouse = mouse;
            _mouse.Cursor.CursorMode = CursorMode.Raw; // FPS cam
            _mouse.MouseMove += OnMouseMove;
            _mouse.Scroll += OnMouseScroll;
        }

        foreach (var keyboard in _inputContext.Keyboards)
        {
            _keyboard = keyboard;
            _keyboard.KeyDown += OnKeyDown;
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if ((DateTime.UtcNow - _lastInputTime).TotalSeconds < InputCooldown)
            return;

        if (key == Key.Tab)
        {
            if (_mouse != null)
            {
                _mouse.Cursor.CursorMode =
                    _mouse.Cursor.CursorMode == CursorMode.Raw ? CursorMode.Normal : CursorMode.Raw;
                _lastInputTime = DateTime.UtcNow;
            }
        }

        if (key == Key.R)
        {
            ReloadPipelines();
            _lastInputTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Makes changing shaders easier by eliminating the need to rebuild the project.
    /// </summary>
    private void ReloadPipelines()
    {
        _simulationTimer.Stop();

        // Stop and dispose render worker to ensure clean state
        _renderWorker?.Dispose(); // disposes the pipelines

        // Create new worker
        _renderWorker = new RenderWorker();

        // Re-initialize pipelines (this sends the new pipeline to the new worker)
        InitializePipelines();

        // Restart worker
        _renderWorker.Start(_vulkan!);
        _simulationTimer.Start();
        Console.WriteLine("Pipelines reloaded.");
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (_firstMouseMove)
        {
            _lastMousePosition = position;
            _firstMouseMove = false;
            return;
        }

        var deltaX = position.X - _lastMousePosition.X;
        var deltaY = position.Y - _lastMousePosition.Y;
        _lastMousePosition = position;

        // Only move camera if in Raw mode
        if (_mouse!.Cursor.CursorMode != CursorMode.Raw)
            return;

        _cameraYaw += deltaX * _mouseSensitivity;
        _cameraPitch -= deltaY * _mouseSensitivity;

        // avoids flipping
        _cameraPitch = Math.Clamp(_cameraPitch, -89.0f, 89.0f);

        // Update camera front vector
        var front = new Vector3D<float>();
        front.X =
            MathF.Cos(MathF.PI / 180.0f * _cameraYaw) * MathF.Cos(MathF.PI / 180.0f * _cameraPitch);
        front.Y = MathF.Sin(MathF.PI / 180.0f * _cameraPitch);
        front.Z =
            MathF.Sin(MathF.PI / 180.0f * _cameraYaw) * MathF.Cos(MathF.PI / 180.0f * _cameraPitch);
        _cameraFront = Vector3D.Normalize(front);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel scroll)
    {
        _cameraSpeed += scroll.Y * 10.0f;
        _cameraSpeed = Math.Clamp(_cameraSpeed, 10.0f, 500.0f);
        Console.WriteLine($"Camera speed: {_cameraSpeed}");
    }

    private void InitializeSimulation()
    {
        // Everything is now happening in the GPU
        // TODO: Figure out how to use the simulation.
    }

    // I don't know where to put this function logic. Vulkan? Pipeline? Renderer?
    private void InitializePipelines()
    {
        // Create ray marching pipeline
        _rayMarchPipeline = new RayMarchPipe(_vulkan!);
        _rayMarchPipeline.build();
        _rayMarchPipeline.CreateGraphicsPipeline(
            "blackHole/Resources/Shaders/raymarch.vert",
            "blackHole/Resources/Shaders/raymarch.frag"
        );

        // Send pipelines to render worker
        _renderWorker!.SendCommand(new SetPipelineDataCommand(_rayMarchPipeline!));
    }

    private void OnUpdate(double deltaTime)
    {
        // Handle keyboard input for camera movement
        if (_keyboard != null && _mouse?.Cursor.CursorMode == CursorMode.Raw)
        {
            var velocity = _cameraSpeed * (float)deltaTime;

            // Calculate right vector
            var camRight = Vector3D.Normalize(Vector3D.Cross(_cameraFront, _cameraUp));

            // WASD movement
            if (_keyboard.IsKeyPressed(Key.W))
                _cameraPosition += _cameraFront * velocity;
            if (_keyboard.IsKeyPressed(Key.S))
                _cameraPosition -= _cameraFront * velocity;
            if (_keyboard.IsKeyPressed(Key.A))
                _cameraPosition -= camRight * velocity;
            if (_keyboard.IsKeyPressed(Key.D))
                _cameraPosition += camRight * velocity;

            // Space/Shift for vertical movement
            if (_keyboard.IsKeyPressed(Key.Space))
                _cameraPosition += _cameraUp * velocity;
            if (_keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight))
                _cameraPosition -= _cameraUp * velocity;

            // ESC to exit
            if (_keyboard.IsKeyPressed(Key.Escape))
                _window?.Close();
        }

        // Update simulation
        double dt = 0.016; // Fixed timestep for stability
        _simState.Update(dt);

        // Build dust vertex data and send to render thread
        if (_simState.Dusts.Count > 0)
        {
            var dustVertices = new Vertex[_simState.Dusts.Count];
            Parallel.For(
                0,
                _simState.Dusts.Count,
                i =>
                {
                    float dist = _simState.Dusts[i].Position.Length;
                    float colorT = (dist - 220.0f) / 1000.0f; // Map from inner to outer radius
                    colorT = Math.Clamp(colorT, 0.0f, 1.0f);

                    dustVertices[i] = new Vertex
                    {
                        Position = new System.Numerics.Vector3(
                            _simState.Dusts[i].Position.X,
                            _simState.Dusts[i].Position.Y,
                            _simState.Dusts[i].Position.Z
                        ),
                        Color = new System.Numerics.Vector3(
                            1.0f, // R: bright red throughout
                            1.0f - colorT * 0.7f, // G: 1.0 (inner) -> 0.3 (outer) - yellow to orange
                            1.0f - colorT * 1.0f // B: 1.0 (inner) -> 0.0 (outer) - white to red
                        ),
                        Normal = new System.Numerics.Vector3(0, 1, 0),
                        TexCoord = new System.Numerics.Vector2(0, 0),
                    };
                }
            );
            _renderWorker?.SendCommand(new UpdateDustVerticesCommand(dustVertices));
        }



        // Camera target is position + front direction
        var cameraTarget = _cameraPosition + _cameraFront;

        // Update camera matrices
        float aspect = (float)_window!.Size.X / _window.Size.Y;

        Matrix4X4<float> view = Matrix4X4.CreateLookAt(_cameraPosition, cameraTarget, _cameraUp);

        Matrix4X4<float> projection = Matrix4X4.CreatePerspectiveFieldOfView(
            MathF.PI / 4.0f, // 45 degree FOV
            aspect,
            1.0f, // near plane
            10000.0f // far plane
        );

        // Calculate orthogonal camera vectors for ray marching
        var right = Vector3D.Normalize(Vector3D.Cross(_cameraFront, _cameraUp));
        var orthoUp = Vector3D.Normalize(Vector3D.Cross(right, _cameraFront));

        // Send updated data to render thread
        _renderWorker?.SendCommand(
            new UpdateSimulationDataCommand(
                _simState,
                view,
                projection,
                _cameraPosition,
                _cameraFront,
                orthoUp,
                right,
                MathF.PI / 4.0f, // FOV
                aspect,
                (float)_simulationTimer.Elapsed.TotalSeconds
            )
        );
    }

    private void OnRender(double deltaTime)
    {
        // Rendering is handled by RenderWorker thread
    }

    private void OnClosing()
    {
        Console.WriteLine("SimulationWindow: Shutting down...");

        _simulationTimer.Stop();
        _renderWorker?.Stop();
        _renderWorker?.Dispose(); // This will dispose the pipelines

        _inputContext?.Dispose();

        Console.WriteLine("SimulationWindow: Shutdown complete.");
    }
}
