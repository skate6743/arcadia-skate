using System.Net;

// Lobby UDP server configuration.
namespace Arcadia.Hosting
{
    public record LobbySettings
    {
        public bool Enabled { get; init; } = true;
        public string ListenAddress { get; init; } = IPAddress.Any.ToString();
        public string EKey { get; init; } = "RELAYKEY";

        public int BasePort { get; init; } = 17000;
        public int MaxLobbies { get; init; } = 500;
    }
}
