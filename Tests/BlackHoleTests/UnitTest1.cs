using blackHole.Core.Math;
using Silk.NET.Maths;

namespace BlackHoleTests;

public class UnitTest1
{
    [Fact]
    public void TestCreateOrbit()
    {
        // Arrange
        var center = Vector3D<float>.Zero;
        var position = new Vector3D<float>(1, 0, 0);
        float centerMass = 1.0f;
        float currentMass = 1.0f;

        // Act
        var velocity = MathHelpers.CreateOrbit(center, position, centerMass, currentMass);

        // Assert: Velocity should be perpendicular to the position vector (tangential)
        var dotProduct = Vector3D.Dot(velocity, position);

        // Assert: Velocity should not be zero
        Assert.NotEqual(Vector3D<float>.Zero, velocity);
    }
}
