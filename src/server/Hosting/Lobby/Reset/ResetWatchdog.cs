using Arcadia.EA;
using Microsoft.Extensions.Logging;

// Post-reset re-convergence watchdog: detect a heartbeat-only wedge after resume, re-broadcast up to N times.
namespace Arcadia.Hosting.Lobby.Reset
{
    public static class ResetWatchdog
    {
        public const int TickMs = 50;
        public const int ResumeGsThreshold = 6;
        public const int ResumeDeadlineMs = 12_000;
        public const int PostResumeGraceMs = 1_000;
        public const int FrozenConfirmMsSkate1 = 3_000;
        public const int FrozenConfirmMsSkate2 = 1_500;
        public const int HealthyConfirmMs = 5_000;   // must exceed FrozenConfirmMs* so a wedge fires before healthy-disarm
        public const int MaxAttempts = 4;
        public const int AliveWindowMs = 5_000;

        public static void Arm(LobbyUdpServer server, string label, Func<CancellationToken, Task> reBroadcast,
            bool allowDuringInProgress = false)
        {
            if (server.Variant is not (GameVariant.Skate1 or GameVariant.Skate2)) return;
            lock (server.RwLock)
            {
                server.RwGeneration++;
                server.RwArmed = true;
                server.RwAttempts = 0;
                server.RwArmedAt = DateTimeOffset.UtcNow;
                server.RwResumed = false;
                server.RwRetryInFlight = false;
                server.RwLabel = label;
                server.RwReBroadcast = reBroadcast;
                server.RwAllowDuringInProgress = allowDuringInProgress;
                SnapshotBaselinesLocked(server);
            }
        }

        private static void SnapshotBaselinesLocked(LobbyUdpServer server)
        {
            Dictionary<long, long> baselines = new Dictionary<long, long>();
            long sum = 0;
            foreach (var kv in server.Sessions)
            {
                LobbySession s = kv.Value;
                if (s.PlayerInfo is null || s.Stage < Protocol.AppStage.JoinCompleteSent) continue;
                long gs = s.GameSyncsReceived;
                s.ResetWatchdogBaselineGameSyncs = gs;
                baselines[s.PlayerInfo.UID] = gs;
                sum += gs;
            }
            server.RwBaselines = baselines;
            server.RwLastGsSum = sum;
            server.RwLastProgressAt = server.RwArmedAt;
        }

        public static async Task RunLoopAsync(LobbyUdpServer server, CancellationToken ct)
        {
            TimeSpan tick = TimeSpan.FromMilliseconds(TickMs);
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(tick, ct); }
                catch (OperationCanceledException) { return; }

                Func<CancellationToken, Task>? fire = null;
                string fireLabel = "";
                int fireAttempt = 0, fireGen = 0;
                bool giveUp = false;
                string disarmReason = "";
                string departedNote = "";

                lock (server.RwLock)
                {
                    if (!server.RwArmed) continue;
                    DateTimeOffset now = DateTimeOffset.UtcNow;

                    List<LobbySession> watched = new List<LobbySession>();
                    foreach (var kv in server.Sessions)
                    {
                        LobbySession s = kv.Value;
                        if (s.PlayerInfo is null || s.Stage < Protocol.AppStage.JoinCompleteSent) continue;
                        if (!server.RwBaselines.ContainsKey(s.PlayerInfo.UID)) continue;
                        watched.Add(s);
                    }

                    if (watched.Count < server.RwBaselines.Count)
                    {
                        // A watched peer left mid-window: prune it and rebase the progress sum,
                        // else the sum drop reads as frozen and the survivors get a spurious reset.
                        HashSet<long> present = new HashSet<long>(watched.Count);
                        foreach (LobbySession s in watched) present.Add(s.PlayerInfo!.UID);
                        List<long> departed = new List<long>();
                        foreach (long uid in server.RwBaselines.Keys)
                            if (!present.Contains(uid)) departed.Add(uid);
                        foreach (long uid in departed) server.RwBaselines.Remove(uid);
                        if (server.RwResumed) server.RwLastGsSum = SumGameSyncs(watched);
                        departedNote = string.Join(",", departed);
                    }

                    if (watched.Count < 2)
                    {
                        server.RwArmed = false;
                        disarmReason = "lobby<2";
                    }
                    else if (!server.RwRetryInFlight)
                    {
                        if (!server.RwResumed)
                        {
                            // PRE-RESUME: no liveness disarm; a resetting/loading peer is app-quiet by design (heartbeats only).
                            bool allResumed = true;
                            foreach (LobbySession s in watched)
                            {
                                long bl = server.RwBaselines[s.PlayerInfo!.UID];
                                if (s.WaitingForFirstFrameAfterReset
                                    || s.GameSyncsReceived < bl + ResumeGsThreshold)
                                { allResumed = false; break; }
                            }
                            if (allResumed)
                            {
                                server.RwResumed = true;
                                server.RwResumedAt = now;
                                server.RwLastProgressAt = now;
                                server.RwLastGsSum = SumGameSyncs(watched);
                            }
                            else if ((now - server.RwArmedAt).TotalMilliseconds >= ResumeDeadlineMs)
                            {
                                if (!server.RwAllowDuringInProgress && server.Game.InProgress)
                                {
                                    server.RwArmed = false;
                                    disarmReason = "in-progress-superseded";
                                }
                                else
                                {
                                    (fire, fireLabel, fireAttempt, giveUp) = PrepareRetryLocked(server, "never-resumed");
                                    fireGen = server.RwGeneration;
                                }
                            }
                        }
                        else
                        {
                            // POST-RESUME: liveness disarm is legitimate here (a quiet peer may have died, not stalled).
                            long gsSum = SumGameSyncs(watched);
                            if (gsSum > server.RwLastGsSum) { server.RwLastProgressAt = now; server.RwLastGsSum = gsSum; }

                            double frozenMs = (now - server.RwLastProgressAt).TotalMilliseconds;
                            double sinceResumeMs = (now - server.RwResumedAt).TotalMilliseconds;
                            double frozenConfirmMs = server.Variant == GameVariant.Skate1 ? FrozenConfirmMsSkate1 : FrozenConfirmMsSkate2;

                            bool allAlive = true;
                            foreach (LobbySession s in watched)
                            {
                                long agoMs = s.LastAppMsgRxAt == default ? long.MaxValue
                                    : (long)(now - s.LastAppMsgRxAt).TotalMilliseconds;
                                if (agoMs >= AliveWindowMs) { allAlive = false; break; }
                            }

                            if (sinceResumeMs < PostResumeGraceMs)
                            {
                                // grace
                            }
                            else if (frozenMs >= frozenConfirmMs && allAlive)
                            {
                                if (!server.RwAllowDuringInProgress && server.Game.InProgress)
                                {
                                    server.RwArmed = false;
                                    disarmReason = "in-progress-superseded";
                                }
                                else
                                {
                                    (fire, fireLabel, fireAttempt, giveUp) = PrepareRetryLocked(server, "resumed-then-froze");
                                    fireGen = server.RwGeneration;
                                }
                            }
                            else if (!allAlive)
                            {
                                server.RwArmed = false;
                                disarmReason = "peer-not-alive(post-resume)";
                            }
                            else if (sinceResumeMs >= HealthyConfirmMs)
                            {
                                server.RwArmed = false;
                                disarmReason = "healthy";
                            }
                        }
                    }
                }

                if (departedNote.Length > 0)
                {
                    server.Logger.LogInformation(
                        "LobbyUdp[{lobby}] ResetWatchdog pruned departed peer(s) uid=[{uids}], progress baseline rebased",
                        server.LobbyId, departedNote);
                }
                if (disarmReason.Length > 0)
                {
                    server.Logger.LogInformation(
                        "LobbyUdp[{lobby}] ResetWatchdog disarmed ({reason})", server.LobbyId, disarmReason);
                    continue;
                }
                if (giveUp)
                {
                    server.Logger.LogWarning(
                        "LobbyUdp[{lobby}] ResetWatchdog GAVE UP after {n} re-broadcast(s) label={label}",
                        server.LobbyId, MaxAttempts, fireLabel);
                    continue;
                }
                if (fire is not null)
                {
                    server.Logger.LogWarning(
                        "LobbyUdp[{lobby}] ResetWatchdog RE-BROADCAST #{n} ({label})",
                        server.LobbyId, fireAttempt, fireLabel);
                    Func<CancellationToken, Task> capFire = fire;
                    int capGen = fireGen;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await capFire(ct);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception e)
                        {
                            server.Logger.LogWarning(e, "LobbyUdp[{lobby}] ResetWatchdog re-broadcast failed", server.LobbyId);
                        }
                        finally
                        {
                            lock (server.RwLock)
                            {
                                if (capGen == server.RwGeneration && server.RwArmed)
                                {
                                    server.RwArmedAt = DateTimeOffset.UtcNow;
                                    server.RwResumed = false;
                                    SnapshotBaselinesLocked(server);
                                    server.RwRetryInFlight = false;
                                }
                            }
                        }
                    }, ct);
                }
            }
        }

        private static (Func<CancellationToken, Task>? fire, string label, int attempt, bool giveUp)
            PrepareRetryLocked(LobbyUdpServer server, string why)
        {
            if (server.RwReBroadcast is null) { server.RwArmed = false; return (null, why, 0, false); }
            if (server.RwAttempts >= MaxAttempts)
            {
                server.RwArmed = false;
                return (null, $"{server.RwLabel}:{why}/cap", server.RwAttempts, true);
            }
            server.RwAttempts++;
            server.RwRetryInFlight = true;
            return (server.RwReBroadcast, $"{server.RwLabel}:{why}", server.RwAttempts, false);
        }

        private static long SumGameSyncs(List<LobbySession> watched)
        {
            long sum = 0;
            foreach (LobbySession s in watched) sum += s.GameSyncsReceived;
            return sum;
        }
    }
}
