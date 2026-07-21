using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Arcadia.Hosting.Lobby.Protocol;
using Microsoft.Extensions.Logging;

// 1Hz LOBBY-SNAPSHOT loop + per-session TALLY + GS-RATE-DROP / WAIT-FF-STUCK / OOO-GAP-STUCK alarms.
namespace Arcadia.Hosting.Lobby.Diagnostics
{
    public class LobbyDiagnostics
    {
        private const int TallyEmitEveryNMsgs = 200;

        private readonly ConcurrentDictionary<IPEndPoint, LobbySession> _sessions;
        private readonly int _lobbyId;
        private readonly ILogger _logger;

        public LobbyDiagnostics(ConcurrentDictionary<IPEndPoint, LobbySession> sessions, int lobbyId, ILogger logger)
        {
            _sessions = sessions;
            _lobbyId = lobbyId;
            _logger = logger;
        }

        public async Task RunSnapshotLoopAsync(CancellationToken ct)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }

                try { EmitSnapshot(startedAt); }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "LobbyUdp[{lobby}] snapshot loop error", _lobbyId);
                }
            }
        }

        public void MaybeEmitTally(IPEndPoint ep, LobbySession session)
        {
            if (session.AppMsgsReceived - session.LastTallyEmitMsgCount < TallyEmitEveryNMsgs) return;

            long windowGs = session.GameSyncsReceived - session.LastTallyGameSyncs;
            long windowRetx = session.RetransmitsSeen - session.LastTallyRetx;
            session.LastTallyEmitMsgCount = session.AppMsgsReceived;
            session.LastTallyGameSyncs = session.GameSyncsReceived;
            session.LastTallyRetx = session.RetransmitsSeen;

            double elapsed = (DateTimeOffset.UtcNow - session.SessionStart).TotalSeconds;
            _logger.LogInformation(
                "LobbyUdp[{lobby}] TALLY {ep} uid={uid} stage={stage} elapsedSec={elapsed:F1} appMsgs={am} gs={gs}(+{gsd}) retx={rt}(+{rtd}) oooOpen={oo} oooClose={oc} forceSkips={fs} parkDrops={pd} cmiss={cm} nacks={nu} redSubs={rs} staleEpoch={se} oopRec={or} staleWrap={sw} oooSize={oss} clientAck={cas} sendSeq={ss} gateRej={gr} staleGsStrips={sgs} stragglerStrips={pss}",
                _lobbyId, ep, session.PlayerInfo?.UID ?? 0, session.Stage, elapsed,
                session.AppMsgsReceived, session.GameSyncsReceived, windowGs,
                session.RetransmitsSeen, windowRetx,
                session.OooGapOpens, session.OooGapCloses,
                session.OooGapForceSkips, session.OrderedPendingRelayDrops,
                session.CatchupCacheMisses, session.NacksSent,
                session.RedundancySubsSent, session.StaleInEpochPackets, session.OopRecoveredPackets, session.StaleAcrossWrapPackets,
                session.ClientOooSeqs.Count, session.ClientAckSeq, session.ServerDataSeq,
                session.GateRejectedRelays, session.StaleEpochGameSyncStrips, session.PreResetStragglerStrips);
        }

        public void LogSessionClose(IPEndPoint ep, LobbySession session, string reason)
        {
            double elapsed = (DateTimeOffset.UtcNow - session.SessionStart).TotalSeconds;
            _logger.LogInformation(
                "LobbyUdp[{lobby}] SESSION-CLOSE {ep} uid={uid} reason={reason} stage={stage} elapsedSec={elapsed:F1} appMsgs={am} gs={gs} retx={rt} oooOpen={oo} oooClose={oc} nacks={nu} clientAck={cas} sendSeq={ss}",
                _lobbyId, ep, session.PlayerInfo?.UID ?? 0, reason, session.Stage, elapsed,
                session.AppMsgsReceived, session.GameSyncsReceived, session.RetransmitsSeen,
                session.OooGapOpens, session.OooGapCloses, session.NacksSent,
                session.ClientAckSeq, session.ServerDataSeq);
        }

        private void EmitSnapshot(DateTimeOffset startedAt)
        {
            if (_sessions.IsEmpty) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            double elapsedSec = (now - startedAt).TotalSeconds;
            KeyValuePair<IPEndPoint, LobbySession>[] snapshot = _sessions.ToArray();

            StringBuilder perPeer = new StringBuilder();
            foreach (var (ep, s) in snapshot)
            {
                long gsDelta = s.GameSyncsReceived - s.LastSnapshotGameSyncs;
                long appDelta = s.AppMsgsReceived - s.LastSnapshotAppMsgs;
                s.LastSnapshotGameSyncs = s.GameSyncsReceived;
                s.LastSnapshotAppMsgs = s.AppMsgsReceived;

                long lastRxAgoMs = s.LastAppMsgRxAt == default ? -1 : (long)(now - s.LastAppMsgRxAt).TotalMilliseconds;
                long oooGapAgeMs = s.OooGapOpenedAt.HasValue
                    ? (long)(now - s.OooGapOpenedAt.Value).TotalMilliseconds : -1;
                long waitFirstFrameAgeMs = s.WaitingForFirstFrameAfterResetSetAt.HasValue
                    ? (long)(now - s.WaitingForFirstFrameAfterResetSetAt.Value).TotalMilliseconds : -1;

                perPeer.Append("\n  uid=").Append(s.PlayerInfo?.UID ?? 0)
                       .Append(" name=").Append(s.PlayerInfo?.Name ?? "?")
                       .Append(" stage=").Append(s.Stage)
                       .Append(" ep=").Append(ep)
                       .Append(" gs=").Append(s.GameSyncsReceived).Append("(+").Append(gsDelta).Append("/s)")
                       .Append(" appMsg=").Append(s.AppMsgsReceived).Append("(+").Append(appDelta).Append("/s)")
                       .Append(" lastRxAgoMs=").Append(lastRxAgoMs)
                       .Append(" oooSize=").Append(s.ClientOooSeqs.Count)
                       .Append(" oooGapAgeMs=").Append(oooGapAgeMs)
                       .Append(" nacks=").Append(s.NacksSent)
                       .Append(" clientAck=").Append(s.ClientAckSeq)
                       .Append(" sendSeq=").Append(s.ServerDataSeq)
                       .Append(" waitFirstFrame=").Append(s.WaitingForFirstFrameAfterReset ? "Y" : "N")
                       .Append(" waitFirstFrameAgeMs=").Append(waitFirstFrameAgeMs);

                if (gsDelta == 0
                    && s.Stage >= AppStage.JoinCompleteSent
                    && s.GameSyncsReceived > 0
                    && lastRxAgoMs >= 0 && lastRxAgoMs < 5000)
                {
                    _logger.LogWarning(
                        "LobbyUdp[{lobby}] GS-RATE-DROP ep={ep} uid={uid} stage={stage} gsTotal={gs} gsDelta=+0/s appDelta=+{ad}/s lastRxAgoMs={lrx} oooSize={oo} clientAck={ca} sendSeq={ss} waitFF={wff} waitFFAgeMs={wffa}",
                        _lobbyId, ep, s.PlayerInfo?.UID ?? 0, s.Stage, s.GameSyncsReceived, appDelta, lastRxAgoMs,
                        s.ClientOooSeqs.Count, s.ClientAckSeq, s.ServerDataSeq,
                        s.WaitingForFirstFrameAfterReset, waitFirstFrameAgeMs);
                }

                if (s.WaitingForFirstFrameAfterReset && waitFirstFrameAgeMs >= 30_000)
                {
                    _logger.LogWarning(
                        "LobbyUdp[{lobby}] WAIT-FF-STUCK ep={ep} uid={uid} ageMs={age} gs={gs} gsDelta=+{gsd}/s",
                        _lobbyId, ep, s.PlayerInfo?.UID ?? 0, waitFirstFrameAgeMs,
                        s.GameSyncsReceived, gsDelta);
                }

                if (s.OooGapOpenedAt.HasValue && oooGapAgeMs >= 5_000 && oooGapAgeMs < 15_000)
                {
                    _logger.LogInformation(
                        "LobbyUdp[{lobby}] OOO-GAP-STUCK ep={ep} uid={uid} ageMs={age} oooSize={oo} clientAck={ca} headAtOpen={head}",
                        _lobbyId, ep, s.PlayerInfo?.UID ?? 0, oooGapAgeMs,
                        s.ClientOooSeqs.Count, s.ClientAckSeq, s.OooGapHeadAtOpen);
                }
            }

            _logger.LogInformation(
                "LobbyUdp[{lobby}] LOBBY-SNAPSHOT t={t:F1}s peers={n}{perPeer}",
                _lobbyId, elapsedSec, snapshot.Length, perPeer.ToString());
        }
    }
}
