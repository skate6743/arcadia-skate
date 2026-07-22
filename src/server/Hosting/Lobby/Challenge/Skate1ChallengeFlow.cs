using System.Net;
using Arcadia.Hosting.Lobby.Handshake;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Reset;
using Arcadia.Hosting.Lobby.Send;
using Microsoft.Extensions.Logging;

// Skate 1 challenge start: single-phase Reset(mResetType=Reset_Challenge=1).
namespace Arcadia.Hosting.Lobby.Challenge
{
    public static class Skate1ChallengeFlow
    {
        public static async Task StartAsync(LobbyUdpServer server, CancellationToken ct)
        {
            long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);
            var eligible = ResetBroadcaster.SnapshotEligible(server);
            if (eligible.Count == 0)
            {
                server.Logger.LogWarning("LobbyUdp[{lobby}] Skate1 challenge start: no eligible peers", server.LobbyId);
                ChallengeFlow.ClearAwaitingReady(server);
                return;
            }

            server.Game.InProgress = true;

            // re-close the gate (refreshes the WAIT-FF anchor to the real reset-send time)
            int epoch = ResetGate.Close(server, eligible, "Skate1-challenge");

            List<Task> sends = new List<Task>(eligible.Count);
            foreach (var (peerEp, peerSession) in eligible)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] resetBody = HandshakeFlow.BuildGameResetForSession(
                            server, peerSession, Sk8ResetType.ChallengeLoad, activity);
                        uint seq = await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, resetBody,
                            $"MT_GameReset(mResetType=Reset_Challenge=1,activity={activity},Skate1-single-phase)", ct, reliable: true);
                        ResetGate.RecordResetSent(peerSession, epoch, seq);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] Skate1 challenge reset send to {ep} failed",
                            server.LobbyId, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);

            ChallengeFlow.ClearAwaitingReady(server);

            server.Logger.LogWarning(
                "LobbyUdp[{lobby}] Skate1 START_CHALLENGE: Reset(mResetType=1=Reset_Challenge,activity={act}) → {n} peer(s)",
                server.LobbyId, activity, eligible.Count);

            ResetWatchdog.Arm(server, "Skate1-challenge", c => ResendAsync(server, c),
                allowDuringInProgress: true);
        }

        // Watchdog retry, targeted. MT_GameReset is inert inside ChallengeSkate, so a peer whose
        // barrier already cleared processed the reset and cannot be helped by a resend — while a
        // re-Close would wipe its live-epoch held bodies and arm a barrier no FirstFrame clears.
        // Still-armed peers only, same epoch: the original ResetSeqToDst stays the clear anchor.
        public static async Task ResendAsync(LobbyUdpServer server, CancellationToken ct)
        {
            long activity = HandshakeFlow.ResolveLobbyChallengeKey(server);
            var eligible = ResetBroadcaster.SnapshotEligible(server);
            if (eligible.Count == 0) return;

            List<(IPEndPoint Ep, LobbySession Session)> pending = new List<(IPEndPoint, LobbySession)>();
            foreach (var (peerEp, peerSession) in eligible)
            {
                bool armed;
                lock (peerSession.RelayLock) { armed = peerSession.WaitingForFirstFrameAfterReset; }
                if (armed) pending.Add((peerEp, peerSession));
            }

            if (pending.Count == 0)
            {
                server.Logger.LogWarning(
                    "LobbyUdp[{lobby}] ResetWatchdog RESEND skipped — every peer cleared the challenge reset (wedge is not reset loss)",
                    server.LobbyId);
                return;
            }

            List<Task> sends = new List<Task>(pending.Count);
            foreach (var (peerEp, peerSession) in pending)
            {
                sends.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] resetBody = HandshakeFlow.BuildGameResetForSession(
                            server, peerSession, Sk8ResetType.ChallengeLoad, activity);
                        await EncryptedSender.SendSk8BodyAsync(server, peerEp, peerSession, resetBody,
                            $"MT_GameReset(mResetType=Reset_Challenge=1,activity={activity},ResetWatchdog-resend)", ct, reliable: true);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogWarning(e, "LobbyUdp[{lobby}] Skate1 challenge reset RESEND to {ep} failed",
                            server.LobbyId, peerEp);
                    }
                }, ct));
            }
            await Task.WhenAll(sends);

            server.Logger.LogWarning(
                "LobbyUdp[{lobby}] ResetWatchdog RESEND Reset(mResetType=1=Reset_Challenge,activity={act}) → {n} still-armed peer(s)",
                server.LobbyId, activity, pending.Count);
        }
    }
}
