using System.Runtime.InteropServices;
using blackHole.Renderer.Vulkan;
using blackHole.Tools;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using static blackHole.Core.Math.MathConstants;

namespace blackHole.Simulation.Entities;

public class BlackHole
{
    internal readonly float radius;

    public double Spin = 0; // RPM. Non rotating, it would make the computation of the schwarzschild radius more complicated (I think)
    public int Sols = 4; // Solar mass of the black hole
    public Vector3D<float> Position = Vector3D<float>.Zero; //  0 0 0 will be our center.

    public Sphere SphereModel = new Sphere(2, 10.0f);

    public double SchwarzschildRadius;
    public double PhotonSphereRadius;

    public BlackHole()
    {
        // Compute the 2 radius.

        SchwarzschildRadius = SchwarzschildConstant * Sols * SolarMass;
        PhotonSphereRadius = 1.5 * SchwarzschildRadius;
    }
}
