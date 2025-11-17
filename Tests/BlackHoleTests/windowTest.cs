using blackHole.Renderer.Vulkan;

namespace BlackHoleTests;

public class WindowTests
{
    [Fact]
    public void WindowOpenAndCloseTest()
    {
        // Arrange
        var window = new SimulationWindow();
        bool exceptionThrown = false;
        Exception? caughtException = null;

        try
        {
            // Act
            window.Initialize();

            // Schedule immediate close after initialization
            bool hasLoaded = false;
            window.OnLoadComplete += () =>
            {
                hasLoaded = true;
            };

            Task.Run(async () =>
            {
                // Wait for window to load
                while (!hasLoaded)
                {
                    await Task.Delay(10);
                }
                await Task.Delay(50); // Small delay after load
                window.RequestClose();
            });

            window.Run();
        }
        catch (Exception ex)
        {
            exceptionThrown = true;
            caughtException = ex;
        }

        // Assert
        Assert.False(
            exceptionThrown,
            $"Exception was thrown: {caughtException?.Message}\n{caughtException?.StackTrace}"
        );
    }
}
