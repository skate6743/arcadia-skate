namespace Arcadia.EA.Ports;

// Only Skate 1 and Skate 2 PS3 titles are supported.
public enum FeslGamePort : int
{
    Skate2 = 18040,
    Skate1 = 18231,
}

public enum TheaterGamePort : int
{
    Skate2 = 18126,
    Skate1 = 18236,
}

public static class PortExtensions
{
    public static bool IsFeslPort(int port) => Enum.IsDefined(typeof(FeslGamePort), port);
    public static bool IsTheater(int port) => Enum.IsDefined(typeof(TheaterGamePort), port);
}
