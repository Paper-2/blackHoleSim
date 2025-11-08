using BlackHoleModel = blackHole.Models.BlackHole;


namespace blackHole.Models;


public class SimState
{
    private BlackHoleModel CentralBlackHole;
    public List<Dust> Dusts;

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
    }

    public SimState(BlackHoleModel setBlackHole, List<Dust> setDustList)
    {
        CentralBlackHole = setBlackHole;
        Dusts = setDustList;
        Time = 0.0;
        FrameCount = 0;
        IsRunning = false;
    }





}