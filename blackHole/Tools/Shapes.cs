using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Maths;

namespace blackHole.Tools;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Color;
    public Vector2 TexCoord;
}

/// <summary>
/// Class use to generate lists vertices for shapes.
/// </summary>
public static class Shapes
{
    /// <summary>
    /// Generates a list of vertices representing a cube.
    /// </summary>
    /// <param name="size">The length of each side of the cube.</param>
    /// <returns>A list of vertices representing the cube.</returns>
    public static List<Vector3D<float>> GenerateCubeVertices(float size)
    {
        float halfSize = size / 2;
        return new List<Vector3D<float>>
        {
            // Front face
            new Vector3D<float>(-halfSize, -halfSize, halfSize),
            new Vector3D<float>(halfSize, -halfSize, halfSize),
            new Vector3D<float>(halfSize, halfSize, halfSize),
            new Vector3D<float>(-halfSize, halfSize, halfSize),
            // Back face
            new Vector3D<float>(-halfSize, -halfSize, -halfSize),
            new Vector3D<float>(halfSize, -halfSize, -halfSize),
            new Vector3D<float>(halfSize, halfSize, -halfSize),
            new Vector3D<float>(-halfSize, halfSize, -halfSize),
        };
    }

    public static List<uint> GenerateCubeIndices()
    {
        return new List<uint>
        {
            // Front face
            0,
            1,
            2,
            2,
            3,
            0,
            // Back face
            4,
            5,
            6,
            6,
            7,
            4,
            // Left face
            4,
            0,
            3,
            3,
            7,
            4,
            // Right face
            1,
            5,
            6,
            6,
            2,
            1,
            // Top face
            3,
            2,
            6,
            6,
            7,
            3,
            // Bottom face
            4,
            5,
            1,
            1,
            0,
            4,
        };
    }

    public static List<uint> GenerateCubeIndicesBackward()
    {
        return new List<uint>
        {
            // Front face
            2,
            1,
            0,
            0,
            3,
            2,
            // Back face
            6,
            5,
            4,
            4,
            7,
            6,
            // Left face
            3,
            0,
            4,
            4,
            7,
            3,
            // Right face
            6,
            5,
            1,
            1,
            2,
            6,
            // Top face
            6,
            2,
            3,
            3,
            7,
            6,
            // Bottom face
            1,
            5,
            4,
            4,
            0,
            1,
        };
    }

    public static List<Vector3D<float>> GeneratePyramidVertices(float baseSize, float height)
    {
        float halfBase = baseSize / 2;
        return new List<Vector3D<float>>
        {
            // Base vertices
            new Vector3D<float>(-halfBase, 0, -halfBase),
            new Vector3D<float>(halfBase, 0, -halfBase),
            new Vector3D<float>(halfBase, 0, halfBase),
            new Vector3D<float>(-halfBase, 0, halfBase),
            // Apex vertex
            new Vector3D<float>(0, height, 0),
        };
    }

    /// <summary>
    /// Generates vertices and indices for a sphere.
    /// Uses a recursive subdivision approach starting with an octahedron.
    /// This version uses indexed vertices to avoid duplicates.
    /// </summary>
    /// <param name="subdivisions">Number of recursive subdivisions (0-5 recommended).</param>
    /// <returns>A tuple containing (vertices, indices).</returns>
    /// <summary>
    /// Generates vertices, indices, and UV coordinates for a sphere.
    /// Uses a recursive subdivision approach starting with an octahedron.
    /// This version uses indexed vertices to avoid duplicates.
    /// </summary>
    /// <param name="subdivisions">Number of recursive subdivisions (0-5 recommended).</param>
    /// <returns>A tuple containing (vertices, indices, uvs).</returns>
    public static (
        List<Vector3D<float>> vertices,
        List<uint> indices,
        List<Vector2D<float>> uvs
    ) GenerateIcoSphere(int subdivisions = 2, bool insideOut = false)
    {
        // Start with octahedron vertices
        List<Vector3D<float>> vertices = new List<Vector3D<float>>
        {
            new Vector3D<float>(0, 1, 0), // 0: top
            new Vector3D<float>(0, -1, 0), // 1: bottom
            new Vector3D<float>(1, 0, 0), // 2: right
            new Vector3D<float>(-1, 0, 0), // 3: left
            new Vector3D<float>(0, 0, 1), // 4: front
            new Vector3D<float>(0, 0, -1), // 5: back
        };

        List<uint> indices = new List<uint>();
        Dictionary<Vector3D<float>, uint> vertexCache = new Dictionary<Vector3D<float>, uint>();
        List<Vector2D<float>> uvs = new List<Vector2D<float>>();

        // Initialize cache with starting vertices
        for (uint i = 0; i < vertices.Count; i++)
        {
            vertexCache[vertices[(int)i]] = i;
            uvs.Add(CalculateSphericalUV(vertices[(int)i]));
        }

        // Define octahedron faces (as triangles using indices)
        SubdivideTriangleIndexedWithUV(
            0,
            2,
            4,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );
        SubdivideTriangleIndexedWithUV(
            0,
            4,
            3,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );
        SubdivideTriangleIndexedWithUV(
            0,
            3,
            5,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );
        SubdivideTriangleIndexedWithUV(
            0,
            5,
            2,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );
        SubdivideTriangleIndexedWithUV(
            1,
            4,
            2,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );
        SubdivideTriangleIndexedWithUV(
            1,
            3,
            4,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );
        SubdivideTriangleIndexedWithUV(
            1,
            5,
            3,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );
        SubdivideTriangleIndexedWithUV(
            1,
            2,
            5,
            subdivisions,
            vertices,
            indices,
            vertexCache,
            uvs,
            insideOut
        );

        return (vertices, indices, uvs);
    }

    /// <summary>
    /// Recursively subdivides a triangle using vertex indices.
    /// Reuses vertices to avoid duplicates. Also generates UVs.
    /// </summary>
    private static void SubdivideTriangleIndexedWithUV(
        uint i1,
        uint i2,
        uint i3,
        int depth,
        List<Vector3D<float>> vertices,
        List<uint> indices,
        Dictionary<Vector3D<float>, uint> vertexCache,
        List<Vector2D<float>> uvs,
        bool insideOut = false
    )
    {
        if (depth == 0)
        {
            indices.Add(i1);
            if (insideOut)
            {
                indices.Add(i3);
                indices.Add(i2);
            }
            else
            {
                indices.Add(i2);
                indices.Add(i3);
            }
            return;
        }

        Vector3D<float> v1 = vertices[(int)i1];
        Vector3D<float> v2 = vertices[(int)i2];
        Vector3D<float> v3 = vertices[(int)i3];

        // Calculate midpoints and normalize to project onto sphere
        Vector3D<float> v12 = Vector3D.Normalize((v1 + v2) / 2);
        Vector3D<float> v23 = Vector3D.Normalize((v2 + v3) / 2);
        Vector3D<float> v31 = Vector3D.Normalize((v3 + v1) / 2);

        // Get or create indices for midpoint vertices
        uint i12 = GetOrCreateVertexIndexWithUV(v12, vertices, vertexCache, uvs);
        uint i23 = GetOrCreateVertexIndexWithUV(v23, vertices, vertexCache, uvs);
        uint i31 = GetOrCreateVertexIndexWithUV(v31, vertices, vertexCache, uvs);

        // Recursively subdivide the 4 new triangles
        SubdivideTriangleIndexedWithUV(
            i1,
            i12,
            i31,
            depth - 1,
            vertices,
            indices,
            vertexCache,
            uvs
        );
        SubdivideTriangleIndexedWithUV(
            i2,
            i23,
            i12,
            depth - 1,
            vertices,
            indices,
            vertexCache,
            uvs
        );
        SubdivideTriangleIndexedWithUV(
            i3,
            i31,
            i23,
            depth - 1,
            vertices,
            indices,
            vertexCache,
            uvs
        );
        SubdivideTriangleIndexedWithUV(
            i12,
            i23,
            i31,
            depth - 1,
            vertices,
            indices,
            vertexCache,
            uvs
        );
    }

    /// <summary>
    /// Gets the index of a vertex, or creates it if it doesn't exist. Also adds UV.
    /// </summary>
    private static uint GetOrCreateVertexIndexWithUV(
        Vector3D<float> vertex,
        List<Vector3D<float>> vertices,
        Dictionary<Vector3D<float>, uint> vertexCache,
        List<Vector2D<float>> uvs
    )
    {
        // Round to avoid floating point precision issues (max 6 digits in C#)
        Vector3D<float> rounded = new Vector3D<float>(
            MathF.Round(vertex.X, 6),
            MathF.Round(vertex.Y, 6),
            MathF.Round(vertex.Z, 6)
        );

        if (!vertexCache.TryGetValue(rounded, out uint index))
        {
            index = (uint)vertices.Count;
            vertices.Add(rounded);
            vertexCache[rounded] = index;
            uvs.Add(CalculateSphericalUV(rounded));
        }

        return index;
    }

    /// <summary>
    /// Calculates spherical UV coordinates for a vertex on a unit sphere.
    /// </summary>
    private static Vector2D<float> CalculateSphericalUV(Vector3D<float> v)
    {
        // v should be normalized
        float u = 0.5f + (MathF.Atan2(v.Z, v.X) / (2 * MathF.PI));
        float vCoord = 0.5f - (MathF.Asin(v.Y) / MathF.PI);
        return new Vector2D<float>(u, vCoord);
    }
}
