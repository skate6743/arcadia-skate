using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Reset;
using Microsoft.Extensions.Logging;

// Post-challenge cleanup: AllPlayersComplete, collect GameResults, FinalResults, dwell, ExitPostChallenge, reset.
namespace Arcadia.Hosting.Lobby.Flow
{
    public static class PostChallengeFlow
    {
        private static readonly TimeSpan Skate1ResultsDwell = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan Skate1ResultsCollectTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan Skate2ResultsDwell = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan Skate2ResultsCollectTimeout = TimeSpan.FromSeconds(10);

        // departed peers never report; drop them or the collect wait stalls the full ceiling
        private static bool StillPresent(LobbyUdpServer server, long uid)
        {
            foreach (LobbySession s in server.Sessions.Values)
            {
                if (s.PlayerInfo?.UID == uid) return true;
            }
            return false;
        }

        public static async Task RunAsync(LobbyUdpServer server, CancellationToken ct)
        {
            try
            {
                if (server.Variant == GameVariant.Skate1)
                {
                    await RunSkate1Async(server, ct);
                }
                else
                {
                    await RunSkate2Async(server, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                server.Logger.LogWarning(ex, "LobbyUdp[{lobby}] POST-CHALLENGE broadcast failed", server.LobbyId);
            }
            finally
            {
                // terminal clear: a throw before the reset would otherwise leave InProgress stuck (lobby unjoinable)
                server.Game.InProgress = false;
                Interlocked.Exchange(ref server.PostChallengeBroadcastInProgress, 0);
            }
        }

        private static async Task RunSkate2Async(LobbyUdpServer server, CancellationToken ct)
        {
            // no PeerGameResults.Clear() here; the round's clear is at Phase2Async, else it races the first completer's stash
            await ResetBroadcaster.BroadcastSk8BodyAsync(server,
                Sk8MessagePackets.BuildGameAllPlayersComplete(server.Variant),
                "MT_GameAllPlayersComplete(post-challenge)", ct);

            List<long> expectedUids = new List<long>();
            foreach (LobbySession s in server.Sessions.Values)
            {
                if (s.PlayerInfo is not null && s.Stage >= AppStage.JoinCompleteSent)
                    expectedUids.Add(s.PlayerInfo.UID);
            }

            DateTimeOffset collectStart = DateTimeOffset.UtcNow;
            while (expectedUids.Count > 0
                   && expectedUids.Any(u => !server.PeerGameResults.ContainsKey(u) && StillPresent(server, u))
                   && DateTimeOffset.UtcNow - collectStart < Skate2ResultsCollectTimeout
                   && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { return; }
            }

            // rows only for peers still present; an unknown uid resolves to index -1 and writes before the leaderboard array
            List<(long Uid, uint EventTime, int Score, int FinishReason, int Ranking, bool PlayersChoice)> rows
                = new List<(long, uint, int, int, int, bool)>();
            foreach (var kv in server.PeerGameResults)
            {
                bool present = false;
                foreach (LobbySession s in server.Sessions.Values)
                {
                    if (s.PlayerInfo?.UID == kv.Key) { present = true; break; }
                }
                if (!present) continue;
                rows.Add((kv.Key, kv.Value.EventTime, kv.Value.Score, kv.Value.FinishReason, kv.Value.Ranking, kv.Value.PlayersChoice));
            }
            rows.Sort((a, b) => a.Ranking.CompareTo(b.Ranking));

            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] POST-CHALLENGE(skate2): collected {n}/{exp} GameResults",
                server.LobbyId, rows.Count, expectedUids.Count);

            byte[] finalResultsBody = rows.Count > 0
                ? Sk8MessagePackets.BuildGameFinalResultsSkate2(rows)
                : Sk8MessagePackets.BuildGameFinalResultsEmpty(server.Variant);
            await ResetBroadcaster.BroadcastSk8BodyAsync(server, finalResultsBody,
                $"MT_GameFinalResults(post-challenge,skate2,rows={rows.Count})", ct);

            try { await Task.Delay(Skate2ResultsDwell, ct); }
            catch (OperationCanceledException) { return; }

            await ResetBroadcaster.BroadcastSk8BodyAsync(server,
                Sk8MessagePackets.BuildGameExitPostChallenge(server.Variant),
                "MT_GameExitPostChallenge(post-challenge)", ct);

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }

            await ResetBroadcaster.BroadcastLobbySkateAsync(server, ct, resetAttributes: true);
            server.Game.InProgress = false;
            server.PeerGameResults.Clear();
        }

        private static async Task RunSkate1Async(LobbyUdpServer server, CancellationToken ct)
        {
            server.PeerGameResults.Clear();

            await ResetBroadcaster.BroadcastSk8BodyAsync(server,
                Sk8MessagePackets.BuildGameAllPlayersComplete(server.Variant),
                "MT_GameAllPlayersComplete(post-challenge,skate1)", ct);

            List<long> expectedUids = new List<long>();
            foreach (LobbySession s in server.Sessions.Values)
            {
                if (s.PlayerInfo is not null && s.Stage >= AppStage.JoinCompleteSent)
                    expectedUids.Add(s.PlayerInfo.UID);
            }

            DateTimeOffset collectStart = DateTimeOffset.UtcNow;
            while (expectedUids.Count > 0
                   && expectedUids.Any(u => !server.PeerGameResults.ContainsKey(u) && StillPresent(server, u))
                   && DateTimeOffset.UtcNow - collectStart < Skate1ResultsCollectTimeout
                   && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { return; }
            }

            List<(long Uid, uint EventTime, int Score, int FinishReason, int Ranking, bool PlayersChoice)> rows
                = new List<(long, uint, int, int, int, bool)>();
            foreach (var kv in server.PeerGameResults)
            {
                rows.Add((kv.Key, kv.Value.EventTime, kv.Value.Score, kv.Value.FinishReason, kv.Value.Ranking, kv.Value.PlayersChoice));
            }
            rows.Sort((a, b) => a.Ranking.CompareTo(b.Ranking));

            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] POST-CHALLENGE(skate1): collected {n}/{exp} GameResults",
                server.LobbyId, rows.Count, expectedUids.Count);

            byte[] finalResultsBody = rows.Count > 0
                ? Sk8MessagePackets.BuildGameFinalResultsSkate1(rows)
                : Sk8MessagePackets.BuildGameFinalResultsEmpty(server.Variant);
            await ResetBroadcaster.BroadcastSk8BodyAsync(server, finalResultsBody,
                $"MT_GameFinalResults(post-challenge,skate1,rows={rows.Count})", ct);

            try { await Task.Delay(Skate1ResultsDwell, ct); }
            catch (OperationCanceledException) { return; }

            await ResetBroadcaster.BroadcastSk8BodyAsync(server,
                Sk8MessagePackets.BuildGameExitPostChallenge(server.Variant),
                "MT_GameExitPostChallenge(post-challenge,skate1)", ct);

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }

            await ResetBroadcaster.BroadcastLobbySkateAsync(server, ct, resetAttributes: true);
            server.Game.InProgress = false;

            // second reset after 1s covers a client that reaches LobbySkate slightly after the first
            await ResetBroadcaster.BroadcastLobbySkateAsync(server, ct);
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
            await ResetBroadcaster.BroadcastLobbySkateAsync(server, ct);

            server.PeerGameResults.Clear();
        }
    }
}
