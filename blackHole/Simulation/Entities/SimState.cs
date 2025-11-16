using Microsoft.VisualBasic;
using BlackHoleModel = blackHole.Simulation.Entities.BlackHole;

namespace blackHole.Simulation.Entities;

public class SimState
{
    private BlackHoleModel CentralBlackHole;
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
        FrameCount++;
    }

    

}
