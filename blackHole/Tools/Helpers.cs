using Silk.NET.Maths;

namespace blackHole.Tools;

public abstract class Thing3D
{
    public Vector3D<float> Position { get; set; }
    public Vector3D<float> Rotation { get; set; }
    public Vector3D<float> Scale { get; set; }

    public List<Vector3D<float>> Vertices { get; set; }
    public List<uint> Indices { get; set; }

    public List<Vector2D<float>> UVs { get; set; }

    protected Thing3D()
    {
        Position = Vector3D<float>.Zero;
        Rotation = Vector3D<float>.Zero;
        Scale = Vector3D<float>.One;
        Vertices = new List<Vector3D<float>>();
        Indices = new List<uint>();
        UVs = new List<Vector2D<float>>();
    }

    /// <summary>
    /// Initialize the shape's vertices and indices.
    /// </summary>
    protected void FoldShape()
    {
        // To be implemented...
    }

    protected void InitializeBuffers() { }
}

public class Cube : Thing3D
{
    public float Size { get; set; }

    public Cube(float size)
    {
        Size = size;
        Vertices = Shapes.GenerateCubeVertices(size);
        Indices = Shapes.GenerateCubeIndices();
    }

    public Cube(float size, bool insideOut)
    {
        Size = size;
        Vertices = Shapes.GenerateCubeVertices(size);
        Indices = insideOut ? Shapes.GenerateCubeIndicesBackward() : Shapes.GenerateCubeIndices();
    }
}

public class Sphere : Thing3D
{
    public float Radius { get; set; }

    public Sphere(int subdivisions, float radius = 1.0f, bool insideOut = false)
    {
        Radius = radius;
        // Assuming GenerateIcoSphere returns a tuple: (List<Vector3D<float>> vertices, List<uint> indices, List<Vector2D<float>> uvs)
        var (verts, indices, uvs) = Shapes.GenerateIcoSphere(subdivisions, insideOut);
        Vertices = verts;
        Indices = indices;
        UVs = uvs;

        for (int i = 0; i < Vertices.Count; i++)
        {
            Vertices[i] *= radius;
        }
    }
}
