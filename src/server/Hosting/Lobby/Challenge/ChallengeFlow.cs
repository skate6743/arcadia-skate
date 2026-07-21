using Arcadia.EA;
using Arcadia.Hosting.Lobby.Handshake;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Reset;
using Arcadia.Hosting.Lobby.Send;
using Microsoft.Extensions.Logging;

// Challenge-start dispatcher: triggered when the walker sees MT_GameRequest(StartGame).
namespace Arcadia.Hosting.Lobby.Challenge
{
    public static class ChallengeFlow
    {
        public static void MaybeStart(LobbyUdpServer server, LobbySession source)
        {
            if (!server.Game.TryStart())
            {
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] in-game START_GAME from uid={uid} ignored (another flow has the startup gate)",
                    server.LobbyId, source.PlayerInfo?.UID ?? 0);
                return;
            }

            bool fire;
            lock (server.ChallengeLock)
            {
                if (server.ChallengeAwaitingReady) { fire = false; }
                else
                {
                    server.ChallengeReporters.Clear();
                    server.ChallengeReadyFired = false;
                    server.ChallengeAwaitingReady = true;
                    fire = true;
                }
            }
            if (!fire)
            {
                server.Game.InProgress = false;
                return;
            }

            server.Logger.LogWarning(
                "LobbyUdp[{lobby}] in-game START_GAME from uid={uid} — kicking off two-phase challenge handshake",
                server.LobbyId, source.PlayerInfo?.UID ?? 0);

            // Skate 1: arm the sync barrier synchronously so a trailing pre-reset GameSync can't leak past the reset.
            if (server.Variant == GameVariant.Skate1)
                ArmSyncBarrier(server, "Skate1-challenge-start(sync)");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Phase1Async(server, server.CancellationToken);
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    server.Logger.LogError(e,
                        "LobbyUdp[{lobby}] challenge-start flow threw — clearing InProgress + AwaitingReady so the lobby self-heals",
                        server.LobbyId);
                    ClearAwaitingReady(server);
                    server.Game.InProgress = false;
                }
            });
        }

        public static void TrackChallengeLoadedReport(LobbyUdpServer server, long peerUid)
        {
            bool fire;
            int reportedCount;
            int eligibleCount;
            lock (server.ChallengeLock)
            {
                if (!server.ChallengeAwaitingReady) return;
                server.ChallengeReporters.Add(peerUid);
                reportedCount = server.ChallengeReporters.Count;
                eligibleCount = ResetBroadcaster.SnapshotEligible(server).Count;
                fire = !server.ChallengeReadyFired && eligibleCount > 0 && reportedCount >= eligibleCount;
                if (fire) server.ChallengeReadyFired = true;
            }
            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] Message_GameChallengeLoaded from peer={peer} ({reported}/{eligible})",
                server.LobbyId, peerUid, reportedCount, eligibleCount);

            if (fire)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Skate2ChallengeFlow.Phase2Async(server, server.CancellationToken);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogError(e,
                            "LobbyUdp[{lobby}] challenge phase2 flow threw — clearing InProgress + AwaitingReady so the lobby self-heals",
                            server.LobbyId);
                        ClearAwaitingReady(server);
                        server.Game.InProgress = false;
                    }
                });
            }
        }

        // Call AFTER the session is out of Sessions and its slot is released.
        public static void OnPeerLeft(LobbyUdpServer server)
        {
            if (server.Variant != GameVariant.Skate2) return;

            bool fire = false;
            bool abandoned = false;
            lock (server.ChallengeLock)
            {
                if (!server.ChallengeAwaitingReady || server.ChallengeReadyFired) return;
                var eligible = ResetBroadcaster.SnapshotEligible(server);
                if (eligible.Count == 0)
                {
                    server.ChallengeAwaitingReady = false;
                    abandoned = true;
                }
                else
                {
                    bool allReported = true;
                    foreach (var (_, peerSession) in eligible)
                    {
                        if (peerSession.PlayerInfo is null
                            || !server.ChallengeReporters.Contains(peerSession.PlayerInfo.UID))
                        {
                            allReported = false;
                            break;
                        }
                    }
                    if (allReported)
                    {
                        server.ChallengeReadyFired = true;
                        fire = true;
                    }
                }
            }

            if (abandoned)
            {
                server.Game.InProgress = false;
                server.Logger.LogWarning(
                    "LobbyUdp[{lobby}] challenge abandoned — no eligible peers remain; gates cleared",
                    server.LobbyId);
                return;
            }

            if (fire)
            {
                server.Logger.LogWarning(
                    "LobbyUdp[{lobby}] last pending ChallengeLoaded reporter left — firing phase2 for the remaining peer(s)",
                    server.LobbyId);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Skate2ChallengeFlow.Phase2Async(server, server.CancellationToken);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        server.Logger.LogError(e,
                            "LobbyUdp[{lobby}] challenge phase2 flow threw — clearing InProgress + AwaitingReady so the lobby self-heals",
                            server.LobbyId);
                        ClearAwaitingReady(server);
                        server.Game.InProgress = false;
                    }
                });
            }
        }

        private static async Task Phase1Async(LobbyUdpServer server, CancellationToken ct)
        {
            server.Game.InProgress = true;

            await server.WaitForJoinersAsync("CHALLENGE-START", 30, ct);

            if (server.Variant == GameVariant.Skate1)
            {
                await Skate1ChallengeFlow.StartAsync(server, ct);
                return;
            }

            await Skate2ChallengeFlow.Phase1Async(server, ct);
        }

        // Caller has already acquired the startup gate.
        public static void ArmSyncBarrier(LobbyUdpServer server, string reason)
        {
            var eligible = ResetBroadcaster.SnapshotEligible(server);
            if (eligible.Count == 0) return;

            ResetGate.Close(server, eligible, reason);

            server.Logger.LogWarning(
                "LobbyUdp[{lobby}] pre-reset GameSync filter armed SYNCHRONOUSLY ({reason}) on {n} peer(s)",
                server.LobbyId, reason, eligible.Count);
        }

        public static void ClearAwaitingReady(LobbyUdpServer server)
        {
            lock (server.ChallengeLock) { server.ChallengeAwaitingReady = false; }
        }
    }
}
