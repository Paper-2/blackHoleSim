using blackHole.Renderer.Vulkan;

Console.WriteLine("ESC - Exit");
Console.WriteLine();

try
{
    var window = new SimulationWindow();
    window.Initialize();
    window.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    return 1;
}

Console.WriteLine("Simulation ended.");
return 0;
