using System.Collections.Concurrent;
using System.Linq.Expressions;
using Silk.NET.Maths;
using System.Threading;
using System.Threading.Tasks;
using blackHole.Core.Math;
using Silk.NET.Vulkan;

namespace blackHole.Simulation.Entities;

public class Dust
{
    public Guid Id;
    public Vector3D<float> Position;
    public Vector3D<float> Velocity;

    public static readonly float DustSize = 0.1f; // Example size

    private Vector2D<float> gravityWellPosition = Vector2D<float>.Zero;
    private float gravityWellMass = 10f;

    public Dust()
    {
        Id = Guid.NewGuid();
        Position = Vector3D<float>.Zero;
        Velocity = Vector3D<float>.Zero;
    }

    public Dust(Vector3D<float> position, Vector3D<float> velocity)
    {
        Id = Guid.NewGuid();
        Position = position;
        Velocity = velocity;
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
        // Calculate gravitational acceleration towards the gravity well
        Vector2D<float> dustPlane = new Vector2D<float>(Position.X, Position.Z);
        Vector2D<float> toWell = gravityWellPosition - dustPlane;
        float distance = toWell.Length;
        if (distance > 0)
        {
            Vector2D<float> direction = Vector2D.Normalize(toWell);
            float accelerationMagnitude = gravityWellMass / (distance * distance);
            Vector2D<float> acceleration = accelerationMagnitude * direction;
            Velocity.X += acceleration.X * (float)deltaTime;
            Velocity.Z += acceleration.Y * (float)deltaTime;
        }

        Position += Velocity * (float)deltaTime;
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
                Vector3D<float> velocity = MathHelpers.CreateOrbit(Vector3D<float>.Zero, position, 1000f, 1f);

                concurrentBag.Add(new Dust(position, velocity));
            }
        );
        dustList.AddRange(concurrentBag);
    }
}
