using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using blackHole.Core.Math;
using blackHole.Renderer.Vulkan;
using blackHole.Tools;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace blackHole.Simulation.Entities;

/// <summary>
/// Class representing a dust particle in the simulation
/// Hive entity
/// </summary>
public class Dust
{
    public Guid Id;
    public Vector3D<float> Position;
    public Vector3D<float> Velocity;

    public static readonly float DustSize = 0.1f; // Example size

    public Vertex VertexData;

    // Black hole gravity parameters
    private Vector3D<float> _blackHolePosition = Vector3D<float>.Zero;
    private float _blackHoleMass = 0f;

    public Dust()
    {
        Id = Guid.NewGuid();
        Position = Vector3D<float>.Zero;
        Velocity = Vector3D<float>.Zero;
    }

    public Dust(
        Vector3D<float> position,
        Vector3D<float> velocity,
        Vector3D<float> blackHolePosition,
        float blackHoleMass
    )
    {
        Id = Guid.NewGuid();
        Position = position;
        Velocity = velocity;
        _blackHolePosition = blackHolePosition;
        _blackHoleMass = blackHoleMass;
        VertexData = new Vertex
        {
            Position = new Vector3(position.X, position.Y, position.Z),
            Normal = Vector3.Normalize(new Vector3(position.X, position.Y, position.Z)),
            Color = new Vector3(0.8f, 0.8f, 1.0f), // Light blue-white dust
            TexCoord = Vector2.Zero, // not used for dust
        };
    }

    public static implicit operator List<object>(Dust v)
    {
        throw new NotImplementedException();
    }

    public override string ToString()
    {
        return $"Dust(Id: {Id}, Position: {Position}, Velocity: {Velocity})";
    }

    public void UpdatePosition(double deltaTime)
    {
        // Calculate gravitational acceleration towards the black hole
        Vector3D<float> toBlackHole = _blackHolePosition - Position;
        float distance = toBlackHole.Length;

        if (distance > 0.1f) // Avoid division by zero and singularity
        {
            Vector3D<float> direction = Vector3D.Normalize(toBlackHole);
            // F = GMm/r^2, but we use a = GM/r^2 (simplified with G=1)
            float accelerationMagnitude = _blackHoleMass / (distance * distance);
            Vector3D<float> acceleration = direction * accelerationMagnitude;

            // Update velocity with acceleration
            Velocity += acceleration * (float)deltaTime;
        }

        // Update position
        Position += Velocity * (float)deltaTime;

        // Update VertexData position
        VertexData.Position = new Vector3(Position.X, Position.Y, Position.Z);
        VertexData.Normal = Vector3.Normalize(new Vector3(Position.X, Position.Y, Position.Z));
    }

    public static void updateAllDustPositions(List<Dust> dustList, double deltaTime)
    {
        Parallel.ForEach(
            dustList,
            dust =>
            {
                dust.UpdatePosition(deltaTime);
            }
        );
    }

    public static void generateDustFieldDisk(
        List<Dust> dustList,
        int count,
        float radius,
        float holeRadius,
        float height,
        float blackHoleMass,
        LambdaExpression? gradientFunction = null
    )
    {
        var randThreadLocal = new ThreadLocal<Random>(() => new Random());
        var concurrentBag = new ConcurrentBag<Dust>();
        Parallel.For(
            0,
            count,
            i =>
            {
                var rand = randThreadLocal.Value!;
                // Generate random angle and distance from center
                double angle = rand.NextDouble() * 2.0 * Math.PI;
                double distance = Math.Sqrt(rand.NextDouble()) * radius; // sqrt for uniform distribution

                if (distance < holeRadius)
                {
                    distance = holeRadius;
                }

                // Calculate position in disk
                float x = (float)(distance * Math.Cos(angle));
                float y = (float)(rand.NextDouble() * height - height / 2); // Random height within the disk thickness
                float z = (float)(distance * Math.Sin(angle));

                Vector3D<float> position = new Vector3D<float>(x, y, z);

                // Initial velocity can be set to zero or some small random value
                Vector3D<float> velocity = MathHelpers.CreateOrbit(
                    Vector3D<float>.Zero,
                    position,
                    blackHoleMass,
                    1f
                );

                concurrentBag.Add(
                    new Dust(position, velocity, Vector3D<float>.Zero, blackHoleMass)
                );
            }
        );
        dustList.AddRange(concurrentBag);
    }
}
