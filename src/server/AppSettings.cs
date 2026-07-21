using Arcadia.EA.Ports;

namespace Arcadia;

public record ArcadiaSettings
{
    public string ListenAddress { get; init; } = System.Net.IPAddress.Loopback.ToString();

    public string PublicAddress { get; init; } = string.Empty;

    public int[] ListenPorts { get; init; } = [
        (int)TheaterGamePort.Skate1,
        (int)FeslGamePort.Skate1,
        (int)FeslGamePort.Skate2,
    ];

    public int[] TheaterPorts { get; init; } = [];

    public int MessengerPort { get; init; } = 42069;
}

public record FileServerSettings
{
    public bool EnableCdn { get; init; } = true;
    public string ContentRoot { get; init; } = "static";
    public int Port { get; init; } = 80;
}

public record Skate2Settings
{
    public int LockstepInputRateHz { get; init; } = 15;
}

public record DnsSettings
{
    public bool EnableDns { get; init; }
    public string ServerAddress { get; init; } = string.Empty;
    public int DnsPort { get; init; } = 53;
}

public record DiscordSettings
{
    public bool EnableBot { get; init; } = false;
    public string BotToken { get; init; } = string.Empty;
    public ulong[] Channels { get; init; } = [];
}

public record DebugSettings
{
    public bool WriteSslDebugKeys { get; init; }
    public bool EnableFileLogging { get; init; }
    public bool DisableTheaterJoinTimeout { get; init; }
    public bool ForcePlaintext { get; set; }
    public bool DisableDatabase { get; init; }
}