using Arcadia.EA;
using Arcadia.EA.Constants;
using Arcadia.EA.Ports;
using Arcadia.Hosting;
using Arcadia.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NPTicket;
using NPTicket.Verification;
using NPTicket.Verification.Keys;
using System.Collections.Immutable;
using System.Globalization;

namespace Arcadia.Handlers;

public class FeslHandler
{
    private readonly ImmutableDictionary<string, Func<Packet, Task>> _handlers;

    private readonly ILogger<FeslHandler> _logger;
    private readonly IOptions<ArcadiaSettings> _settings;
    private readonly FileServerSettings _fileServer;
    private readonly IPublicIpProvider _publicIp;
    private readonly SharedCounters _sharedCounters;
    private readonly ConnectionManager _sharedCache;
    private readonly IEAConnection _conn;
    private readonly Database _db;
    private readonly RecipeBlobStore _blobs;
    private readonly UdpSessionCache _udpCache;
    private readonly LobbyUdpServerPool _lobbyPool;

    private PlasmaSession? _plasma;
    private string clientString = string.Empty;
    private string partitionId = string.Empty;
    private string subDomain = string.Empty;

    private DateTimeOffset _lastClientPacketAt = DateTimeOffset.UtcNow;

    private readonly static TimeSpan PingPeriod = TimeSpan.FromSeconds(60);
    private readonly static TimeSpan MemCheckPeriod = TimeSpan.FromSeconds(120);

    private readonly static TimeSpan FeslSilenceThreshold = TimeSpan.FromSeconds(180);

    private static int? DefaultTheaterPort;

    private readonly Timer _pingTimer;
    private readonly Timer _memchTimer;
    private int _timersDisposed;

    public FeslHandler(IEAConnection conn, ILogger<FeslHandler> logger, IOptions<ArcadiaSettings> settings, IOptions<FileServerSettings> fileServerSettings, IPublicIpProvider publicIp, SharedCounters sharedCounters, ConnectionManager sharedCache, Database db, RecipeBlobStore blobs, UdpSessionCache udpCache, LobbyUdpServerPool lobbyPool)
    {
        _logger = logger;
        _settings = settings;
        _fileServer = fileServerSettings.Value;
        _publicIp = publicIp;
        _sharedCounters = sharedCounters;
        _sharedCache = sharedCache;
        _conn = conn;
        _db = db;
        _blobs = blobs;
        _udpCache = udpCache;
        _lobbyPool = lobbyPool;

        _pingTimer = new(async _ => await SendPing(), null, Timeout.Infinite, Timeout.Infinite);
        _memchTimer = new(async _ => await SendMemCheck(), null, Timeout.Infinite, Timeout.Infinite);
        DefaultTheaterPort ??= settings.Value.ListenPorts.First(PortExtensions.IsTheater);

        _handlers = new Dictionary<string, Func<Packet, Task>>()
        {
            ["fsys/Hello"] = HandleHello,
            ["fsys/MemCheck"] = HandleMemCheck,
            ["fsys/Ping"] = HandlePing,
            ["fsys/GetPingSites"] = HandleGetPingSites,
            ["fsys/Goodbye"] = HandleGoodbye,
            ["pnow/Start"] = HandlePlayNow,
            ["acct/NuPS3Login"] = HandleNuPs3Login,
            ["acct/PS3Login"] = HandleNuPs3Login,
            ["acct/NuGetPersonas"] = HandleNuGetPersonas,
            ["acct/NuLoginPersona"] = HandleNuLoginPersona,
            ["acct/NuGetTos"] = HandleGetTos,
            ["acct/GetTos"] = HandleGetTos,
            ["acct/LookupUserInfo"] = HandleLookupUserInfo,
            ["acct/NuLookupUserInfo"] = HandleLookupUserInfo,
            ["acct/NuGetEntitlements"] = HandleNuGetEntitlements,
            ["acct/GetEntitlementByBundle"] = HandleGetEntitlementByBundle,
            ["subs/GetEntitlementByBundle"] = HandleGetEntitlementByBundle,
            ["acct/GetLockerURL"] = HandleGetLockerUrl,
            ["recp/GetRecord"] = HandleGetRecord,
            ["recp/GetRecordAsMap"] = HandleGetRecordAsMap,
            ["recp/UpdateRecord"] = AcknowledgeRequest,
            ["recp/AddRecord"] = AcknowledgeRequest,
            ["recp/CreateRecord"] = AcknowledgeRequest,
            ["asso/GetAssociations"] = HandleGetAssociations,
            ["asso/AddAssociations"] = HandleAddAssociations,
            ["asso/DeleteAssociations"] = HandleDeleteAssociations,
            ["pres/PresenceSubscribe"] = HandlePresenceSubscribe,
            ["rank/GetStats"] = HandleGetStats,
            ["rank/GetTopNAndStats"] = HandleGetTopNAndStats,
            ["rank/GetRankedStats"] = HandleGetRankedStats,
            ["rank/GetRankedStatsForOwners"] = HandleGetRankedStatsForOwners,
            ["rank/UpdateStats"] = HandleUpdateStats,
            ["xmsg/GetMessages"] = HandleGetMessages,
            ["blob/ListBlobInfo"] = HandleListBlobInfo,
            ["blob/ListNewestBlobInfo"] = HandleListBlobInfo,
            ["blob/TopNBlobRatings"] = HandleTopNBlobs,
            ["blob/TopNBlobDownloads"] = HandleTopNBlobs,
            ["blob/AddBlob"] = HandleAddBlob,
            ["blob/GetBlob"] = HandleGetBlob,
            ["blob/DeleteBlob"] = HandleDeleteBlob,
            ["blob/UpdateBlob"] = AcknowledgeRequest,
            ["blob/RateBlob"] = AcknowledgeRequest,
            ["blob/RecordBlobDownload"] = AcknowledgeRequest,

            ["pres/SetPresenceStatus"] = AcknowledgeRequest,
            ["acct/NuGrantEntitlement"] = AcknowledgeRequest,
            ["acct/GetTelemetryToken"] = AcknowledgeRequest,
            ["acct/NuPS3AddAccount"] = AcknowledgeRequest,
            ["acct/PS3AddAccount"] = AcknowledgeRequest,
            ["xmsg/ModifySettings"] = AcknowledgeRequest,
            ["mtrx/ReportMetrics"] = AcknowledgeRequest,
            ["rank/GetStatsForOwners"] = AcknowledgeRequest,
            ["rank/ReportMetrics"] = AcknowledgeRequest,
            ["pnow/ReportMetrics"] = AcknowledgeRequest,
            ["pnow/Cancel"] = AcknowledgeRequest,
            ["achi/GetAchievementGroupDefinitions"] = AcknowledgeRequest,
            ["achi/GetAchievementDefinitionsByGroup"] = AcknowledgeRequest,
            ["achi/GetOwnerAchievementsByGroup"] = AcknowledgeRequest,
            ["achi/SynchAchievements"] = AcknowledgeRequest,
            ["achi/SetAchievements"] = AcknowledgeRequest,
            ["achi/GrantAchievements"] = AcknowledgeRequest,
        }.ToImmutableDictionary();
    }

    public async Task<PlasmaSession> HandleClientConnection(Stream network, string clientEndpoint, string serverEndpoint, CancellationToken ct)
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
            _logger.LogError(e, "Error in fesl: {Message}", e.Message);
        }
        finally
        {
            await DisposeTimers();
        }

        if (_plasma is not null)
        {
            await _sharedCache.RemovePlasmaSession(_plasma);
            _udpCache.Remove(_conn.RemoteAddress, _plasma.UID);
        }

        _logger.LogInformation("Closing FESL connection: {clientEndpoint} | {name}", clientEndpoint, _plasma?.NAME);

        return _plasma ?? new()
        {
            FeslConnection = _conn,
            UID = 0,
            NAME = string.Empty,
            LKEY = string.Empty,
            ClientString = string.Empty,
            PartitionId = string.Empty
        };
    }

    private static readonly ImmutableHashSet<string> PreAuthTransactions = ImmutableHashSet.Create(
        "fsys/Hello", "fsys/MemCheck", "fsys/Ping", "fsys/Goodbye",
        "acct/NuGetTos", "acct/GetTos", "acct/NuPS3Login", "acct/PS3Login");

    public async Task HandlePacket(Packet packet)
    {
        _lastClientPacketAt = DateTimeOffset.UtcNow;

        var reqTxn = packet.TXN;
        var packetType = packet.Type;
        var key = $"{packetType}/{reqTxn}";

        if (_plasma is null && !PreAuthTransactions.Contains(key))
        {
            _logger.LogWarning("Rejected pre-auth transaction {key} from {ep}", key, _conn.RemoteEndpoint);
            return;
        }

        _handlers.TryGetValue(key, out var handler);

        if (handler is null)
        {
            _logger.LogWarning("Unknown packet type: {type}, TXN: {txn}", packet.Type, reqTxn);
            return;
        }

        await handler(packet);
    }

    private async Task HandleHello(Packet request)
    {
        clientString = request["clientString"];
        if (!clientString.Contains("skate", StringComparison.OrdinalIgnoreCase))
            return;

        subDomain = clientString.Split('-').First().ToUpperInvariant();
        partitionId = $"/{request["sku"]}/{subDomain}";

        var publicAddress = _publicIp.CurrentAddress;
        var currentTime = DateTime.UtcNow.ToString("MMM-dd-yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        var serverHelloData = new Dictionary<string, string>
                {
                    { "domainPartition.domain", request["sku"] },
                    { "messengerIp", publicAddress },
                    { "messengerPort", $"{42069}" },
                    { "domainPartition.subDomain", subDomain },
                    { "TXN", "Hello" },
                    { "activityTimeoutSecs", "0" },
                    { "curTime", currentTime },
                    { "theaterIp", publicAddress },
                    { "theaterPort", $"{DefaultTheaterPort}" },
                    { "customUrl", BuildCustomUrl(publicAddress) }
                };

        var helloPacket = new Packet("fsys", FeslTransmissionType.SinglePacketResponse, request.Id, serverHelloData);
        await _conn.SendPacket(helloPacket);
        await SendMemCheck();
    }

    private string BuildCustomUrl(string publicAddress)
        => $"http://{publicAddress}:{_fileServer.Port}";

    private async Task HandleGoodbye(Packet request)
    {
        await _conn.Terminate();
    }

    private async Task HandlePlayNow(Packet request)
    {
        if (_plasma is null) throw new InvalidOperationException("PlayNow before login");
        var pnowId = _sharedCounters.GetNextPnowId();

        await _conn.SendPacket(
            new(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, new()
            {
                { "TXN", "Start" },
                { "id.id", $"{pnowId}" },
                { "id.partition", partitionId },
            })
        );

        var pnowResult = new Dictionary<string, string>
        {
            { "TXN", "Status" },
            { "id.id", $"{pnowId}" },
            { "id.partition", partitionId },
            { "sessionState", "COMPLETE" },
        };

        var isSkate2 = clientString.Contains("skate2", StringComparison.OrdinalIgnoreCase);

        var callerVariant = isSkate2 ? GameVariant.Skate2 : GameVariant.Skate1;
        var defaultChallengeType = "OnlineFreeSkate";
        var defaultChallengeKey = "-1";

        var sessionType = request["players.0.props.{sessionType}"];
        var isResetServer = sessionType == "resetServer";
        var challengeKey = PickClientPref(request.DataDict, "challenge_key", defaultChallengeKey);
        bool challengeTypePresent = TryPickClientPref(request.DataDict, "challenge_type", out var challengeTypeValue);
        var challengeType = challengeTypePresent ? challengeTypeValue : defaultChallengeType;
        var pingSite = PickClientPref(request.DataDict, "ping_site", "");

        var prefIsRanked = PickClientPrefBool(request.DataDict, "is_ranked", false);

        // fitTable without "-1;" = specific-challenge hunt: join or NOSERVER, never create (resetServer included).
        request.DataDict.TryGetValue("players.0.props.{fitTable-challenge_key}", out var fitTableChallengeKey);
        bool specificChallengeHunt = isSkate2
            && !string.IsNullOrWhiteSpace(fitTableChallengeKey)
            && !fitTableChallengeKey.Contains("-1;", StringComparison.Ordinal);

        var plan = isSkate2
            ? PnowPolicy.Skate2(isResetServer, challengeTypePresent, specificChallengeHunt, challengeKey != "-1")
            : PnowPolicy.Skate1(isResetServer, challengeTypePresent);

        GameServerListing[] candidates = plan.Search switch
        {
            PnowSearch.QuickMatch => _sharedCache.FindQuickMatchJoinable(callerVariant, _plasma.OnlinePlatformId).ToArray(),
            PnowSearch.Skate1ByType => _sharedCache.FindJoinableSkate1ByChallengeType(challengeType, _plasma.OnlinePlatformId).ToArray(),
            PnowSearch.Skate2ByType => _sharedCache.FindJoinableSkate2ByChallengeType(challengeType, _plasma.OnlinePlatformId).ToArray(),
            PnowSearch.ByChallengeKey => _sharedCache.FindJoinableByChallengeKey(callerVariant, challengeKey, _plasma.OnlinePlatformId).ToArray(),
            _ => Array.Empty<GameServerListing>(),
        };

        _logger.LogInformation(
            "PlayNow uid={uid} variant={v} session={s} search={search} type={type} key={key} hunt={hunt} candidates={n}",
            _plasma.UID, isSkate2 ? "Skate2" : "Skate1", sessionType, plan.Search,
            challengeType, challengeKey, specificChallengeHunt, candidates.Length);

        if (prefIsRanked && !plan.RankedExempt)
        {
            SetNoServerResult(pnowResult);
            _logger.LogInformation(
                "PlayNow NOSERVER (pref-is_ranked=true) uid={uid} variant={v}",
                _plasma.UID, isSkate2 ? "Skate2" : "Skate1");
        }
        else if (candidates.Length > 0)
        {
            SetJoinResult(pnowResult, candidates.Length, candidates[0].GID, candidates[0].LID);
        }
        else if (plan.MayCreate)
        {
            long newGameId = _sharedCounters.GetNextGameId();
            var variant = isSkate2 ? GameVariant.Skate2 : GameVariant.Skate1;
            var bVersion = isSkate2 ? "Skate2-221" : "Skate-221";
            var gameVersion = isSkate2 ? "222000/515823/515600/f" : bVersion;

            SetJoinResult(pnowResult, 1, newGameId, 257);

            var game = new GameServerListing()
            {
                PartitionId = _plasma.PartitionId,
                TheaterConnection = _plasma.TheaterConnection,
                UID = _plasma.UID,
                GID = newGameId,
                Platform = "PS3",
                OnlinePlatform = _plasma.OnlinePlatformId,
                LID = 257,
                Variant = variant,
                // S1 lobbies are always private (no in-game public toggle); S1 queries ignore the flag so they stay matchable.
                IsPrivate = !isSkate2 || isResetServer,
                ChallengeKey = challengeKey,
                UGID = "",
                EKEY = "RELAYKEY",
                SECRET = "",
                NAME = _plasma.NAME,

                Data = new()
                {
                    ["NAME"] = _plasma.NAME,
                    ["TYPE"] = "G",
                    ["INT-IP"] = _publicIp.CurrentAddress,
                    ["MAX-PLAYERS"] = variant == GameVariant.Skate2 ? "6" : "2",
                    ["B-maxObservers"] = "0",
                    ["B-numObservers"] = "0",
                    ["B-version"] = bVersion,
                    ["B-U-game_version"] = gameVersion,
                    ["B-U-challenge_type"] = challengeType,
                    ["B-U-challenge_key"] = challengeKey,
                    ["B-U-ping_site"] = pingSite,
                    ["B-U-is_private"] = (!isSkate2 || isResetServer) ? "true" : "false",
                    ["JOIN"] = "O",
                    ["TICKET"] = $"{_sharedCounters.GetNextTicket()}"
                }
            };

            _lobbyPool.Allocate(game);
            var allocatedPort = game.UdpPort > 0
                ? game.UdpPort.ToString()
                : (isSkate2 ? "12000" : "12001");
            game.Data["PORT"] = allocatedPort;
            game.Data["INT-PORT"] = allocatedPort;

            if (!game.HasConnectedPlayer(_plasma.UID))
            {
                if (_plasma.PID == 0)
                {
                    _plasma.PID = game.AllocateJoinerPid();
                }
                if (game.TryReserveJoiningSlot(_plasma, game.MaxPlayers))
                {
                    game.TryPromoteJoining(out _);
                }
            }

            await _sharedCache.AddGameListing(game, request.DataDict);
        }
        else
        {
            SetNoServerResult(pnowResult);
            _logger.LogInformation("PlayNow NOSERVER uid={uid} (no candidates, create not allowed)", _plasma.UID);
        }

        await _conn.SendPacket(new(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, pnowResult));
    }

    private static void SetNoServerResult(Dictionary<string, string> result)
    {
        result["props.{}"] = "50";
        result["resultType"] = PlayNowResultType.NOSERVER;
        result["props.{resultType}"] = PlayNowResultType.NOSERVER;
    }

    private static void SetJoinResult(Dictionary<string, string> result, int gameCount, long gid, long lid)
    {
        result["props.{}"] = "50";
        result["resultType"] = PlayNowResultType.JOIN;
        result["props.{resultType}"] = PlayNowResultType.JOIN;
        result["props.{games}.[]"] = $"{gameCount}";
        result["props.{games}.0.gid"] = $"{gid}";
        result["props.{games}.0.lid"] = $"{lid}";
    }

    private static bool PickClientPrefBool(IDictionary<string, string> dd, string fieldName, bool fallback)
    {
        var raw = PickClientPref(dd, fieldName, string.Empty);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return fallback;
    }

    private static string PickClientPref(IDictionary<string, string> dd, string fieldName, string fallback)
        => TryPickClientPref(dd, fieldName, out var value) ? value : fallback;

    private static bool TryPickClientPref(IDictionary<string, string> dd, string fieldName, out string value)
    {
        var canonical = $"players.0.props.{{pref-{fieldName}}}";
        if (dd.TryGetValue(canonical, out var pref) && !string.IsNullOrWhiteSpace(pref))
        {
            value = pref;
            return true;
        }

        foreach (var kv in dd)
        {
            var k = kv.Key;
            if (!k.StartsWith("players.0.props.{", StringComparison.Ordinal)) continue;
            if (!k.Contains(fieldName, StringComparison.Ordinal)) continue;
            if (k.EndsWith(".[]}", StringComparison.Ordinal) || k.EndsWith(".[]", StringComparison.Ordinal)) continue;
            if (!string.IsNullOrWhiteSpace(kv.Value) && kv.Value != "0")
            {
                value = kv.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private async Task HandleGetRecord(Packet request)
    {
        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "localizedMessage", "Nope" },
            { "errorContainer.[]", "0" },
            { "errorCode", "5000" },
        };

        await _conn.SendPacket(new Packet("recp", FeslTransmissionType.SinglePacketResponse, request.Id, responseData));
    }

    private async Task HandleGetRecordAsMap(Packet request)
    {
        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "TTL", "0" },
            { "state", "1" },
            { "values.{}", "0" }
        };

        await _conn.SendPacket(new Packet("recp", FeslTransmissionType.SinglePacketResponse, request.Id, responseData));
    }

    private async Task HandleGetStats(Packet request)
    {
        if (_plasma is null) throw new NotImplementedException();

        var responseData = new Dictionary<string, string>
        {
            { "TXN", "GetStats" },
        };

        var keysStr = request.DataDict["keys.[]"] ?? "0";
        responseData.Add("stats.[]", keysStr);

        var (keys, values) = ReadRequestedStats(request, keysStr);
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];

            responseData.Add($"stats.{i}.key", key);
            responseData.Add($"stats.{i}.value", values.GetValueOrDefault(key) ?? string.Empty);
        }

        var packet = new Packet("rank", FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    // Shared parse + guard + DB lookup for the rank/GetStats family. Callers keep their own
    // response shaping; keysRaw is passed in so each preserves its own "keys.[]" read semantics.
    private (string[] Keys, IReadOnlyDictionary<string, string> Values) ReadRequestedStats(Packet request, string keysRaw)
    {
        var keyCount = int.Parse(keysRaw, CultureInfo.InvariantCulture);
        if (keyCount > 201)
        {
            throw new($"Too many stat keys '{keyCount}' in a request!");
        }

        var keys = new string[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            keys[i] = request.DataDict[$"keys.{i}"];
        }

        return (keys, _db.GetStatsBySession(_plasma!, keys));
    }

    private async Task HandleGetRankedStats(Packet request)
    {
        if (_plasma is null) throw new NotImplementedException();

        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "rankedStats.[]", "1" },
            { "rankedStats.0.ownerId", request["owners.0.ownerId"] },
            { "rankedStats.0.ownerType", "1" },
        };

        var keysStr = request.DataDict["keys.[]"] ?? "0";
        responseData.Add("rankedStats.0.rankedStats.[]", keysStr);

        var (keys, values) = ReadRequestedStats(request, keysStr);
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];

            responseData.Add($"stats.{i}.key", key);
            responseData.Add($"stats.{i}.value", values.GetValueOrDefault(key) ?? string.Empty);
            responseData.Add($"stats.{i}.rank", "-1");
        }

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleGetTopNAndStats(Packet request)
    {
        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "stats.[]", "0" },
            { "stats.0.addStats.[]", "0" }
        };

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleGetRankedStatsForOwners(Packet request)
    {
        if (_plasma is null) throw new NotImplementedException();

        var ownersStr = request.DataDict.GetValueOrDefault("owners.[]") ?? "0";
        var keysStr = request.DataDict.GetValueOrDefault("keys.[]") ?? "0";

        var ownerCount = int.Parse(ownersStr, CultureInfo.InvariantCulture);

        var (keys, values) = ReadRequestedStats(request, keysStr);
        var keyCount = keys.Length;

        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "rankedStats.[]", ownerCount.ToString(CultureInfo.InvariantCulture) },
        };

        for (var i = 0; i < ownerCount; i++)
        {
            var ownerId = request.DataDict.GetValueOrDefault($"owners.{i}.ownerId") ?? "0";
            var ownerType = request.DataDict.GetValueOrDefault($"owners.{i}.ownerType") ?? "1";

            responseData.Add($"rankedStats.{i}.ownerId", ownerId);
            responseData.Add($"rankedStats.{i}.ownerType", ownerType);
            responseData.Add($"rankedStats.{i}.rankedStats.[]", keyCount.ToString(CultureInfo.InvariantCulture));

            for (var j = 0; j < keyCount; j++)
            {
                var key = keys[j];
                responseData.Add($"rankedStats.{i}.rankedStats.{j}.key", key);
                responseData.Add($"rankedStats.{i}.rankedStats.{j}.value", values.GetValueOrDefault(key) ?? "0");
                responseData.Add($"rankedStats.{i}.rankedStats.{j}.rank", "-1");
            }
        }

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleListBlobInfo(Packet request)
    {
        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "blobs.[]", "0" },
        };
        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleTopNBlobs(Packet request)
    {
        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "topBlobs.[]", "0" },
        };
        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleAddBlob(Packet request)
    {
        foreach (var kv in request.DataDict)
        {
            _logger.LogDebug("AddBlob field {Key}={Value}", kv.Key, kv.Value);
        }

        var uid = _plasma?.UID ?? 0;
        var contentType = ParseContentType(request);
        var blob = ExtractBlobBytes(request);

        if (blob is not null && uid != 0)
        {
            _blobs.Put(uid, contentType, blob);
        }
        else
        {
            _logger.LogWarning("AddBlob ignored uid={Uid} type={Type} bytes={Bytes}", uid, contentType, blob?.Length ?? -1);
        }

        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
        };

        if (request.DataDict.TryGetValue("name", out var blobName) && blobName is string name)
        {
            responseData["name"] = name;
        }

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleGetBlob(Packet request)
    {
        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
        };

        if (request.DataDict.TryGetValue("name", out var blobName) && blobName is string name)
        {
            responseData["name"] = name;
        }

        var ownerUid = ParseOwnerUid(request) ?? _plasma?.UID ?? 0;
        var contentType = ParseContentType(request);
        var stored = ownerUid != 0 ? _blobs.Get(ownerUid, contentType) : null;

        if (stored is not null && stored.Length > 0)
        {
            var hex = Convert.ToHexString(stored);
            responseData["data.[]"] = "1";
            responseData["data.0"] = hex;
            responseData["data"] = hex;
            responseData["size"] = stored.Length.ToString();
            _logger.LogInformation("GetBlob served uid={Uid} type={Type} size={Size}", ownerUid, contentType, stored.Length);
        }
        else
        {
            responseData["data.[]"] = "0";
        }

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleDeleteBlob(Packet request)
    {
        var uid = _plasma?.UID ?? 0;
        var contentType = ParseContentType(request);
        if (uid != 0)
        {
            _blobs.Delete(uid, contentType);
        }

        await AcknowledgeRequest(request);
    }

    private static int ParseContentType(Packet request)
    {
        if (request.DataDict.TryGetValue("type", out var t) && int.TryParse(t, out var ct)) return ct;
        if (request.DataDict.TryGetValue("contentType", out var t2) && int.TryParse(t2, out var ct2)) return ct2;
        return RecipeBlobStore.CT_Recipe;
    }

    private static long? ParseOwnerUid(Packet request)
    {
        foreach (var key in new[] { "ownerId", "owner.id", "userId", "uid", "owner" })
        {
            if (request.DataDict.TryGetValue(key, out var v) && long.TryParse(v, out var id)) return id;
        }
        return null;
    }

    private static byte[]? ExtractBlobBytes(Packet request)
    {
        var dd = request.DataDict;

        if (dd.TryGetValue("data.[]", out var countStr) && int.TryParse(countStr, out var count) && count > 0)
        {
            using var ms = new MemoryStream();
            for (var i = 0; i < count; i++)
            {
                if (dd.TryGetValue($"data.{i}", out var chunk) && chunk.Length > 0)
                {
                    var decoded = DecodeBlobField(chunk);
                    if (decoded is not null) ms.Write(decoded, 0, decoded.Length);
                }
            }
            if (ms.Length > 0) return ms.ToArray();
        }

        if (dd.TryGetValue("data", out var single) && single.Length > 0)
        {
            var decoded = DecodeBlobField(single);
            if (decoded is not null && decoded.Length > 0) return decoded;
        }

        if (dd.TryGetValue("content", out var alt) && alt.Length > 0)
        {
            var decoded = DecodeBlobField(alt);
            if (decoded is not null && decoded.Length > 0) return decoded;
        }

        return null;
    }

    private static byte[]? DecodeBlobField(string value)
    {
        // Wire percent-encodes '=' as %3d to protect base64 padding; unescape before decoding (no-op for hex).
        var decoded = value;
        try { decoded = Uri.UnescapeDataString(value); } catch { }

        try
        {
            if (decoded.Length >= 2 && (decoded.Length % 2 == 0) && IsHex(decoded))
            {
                return Convert.FromHexString(decoded);
            }
        }
        catch { }

        try
        {
            return Convert.FromBase64String(decoded);
        }
        catch { }

        return null;
    }

    private static bool IsHex(string v)
    {
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        return true;
    }

    private async Task HandlePresenceSubscribe(Packet request)
    {
        if (_plasma is null) throw new NotImplementedException();

        var responseData = new Dictionary<string, string>
        {
            { "TXN", "PresenceSubscribe" },
            { "responses.0.outcome", "0" },
            { "responses.[]", "1" },
            { "responses.0.owner.type", "1" },
            { "responses.0.owner.id", _plasma.UID.ToString() },
        };

        var packet = new Packet("pres", FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleLookupUserInfo(Packet request)
    {
        var responseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "userInfo.[]", request["userInfo.[]"] }
        };

        var queryCount = int.Parse(request["userInfo.[]"]);
        for (var i = 0; i < queryCount; i++)
        {
            var query = request[$"userInfo.{i}.userName"];
            responseData.Add($"userInfo.{i}.userName", query);

            if (string.IsNullOrEmpty(query)) continue;

            responseData.Add($"userInfo.{i}.userId", _sharedCache.GetOrMintUserId(partitionId, query).ToString());
        }

        var packet = new Packet("acct", FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleGetAssociations(Packet request)
    {
        if (_plasma is null) throw new NotImplementedException();

        var assoType = request.DataDict["type"] as string ?? string.Empty;
        var responseData = new Dictionary<string, string>
        {
            { "TXN", "GetAssociations" },
            { "domainPartition.domain", request.DataDict["domainPartition.domain"] },
            { "domainPartition.subDomain", request.DataDict["domainPartition.subDomain"] },
            { "owner.id", _plasma.UID.ToString() },
            { "owner.type", "1" },
            { "type", assoType },
            { "members.[]", "0" },
        };

        if (assoType == "PlasmaMute")
        {
            responseData.Add("maxListSize", "100");
            responseData.Add("owner.name", _plasma.NAME);
        }
        else
        {
            _logger.LogWarning("Unknown association type: {assoType}", assoType);
        }

        var packet = new Packet("asso", FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private Task HandleAddAssociations(Packet request) => RespondToAssociationWrite(request, "addRequests");
    private Task HandleDeleteAssociations(Packet request) => RespondToAssociationWrite(request, "deleteRequests");

    // Client unblocks only on a populated result.[] echoing owner/member/type/domainPartition verbatim; a bare ack stalls the post-match results board.
    private async Task RespondToAssociationWrite(Packet request, string reqTag)
    {
        var dd = request.DataDict;

        var count = 0;
        if (dd.TryGetValue($"{reqTag}.[]", out var countStr))
            int.TryParse(countStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);

        var response = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "type", request["type"] },
            { "result.[]", count.ToString(CultureInfo.InvariantCulture) },
        };

        foreach (var kv in dd)
        {
            if (kv.Key.StartsWith("domainPartition.", StringComparison.Ordinal))
                response[kv.Key] = kv.Value;
        }

        for (var i = 0; i < count; i++)
        {
            EchoKeyBlock(dd, response, $"{reqTag}.{i}.owner.", $"result.{i}.owner.");
            EchoKeyBlock(dd, response, $"{reqTag}.{i}.member.", $"result.{i}.member.");
            response[$"result.{i}.outcome"] = "0";
            response[$"result.{i}.mutual"] = dd.GetValueOrDefault($"{reqTag}.{i}.mutual") ?? "0";
        }

        await _conn.SendPacket(new Packet("asso", FeslTransmissionType.SinglePacketResponse, request.Id, response));
    }

    private static void EchoKeyBlock(Dictionary<string, string> src, Dictionary<string, string> dst, string srcPrefix, string dstPrefix)
    {
        foreach (var kv in src)
        {
            if (kv.Key.StartsWith(srcPrefix, StringComparison.Ordinal))
                dst[dstPrefix + kv.Key[srcPrefix.Length..]] = kv.Value;
        }
    }

    private async Task HandleGetPingSites(Packet request)
    {
        var serverIp = _plasma!.FeslConnection!.LocalAddress;
        var responseData = new Dictionary<string, string>
        {
            { "TXN", "GetPingSites" },
            { "pingSite.[]", "2" },
            { "pingSite.0.name", "arcadia" },
            { "pingSite.0.addr", serverIp },
            { "pingSite.0.type", "0" },
            { "pingSite.1.name", "arcadia-eu" },
            { "pingSite.1.addr", serverIp },
            { "pingSite.1.type", "0" },
            { "minPingSitesToPing", "0" }
        };

        var packet = new Packet("fsys", FeslTransmissionType.SinglePacketResponse, request.Id, responseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleGetTos(Packet request)
    {
        const string tos = "Welcome to Arcadia!\nBeware, here be dragons!";

        var data = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "version", "20426_17.20426_17" },
            { "tos", $"{System.Net.WebUtility.UrlEncode(tos).Replace('+', ' ')}" },
        };

        var packet = new Packet("acct", FeslTransmissionType.SinglePacketResponse, request.Id, data);
        await _conn.SendPacket(packet);
    }

    private async Task HandleNuGetPersonas(Packet request)
    {
        if (_plasma is null) throw new NotImplementedException();

        var loginResponseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "personas.[]", "1" },
            { "personas.0", _plasma.NAME },
        };

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, loginResponseData);
        await _conn.SendPacket(packet);
    }
    
    private async Task HandleNuLoginPersona(Packet request)
    {
        var loginResponseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "lkey", _plasma!.LKEY },
            { "userId", _plasma.UID.ToString() }
        };

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, loginResponseData);
        await _conn.SendPacket(packet);
    }

    private async Task HandleNuGetEntitlements(Packet request)
    {
        var response = new Dictionary<string, string>()
        {
            { "TXN", request.TXN },
        };

        response.Add("entitlements.[]", "0");

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, response);
        await _conn.SendPacket(packet);
    }

    private async Task HandleGetEntitlementByBundle(Packet request)
    {
        var response = new Dictionary<string, string>()
        {
            { "TXN", request.TXN },
            { "entitlements.[]", "0" },
        };

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, response);
        await _conn.SendPacket(packet);
    }

    private async Task HandleGetLockerUrl(Packet request)
    {
        var loginResponseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "url", $"http://{_publicIp.CurrentAddress}/arcadia.jsp" }
        };

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, loginResponseData);
        await _conn.SendPacket(packet);
    }

    private static readonly SkateSigningKey PsnSkateKey = new();

    private static string? DetectTicketPlatform(byte[] ticketBytes, Ticket ticket)
    {
        if (new TicketVerifier(ticketBytes, ticket, RpcnSigningKey.Instance).IsTicketValid())
            return "RPCN";
        if (new TicketVerifier(ticketBytes, ticket, PsnSkateKey).IsTicketValid())
            return "PSN";
        return null;
    }

    private sealed class SkateSigningKey : PsnSigningKey
    {
        public override string PublicKeyX => "a93f2d73da8fe51c59872fad192b832f8b9dabde8587233";
        public override string PublicKeyY => "93131936a54a0ea51117f74518e56aae95f6baff4b29f999";
    }

    private async Task HandleNuPs3Login(Packet request)
    {
        var ticketPayload = request["ticket"];

        // Ticket.ReadFromBytes only parses; verify expiry + signature below or a forged ticket can spoof any identity.
        Ticket ticket;
        string? platform;
        try
        {
            var ticketBytes = Convert.FromHexString(ticketPayload[1..]);
            ticket = Ticket.ReadFromBytes(ticketBytes);

            if (ticket.ExpiryDate < DateTimeOffset.UtcNow)
            {
                _logger.LogWarning("Rejected login: PSN ticket expired user={user} expiry={expiry:O}",
                    ticket.Username, ticket.ExpiryDate);
                await SendError(request, 102, "Ticket expired");
                return;
            }

            platform = DetectTicketPlatform(ticketBytes, ticket);
            if (platform is null)
            {
                _logger.LogWarning("Rejected login: PSN ticket signature verification failed user={user}",
                    ticket.Username);
                await SendError(request, 102, "Invalid ticket");
                return;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Rejected login: malformed/unparseable PSN ticket");
            await SendError(request, 102, "Invalid ticket");
            return;
        }

        var onlineId = ticket.Username;

        var creation = _sharedCache.CreatePlasmaConnectionEvicting(_conn, onlineId, clientString, partitionId);
        _plasma = creation.Session;
        _plasma.OnlinePlatformId = platform;

        if (creation.Evicted is not null)
        {
            _logger.LogWarning(
                "Evicted lingering session for '{name}' (partition={partition}, UID={uid}) — newest login wins",
                onlineId, partitionId, creation.Evicted.UID);

            // Same-connection relogin: registration already swapped, don't Terminate our own transport.
            if (!ReferenceEquals(creation.Evicted.FeslConnection, _conn))
            {
                try
                {
                    if (creation.Evicted.TheaterConnection is not null)
                        await creation.Evicted.TheaterConnection.Terminate();
                }
                catch (Exception e) { _logger.LogDebug(e, "Eviction: old Theater Terminate failed"); }

                try
                {
                    if (creation.Evicted.FeslConnection is not null)
                        await creation.Evicted.FeslConnection.Terminate();
                }
                catch (Exception e) { _logger.LogDebug(e, "Eviction: old FESL Terminate failed"); }
            }

            foreach (var lobbyId in creation.LobbiesToRelease)
                await _lobbyPool.ReleaseAsync(lobbyId);
        }

        _db.RecordLoginMetric(ticket, _plasma.OnlinePlatformId ?? string.Empty);

        var loginResponseData = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "lkey", _plasma.LKEY },
            { "userId", _plasma.UID.ToString() }
        };

        var loginPacket = new Packet("acct", FeslTransmissionType.SinglePacketResponse, request.Id, loginResponseData);
        await _conn.SendPacket(loginPacket);
    }

    private async Task HandleGetMessages(Packet request)
    {
        var response = new Dictionary<string, string>
        {
            { "TXN", request.TXN },
            { "messages.[]", "0" }
        };

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, response);
        await _conn.SendPacket(packet);
    }

    private async Task HandleUpdateStats(Packet request)
    {
        if (_plasma is null) throw new NotImplementedException();

        if (!int.TryParse(request["u.0.s.[]"], out var statsCount) || statsCount < 1)
        {
            await AcknowledgeRequest(request);
            return;
        }

        _logger.LogTrace("Client submitting {statsCount} stats!", statsCount);

        var stats = new Dictionary<string, string>();
        for (var i = 0; i < statsCount; i++)
        {
            stats.Add(request.DataDict[$"u.0.s.{i}.k"], request.DataDict[$"u.0.s.{i}.v"]);
        }

        _db.SetStatsBySession(_plasma, stats);
        await AcknowledgeRequest(request);
    }

    private async Task AcknowledgeRequest(Packet request)
    {
        var response = new Dictionary<string, string>
        {
            { "TXN", request.TXN }
        };

        var packet = new Packet(request.Type, FeslTransmissionType.SinglePacketResponse, request.Id, response);
        await _conn.SendPacket(packet);
    }

    private Task HandlePing(Packet _)
    {
        try { _pingTimer.Change(PingPeriod, PingPeriod); }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    private Task HandleMemCheck(Packet packet)
    {
        try { _memchTimer.Change(MemCheckPeriod, MemCheckPeriod); }
        catch (ObjectDisposedException) { }
        HandlePing(packet);

        return Task.CompletedTask;
    }

    private async Task SendMemCheck()
    {
        var memCheckData = new Dictionary<string, string>
            {
                { "TXN", "MemCheck" },
                { "memcheck.[]", "0" },
                { "type", "0" },
                { "salt", PacketUtils.GenerateSalt() }
            };

        var memcheckPacket = new Packet("fsys", FeslTransmissionType.SinglePacketRequest, 0, memCheckData);

        try
        {
            await _conn.SendPacket(memcheckPacket);
        }
        catch
        {
            await DisposeTimers();
        }
    }

    private async Task SendPing()
    {
        // Must run before the CanWrite guard below: a zombie still reports CanWrite=true and would otherwise never be reaped.
        if (_plasma is not null && DateTimeOffset.UtcNow - _lastClientPacketAt > FeslSilenceThreshold)
        {
            _logger.LogWarning(
                "FESL-SILENT reaper: no inbound from {endpoint} (uid={uid}, name={name}) for {s:F0}s — terminating connection",
                _conn.RemoteEndpoint, _plasma.UID, _plasma.NAME,
                (DateTimeOffset.UtcNow - _lastClientPacketAt).TotalSeconds);
            try { await _conn.Terminate(); }
            catch { }
            return;
        }

        if (_plasma?.FeslConnection?.NetworkStream?.CanWrite != true || _plasma.TheaterConnection?.NetworkStream?.CanWrite != true)
        {
            return;
        }

        var feslPing = new Packet("fsys", FeslTransmissionType.SinglePacketRequest, 0, new() { { "TXN", "Ping" } });
        var theaterPing = new Packet("PING", TheaterTransmissionType.Request, 0);

        try
        {
            await _conn.SendPacket(feslPing);
            await _plasma.TheaterConnection.SendPacket(theaterPing);
        }
        catch
        {
            await DisposeTimers();
        }
    }

    private async Task SendError(Packet req, int code, string? localizedMessage = null)
    {
        var response = new Dictionary<string, string>
        {
            { "TXN", req.TXN },
            { "localizedMessage", localizedMessage ?? string.Empty },
            { "errorContainer.[]", "0" },
            { "errorCode", $"{code}" }
        };

        var loginPacket = new Packet("acct", FeslTransmissionType.SinglePacketResponse, req.Id, response);
        await _conn.SendPacket(loginPacket);
    }

    // Idempotent: the SendPing/SendMemCheck failure paths race the connection-close finally,
    // and a second Change-after-dispose would throw inside an async-void timer callback.
    private async Task DisposeTimers()
    {
        if (Interlocked.Exchange(ref _timersDisposed, 1) != 0) return;
        await _pingTimer.DisposeAsync();
        await _memchTimer.DisposeAsync();
    }
}