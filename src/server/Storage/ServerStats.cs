namespace Arcadia.Storage;

public sealed record ServerStats(
    DateTimeOffset GeneratedAt,
    GameSplit SignedIn,
    GameSplit Lobbies,
    GameSplit InLobbies,
    PlatformSplit Platforms,
    IReadOnlyList<LobbyStat> LobbyList);

public sealed record GameSplit(int Total, int Skate1, int Skate2);

public sealed record PlatformSplit(int Psn, int Rpcn);

public sealed record LobbyStat(
    string Game,
    int Players,
    int MaxPlayers,
    bool Private,
    bool InProgress,
    string? Platform,
    string ChallengeKey,
    IReadOnlyList<string> PlayerNames);
