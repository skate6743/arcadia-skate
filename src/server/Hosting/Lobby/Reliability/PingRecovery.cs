using System.Globalization;
using System.Net;
using Arcadia.Hosting.Lobby.Send;
using Arcadia.Hosting.Lobby.Wire;
using Microsoft.Extensions.Logging;

// Client catchup on kind=4 PING: pull N..N+15 from SentFrameCache, send cached bodies bundled (<=16 subs/packet).
namespace Arcadia.Hosting.Lobby.Reliability
{
    public static class PingRecovery
    {
        public const uint CatchupWindowSize = 16;
        public const int MaxBundleSubs = 16;
        public const int OversizeBoundaryBytes = 250;

        public static void StartCatchUp(LobbyUdpServer server, IPEndPoint ep, LobbySession session, CancellationToken ct)
        {
            if (session.ClientRequestedSeq == 0 || session.ClientRequestedSeq >= session.ServerDataSeq)
                return;

            // Single-flight per session.
            if (Interlocked.CompareExchange(ref session.CatchupInFlight, 1, 0) != 0)
                return;

            uint fromSeq = session.ClientRequestedSeq;
            uint toSeq = Math.Min(session.ServerDataSeq, fromSeq + CatchupWindowSize);
            session.ClientRequestedSeq = 0;

            if (fromSeq + LobbySession.SentFrameCacheCapacity < session.ServerDataSeq)
            {
                server.Logger.LogWarning(
                    "LobbyUdp[{lobby}] client {ep} PINGed for seq={seq} but ServerDataSeq={cur} (lag={lag}) — cache eviction likely",
                    server.LobbyId, ep, fromSeq, session.ServerDataSeq, session.ServerDataSeq - fromSeq);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    server.PingEvents.Append(string.Format(CultureInfo.InvariantCulture,
                        "[{0:O}] CATCHUP-TX lobby={1} ep={2} uid={3} fromSeq={4} toSeq={5}\n",
                        DateTimeOffset.UtcNow, server.LobbyId, ep,
                        session.PlayerInfo?.UID ?? 0, fromSeq, toSeq));

                    await RunCatchupAsync(server, ep, session, fromSeq, toSeq, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    server.Logger.LogWarning(e, "LobbyUdp[{lobby}] catchup to {ep} from={from} failed",
                        server.LobbyId, ep, fromSeq);
                }
                finally
                {
                    Interlocked.Exchange(ref session.CatchupInFlight, 0);
                }
            }, ct);
        }

        // A cache miss synthesizes an ack-only at the requested seq so the client frontier advances past evictions.
        private static async Task RunCatchupAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, uint fromSeq, uint toSeq, CancellationToken ct)
        {
            List<(uint Seq, byte[] Body)> pendingBundle = new List<(uint, byte[])>();
            int bundleCount = 0;
            int oversizeSent = 0;

            async Task FlushBundle(string reason)
            {
                if (pendingBundle.Count == 0) return;
                try
                {
                    await EncryptedSender.SendBundledDataAsync(
                        server, ep, session, pendingBundle,
                        $"catchup-bundle subs={pendingBundle.Count} fromSeq={pendingBundle[0].Seq} flush={reason}",
                        ct);
                    bundleCount++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    server.Logger.LogWarning(ex,
                        "LobbyUdp[{lobby}] catchup-bundle flush({reason}) fromSeq={s} subs={n} failed",
                        server.LobbyId, reason, pendingBundle[0].Seq, pendingBundle.Count);
                }
                pendingBundle = new List<(uint, byte[])>();
            }

            for (uint seq = fromSeq; seq < toSeq; seq++)
            {
                if (!session.SentFrameCache.TryGetValue(seq, out byte[]? cached))
                {
                    session.CatchupCacheMisses++;
                    server.Logger.LogWarning(
                        "LobbyUdp[{lobby}] catchup seq={seq} not in SentFrameCache — using ack-only (cumMisses={misses})",
                        server.LobbyId, seq, session.CatchupCacheMisses);
                    uint nowTick = (uint)Environment.TickCount;
                    uint echo = NetGameLinkEcho.Estimate(session, nowTick);
                    cached = NetGameLink.BuildAckOnly(echo, nowTick);
                }

                if (cached.Length == 0)
                {
                    await FlushBundle("empty-body");
                    continue;
                }

                if (cached.Length > OversizeBoundaryBytes)
                {
                    await FlushBundle("oversize-boundary");
                    try
                    {
                        await EncryptedSender.SendDataAsync(
                            server, ep, session, cached,
                            $"catchup-oversize seq={seq} bodyLen={cached.Length}",
                            ct, explicitSeq: seq);
                        oversizeSent++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception sendEx)
                    {
                        server.Logger.LogWarning(sendEx,
                            "LobbyUdp[{lobby}] catchup-oversize seq={seq} send failed",
                            server.LobbyId, seq);
                    }
                    continue;
                }

                pendingBundle.Add((seq, cached));

                if (pendingBundle.Count >= MaxBundleSubs)
                    await FlushBundle($"{MaxBundleSubs}-cap");
            }

            await FlushBundle("end-of-window");

            server.PingEvents.Append(string.Format(CultureInfo.InvariantCulture,
                "[{0:O}] CATCHUP-SENT lobby={1} ep={2} uid={3} fromSeq={4} bundles={5} oversizeSingletons={6}\n",
                DateTimeOffset.UtcNow, server.LobbyId, ep,
                session.PlayerInfo?.UID ?? 0, fromSeq, bundleCount, oversizeSent));
        }
    }
}
