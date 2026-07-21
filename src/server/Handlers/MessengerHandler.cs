using System.Collections.Immutable;
using System.Net.Sockets;
using Arcadia.EA;
using Arcadia.EA.Constants;
using Arcadia.Storage;
using Microsoft.Extensions.Logging;

namespace Arcadia.Handlers;

public class MessengerHandler
{
    private readonly ILogger<MessengerHandler> _logger;
    private readonly IEAConnection _conn;
    private readonly ConnectionManager _storage;

    private readonly ImmutableDictionary<string, Func<Packet, Task>> _handlers;

    private PlasmaSession? _plasma;

    public MessengerHandler(IEAConnection conn, ILogger<MessengerHandler> logger, ConnectionManager storage)
    {
        _logger = logger;
        _conn = conn;
        _storage = storage;

        _handlers = new Dictionary<string, Func<Packet, Task>>
        {
            ["AUTH"] = HandleAUTH,
            ["RGET"] = HandleRGET,

            ["PSET"] = AcknowledgeRequest,
            ["PADD"] = HandlePADD,
            ["RADD"] = AcknowledgeRequest,
            ["GINV"] = HandleGINV,
        }.ToImmutableDictionary();
    }

    public async Task HandleClientConnection(NetworkStream network, string clientEndpoint, string serverEndpoint, CancellationToken ct)
    {
        _conn.Initialize(network, clientEndpoint, serverEndpoint, ct);
        await foreach (var packet in _conn.ReceiveAsync(_logger))
        {
            await HandlePacket(packet);
        }
    }

    public async Task HandlePacket(Packet packet)
    {
        var packetType = packet.Type;
        _handlers.TryGetValue(packetType, out var handler);

        if (handler is null)
        {
            _logger.LogWarning("Unknown packet type: {type}", packetType);
            return;
        }

        await handler(packet);
    }

    private async Task HandleAUTH(Packet request)
    {
        var lkey = request["LKEY"];
        var plasma = _storage.FindSessionByLkey(lkey);

        if (string.IsNullOrWhiteSpace(lkey) || plasma is null)
        {
            throw new Exception("No plasma session found with requested LKEY");
        }

        _plasma = plasma;
        plasma.MessengerConnection = _conn;

        var response = new Dictionary<string, string>
        {
            ["TTID"] = "0",
            ["TITL"] = "A Game",
            ["ID"] = request["ID"],
            ["USER"] = $"{request["USER"]}@messaging.ea.com/eagames/{plasma.PartitionId}"
        };

        var packet = new Packet(request.Type, TheaterTransmissionType.OkResponse, 0, response);
        await _conn.SendPacket(packet);

        var others = _storage.GetPartitionMessengerConnections(plasma.PartitionId, plasma.UID);
        if (others.Length > 0)
        {
            var notice = BuildPresenceNotice(plasma.NAME);
            await Task.WhenAll(others.Select(c => c.SendPacket(notice)));
        }
    }

    private async Task HandleRGET(Packet request)
    {
        var response = new Dictionary<string, string>
        {
            ["ID"] = request["ID"],
            ["SIZE"] = "0",
        };

        var packet = new Packet(request.Type, TheaterTransmissionType.OkResponse, 0, response);
        await _conn.SendPacket(packet);
    }

    private async Task HandlePADD(Packet request)
    {
        await AcknowledgeRequest(request);

        var friendName = request["USER"];
        if (_plasma is null || string.IsNullOrEmpty(friendName)) return;

        var friend = _storage.FindPartitionSessionByUser(_plasma.PartitionId, friendName);
        if (friend is null) return;

        await _conn.SendPacket(BuildPresenceNotice(friend.NAME));
    }

    private async Task HandleGINV(Packet request)
    {
        await AcknowledgeRequest(request);

        var targetName = request["USER"];
        if (_plasma is null || string.IsNullOrEmpty(targetName)) return;

        var target = _storage.FindPartitionSessionByUser(_plasma.PartitionId, targetName);
        if (target?.MessengerConnection is null) return;

        var notice = new Dictionary<string, string>
        {
            ["HOST"] = _plasma.NAME,        // target's client resolves the invite by this name via GetByName(HOST)
            ["USER"] = targetName,
            ["TYPE"] = "I",
            ["SESS"] = request["SESS"],
            ["GSTR"] = request["GSTR"],
        };
        var titl = request["TITL"];
        if (!string.IsNullOrEmpty(titl)) notice["TIID"] = titl;

        await target.MessengerConnection.SendPacket(new Packet("GNOT", TheaterTransmissionType.OkResponse, 0, notice));
    }

    // Must have no body id (0) so the client routes it as an async presence notice, matched by USER.
    private static Packet BuildPresenceNotice(string user)
        => new("PGET", TheaterTransmissionType.OkResponse, 0, new Dictionary<string, string>
        {
            ["USER"] = user,
            ["SHOW"] = "CHAT",
            ["STAT"] = "en%3dPlaying A Game",
            ["TITL"] = "A Game",
        });

    private async Task AcknowledgeRequest(Packet request)
    {
        var response = new Dictionary<string, string>
        {
            ["ID"] = request["ID"]
        };

        var packet = new Packet(request.Type, TheaterTransmissionType.OkResponse, 0, response);
        await _conn.SendPacket(packet);
    }
}