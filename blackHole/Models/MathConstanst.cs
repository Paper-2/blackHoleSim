namespace blackHole.Models;

public static class MathConstants
{
    // Physics 

    public const double C = 299792458.0, SpeedOfLight = 299792458.0; // m/s
    public const double G = 6.67430e-11, GravitationalConstant = 6.67430e-11; // m³/(kg·s²)
    public const double SO = 1.98847e30, SolarMass = 1.98847e30; // kg
    public const double SchwarzschildConstant = 2.0 * GravitationalConstant / (SpeedOfLight * SpeedOfLight);

}