using System.Runtime.InteropServices;
using blackHole.Renderer.Vulkan;
using blackHole.Tools;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace blackHole.Simulation.Entities;

/// <summary>
/// Class that contains data for the skybox doubt that its necessary but whatever...
/// </summary>
public class Bound
{
    public string TexturePath = "Textures/star_map.png";

    public int Size = 5000; // Should be big enough to avoid a parallax effect when rotating the camera.

    public Sphere SphereModel;

    public Bound()
    {
        SphereModel = new Sphere(2, Size, insideOut: true);
    }
}
