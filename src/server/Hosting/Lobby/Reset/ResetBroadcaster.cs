using System.Net;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Handshake;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Send;
using Microsoft.Extensions.Logging;

// MT_GameReset broadcasts for the LobbySkate path (join finalize, 'C', map-change, post-challenge).
// Every site arms the FirstFrame barrier before sending, then arms the ResetWatchdog.
namespace Arcadia.Hosting.Lobby.Reset
{
    public static class ResetBroadcaster
    {
        // Confirm GameAttributes actually landed before the dependent reset restarts the sim.
        public const int GameAttributesAckWaitMs = 4000;

        public static List<(IPEndPoint Ep, LobbySession Session)> SnapshotEligible(LobbyUdpServer server)
        {
            List<(IPEndPoint, LobbySession)> list = new List<(IPEndPoint, LobbySession)>();
            foreach (var kv in server.Sessions)
            {
                LobbySession s = kv.Value;
                if (s.PlayerInfo is null) continue;
                if (s.Stage < AppStage.JoinCompleteSent) continue;
                list.Add((kv.Key, s));
            }
            return list;
        }

        public static async Task BroadcastLobbySkateAsync(LobbyUdpServer server, CancellationToken ct, bool resetAttributes = false)
        {
            var eligible = SnapshotEligible(server);
            if (eligible.Count == 0) return;

            if (resetAttributes)
            {
                server.Game.Data.TryGetValue("B-U-challenge_type", out string? challengeType);
                server.Game.Data.TryGetValue("B-U-challenge_key", out string? challengeKey);
                server.Game.Data.TryGetValue("B-U-ping_site", out string? pingSite);
                server.Game.Data.TryGetValue("B-U-is_private", out string? isPrivate);
                byte[] attribsBody = GameAttributesPacket.Build(server.Variant, challengeType, challengeKey, pingSite, isPrivate);

                // Same ack-gate as the map change: confirm the pre-reset attributes landed before
                // the reset, so a lost one on a lossy link can't reset the client onto the old map/mode.
                var pending = await SendReliableCapturingSeqsAsync(server, eligible, attribsBody, "MT_GameAttributes(pre-reset)", ct);
                await AwaitPeersAckedAsync(server, pending, GameAttributesAckWaitMs, "MT_GameAttributes(pre-reset)", ct);
            }

            int epoch = ResetGate.Close(server, eligible, "LobbySkate");
            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] MT_GameReset broadcast → {n} peer(s); pre-reset GameSync filter armed on every session",
                server.LobbyId, eligible.Count);

            long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);

            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] body = HandshakeFlow.BuildGameResetForSession(server, peerSession, Sk8ResetType.LobbySkate, activity);
                        uint seq = await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, body,
                            "MT_GameReset(LobbySkate)", ct, reliable: true);
                        ResetGate.RecordResetSent(peerSession, epoch, seq);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] LobbySkate reset to {ep} failed",
                            server.LobbyId, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);

            foreach (var (_, peerSession) in eligible)
            {
                if (peerSession.PlayerInfo is not null && peerSession.PlayerInfo.UID == server.Game.UID)
                {
                    server.Game.MarkCreatorResetSent();
                    break;
                }
            }

            ResetWatchdog.Arm(server, "LobbySkate", c => ResendLobbySkateAsync(server, c));
        }

        // Watchdog-only re-send; does NOT re-arm the watchdog.
        public static async Task ResendLobbySkateAsync(LobbyUdpServer server, CancellationToken ct)
        {
            var eligible = SnapshotEligible(server);
            if (eligible.Count == 0) return;

            int epoch = ResetGate.Close(server, eligible, "LobbySkate-resend");

            long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);

            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] body = HandshakeFlow.BuildGameResetForSession(server, peerSession, Sk8ResetType.LobbySkate, activity);
                        uint seq = await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, body,
                            "MT_GameReset(ResetWatchdog-resend)", ct, reliable: true);
                        ResetGate.RecordResetSent(peerSession, epoch, seq);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] ResetWatchdog LobbySkate resend to {ep} failed",
                            server.LobbyId, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);
        }

        public static async Task BroadcastSk8BodyAsync(LobbyUdpServer server, byte[] body, string label, CancellationToken ct)
        {
            var eligible = SnapshotEligible(server);
            if (eligible.Count == 0) return;

            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, body, label, ct, reliable: true);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] {label} to {ep} failed",
                            server.LobbyId, label, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);
        }

        // Broadcast a reliable Sk8 body, then wait until every peer has cumulatively acked it
        // (delivery-confirmed), bounded by maxWaitMs. Used before a map-change reset so the sim
        // restart can't outrun the new-map GameAttributes on a lossy link.
        public static async Task BroadcastSk8BodyAwaitAckAsync(LobbyUdpServer server, byte[] body, string label, int maxWaitMs, CancellationToken ct)
        {
            var eligible = SnapshotEligible(server);
            if (eligible.Count == 0) return;
            var pending = await SendReliableCapturingSeqsAsync(server, eligible, body, label, ct);
            await AwaitPeersAckedAsync(server, pending, maxWaitMs, label, ct);
        }

        // Send a reliable body to every eligible peer, returning each peer's allocated seq (0-seq
        // failures dropped) so a caller can later confirm delivery via AwaitPeersAckedAsync.
        private static async Task<List<(LobbySession Session, uint Seq)>> SendReliableCapturingSeqsAsync(
            LobbyUdpServer server, List<(IPEndPoint Ep, LobbySession Session)> eligible, byte[] body, string label, CancellationToken ct)
        {
            List<(LobbySession Session, uint Seq)> pending = new List<(LobbySession, uint)>(eligible.Count);
            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        uint seq = await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, body, label, ct, reliable: true);
                        if (seq > 0) lock (pending) pending.Add((peerSession, seq));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] {label} to {ep} failed",
                            server.LobbyId, label, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);
            return pending;
        }

        // Block until every captured peer's cumulative ack reaches its seq, bounded by maxWaitMs.
        private static async Task AwaitPeersAckedAsync(
            LobbyUdpServer server, List<(LobbySession Session, uint Seq)> pending, int maxWaitMs, string label, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
            while (true)
            {
                int unacked = 0;
                lock (pending)
                {
                    foreach (var (session, seq) in pending)
                        if (!session.LastAckFromClientInitialized || session.LastAckFromClient < seq) unacked++;
                }
                if (unacked == 0)
                {
                    server.Logger.LogInformation("LobbyUdp[{lobby}] {label} ack-confirmed by {n} peer(s)",
                        server.LobbyId, label, pending.Count);
                    return;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    server.Logger.LogWarning("LobbyUdp[{lobby}] {label} ack-wait timed out after {ms}ms, {n} peer(s) still unacked",
                        server.LobbyId, label, maxWaitMs, unacked);
                    return;
                }
                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
