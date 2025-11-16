using System;
using Silk.NET.Maths;
using static System.Math;

namespace blackHole.Core.Math;

public class MathHelpers
{
    public static Vector3D<float> CreateOrbit(
        Vector3D<float> center,
        Vector3D<float> currentPosition,
        float centerMass,
        float currentMass
    )
    {
        Vector3D<float> TangentialVelocity = Vector3D<float>.Zero; // the velocity
        Vector3D<float> directionToCenter = center - currentPosition;
        float distance = directionToCenter.Length;

        if (distance > 0)
        {
            Vector3D<float> direction = Vector3D.Normalize(directionToCenter);
            float orbitalSpeed = (float)Sqrt((centerMass) / distance); // simplified G=1
            Vector3D<float> tangentDirection = new Vector3D<float>(-direction.Z, 0, direction.X); // perpendicular in XZ plane
            TangentialVelocity = tangentDirection * orbitalSpeed;
        }

        return TangentialVelocity;
    }
}
