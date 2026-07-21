using System.Net;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Send;
using Arcadia.Hosting.Lobby.Wire;
using Microsoft.Extensions.Logging;

// Per-stage handshake emit: drives AppStage transitions on inbound HELLO/ROSTER_ACK and while NeedsDrive(stage).
namespace Arcadia.Hosting.Lobby.Handshake
{
    public static class HandshakeFlow
    {
        public static bool NeedsDrive(LobbySession session) => session.Stage switch
        {
            AppStage.HelloReceived
                or AppStage.HostHelloSent
                or AppStage.RosterAckReceived
                or AppStage.JoinCompleteSent
                or AppStage.GameAttribsSent => true,
            _ => false,
        };

        public static async Task DriveAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, CancellationToken ct)
        {
            byte[]? appBody;
            byte sysFlag;
            string? label;
            AppStage? nextStage;

            if (session.Stage == AppStage.HelloReceived)
            {
                session.FrozenRoster = RosterBuilder.Build(session, server.Game, server.IsPeerReady);
                IPAddress hostIp = RosterBuilder.ResolveHostIp((IPEndPoint)server.Listener.Client.LocalEndPoint!, server.Game);
                ushort hostPort = (ushort)((IPEndPoint)server.Listener.Client.LocalEndPoint!).Port;

                int rosterCount = session.FrozenRoster.Count;
                ushort rosterSizeWire = (ushort)rosterCount;
                appBody = HandshakePackets.BuildHostHello(rosterSizeWire, hostIp, hostPort);
                sysFlag = NetGameLink.SysFlagSystem;
                label = $"HOST_HELLO(rosterElems={rosterCount}, rosterSizeWire={rosterSizeWire}, hasHost=0)";
                nextStage = AppStage.HostHelloSent;
            }
            else if (session.Stage == AppStage.HostHelloSent && session.PlayerInfo is not null)
            {
                var roster = session.FrozenRoster ?? RosterBuilder.Build(session, server.Game, server.IsPeerReady);

                if (session.RosterEmittedCount >= roster.Count) return;

                var (elem, slot) = roster[session.RosterEmittedCount];
                IPAddress hostIp = RosterBuilder.ResolveHostIp((IPEndPoint)server.Listener.Client.LocalEndPoint!, server.Game);
                ushort hostPort = (ushort)((IPEndPoint)server.Listener.Client.LocalEndPoint!).Port;
                appBody = HandshakePackets.BuildHostRosterElem(elem, slot, hostIp, hostPort);
                sysFlag = NetGameLink.SysFlagSystem;
                label = $"HOST_ROSTER_ELEM({session.RosterEmittedCount + 1}/{roster.Count}, uid={elem.UID}, slot={slot}, name={elem.Name})";
                session.RosterEmittedCount++;
                nextStage = session.RosterEmittedCount >= roster.Count
                    ? AppStage.HostRosterElemSent
                    : AppStage.HostHelloSent;
            }
            else if (session.Stage == AppStage.RosterAckReceived && session.PlayerInfo is not null)
            {
                // creator-first full-mesh BEFORE JOIN_COMPLETE so the creator is the joiner's first active PLAYER (owns host actions)
                await PlayerEvents.PlayerEventBroadcaster.SendExistingPeerActivationsToNewcomerAsync(server, ep, session, ct);

                appBody = HandshakePackets.BuildPlayerJoinComplete(session.PlayerInfo);
                sysFlag = NetGameLink.SysFlagSystem;
                label = "PLAYER_JOIN_COMPLETE";
                nextStage = AppStage.JoinCompleteSent;
            }
            else if (session.Stage == AppStage.JoinCompleteSent)
            {
                server.Game.Data.TryGetValue("B-U-challenge_type", out string? challengeType);
                server.Game.Data.TryGetValue("B-U-challenge_key", out string? challengeKey);
                server.Game.Data.TryGetValue("B-U-ping_site", out string? pingSite);
                server.Game.Data.TryGetValue("B-U-is_private", out string? isPrivate);
                appBody = GameAttributesPacket.Build(server.Variant, challengeType, challengeKey, pingSite, isPrivate);
                sysFlag = NetGameLink.SysFlagApp;
                label = "MT_GameAttributes";
                nextStage = AppStage.GameAttribsSent;
            }
            else if (session.Stage == AppStage.GameAttribsSent && session.PlayerInfo is not null)
            {
                if (server.Variant == GameVariant.Skate2)
                {
                    appBody = RecipePackets.BuildRequest(server.Variant, session.PlayerInfo.UID);
                    sysFlag = NetGameLink.SysFlagApp;
                    label = $"MT_GameRecipeRequest(peer={session.PlayerInfo.UID})";
                    nextStage = AppStage.GameRecipeRequestSent;
                }
                else
                {
                    // S1: no inline reset; deferred reset fires from the PendingJoinBroadcast tail in HandleProtoTunnel.
                    appBody = null;
                    sysFlag = 0;
                    label = null;
                    nextStage = AppStage.GameResetSent;

                    if (!session.JoinBroadcasted)
                    {
                        session.JoinBroadcasted = true;
                        session.PendingJoinBroadcast = true;
                    }
                }
            }
            else
            {
                return;
            }

            if (appBody is not null)
            {
                uint nowTick = (uint)Environment.TickCount;
                uint echo = NetGameLinkEcho.Estimate(session, nowTick);
                byte[] frame = NetGameLink.BuildWithBody(sysFlag, appBody, echo, nowTick);
                await EncryptedSender.SendDataAsync(server, ep, session, frame, label, ct);
            }

            if (nextStage.HasValue && nextStage.Value != session.Stage)
            {
                server.Logger.LogInformation("LobbyUdp[{lobby}] stage {old}→{new} for {ep}",
                    server.LobbyId, session.Stage, nextStage.Value, ep);
                session.Stage = nextStage.Value;
            }

            bool chainAgain =
                session.Stage == AppStage.GameAttribsSent ||
                (session.Stage == AppStage.HostHelloSent && session.RosterEmittedCount > 0);

            if (chainAgain)
                await DriveAsync(server, ep, session, ct);
        }

        public static byte[] BuildGameResetForSession(LobbyUdpServer server, LobbySession session, Sk8ResetType resetType, long activity)
        {
            int slotCount = server.Variant == GameVariant.Skate1
                ? GameResetPacket.Skate1SlotCount
                : GameResetPacket.Skate2SlotCount;
            Span<long> slots = stackalloc long[slotCount];
            for (int i = 0; i < slots.Length; i++) slots[i] = -1;

            // Broadcast tables must be dense: an interior empty slot becomes a phantom player.
            server.Game.CompactSlots();

            foreach (var p in server.Game.ConnectedPlayers)
            {
                if (!server.IsPeerReady(p.UID)) continue;
                int slot = server.Game.AllocateSlot(p.UID);
                if (slot >= 0 && slot < slots.Length) slots[slot] = p.UID;
            }
            return GameResetPacket.Build(server.Variant, slots, resetType, activity);
        }

        public static long ResolveLobbyChallengeKey(LobbyUdpServer server)
        {
            string? key = null;
            if (server.Game.Data.TryGetValue("B-U-challenge_key", out string? dataKey) && !string.IsNullOrWhiteSpace(dataKey))
                key = dataKey;
            else if (!string.IsNullOrWhiteSpace(server.Game.ChallengeKey))
                key = server.Game.ChallengeKey;

            if (key is not null && long.TryParse(key, out long parsed)) return parsed;
            return -1L;
        }
    }
}
