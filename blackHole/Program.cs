using blackHole.Renderer.Vulkan;

Console.WriteLine("Starting Black Hole Simulation...");

// Create and initialize the simulation window
var simWindow = new SimulationWindow();
simWindow.Initialize();

// Run the simulation
simWindow.Run();

Console.WriteLine("Simulation ended.");
