# Black Hole Simulation

A 3D simulation of a black hole using Vulkan rendering via Silk.NET.

## Project Overview

This project simulates the gravitational effects and visual phenomena of a black hole in real-time using modern graphics APIs.

## Technology Stack

- **.NET 9.0** - Modern C# framework
- **Silk.NET** - .NET wrapper for Vulkan, OpenGL, and other native APIs
- **Vulkan** - Low-level graphics and compute API for high-performance rendering

## Project Structure

- `blackHole/` - Main application source code
  - `Core/` - Core utilities
    - `Math/` - Mathematical constants and helpers
  - `Renderer/` - Rendering system
    - `Vulkan/` - Vulkan-specific rendering code
  - `Resources/` - Assets, shaders, and textures
  - `Simulation/` - Simulation logic
    - `Entities/` - Simulation entities (BlackHole, Dust, SimState, etc.)
  - `Tools/` - Utility classes (Shapes, Helpers)
- `Tests/` - Unit tests

## Building the Project

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Vulkan SDK (for development)
- Windows/Linux/macOS with Vulkan-capable GPU

### Build Instructions

```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run the application
dotnet run --project blackHole
```

## Development Roadmap

See [3D_SCENE_TODO.md](3D_SCENE_TODO.md) for the detailed implementation roadmap for the 3D rendering system.

## Current Status

**In Development** - Currently implementing the Vulkan rendering pipeline for 3D visualization.
