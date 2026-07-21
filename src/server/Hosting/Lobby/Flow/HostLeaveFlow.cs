using System.Net;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.PlayerEvents;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Reset;
using Arcadia.Hosting.Lobby.Send;
using Microsoft.Extensions.Logging;

// Host-leave dissolve (both games): LostConnection kick to every remaining peer; host kick (0x8D) targets one peer.
namespace Arcadia.Hosting.Lobby.Flow
{
    public static class HostLeaveFlow
    {
        public static async Task HandleHostKickAsync(LobbyUdpServer server, int destRef, CancellationToken ct)
        {
            IPEndPoint? targetEp = null;
            LobbySession? target = null;
            foreach (var kv in server.Sessions)
            {
                LobbySession ks = kv.Value;
                if (ks.PlayerInfo is not null
                    && ks.PlayerInfo.PlayerRef == destRef
                    && ks.PlayerInfo.UID != server.Game.UID)
                {
                    targetEp = kv.Key;
                    target = ks;
                    break;
                }
            }

            if (targetEp is null || target?.PlayerInfo is null)
            {
                server.Logger.LogWarning(
                    "LobbyUdp[{lobby}] HOST-KICK: no live non-host session for destRef={dref}",
                    server.LobbyId, destRef);
                return;
            }

            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] HOST-KICK executing: target uid={uid} ({name}) ref={dref}",
                server.LobbyId, target.PlayerInfo.UID, target.PlayerInfo.Name, destRef);

            try
            {
                byte[] kick = GameRequestPacket.HostKick(server.Variant);
                await EncryptedSender.SendSk8BodyAsync(server, targetEp, target, kick,
                    "MT_GameRequest(host-kick)", ct, reliable: true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                server.Logger.LogWarning(e,
                    "LobbyUdp[{lobby}] HOST-KICK: LostConnection→target {ep} failed",
                    server.LobbyId, targetEp);
            }

            server.Sessions.TryRemove(targetEp, out _);
            await server.RemoveAndAnnounceLeaveAsync(targetEp, target, "host-kick", ct);
        }

        public static Task BroadcastHostLeaveDissolveAsync(LobbyUdpServer server, string reason, CancellationToken ct)
        {
            byte[] kick = GameRequestPacket.LostConnection(server.Variant);
            return ResetBroadcaster.BroadcastSk8BodyAsync(server, kick,
                $"MT_GameRequest(LostConnection,host-left,dissolve,reason={reason})", ct);
        }
    }
}
