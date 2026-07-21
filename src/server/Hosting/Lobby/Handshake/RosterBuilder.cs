using System.Net;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Storage;

// Frozen roster snapshot at HOST_HELLO time + bind-address resolution.
namespace Arcadia.Hosting.Lobby.Handshake
{
    public static class RosterBuilder
    {
        public static List<(UdpSessionCache.ClientInfo Info, int Slot)> Build(
            LobbySession session, GameServerListing game, Func<long, bool> isReady)
        {
            List<(UdpSessionCache.ClientInfo, int)> list = new List<(UdpSessionCache.ClientInfo, int)>();
            foreach (var p in game.ConnectedPlayers)
            {
                if (!isReady(p.UID)) continue;
                var info = new UdpSessionCache.ClientInfo(p.UID, p.NAME, p.PID, game.Platform);
                list.Add((info, game.AllocateSlot(p.UID)));
            }
            if (session.PlayerInfo is not null && !list.Any(c => c.Item1.UID == session.PlayerInfo.UID))
            {
                list.Add((session.PlayerInfo, game.AllocateSlot(session.PlayerInfo.UID)));
            }
            return list;
        }

        public static IPAddress ResolveHostIp(IPEndPoint listenerLocalEp, GameServerListing game)
        {
            IPAddress hostIp = listenerLocalEp.Address;
            if (!hostIp.Equals(IPAddress.Any)) return hostIp;
            if (game.Data.TryGetValue("INT-IP", out string? intIp) && IPAddress.TryParse(intIp, out IPAddress? parsed))
                return parsed;
            return IPAddress.Loopback;
        }
    }
}
