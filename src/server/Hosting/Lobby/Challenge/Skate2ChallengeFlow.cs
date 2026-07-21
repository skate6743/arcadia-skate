using Arcadia.Hosting.Lobby.Handshake;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Reset;
using Arcadia.Hosting.Lobby.Send;
using Microsoft.Extensions.Logging;

// Skate 2 challenge: two-phase (Phase 1 asset-load, Phase 2 Reset(Challenge)).
// Only Phase 2 arms the WaitFirstFrame barrier (Phase 1 has no SimController::Reset).
namespace Arcadia.Hosting.Lobby.Challenge
{
    public static class Skate2ChallengeFlow
    {
        public static async Task Phase1Async(LobbyUdpServer server, CancellationToken ct)
        {
            long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);
            byte[] startGameBody = GameRequestPacket.StartGame(server.Variant);

            var eligible = ResetBroadcaster.SnapshotEligible(server);
            if (eligible.Count == 0)
            {
                server.Logger.LogWarning("LobbyUdp[{lobby}] challenge handshake: no eligible peers", server.LobbyId);
                return;
            }

            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] resetBody = HandshakeFlow.BuildGameResetForSession(
                            server, peerSession, Sk8ResetType.ChallengeLoad, activity);
                        await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, resetBody,
                            $"MT_GameReset(mResetType=ChallengeLoad,mActivity={activity},phase1)", ct, reliable: true);
                        await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, startGameBody,
                            "MT_GameRequest(mRequest=StartGame,phase1)", ct, reliable: true);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] challenge phase1 send to {ep} failed",
                            server.LobbyId, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);

            server.Logger.LogWarning(
                "LobbyUdp[{lobby}] PHASE 1 sent: Reset(ChallengeLoad,{act})+START_GAME → {n} peer(s)",
                server.LobbyId, activity, eligible.Count);
        }

        public static async Task Phase2Async(LobbyUdpServer server, CancellationToken ct)
        {
            long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);
            var eligible = ResetBroadcaster.SnapshotEligible(server);
            if (eligible.Count == 0)
            {
                server.Logger.LogWarning("LobbyUdp[{lobby}] challenge phase2: no eligible peers", server.LobbyId);
                return;
            }

            server.PeerGameResults.Clear();

            // Close the gate BEFORE Reset(Challenge) hits the wire.
            int epoch = ResetGate.Close(server, eligible, "Skate2-challenge-phase2");

            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] resetBody = HandshakeFlow.BuildGameResetForSession(
                            server, peerSession, Sk8ResetType.Challenge, activity);
                        uint seq = await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, resetBody,
                            $"MT_GameReset(mResetType=Challenge,mActivity={activity},phase2)", ct, reliable: true);
                        ResetGate.RecordResetSent(peerSession, epoch, seq);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] challenge phase2 send to {ep} failed",
                            server.LobbyId, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);

            ChallengeFlow.ClearAwaitingReady(server);

            server.Logger.LogWarning(
                "LobbyUdp[{lobby}] PHASE 2 sent: Reset(Challenge,{act}) → {n} peer(s)",
                server.LobbyId, activity, eligible.Count);

            ResetWatchdog.Arm(server, "Skate2-challenge-phase2", c => ResendPhase2Async(server, c),
                allowDuringInProgress: true);
        }

        // Watchdog-only re-send; does NOT re-arm the watchdog.
        public static async Task ResendPhase2Async(LobbyUdpServer server, CancellationToken ct)
        {
            long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);
            var eligible = ResetBroadcaster.SnapshotEligible(server);
            if (eligible.Count == 0) return;

            int epoch = ResetGate.Close(server, eligible, "Skate2-challenge-phase2-resend");

            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] resetBody = HandshakeFlow.BuildGameResetForSession(
                            server, peerSession, Sk8ResetType.Challenge, activity);
                        uint seq = await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, resetBody,
                            $"MT_GameReset(mResetType=Challenge,mActivity={activity},ResetWatchdog-resend)", ct, reliable: true);
                        ResetGate.RecordResetSent(peerSession, epoch, seq);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] challenge phase2 RESEND to {ep} failed",
                            server.LobbyId, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);
        }
    }
}
