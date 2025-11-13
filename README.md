# Black Hole Simulation

A 3D simulation of a black hole using Vulkan rendering via Silk.NET.

## Project Overview

This project simulates the gravitational effects and visual phenomena of a black hole in real-time using modern graphics APIs.

## Technology Stack

- **.NET 9.0** - Modern C# framework
- **Silk.NET** - .NET wrapper for Vulkan, OpenGL, and other native APIs
- **Vulkan** - Low-level graphics and compute API for high-performance rendering
- **ASP.NET Core MVC** - Web framework for simulation controls and interface

## Project Structure

- `blackHole/` - Main application source code
  - `Controllers/` - MVC controllers for web interface
  - `Models/` - Data models (BlackHole, Dust, SimState, etc.)
  - `Rendering/` - Vulkan rendering implementation
  - `Views/` - Web UI views
  - `wwwroot/` - Static web assets

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


## Current Status

**In Development** - Currently implementing the Vulkan rendering pipeline for 3D visualization.
