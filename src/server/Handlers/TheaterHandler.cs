using Arcadia.EA;
using Arcadia.EA.Constants;
using Arcadia.Hosting;
using Arcadia.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Net.Sockets;

namespace Arcadia.Handlers;

public class TheaterHandler
{
    private readonly ILogger<TheaterHandler> _logger;
    private readonly SharedCounters _sharedCounters;
    private readonly ConnectionManager _sharedCache;
    private readonly UdpSessionCache _udpSessionCache;
    private readonly LobbyUdpServerPool _lobbyPool;
    private readonly IEAConnection _conn;
    private readonly DebugSettings _dbgSettings;
    private readonly ArcadiaSettings _arcadiaSettings;
    private readonly IPublicIpProvider _publicIp;

    private readonly ImmutableDictionary<string, Func<Packet, Task>> _handlers;

    private PlasmaSession? _plasma;
    private string? _platform;
    private int _brackets;

    public TheaterHandler(IEAConnection conn, ILogger<TheaterHandler> logger, SharedCounters sharedCounters, ConnectionManager sharedCache, UdpSessionCache udpSessionCache, LobbyUdpServerPool lobbyPool, IOptions<DebugSettings> dbgOptions, IOptions<ArcadiaSettings> arcadiaOptions, IPublicIpProvider publicIp)
    {
        _logger = logger;
        _sharedCounters = sharedCounters;
        _sharedCache = sharedCache;
        _udpSessionCache = udpSessionCache;
        _lobbyPool = lobbyPool;
        _conn = conn;
        _dbgSettings = dbgOptions.Value;
        _arcadiaSettings = arcadiaOptions.Value;
        _publicIp = publicIp;

        _handlers = new Dictionary<string, Func<Packet, Task>>
        {
            ["CONN"] = HandleCONN,
            ["USER"] = HandleUSER,
            ["ECNL"] = HandleECNL,
            ["EGAM"] = HandleEGAM,
            ["GDAT"] = HandleGDAT,
            ["LLST"] = HandleLLST,
            ["GLST"] = HandleGLST,
            ["PENT"] = HandlePENT,
            ["UBRA"] = HandleUBRA,
            ["UGAM"] = HandleUGAM,
            ["RGAM"] = HandleRGAM,
            ["PLVT"] = HandlePLVT,
            ["UGDE"] = HandleUGDE,
            ["PING"] = HandlePING,
            ["PCNT"] = HandlePCNT,
            ["UPLA"] = HandleUPLA,
        }.ToImmutableDictionary();
    }

    public async Task HandleClientConnection(NetworkStream network, string clientEndpoint, string serverEndpoint, CancellationToken ct)
    {
        try
        {
            _conn.Initialize(network, clientEndpoint, serverEndpoint, ct);
            await foreach (var packet in _conn.ReceiveAsync(_logger))
            {
                await HandlePacket(packet);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error in theater: {Message}", e.Message);
        }

        _logger.LogInformation("Closing Theater connection: {clientEndpoint} | {name}", clientEndpoint, _plasma?.NAME);
    }

    public async Task HandlePacket(Packet packet)
    {
        if (!_handlers.TryGetValue(packet.Type, out var handler))
        {
            _logger.LogWarning("Unknown packet type: {type}", packet.Type);
            return;
        }

        await handler(packet);
    }

    private PlasmaSession RequirePlasma() =>
        _plasma ?? throw new InvalidOperationException("Theater packet received before USER auth");

    private GameServerListing? TryGetGame(Packet request)
    {
        RequirePlasma();
        if (!long.TryParse(request["GID"], out var gid)) return null;
        return _sharedCache.GetGameByGid(gid);
    }

    private async Task HandleCONN(Packet request)
    {
        _platform = request["PLAT"]?.ToLower();

        var response = new Dictionary<string, string>
        {
            ["TIME"] = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            ["TID"] = request["TID"],
            ["activityTimeoutSecs"] = "0",
            ["PROT"] = request["PROT"]
        };

        await _conn.SendPacket(new Packet("CONN", TheaterTransmissionType.OkResponse, 0, response));
    }

    private async Task HandleUSER(Packet request)
    {
        var lkey = request["LKEY"];
        _plasma = _sharedCache.PairTheaterConnection(_conn, lkey);

        var response = new Dictionary<string, string>
        {
            ["NAME"] = _plasma.NAME,
            ["TID"] = request["TID"]
        };

        await _conn.SendPacket(new Packet("USER", TheaterTransmissionType.OkResponse, 0, response));
    }

    private async Task HandleECNL(Packet request)
    {
        var response = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["LID"] = request["LID"],
            ["GID"] = request["GID"],
        };

        var plasma = RequirePlasma();
        var game = TryGetGame(request);
        if (game is not null)
        {
            await _sharedCache.RemovePlayerFromGame(game, plasma.UID);
        }

        await _conn.SendPacket(new Packet("ECNL", TheaterTransmissionType.OkResponse, 0, response));
    }

    // Do NOT send EGRQ/EGRS here: with no p2p host they NULL-deref and crash the recipient. Peers are learned via the UDP PLAYER_JOIN broadcast.
    private async Task HandleEGAM(Packet request)
    {
        var plasma = RequirePlasma();

        var game = TryGetGame(request);
        if (game is null)
        {
            var joinPlayerName = request["USER"];
            if (!string.IsNullOrWhiteSpace(joinPlayerName))
                game = _sharedCache.FindGameWithPlayer(plasma.PartitionId, joinPlayerName);

            game ??= ResolveInviteTarget(request, plasma);

            if (game is null)
            {
                await SendError(request);
                return;
            }
        }

        if (!game.IsHostedBy(plasma) && game.InProgress)
        {
            _logger.LogInformation(
                "EGAM rejected (game in progress): GID={gid} from uid={uid} ({name})",
                game.GID, plasma.UID, plasma.NAME);
            await SendError(request);
            return;
        }

        if (!game.IsHostedBy(plasma) && !await AwaitOpenGame(game))
        {
            await SendError(request);
            return;
        }

        plasma.PID = game.AllocateJoinerPid();
        if (!game.TryReserveJoiningSlot(plasma, game.MaxPlayers))
        {
            _logger.LogInformation(
                "EGAM rejected (lobby full): GID={gid} from uid={uid} ({name}) — connected={c} joining={j}",
                game.GID, plasma.UID, plasma.NAME, game.ConnectedCount, game.JoiningCount);
            await SendError(request);
            return;
        }

        var response = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["LID"] = $"{game.LID}",
            ["GID"] = $"{game.GID}",
        };

        await _conn.SendPacket(new Packet("EGAM", TheaterTransmissionType.OkResponse, 0, response));

        await SendEGEG_ToPlayerInQueue(request, game.GID);
    }

    private GameServerListing? ResolveInviteTarget(Packet request, PlasmaSession plasma)
    {
        if (!long.TryParse(request["R-UID"], out var friendUid) || friendUid <= 0)
            long.TryParse(request["UID"], out friendUid);
        if (friendUid <= 0 || friendUid == plasma.UID) return null;

        var game = _sharedCache.FindGameByMemberUid(friendUid);
        if (game is null || game.HasConnectedPlayer(plasma.UID)) return null;
        return game;
    }

    private async Task SendError(Packet request)
    {
        var response = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["localizedMessage"] = "Generic error",
            ["errorContainer"] = "0",
            ["errorCode"] = "100"
        };

        await _conn.SendPacket(new Packet(request.Type, TheaterTransmissionType.OkResponse, 0, response));
    }

    private async Task<bool> AwaitOpenGame(GameServerListing game)
    {
        if (_dbgSettings.DisableTheaterJoinTimeout) return true;

        const int MaxRetries = 30;
        for (var retries = 0; !game.CanJoin; retries++)
        {
            _logger.LogDebug("Waiting for host game to open ({retry}/{max})...", retries, MaxRetries);
            await Task.Delay(1000);

            if (game.TheaterConnection?.NetworkStream is null)
            {
                _logger.LogError("Not connecting to game, host not connected");
                return false;
            }

            if (retries >= MaxRetries)
            {
                _logger.LogWarning("AwaitOpenGame timeout for GID={gid} after {sec}s — host did not settle into joinable state",
                    game.GID, MaxRetries);
                return false;
            }
        }

        return true;
    }

    private async Task HandleGDAT(Packet request)
    {
        var game = TryGetGame(request);
        if (game is null && long.TryParse(request["UID"], out var inviterUid) && inviterUid > 0)
            game = _sharedCache.FindGameByMemberUid(inviterUid);

        if (game is null || game.TheaterConnection is null)
        {
            await SendError(request);
            return;
        }

        var host = _sharedCache.FindSessionByUID(game.UID) ?? RequirePlasma();

        var info = game.Data;
        var response = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["LID"] = $"{game.LID}",
            ["GID"] = $"{game.GID}",
            ["HU"] = $"{host.UID}",
            ["HN"] = host.NAME,
            ["N"] = host.NAME,
            ["I"] = game.TheaterConnection.RemoteAddress,
            ["P"] = PickServerInfo(info, "PORT", "12000"),
            ["AP"] = $"{game.ConnectedCount}",
            ["MP"] = PickServerInfo(info, "MAX-PLAYERS", "6"),
            ["JP"] = $"{game.JoiningCount}",
            ["PL"] = "PS3",
            ["NF"] = "0",
            ["F"] = "0",
            ["TYPE"] = PickServerInfo(info, "TYPE", "G"),
            ["J"] = PickServerInfo(info, "JOIN", "O"),
            ["B-version"] = PickServerInfo(info, "B-version", ""),
            ["B-numObservers"] = PickServerInfo(info, "B-numObservers", "0"),
            ["B-maxObservers"] = PickServerInfo(info, "B-maxObservers", "0"),
            ["B-U-challenge_key"] = PickServerInfo(info, "B-U-challenge_key", ""),
            ["B-U-challenge_type"] = PickServerInfo(info, "B-U-challenge_type", ""),
            ["B-U-game_version"] = PickServerInfo(info, "B-U-game_version", PickServerInfo(info, "B-version", "")),
            ["B-U-max_players"] = PickServerInfo(info, "B-U-max_players", PickServerInfo(info, "MAX-PLAYERS", "6")),
        };

        await _conn.SendPacket(new Packet("GDAT", TheaterTransmissionType.OkResponse, 0, response));
        await SendGDET(request, game);
    }

    private async Task SendGDET(Packet request, GameServerListing game)
    {
        var response = new Dictionary<string, string>
        {
            ["LID"] = $"{game.LID}",
            ["GID"] = $"{game.GID}",
            ["TID"] = request["TID"],
            ["UGID"] = game.UGID
        };

        var maxPlayers = int.Parse(game.Data["MAX-PLAYERS"]);
        for (var i = 0; i < maxPlayers; i++)
        {
            var pdatId = $"D-pdat{i:D2}";
            if (!game.Data.TryGetValue(pdatId, out var pdat) || string.IsNullOrEmpty(pdat)) break;
            response.Add(pdatId, pdat);
        }

        await _conn.SendPacket(new Packet("GDET", TheaterTransmissionType.OkResponse, 0, response));
    }

    private async Task SendEGEG_ToPlayerInQueue(Packet request, long gid)
    {
        var plasma = RequirePlasma();
        var game = _sharedCache.GetGameByGid(gid)
            ?? throw new InvalidOperationException($"GID {gid} vanished before EGEG");

        if (!game.TryPromoteJoining(out var player) || player?.TheaterConnection is null)
        {
            _logger.LogWarning("EGEG: no joining player to promote for GID={gid}", gid);
            return;
        }

        if (game.TheaterConnection is null) throw new InvalidOperationException("Host has no Theater connection");

        var udpPort = PickServerInfo(game.Data, "INT-PORT", "12000");

        var response = new Dictionary<string, string>
        {
            ["PL"] = game.Platform,
            ["TICKET"] = game.Data["TICKET"],
            ["PID"] = $"{player.PID}",
            ["P"] = udpPort,
            ["HUID"] = $"{player.UID}",
            ["INT-PORT"] = udpPort,
            ["EKEY"] = game.EKEY,
            ["INT-IP"] = PickServerInfo(game.Data, "INT-IP", _publicIp.CurrentAddress),
            ["UGID"] = game.UGID,
            ["I"] = PickServerInfo(game.Data, "INT-IP", _publicIp.CurrentAddress),
            ["LID"] = $"{game.LID}",
            ["GID"] = $"{game.GID}"
        };

        _udpSessionCache.Register(
            player.TheaterConnection.RemoteAddress,
            new UdpSessionCache.ClientInfo(player.UID, player.NAME, PlayerRef: player.PID, game.Platform));

        _logger.LogInformation(
            "EGEG → {to}: advertising UDP endpoint {ip}:{port} (GID={gid}, LobbyId={lobbyId}, variant={variant})",
            player.TheaterConnection.RemoteAddress, response["INT-IP"], udpPort,
            game.GID, game.LobbyId, game.Variant);

        await player.TheaterConnection.SendPacket(new Packet("EGEG", TheaterTransmissionType.OkResponse, 0, response));
    }

    private async Task HandleLLST(Packet request)
    {
        var plasma = RequirePlasma();

        var header = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["NUM-LOBBIES"] = "1"
        };
        await _conn.SendPacket(new Packet("LLST", TheaterTransmissionType.OkResponse, 0, header));

        var body = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["LID"] = request["LID"],
            ["PASSING"] = "1",
            ["NAME"] = plasma.PartitionId.Split('/', StringSplitOptions.TrimEntries).LastOrDefault()?.ToLower() ?? "arcadia",
            ["LOCALE"] = "en_US",
            ["MAX-GAMES"] = "1000",
            ["FAVORITE-GAMES"] = "0",
            ["FAVORITE-PLAYERS"] = "0",
            ["NUM-GAMES"] = $"{_sharedCache.GetPartitionServers(plasma.PartitionId).Length}"
        };
        await _conn.SendPacket(new Packet("LDAT", TheaterTransmissionType.OkResponse, 0, body));
    }

    public async Task HandleGLST(Packet request)
    {
        var plasma = RequirePlasma();
        var games = _sharedCache.GetJoinablePartitionServers(plasma.PartitionId, plasma.OnlinePlatformId);

        var header = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["LID"] = request["LID"],
            ["LOBBY-NUM-GAMES"] = $"{games.Length}",
            ["LOBBY-MAX-GAMES"] = "100",
            ["FAVORITE-GAMES"] = "0",
            ["FAVORITE-PLAYERS"] = "0",
            ["NUM-GAMES"] = $"{games.Length}"
        };
        await _conn.SendPacket(new Packet("GLST", TheaterTransmissionType.OkResponse, 0, header));

        foreach (var game in games)
        {
            var hostIp = game.TheaterConnection?.RemoteAddress
                ?? throw new InvalidOperationException("Game in joinable list has no host connection");

            var gameData = new Dictionary<string, string>
            {
                ["TID"] = request["TID"],
                ["LID"] = request["LID"],
                ["GID"] = $"{game.GID}",
                ["HN"] = game.NAME,
                ["HU"] = $"{game.UID}",
                ["N"] = game.NAME,
                ["I"] = hostIp,
                ["P"] = game.Data["PORT"],
                ["MP"] = game.Data["MAX-PLAYERS"],
                ["F"] = "0",
                ["NF"] = "0",
                ["J"] = game.Data["JOIN"],
                ["TYPE"] = game.Data["TYPE"],
                ["B-version"] = game.Data["B-version"],
                ["B-numObservers"] = game.Data["B-numObservers"],
                ["B-maxObservers"] = game.Data["B-maxObservers"],
                ["AP"] = $"{game.ConnectedCount}"
            };

            await _conn.SendPacket(new Packet("GDAT", TheaterTransmissionType.OkResponse, 0, gameData));
        }
    }

    private async Task HandlePENT(Packet request)
    {
        var response = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["PID"] = request["PID"]
        };
        await _conn.SendPacket(new Packet("PENT", TheaterTransmissionType.OkResponse, 0, response));
    }

    private async Task HandleUBRA(Packet request)
    {
        if (request["START"] == "1")
        {
            _brackets += 2;
            return;
        }

        var reqTid = int.Parse(request["TID"]);
        var originalTid = reqTid - _brackets / 2;

        for (var i = 0; i < _brackets; i++)
        {
            var response = new Packet(request.Type, TheaterTransmissionType.OkResponse, 0);
            response["TID"] = $"{originalTid + i}";
            await _conn.SendPacket(response);
        }
        _brackets = 0;
    }

    private Task HandleUGDE(Packet request)
    {
        var game = TryGetGame(request)
            ?? throw new InvalidOperationException($"UGDE for unknown GID={request["GID"]}");

        _sharedCache.UpsertGameServerDataByGid(game.GID, request.DataDict);

        return Task.CompletedTask;
    }

    private Task HandleUGAM(Packet request)
    {
        var plasma = RequirePlasma();
        var game = TryGetGame(request)
            ?? throw new InvalidOperationException($"UGAM for unknown GID={request["GID"]}");

        if (!game.IsHostedBy(plasma)) return Task.CompletedTask;

        _sharedCache.UpsertGameServerDataByGid(game.GID, request.DataDict);

        return Task.CompletedTask;
    }

    private async Task HandleRGAM(Packet request)
    {
        var game = TryGetGame(request)
            ?? throw new InvalidOperationException($"RGAM for unknown GID={request["GID"]}");

        await _conn.SendPacket(new Packet("RGAM", TheaterTransmissionType.OkResponse, 0, new()
        {
            ["TID"] = request["TID"]
        }));

        await _sharedCache.RemoveGameListing(game);
    }

    private async Task HandlePLVT(Packet request)
    {
        var game = TryGetGame(request)
            ?? throw new InvalidOperationException($"PLVT for unknown GID={request["GID"]}");

        var pid = int.Parse(request["PID"]);
        var player = game.FindConnectedByPid(pid);
        if (player is not null)
        {
            await _sharedCache.RemovePlayerFromGame(game, player.UID);
        }

        await _conn.SendPacket(new Packet("PLVT", TheaterTransmissionType.OkResponse, 0, new()
        {
            ["TID"] = request["TID"]
        }));
    }

    private async Task HandlePCNT(Packet request)
    {
        var plasma = RequirePlasma();
        var response = new Dictionary<string, string>
        {
            ["TID"] = request["TID"],
            ["LID"] = request["LID"],
            ["COUNT"] = $"{_sharedCache.GetPartitionPlayerCount(plasma.PartitionId)}"
        };
        await _conn.SendPacket(new Packet("PCNT", TheaterTransmissionType.OkResponse, 0, response));
    }

    private async Task HandleUPLA(Packet request)
    {
        await _conn.SendPacket(new Packet("UPLA", TheaterTransmissionType.OkResponse, 0, new()
        {
            ["TID"] = request["TID"]
        }));
    }

    private static Task HandlePING(Packet _) => Task.CompletedTask;

    private static string PickServerInfo(IDictionary<string, string> info, string key, string fallback)
        => info.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}
