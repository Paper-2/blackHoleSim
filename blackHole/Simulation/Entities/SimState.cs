using BlackHoleModel = blackHole.Simulation.Entities.BlackHole;

namespace blackHole.Simulation.Entities;

public class SimState
{
    public BlackHoleModel CentralBlackHole { get; private set; }
    public List<Dust> Dusts;
    public Bound Skybox;

    public double Time;
    public int FrameCount;
    public bool IsRunning;

    public SimState()
    {
        CentralBlackHole = new BlackHoleModel();
        Dusts = new List<Dust>();
        Time = 0.0;
        FrameCount = 0;
        IsRunning = false;
        Skybox = new Bound();
    }

    public SimState(BlackHoleModel setBlackHole, List<Dust> setDustList)
    {
        CentralBlackHole = setBlackHole;
        Dusts = setDustList;
        Time = 0.0;
        FrameCount = 0;
        IsRunning = false;
        Skybox = new Bound();
    }

    public void Update(double deltaTime)
    {
        // Update simulation state here
        Time += deltaTime;
        Dust.updateAllDustPositions(Dusts, deltaTime);
        FrameCount++;
    }

    public RenderData GetRenderData()
    {
        return new RenderData
        {
            BlackHolePosition = CentralBlackHole.Position,
            BlackHoleRadius = (float)CentralBlackHole.SphereModel.Radius,
            DustParticles = Dusts
                .Select(d => new ParticleData
                {
                    Position = d.Position,
                    Color = new Silk.NET.Maths.Vector3D<float>(0.8f, 0.8f, 1.0f),
                })
                .ToArray(),
            SkyboxSize = Skybox.Size,
        };
    }
}

// Data structures for rendering
public struct RenderData
{
    public Silk.NET.Maths.Vector3D<float> BlackHolePosition;
    public float BlackHoleRadius;
    public ParticleData[] DustParticles;
    public int SkyboxSize;
}

public struct ParticleData
{
    public Silk.NET.Maths.Vector3D<float> Position;
    public Silk.NET.Maths.Vector3D<float> Color;
}
