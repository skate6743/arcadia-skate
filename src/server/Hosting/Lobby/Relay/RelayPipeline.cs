using System.Net;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Arcadia.Hosting.Lobby.Send;
using Arcadia.Hosting.Lobby.Wire;
using Microsoft.Extensions.Logging;

// Game-layer relay fan-out: forwards parked Sk8 bodies (sysFlag=App) to every other peer.
namespace Arcadia.Hosting.Lobby.Relay
{
    public static class RelayPipeline
    {
        public const int MaxRelayBatchBytes = 800;
        public const int MaxResetReleaseBatchBytes = 800;

        // Runs from the serial HandleProtoTunnel inbound tail (one datagram awaited at a time).
        public static async Task DrainAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, CancellationToken ct)
        {
            while (true)
            {
                var batch = new List<KeyValuePair<uint, byte[]>>();
                int batchTotalLen = 0;
                bool stoppedAtGate = false;
                int drainEpoch = Volatile.Read(ref server.ResetEpoch);

                while (true)
                {
                    uint srcSeq;
                    byte[] body;

                    lock (session.RelayLock)
                    {
                        if (session.OrderedPendingRelayBodies.Count == 0) break;
                        var firstEntry = session.OrderedPendingRelayBodies.First();

                        // Hold back from relaying past the source's own ack frontier.
                        if (session.ClientAckInitialized && firstEntry.Key > session.ClientAckSeq)
                        {
                            stoppedAtGate = true;
                            break;
                        }

                        if (batch.Count > 0 && batchTotalLen + firstEntry.Value.Length > MaxRelayBatchBytes)
                            break;

                        srcSeq = firstEntry.Key;
                        body = firstEntry.Value;
                        session.OrderedPendingRelayBodies.Remove(srcSeq);
                    }

                    // Reorder guard; with serial inbound drain this should never fire.
                    if (session.LastRelayedSrcSeqInitialized && srcSeq <= session.LastRelayedSrcSeq)
                    {
                        server.Logger.LogWarning(
                            "LobbyUdp[{lobby}] DRAIN-DROP-REORDER ep={ep} uid={uid} srcSeq={k}",
                            server.LobbyId, ep, session.PlayerInfo?.UID ?? 0, srcSeq);
                        continue;
                    }

                    batch.Add(new KeyValuePair<uint, byte[]>(srcSeq, body));
                    batchTotalLen += body.Length;
                }

                if (batch.Count == 0) return;

                byte[] combined;
                uint highestSrcSeq = batch[batch.Count - 1].Key;
                if (batch.Count == 1)
                {
                    combined = batch[0].Value;
                }
                else
                {
                    combined = new byte[batchTotalLen];
                    int off = 0;
                    foreach (var kv in batch)
                    {
                        Array.Copy(kv.Value, 0, combined, off, kv.Value.Length);
                        off += kv.Value.Length;
                    }
                }

                await RelayAsync(server, session, combined, highestSrcSeq, drainEpoch, ct);
                session.LastRelayedSrcSeq = highestSrcSeq;
                session.LastRelayedSrcSeqInitialized = true;

                if (stoppedAtGate) return;
            }
        }

        // Releases bodies held in PendingForResetReleaseToDst once dst's FirstFrame clears.
        public static async Task DrainResetGateReleaseAsync(LobbyUdpServer server, IPEndPoint ep, LobbySession session, CancellationToken ct)
        {
            List<byte[]>? heldBodies = null;
            int releaseEpoch;
            lock (session.RelayLock)
            {
                releaseEpoch = Volatile.Read(ref server.ResetEpoch);
                if (session.PendingForResetReleaseToDst.Count > 0)
                {
                    heldBodies = new List<byte[]>(session.PendingForResetReleaseToDst.Count);
                    while (session.PendingForResetReleaseToDst.TryDequeue(out byte[]? body))
                        heldBodies.Add(body);
                }
            }
            if (heldBodies is null || heldBodies.Count == 0) return;

            Func<bool> releaseGate = () => Volatile.Read(ref server.ResetEpoch) == releaseEpoch
                                        && !session.WaitingForFirstFrameAfterReset;

            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] RESET-GATE-RELEASE ep={ep} uid={uid} draining {n} held body(ies) post-FirstFrame (coalesced ≤{max}B/frame)",
                server.LobbyId, ep, session.PlayerInfo?.UID ?? 0, heldBodies.Count, MaxResetReleaseBatchBytes);

            int rgi = 0;
            while (rgi < heldBodies.Count)
            {
                int rgStart = rgi;
                int rgLen = heldBodies[rgi].Length;
                rgi++;
                while (rgi < heldBodies.Count
                    && rgLen + heldBodies[rgi].Length <= MaxResetReleaseBatchBytes)
                {
                    rgLen += heldBodies[rgi].Length;
                    rgi++;
                }
                int rgCount = rgi - rgStart;
                byte[] rgCombined;
                if (rgCount == 1)
                {
                    rgCombined = heldBodies[rgStart];
                }
                else
                {
                    rgCombined = new byte[rgLen];
                    int rgOff = 0;
                    for (int k = rgStart; k < rgi; k++)
                    {
                        Array.Copy(heldBodies[k], 0, rgCombined, rgOff, heldBodies[k].Length);
                        rgOff += heldBodies[k].Length;
                    }
                }
                try
                {
                    uint releasedSeq = await EncryptedSender.SendSk8BodyAsync(
                        server, ep, session, rgCombined, "RESET-HELD-RELEASE", ct, sendGate: releaseGate);
                    if (releasedSeq == 0)
                    {
                        // Re-armed mid-release: these bodies belong to the epoch the wipe just retired.
                        server.Logger.LogInformation(
                            "LobbyUdp[{lobby}] RESET-HELD-RELEASE voided by re-arm ep={ep} uid={uid} dropped={n}",
                            server.LobbyId, ep, session.PlayerInfo?.UID ?? 0, heldBodies.Count - rgStart);
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    server.Logger.LogWarning(e, "LobbyUdp[{lobby}] RESET-HELD-RELEASE to {ep} uid={uid} failed",
                        server.LobbyId, ep, session.PlayerInfo?.UID ?? 0);
                }
            }
        }

        public static async Task RelayAsync(LobbyUdpServer server, LobbySession sender, byte[] appBody, uint srcBareSeq, int drainEpoch, CancellationToken ct)
        {
            if (sender.PlayerInfo is null) return;

            long srcUid = sender.PlayerInfo.UID;

            List<(IPEndPoint Ep, LobbySession Session, bool LoadingGate)> eligible
                = new List<(IPEndPoint, LobbySession, bool)>(server.Sessions.Count);
            bool anyLoadingGate = false;
            foreach (var kv in server.Sessions)
            {
                LobbySession otherSession = kv.Value;
                if (ReferenceEquals(otherSession, sender)) continue;
                if (otherSession.PlayerInfo is null) continue;
                if (otherSession.Stage < AppStage.JoinCompleteSent) continue;
                bool loading = otherSession.GameSyncsReceived == 0 && !otherSession.WaitingForFirstFrameAfterReset;
                if (loading) anyLoadingGate = true;
                eligible.Add((kv.Key, otherSession, loading));
            }
            if (eligible.Count == 0) return;

            int gameSyncCount;
            List<(int Offset, int Length, bool IsGameSync)>? msgs = null;
            if (anyLoadingGate)
            {
                var (gs, ranges) = LoadingGate.WalkRanges(appBody, server.Variant);
                gameSyncCount = gs;
                msgs = ranges;
            }
            else
            {
                gameSyncCount = LoadingGate.CountGameSyncsFast(appBody, server.Variant);
            }

            // Batch drained before the current reset closed the gate: its GameSyncs are pre-reset.
            if (gameSyncCount > 0 && Volatile.Read(ref server.ResetEpoch) != drainEpoch)
            {
                var (staleGs, staleRanges) = LoadingGate.WalkRanges(appBody, server.Variant);
                byte[]? strippedStale = LoadingGate.TryBuildGameSyncStrippedBody(appBody, staleRanges);
                Interlocked.Add(ref sender.StaleEpochGameSyncStrips, staleGs);
                if (strippedStale is null)
                {
                    server.Logger.LogWarning(
                        "LobbyUdp[{lobby}] STALE-EPOCH-DROP srcUid={uid} srcSeq={seq} unparseable body dropped ({len}B)",
                        server.LobbyId, srcUid, srcBareSeq, appBody.Length);
                    return;
                }
                if (strippedStale.Length == 0) return;
                appBody = strippedStale;
                gameSyncCount = 0;
                msgs = null;
            }

            byte[]? loadingPhaseBody = null;
            bool loadingPhaseBodyBuilt = false;

            List<Task> sendTasks = new List<Task>(eligible.Count);

            foreach (var (otherEp, otherSession, loading) in eligible)
            {
                byte[] dstBody = appBody;
                int dstGameSyncCount = gameSyncCount;
                long dstUid = otherSession.PlayerInfo!.UID;   // non-null: eligible filter drops null PlayerInfo

                if (gameSyncCount > 0 && loading)
                {
                    if (!loadingPhaseBodyBuilt)
                    {
                        loadingPhaseBody = msgs is null ? null : LoadingGate.TryBuildGameSyncStrippedBody(appBody, msgs);
                        loadingPhaseBodyBuilt = true;
                    }
                    if (loadingPhaseBody is null)
                    {
                        // fall through with raw body: strip parse failed
                    }
                    else if (loadingPhaseBody.Length == 0)
                    {
                        if (!otherSession.LoadingGateLogged)
                        {
                            otherSession.LoadingGateLogged = true;
                            server.Logger.LogInformation(
                                "LobbyUdp[{lobby}] LOADING-GATE first-suppress dstUid={dstUid}",
                                server.LobbyId, dstUid);
                        }
                        continue;
                    }
                    else
                    {
                        dstBody = loadingPhaseBody;
                        dstGameSyncCount = 0;
                        if (!otherSession.LoadingGateLogged)
                        {
                            otherSession.LoadingGateLogged = true;
                            server.Logger.LogInformation(
                                "LobbyUdp[{lobby}] LOADING-GATE first-strip dstUid={dstUid} gs={gs} keep={n}",
                                server.LobbyId, dstUid, gameSyncCount, dstBody.Length);
                        }
                    }
                }

                // RESET-GATE: hold this body while dst is still pre-reset.
                if (otherSession.WaitingForFirstFrameAfterReset)
                {
                    lock (otherSession.RelayLock)
                    {
                        otherSession.PendingForResetReleaseToDst.Enqueue(dstBody);
                    }
                    continue;
                }

                sendTasks.Add(SendRelayBodyAsync(server, sender, otherSession, otherEp, dstBody, srcBareSeq, dstGameSyncCount > 0, drainEpoch, ct));
            }

            if (sendTasks.Count == 0) return;
            await Task.WhenAll(sendTasks);

            if (!sender.FirstRelayLogged)
            {
                sender.FirstRelayLogged = true;
                server.Logger.LogInformation(
                    "LobbyUdp[{lobby}] relay active from {src} (uid={uid}) — first packet forwarded to {n} peer(s)",
                    server.LobbyId, sender.PlayerInfo.Name, sender.PlayerInfo.UID, sendTasks.Count);
            }
        }

        private static async Task SendRelayBodyAsync(
            LobbyUdpServer server,
            LobbySession sender,
            LobbySession dstSession,
            IPEndPoint dstEp,
            byte[] body,
            uint srcBareSeq,
            bool hasGameSyncs,
            int drainEpoch,
            CancellationToken ct)
        {
            if (sender.PlayerInfo is null || dstSession.PlayerInfo is null) return;

            uint nowTick = (uint)Environment.TickCount;
            uint echo = NetGameLinkEcho.Estimate(dstSession, nowTick);
            byte[] frame = NetGameLink.BuildWithBody(NetGameLink.SysFlagApp, body, echo, nowTick);

            string? label = server.Logger.IsEnabled(LogLevel.Trace)
                ? $"RELAY uid={sender.PlayerInfo.UID}→uid={dstSession.PlayerInfo.UID} bodyLen={body.Length} srcSeq={srcBareSeq}"
                : null;

            Func<bool>? gate = null;
            if (hasGameSyncs)
                gate = () => Volatile.Read(ref server.ResetEpoch) == drainEpoch
                          && !dstSession.WaitingForFirstFrameAfterReset;

            try
            {
                uint sentSeq = await EncryptedSender.SendDataAsync(server, dstEp, dstSession, frame, label, ct, sendGate: gate);

                if (sentSeq == 0 && hasGameSyncs)
                {
                    // Gate closed between the relay decision and seq allocation; the GameSyncs
                    // are pre-reset and expendable — deliver the epoch-agnostic remainder only.
                    Interlocked.Increment(ref dstSession.GateRejectedRelays);
                    var (_, ranges) = LoadingGate.WalkRanges(body, server.Variant);
                    byte[]? stripped = LoadingGate.TryBuildGameSyncStrippedBody(body, ranges);
                    if (stripped is not null && stripped.Length > 0)
                    {
                        uint retryTick = (uint)Environment.TickCount;
                        uint retryEcho = NetGameLinkEcho.Estimate(dstSession, retryTick);
                        byte[] retryFrame = NetGameLink.BuildWithBody(NetGameLink.SysFlagApp, stripped, retryEcho, retryTick);
                        await EncryptedSender.SendDataAsync(server, dstEp, dstSession, retryFrame, label, ct);
                    }
                }
            }
            catch (Exception e)
            {
                server.Logger.LogWarning(e, "LobbyUdp[{lobby}] relay to {ep} failed", server.LobbyId, dstEp);
            }
        }
    }
}
