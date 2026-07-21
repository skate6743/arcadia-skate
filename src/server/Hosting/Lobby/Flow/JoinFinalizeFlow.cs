using System.Net;
using Arcadia.Hosting.Lobby.PlayerEvents;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Reset;
using Microsoft.Extensions.Logging;

// Post-handshake join finalization: roster/full-mesh + VoIP broadcast, then a deferred MT_GameReset gated on the recipe mesh.
namespace Arcadia.Hosting.Lobby.Flow
{
    public static class JoinFinalizeFlow
    {
        // ceiling for the mesh wait (joiner requests peers' recipes 5-16s after upload)
        public const int MeshWaitCeilingSeconds = 20;

        // post-mesh settle so clients finish assembling/loading recipes
        public const int SettleSeconds = 5;

        public const int LateRecoveryWindowSeconds = 30;

        public static async Task FireAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, CancellationToken ct)
        {
            session.PendingJoinBroadcast = false;
            session.PostJoinResetPending = true;

            IPAddress hostIp = Handshake.RosterBuilder.ResolveHostIp(
                (IPEndPoint)server.Listener.Client.LocalEndPoint!, server.Game);
            ushort hostPort = (ushort)((IPEndPoint)server.Listener.Client.LocalEndPoint!).Port;
            await PlayerEventBroadcaster.BroadcastJoinAsync(server, session, hostIp, hostPort, ct);
            await PlayerEventBroadcaster.BroadcastVoipDisabledForAllAsync(server, ct);

            server.ReleaseJoinGate(session, "join-finalized");

            long myFinalizationIndex = Interlocked.Increment(ref server.Game.LatestJoinFinalizationIndex);
            _ = Task.Run(async () =>
            {
                try
                {
                    bool meshComplete = await WaitForRecipeMeshAsync(server, session, MeshWaitCeilingSeconds, ct);

                    await Task.Delay(TimeSpan.FromSeconds(SettleSeconds), ct);

                    long latest = Interlocked.Read(ref server.Game.LatestJoinFinalizationIndex);
                    if (myFinalizationIndex != latest)
                    {
                        server.Logger.LogInformation(
                            "LobbyUdp[{lobby}] post-handshake MT_GameReset skipped — newer joiner finalization arrived (mine={mine}, latest={latest})",
                            server.LobbyId, myFinalizationIndex, latest);
                        return;
                    }

                    await ResetBroadcaster.BroadcastLobbySkateAsync(server, ct);

                    // detached so PostJoinResetPending clears immediately and joiner-waits don't stall
                    if (!meshComplete)
                    {
                        _ = Task.Run(() => RunLateRecoveryAsync(server, session, myFinalizationIndex, ct), ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    server.Logger.LogWarning(ex,
                        "LobbyUdp[{lobby}] deferred post-handshake MT_GameReset broadcast failed",
                        server.LobbyId);
                }
                finally
                {
                    session.PostJoinResetPending = false;
                }
            }, ct);
        }

        private static bool ServeAcked(LobbySession dst, long uid)
        {
            uint seq;
            lock (dst.RelayLock)
            {
                if (!dst.RecipeServeSeqByUid.TryGetValue(uid, out seq)) return false;
            }
            return dst.LastAckFromClient >= seq;
        }

        private static (bool Complete, string Missing) RecipeMeshState(LobbyUdpServer server, LobbySession joiner)
        {
            if (joiner.PlayerInfo is null) return (true, "");
            long jUid = joiner.PlayerInfo.UID;

            List<string>? missing = null;
            foreach (var kv in server.Sessions)
            {
                LobbySession p = kv.Value;
                if (ReferenceEquals(p, joiner)) continue;
                if (p.PlayerInfo is null || p.Stage < AppStage.JoinCompleteSent) continue;
                long pUid = p.PlayerInfo.UID;

                if (!ServeAcked(p, jUid))
                    (missing ??= new List<string>()).Add($"uid{pUid}<-recipe({jUid})");
                if (!ServeAcked(joiner, pUid))
                    (missing ??= new List<string>()).Add($"uid{jUid}<-recipe({pUid})");
            }
            return missing is null ? (true, "") : (false, string.Join(",", missing));
        }

        private static async Task<bool> WaitForRecipeMeshAsync(LobbyUdpServer server, LobbySession joiner, int ceilingSeconds, CancellationToken ct)
        {
            DateTimeOffset start = DateTimeOffset.UtcNow;
            string lastMissing = "";
            while (!ct.IsCancellationRequested)
            {
                (bool complete, string missing) = RecipeMeshState(server, joiner);
                if (complete)
                {
                    server.Logger.LogInformation(
                        "LobbyUdp[{lobby}] RECIPE-MESH complete for joiner uid={uid} after {ms}ms",
                        server.LobbyId, joiner.PlayerInfo?.UID ?? 0,
                        (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds);
                    return true;
                }
                lastMissing = missing;
                if ((DateTimeOffset.UtcNow - start).TotalSeconds >= ceilingSeconds) break;
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { return false; }
            }
            server.Logger.LogWarning(
                "LobbyUdp[{lobby}] RECIPE-MESH TIMEOUT for joiner uid={uid} after {s}s — firing reset anyway; missing=[{missing}] (late-recovery armed)",
                server.LobbyId, joiner.PlayerInfo?.UID ?? 0, ceilingSeconds, lastMissing);
            return false;
        }

        private static async Task RunLateRecoveryAsync(LobbyUdpServer server, LobbySession joiner, long myFinalizationIndex, CancellationToken ct)
        {
            DateTimeOffset start = DateTimeOffset.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested
                    && (DateTimeOffset.UtcNow - start).TotalSeconds < LateRecoveryWindowSeconds)
                {
                    await Task.Delay(250, ct);

                    if (joiner.PlayerInfo is null) return;
                    bool stillPresent = false;
                    foreach (var kv in server.Sessions)
                    {
                        if (ReferenceEquals(kv.Value, joiner)) { stillPresent = true; break; }
                    }
                    if (!stillPresent) return;
                    if (Interlocked.Read(ref server.Game.LatestJoinFinalizationIndex) != myFinalizationIndex)
                        return;

                    (bool complete, _) = RecipeMeshState(server, joiner);
                    if (!complete) continue;

                    if (server.Game.InProgress)
                    {
                        server.Logger.LogWarning(
                            "LobbyUdp[{lobby}] RECIPE-LATE-RESET suppressed for joiner uid={uid} — challenge/map flow in progress",
                            server.LobbyId, joiner.PlayerInfo.UID);
                        return;
                    }

                    server.Logger.LogWarning(
                        "LobbyUdp[{lobby}] RECIPE-LATE-RESET joiner uid={uid} — recipe mesh completed {ms}ms after the join reset; broadcasting one recovery MT_GameReset",
                        server.LobbyId, joiner.PlayerInfo.UID,
                        (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds);
                    await ResetBroadcaster.BroadcastLobbySkateAsync(server, ct);
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                server.Logger.LogWarning(ex,
                    "LobbyUdp[{lobby}] RECIPE-LATE-RESET watcher failed", server.LobbyId);
            }
        }
    }
}
