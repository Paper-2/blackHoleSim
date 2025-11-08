using System.Numerics;

namespace blackHole.Models;

public class Dust
{
    public Guid Id;
    public Vector3 Position;
    public Vector3 Velocity;
    public double Mass; // Kg

    public Dust()
    {
        Id = Guid.NewGuid();
        Position = Vector3.Zero;
        Velocity = Vector3.Zero;
        Mass = 1;

    }

    public Dust(Vector3 position, Vector3 velocity, double mass)
    {
        Id = Guid.NewGuid();
        Position = position;
        Velocity = velocity;
        Mass = mass;
    }

    public static implicit operator List<object>(Dust v)
    {
        throw new NotImplementedException();
    }
}