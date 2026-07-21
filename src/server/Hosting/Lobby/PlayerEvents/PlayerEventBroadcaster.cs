using System.Net;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Send;
using Arcadia.Storage;
using Microsoft.Extensions.Logging;

// HOST_ROSTER_ELEM + PLAYER_JOIN_FULL_MESH + PLAYER_LEFT + VoIP-disable broadcasts.
namespace Arcadia.Hosting.Lobby.PlayerEvents
{
    public static class PlayerEventBroadcaster
    {
        public static async Task BroadcastJoinAsync(LobbyUdpServer server, LobbySession joinerSession, IPAddress hostIp, ushort hostPort, CancellationToken ct)
        {
            if (joinerSession.PlayerInfo is null) return;

            int slot = server.Game.AllocateSlot(joinerSession.PlayerInfo.UID);
            byte[] rosterElem = HandshakePackets.BuildHostRosterElem(joinerSession.PlayerInfo, slot, hostIp, hostPort);
            byte[] fullMesh = HandshakePackets.BuildPlayerJoinFullMesh(joinerSession.PlayerInfo.PlayerRef);
            string rosterLabel = $"HOST_ROSTER_ELEM(joiner={joinerSession.PlayerInfo.Name},uid={joinerSession.PlayerInfo.UID},slot={slot})";
            string fullMeshLabel = $"PLAYER_JOIN_FULL_MESH(joiner={joinerSession.PlayerInfo.Name},ref={joinerSession.PlayerInfo.PlayerRef})";

            int recipients = 0;
            foreach (var kv in server.Sessions)
            {
                LobbySession otherSession = kv.Value;
                if (ReferenceEquals(otherSession, joinerSession)) continue;
                if (otherSession.PlayerInfo is null) continue;
                if (otherSession.Stage < AppStage.JoinCompleteSent) continue;
                try
                {
                    await EncryptedSender.SendSystemBodyAsync(server, kv.Key, otherSession, rosterElem, rosterLabel, ct);
                    await EncryptedSender.SendSystemBodyAsync(server, kv.Key, otherSession, fullMesh, fullMeshLabel, ct);
                    recipients++;
                }
                catch (Exception e)
                {
                    server.Logger.LogWarning(e, "LobbyUdp[{lobby}] HOST_ROSTER_ELEM+FULL_MESH broadcast to {ep} failed",
                        server.LobbyId, kv.Key);
                }
            }

            if (recipients > 0)
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] HOST_ROSTER_ELEM+FULL_MESH(uid={uid}) → {n} existing peer(s)",
                    server.LobbyId, joinerSession.PlayerInfo.UID, recipients);
            }

        }

        // Activate existing peers on the newcomer, host/creator FIRST, BEFORE its own JOIN_COMPLETE.
        public static async Task SendExistingPeerActivationsToNewcomerAsync(LobbyUdpServer server, IPEndPoint newcomerEp, LobbySession newcomer, CancellationToken ct)
        {
            if (newcomer.PlayerInfo is null) return;

            List<LobbySession> peers = new List<LobbySession>();
            foreach (var kv in server.Sessions)
            {
                LobbySession otherSession = kv.Value;
                if (ReferenceEquals(otherSession, newcomer)) continue;
                if (otherSession.PlayerInfo is null) continue;
                if (otherSession.Stage < AppStage.JoinCompleteSent) continue;
                peers.Add(otherSession);
            }

            peers.Sort((a, b) =>
            {
                bool aHost = a.PlayerInfo!.UID == server.Game.UID;
                bool bHost = b.PlayerInfo!.UID == server.Game.UID;
                return aHost == bHost ? 0 : aHost ? -1 : 1;
            });

            int sent = 0;
            foreach (LobbySession peer in peers)
            {
                UdpSessionCache.ClientInfo existing = peer.PlayerInfo!;
                byte[] fullMesh = HandshakePackets.BuildPlayerJoinFullMesh(existing.PlayerRef);
                try
                {
                    await EncryptedSender.SendSystemBodyAsync(server, newcomerEp, newcomer, fullMesh,
                        $"PLAYER_JOIN_FULL_MESH(existing={existing.Name},ref={existing.PlayerRef}) → newcomer (pre-join-complete)", ct);
                    sent++;
                }
                catch (Exception e)
                {
                    server.Logger.LogWarning(e, "LobbyUdp[{lobby}] pre-join-complete full-mesh to newcomer {ep} failed",
                        server.LobbyId, newcomerEp);
                }
            }

            if (sent > 0)
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] activated {n} existing peer(s) on newcomer uid={newUid} (creator-first, pre-join-complete)",
                    server.LobbyId, sent, newcomer.PlayerInfo.UID);
            }
        }

        public static async Task BroadcastVoipDisabledForAllAsync(LobbyUdpServer server, CancellationToken ct)
        {
            List<LobbySession> peers = new List<LobbySession>();
            foreach (var kv in server.Sessions)
            {
                LobbySession s = kv.Value;
                if (s.PlayerInfo is null) continue;
                if (s.Stage < AppStage.JoinCompleteSent) continue;
                peers.Add(s);
            }
            if (peers.Count == 0) return;

            int totalSent = 0;
            foreach (LobbySession subject in peers)
            {
                UdpSessionCache.ClientInfo subjectInfo = subject.PlayerInfo!;
                byte[] body = HandshakePackets.BuildVoipEnabledChange(subjectInfo.PlayerRef, enabled: false);
                string label = $"VOIP_ENABLED_CHANGE(player={subjectInfo.Name},ref={subjectInfo.PlayerRef},enabled=false)";

                foreach (var kv in server.Sessions)
                {
                    LobbySession recvSession = kv.Value;
                    if (recvSession.PlayerInfo is null) continue;
                    if (recvSession.Stage < AppStage.JoinCompleteSent) continue;
                    try
                    {
                        await EncryptedSender.SendSystemBodyAsync(server, kv.Key, recvSession, body, label, ct);
                        totalSent++;
                    }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] VOIP_ENABLED_CHANGE to {ep} failed",
                            server.LobbyId, kv.Key);
                    }
                }
            }

            if (totalSent > 0)
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] VOIP_ENABLED_CHANGE auto-disable: {peers} × {recv} = {n} packet(s)",
                    server.LobbyId, peers.Count, peers.Count, totalSent);
            }
        }

        public static async Task BroadcastLeftAsync(LobbyUdpServer server, UdpSessionCache.ClientInfo leaver, CancellationToken ct)
        {
            byte[] body = HandshakePackets.BuildPlayerLeft(leaver, reason: 0);
            string label = $"PLAYER_LEFT(leaver={leaver.Name},uid={leaver.UID})";
            int recipients = 0;

            foreach (var kv in server.Sessions)
            {
                LobbySession otherSession = kv.Value;
                if (otherSession.PlayerInfo is null) continue;
                if (otherSession.Stage < AppStage.JoinCompleteSent) continue;
                try
                {
                    await EncryptedSender.SendSystemBodyAsync(server, kv.Key, otherSession, body, label, ct);
                    recipients++;
                }
                catch (Exception e)
                {
                    server.Logger.LogWarning(e, "LobbyUdp[{lobby}] PLAYER_LEFT broadcast to {ep} failed",
                        server.LobbyId, kv.Key);
                }
            }

            if (recipients > 0)
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] PLAYER_LEFT(uid={uid}) → {n} remaining peer(s)",
                    server.LobbyId, leaver.UID, recipients);
            }
        }
    }
}
