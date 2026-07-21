using System.Collections.Concurrent;
using System.Collections.Immutable;
using Arcadia.EA;
using Arcadia.Hosting;
using Microsoft.Extensions.Logging;

namespace Arcadia.Storage;

public class ConnectionManager(ILogger<ConnectionManager> logger, SharedCounters counters, LobbyUdpServerPool lobbyPool)
{
    private static readonly ImmutableArray<string> DataKeyBlacklist = ["TID", "PID"];

    private readonly ILogger<ConnectionManager> _logger = logger;
    private readonly SharedCounters _counters = counters;
    private readonly LobbyUdpServerPool _lobbyPool = lobbyPool;

    private readonly List<GameServerListing> _gameServers = [];
    private readonly List<PlasmaSession> _connections = [];

    // All reads and writes of _gameServers/_connections hold this lock (plain List<T>).
    // Never await inside it; it is non-reentrant.
    private readonly object _sync = new();

    private readonly ConcurrentDictionary<(string Partition, string Name), long> _stickyUids = new();

    public readonly record struct SessionCreation(PlasmaSession Session, PlasmaSession? Evicted, IReadOnlyList<int> LobbiesToRelease);

    public SessionCreation CreatePlasmaConnectionEvicting(IEAConnection fesl, string onlineId, string clientString, string partitionId)
    {
        bool returning = _stickyUids.TryGetValue((partitionId, onlineId), out long uid);
        if (!returning)
        {
            uid = _stickyUids.GetOrAdd((partitionId, onlineId), _ => _counters.GetNextUserId());
        }

        PlasmaSession result = new()
        {
            FeslConnection = fesl,
            UID = uid,
            LKEY = SharedCounters.GenerateLKey(),
            NAME = onlineId,
            ClientString = clientString,
            PartitionId = partitionId
        };

        PlasmaSession? evicted;
        List<int> toRelease = [];
        lock (_sync)
        {
            evicted = _connections.FirstOrDefault(x => x.PartitionId == partitionId && x.NAME == onlineId);
            if (evicted is not null)
            {
                for (var i = _gameServers.Count - 1; i >= 0; i--)
                {
                    var game = _gameServers[i];
                    game.RemoveConnectedPlayer(evicted.UID);
                    game.RemoveJoiningPlayer(evicted.UID);
                    if (game.IsEmpty)
                    {
                        if (game.LobbyId > 0) toRelease.Add(game.LobbyId);
                        _gameServers.RemoveAt(i);
                    }
                }
                _connections.Remove(evicted);
            }
            _connections.Add(result);
        }

        if (returning)
        {
            _logger.LogInformation(
                "Sticky UID {uid} re-issued to returning player '{name}' (partition={partition})",
                uid, onlineId, partitionId);
        }

        return new SessionCreation(result, evicted, toRelease);
    }

    public PlasmaSession PairTheaterConnection(IEAConnection theater, string lkey)
    {
        PlasmaSession plasma;
        lock (_sync)
        {
            plasma = _connections.SingleOrDefault(x => x.LKEY == lkey) ?? throw new Exception("Failed to find a plasma session pair!");
        }
        plasma.TheaterConnection = theater;
        return plasma;
    }

    public async Task RemovePlasmaSession(PlasmaSession plasma)
    {
        var toRelease = new List<int>();
        lock (_sync)
        {
            if (!_connections.Contains(plasma)) return;

            var emptied = 0;
            for (var i = _gameServers.Count - 1; i >= 0; i--)
            {
                var game = _gameServers[i];
                // Sweep every listing, not just this partition: UIDs are server-global and cross-region joins put this UID in other partitions.
                game.RemoveConnectedPlayer(plasma.UID);
                game.RemoveJoiningPlayer(plasma.UID);

                if (game.IsEmpty)
                {
                    if (game.LobbyId > 0) toRelease.Add(game.LobbyId);
                    _gameServers.RemoveAt(i);
                    emptied++;
                }
            }
            if (emptied > 0)
            {
                _logger.LogInformation("Disconnect GC: removed {count} empty game(s) after UID={uid} left", emptied, plasma.UID);
            }

            _connections.Remove(plasma);
        }

        foreach (var lobbyId in toRelease)
        {
            await _lobbyPool.ReleaseAsync(lobbyId);
        }
    }

    public Task AddGameListing(GameServerListing game, Dictionary<string, string> data)
    {
        foreach (var line in data)
        {
            if (DataKeyBlacklist.Contains(line.Key)) continue;
            game.Data.TryAdd(line.Key, line.Value);
        }

        lock (_sync)
        {
            _gameServers.Add(game);
        }

        return Task.CompletedTask;
    }

    public async Task RemoveGameListing(GameServerListing game)
    {
        bool removed;
        lock (_sync)
        {
            removed = _gameServers.Remove(game);
            if (removed)
            {
                _logger.LogInformation("Removed game listing GID={gid} name='{name}'", game.GID, game.NAME);
            }
        }

        if (removed && game.LobbyId > 0)
        {
            await _lobbyPool.ReleaseAsync(game.LobbyId);
        }
    }

    public async Task<bool> RemovePlayerFromGame(GameServerListing game, long uid)
    {
        int? releaseId = null;
        bool emptied;
        lock (_sync)
        {
            game.RemoveConnectedPlayer(uid);
            game.RemoveJoiningPlayer(uid);

            emptied = game.IsEmpty;
            if (emptied)
            {
                if (_gameServers.Remove(game))
                {
                    _logger.LogInformation("Empty-lobby GC: removed GID={gid} name='{name}'", game.GID, game.NAME);
                    if (game.LobbyId > 0) releaseId = game.LobbyId;
                }
            }
        }

        if (releaseId.HasValue)
        {
            await _lobbyPool.ReleaseAsync(releaseId.Value);
        }

        return emptied;
    }

    public PlasmaSession? FindPartitionSessionByUser(string partitionId, string playerName)
    {
        lock (_sync)
        {
            return _connections.SingleOrDefault(x => x.PartitionId == partitionId && x.NAME == playerName);
        }
    }

    public long GetOrMintUserId(string partitionId, string playerName)
        => _stickyUids.GetOrAdd((partitionId, playerName), _ => _counters.GetNextUserId());

    public ImmutableArray<IEAConnection> GetPartitionMessengerConnections(string partitionId, long exceptUid)
    {
        lock (_sync)
        {
            return _connections
                .Where(x => x.PartitionId == partitionId && x.UID != exceptUid && x.MessengerConnection is not null)
                .Select(x => x.MessengerConnection!)
                .ToImmutableArray();
        }
    }

    public PlasmaSession? FindSessionByIp(string clientIp)
    {
        lock (_sync)
        {
            return _connections.LastOrDefault(x => x.FeslConnection?.RemoteAddress == clientIp);
        }
    }

    public PlasmaSession? FindSessionByLkey(string lkey)
    {
        lock (_sync)
        {
            return _connections.SingleOrDefault(x => x.LKEY == lkey);
        }
    }

    public PlasmaSession? FindSessionByUID(long uid)
    {
        lock (_sync)
        {
            return _connections.SingleOrDefault(x => x.UID == uid);
        }
    }

    public void UpsertGameServerDataByGid(long serverGid, IDictionary<string, string> data)
    {
        if (serverGid < 1)
        {
            _logger.LogWarning("Tried to update server with GID=0");
            return;
        }

        foreach (var item in DataKeyBlacklist)
        {
            data.Remove(item);
        }

        lock (_sync)
        {
            var server = _gameServers.SingleOrDefault(x => x.GID == serverGid) ?? throw new("Tried to update non-existant server");
            foreach (var line in data)
            {
                server.Data.Remove(line.Key, out _);
                server.Data.TryAdd(line.Key, line.Value);
            }
        }
    }

    public GameServerListing? FindGameWithPlayer(string partitionId, string playerName)
    {
        lock (_sync)
        {
            return _gameServers.FirstOrDefault(x =>
                x.PartitionId == partitionId &&
                x.ConnectedPlayers.Any(p => p.NAME.Equals(playerName)));
        }
    }

    public GameServerListing? FindGameWithPlayerByUid(string partitionId, long uid)
    {
        lock (_sync)
        {
            return _gameServers.FirstOrDefault(x => x.PartitionId == partitionId && x.HasConnectedPlayer(uid));
        }
    }

    public GameServerListing? FindGameByMemberUid(long uid)
    {
        if (uid <= 0) return null;
        lock (_sync)
        {
            return _gameServers.FirstOrDefault(x => x.HasConnectedPlayer(uid));
        }
    }

    // GID is server-global and unique; do not scope by partition (breaks cross-region joins).
    public GameServerListing? GetGameByGid(long serverGid)
    {
        if (serverGid is 0) return null;
        lock (_sync)
        {
            return _gameServers.SingleOrDefault(x => x.GID == serverGid);
        }
    }

    public ImmutableArray<GameServerListing> GetPartitionServers(string partitionId)
    {
        lock (_sync)
        {
            return _gameServers.Where(x => x.PartitionId == partitionId).ToImmutableArray();
        }
    }

    public ImmutableArray<GameServerListing> GetJoinablePartitionServers(string partitionId, string? platform)
    {
        lock (_sync)
        {
            return _gameServers
                .Where(x => x.PartitionId == partitionId
                    && x.OnlinePlatform == platform
                    && x.CanJoin
                    && !x.IsEmpty
                    && !x.InProgress
                    && x.ConnectedCount + x.JoiningCount < x.MaxPlayers)
                .ToImmutableArray();
        }
    }

    // Skate 1 intentionally ignores IsPrivate (no in-game public toggle, so private S1 lobbies must stay joinable).
    public ImmutableArray<GameServerListing> FindJoinableByChallengeKey(GameVariant variant, string challengeKey, string? platform)
    {
        lock (_sync)
        {
            return _gameServers
                .Where(x => x.Variant == variant
                    && x.OnlinePlatform == platform
                    && !x.InProgress
                    && x.CanJoin
                    && !x.IsEmpty
                    && (x.Variant == GameVariant.Skate1 || !x.IsPrivate)
                    && string.Equals(x.ChallengeKey, challengeKey, StringComparison.Ordinal)
                    && x.ConnectedCount + x.JoiningCount < x.MaxPlayers)
                .ToImmutableArray();
        }
    }

    // Skate 1 intentionally ignores IsPrivate (see FindJoinableByChallengeKey).
    public ImmutableArray<GameServerListing> FindQuickMatchJoinable(GameVariant variant, string? platform)
    {
        lock (_sync)
        {
            return _gameServers
                .Where(x => x.Variant == variant
                    && x.OnlinePlatform == platform
                    && !x.InProgress
                    && x.CanJoin
                    && !x.IsEmpty
                    && (x.Variant == GameVariant.Skate1 || !x.IsPrivate)
                    && x.ConnectedCount + x.JoiningCount < x.MaxPlayers)
                .ToImmutableArray();
        }
    }

    public ImmutableArray<GameServerListing> FindJoinableSkate2ByChallengeType(string challengeType, string? platform)
    {
        lock (_sync)
        {
            return _gameServers
                .Where(x => x.Variant == GameVariant.Skate2
                    && x.OnlinePlatform == platform
                    && !x.InProgress
                    && x.CanJoin
                    && !x.IsEmpty
                    && !x.IsPrivate
                    && x.Data.TryGetValue("B-U-challenge_type", out var ct)
                    && string.Equals(ct, challengeType, StringComparison.Ordinal)
                    && x.ConnectedCount + x.JoiningCount < x.MaxPlayers)
                .ToImmutableArray();
        }
    }

    public ImmutableArray<GameServerListing> FindJoinableSkate1ByChallengeType(string challengeType, string? platform)
    {
        lock (_sync)
        {
            return _gameServers
                .Where(x => x.Variant == GameVariant.Skate1
                    && x.OnlinePlatform == platform
                    && !x.InProgress
                    && x.CanJoin
                    && !x.IsEmpty
                    && x.Data.TryGetValue("B-U-challenge_type", out var ct)
                    && string.Equals(ct, challengeType, StringComparison.Ordinal)
                    && x.ConnectedCount + x.JoiningCount < x.MaxPlayers)
                .ToImmutableArray();
        }
    }

    public int GetPartitionPlayerCount(string partitionId)
    {
        lock (_sync)
        {
            return _connections.Count(x => x.PartitionId == partitionId);
        }
    }

    public ImmutableArray<GameServerListing> GetAllServersInternal()
    {
        lock (_sync)
        {
            return [.. _gameServers];
        }
    }

    public ServerStats GetStats()
    {
        lock (_sync)
        {
            int s1Signed = 0, s2Signed = 0, psn = 0, rpcn = 0;
            foreach (var c in _connections)
            {
                if (c.ClientString.Contains("skate2", StringComparison.OrdinalIgnoreCase)) s2Signed++;
                else s1Signed++;
                if (c.OnlinePlatformId == "PSN") psn++;
                else if (c.OnlinePlatformId == "RPCN") rpcn++;
            }

            int s1Lobbies = 0, s2Lobbies = 0, s1InGame = 0, s2InGame = 0;
            var lobbies = new List<LobbyStat>(_gameServers.Count);
            foreach (var g in _gameServers)
            {
                bool s2 = g.Variant == GameVariant.Skate2;
                if (s2) { s2Lobbies++; s2InGame += g.ConnectedCount; }
                else { s1Lobbies++; s1InGame += g.ConnectedCount; }

                lobbies.Add(new LobbyStat(
                    s2 ? "skate2" : "skate1",
                    g.ConnectedCount,
                    g.MaxPlayers,
                    g.IsPrivate,
                    g.InProgress,
                    g.OnlinePlatform,
                    g.ChallengeKey,
                    g.ConnectedPlayers.Select(p => p.NAME).ToArray()));
            }

            return new ServerStats(
                DateTimeOffset.UtcNow,
                new GameSplit(_connections.Count, s1Signed, s2Signed),
                new GameSplit(_gameServers.Count, s1Lobbies, s2Lobbies),
                new GameSplit(s1InGame + s2InGame, s1InGame, s2InGame),
                new PlatformSplit(psn, rpcn),
                lobbies);
        }
    }
}
