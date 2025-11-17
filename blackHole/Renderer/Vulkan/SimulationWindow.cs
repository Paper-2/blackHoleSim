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

    private DustPipeline? _dustPipeline;
    private BlackHolePipeline? _blackHolePipeline;
    private SkyboxPipeline? _skyboxPipeline;

    // Camera state. I should probably make a Camera class for this...
    private Vector3D<float> _cameraPosition = new(0, 1000, 3000);
    private Vector3D<float> _cameraFront = new(0, 0, -1);
    private Vector3D<float> _cameraUp = new(0, 1, 0);
    private float _cameraYaw = -90.0f; // Start facing -Z
    private float _cameraPitch = 0.0f;
    private float _cameraSpeed = 50.0f;
    private float _mouseSensitivity = 0.1f;

    private Stopwatch _simulationTimer = new();

    // Input state
    private IInputContext? _inputContext;
    private IMouse? _mouse;
    private IKeyboard? _keyboard;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMove = true;

    // Event for testing
    public event Action? OnLoadComplete;

    // Track first dust particle
    private Dust? _firstDust;

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
        }
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

        _cameraYaw += deltaX * _mouseSensitivity;
        _cameraPitch += deltaY * _mouseSensitivity;

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
        // Create black hole at center with icosphere
        _simState.CentralBlackHole.Sols = 4000000; // name doesn't really matter here 
        _simState.CentralBlackHole.Position = Vector3D<float>.Zero;
        _simState.CentralBlackHole.SphereModel = new Sphere(4, 200.0f); // Bigger please

        // Create skybox as large inside-out sphere
        _simState.Skybox.SphereModel = new Sphere(5, 50000.0f, insideOut: true);

        // Clear auto-generated dust and create orbital configuration using generateDustFieldDisk
        _simState.Dusts.Clear();
        
        // Disk parameters
        const float diskOuterRadius = 1220.0f; // 220 (inner) + 1000 (width)
        const float diskInnerRadius = 220.0f; // 110% of sphere radius (200.0 * 1.1)
        const float diskThickness = 0.1f;
        const int particleCount = 100000;
        
        Dust.generateDustFieldDisk(
            _simState.Dusts,
            particleCount,
            diskOuterRadius,
            diskInnerRadius,
            diskThickness,
            (float)_simState.CentralBlackHole.SchwarzschildRadius * 0.8f
        );
    }


// I don't know where to put this function logic. Vulkan? Pipeline? Renderer?
    private void InitializePipelines()
    {
        // Create dust pipeline
        _dustPipeline = new DustPipeline(_vulkan!);

        var dustVertices = new Vertex[_simState.Dusts.Count];
        for (int i = 0; i < _simState.Dusts.Count; i++)
        {
            // Color: inner (hot/bright white-yellow) to outer (cool/dim red-orange)
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
                    1.0f,                      // R: bright red throughout
                    1.0f - colorT * 0.7f,      // G: 1.0 (inner) -> 0.3 (outer) - yellow to orange
                    1.0f - colorT * 1.0f       // B: 1.0 (inner) -> 0.0 (outer) - white to red
                ),
                Normal = new System.Numerics.Vector3(0, 1, 0),
                TexCoord = new System.Numerics.Vector2(0, 0),
            };
        }
        _dustPipeline.SetVertexArray(dustVertices);
        _dustPipeline.build();
        _dustPipeline.CreateGraphicsPipeline(
            "blackHole/Resources/Shaders/dust.vert",
            "blackHole/Resources/Shaders/dust.frag"
        );

        // Create black hole pipeline
        _blackHolePipeline = new BlackHolePipeline(_vulkan!);
        _blackHolePipeline.SetSphere(_simState.CentralBlackHole.SphereModel);
        _blackHolePipeline.build();
        _blackHolePipeline.CreateGraphicsPipeline(
            "blackHole/Resources/Shaders/blackhole.vert",
            "blackHole/Resources/Shaders/blackhole.frag"
        );

        // Create skybox pipeline
        _skyboxPipeline = new SkyboxPipeline(_vulkan);
        _skyboxPipeline.SetSphere(_simState.Skybox.SphereModel);
        _skyboxPipeline.build();
        _skyboxPipeline.CreateGraphicsPipeline(
            "blackHole/Resources/Shaders/skybox.vert",
            "blackHole/Resources/Shaders/skybox.frag"
        );

        // Send pipelines to render worker
        _renderWorker!.SendCommand(
            new SetPipelineDataCommand(_dustPipeline, _blackHolePipeline, _skyboxPipeline)
        );
    }

    private void OnUpdate(double deltaTime)
    {
        // Handle keyboard input for camera movement
        if (_keyboard != null)
        {
            var velocity = _cameraSpeed * (float)deltaTime;

            // Calculate right vector
            var right = Vector3D.Normalize(Vector3D.Cross(_cameraFront, _cameraUp));

            // WASD movement
            if (_keyboard.IsKeyPressed(Key.W))
                _cameraPosition += _cameraFront * velocity;
            if (_keyboard.IsKeyPressed(Key.S))
                _cameraPosition -= _cameraFront * velocity;
            if (_keyboard.IsKeyPressed(Key.A))
                _cameraPosition -= right * velocity;
            if (_keyboard.IsKeyPressed(Key.D))
                _cameraPosition += right * velocity;

            // Space/Shift for vertical movement
            if (_keyboard.IsKeyPressed(Key.Space))
                _cameraPosition -= _cameraUp * velocity;
            if (_keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight))
                _cameraPosition += _cameraUp * velocity;

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
                            1.0f,                      // R: bright red throughout
                            1.0f - colorT * 0.7f,      // G: 1.0 (inner) -> 0.3 (outer) - yellow to orange
                            1.0f - colorT * 1.0f       // B: 1.0 (inner) -> 0.0 (outer) - white to red
                        ),
                        Normal = new System.Numerics.Vector3(0, 1, 0),
                        TexCoord = new System.Numerics.Vector2(0, 0),
                    };
                }
            );
            _renderWorker?.SendCommand(new UpdateDustVerticesCommand(dustVertices));
        }

        // // Print first dust particle info every 60 frames (~1 second)
        // if (_firstDust != null && (int)(_simulationTimer.Elapsed.TotalSeconds * 60) % 60 == 0)
        // {
        //     Console.WriteLine(
        //         $"First dust - Pos: ({_firstDust.Position.X:F2}, {_firstDust.Position.Y:F2}, {_firstDust.Position.Z:F2}), "
        //             + $"Vel: ({_firstDust.Velocity.X:F4}, {_firstDust.Velocity.Y:F4}, {_firstDust.Velocity.Z:F4})"
        //     );
        // }

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

        // Send updated data to render thread
        _renderWorker?.SendCommand(new UpdateSimulationDataCommand(_simState, view, projection));
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
