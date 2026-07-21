using System.Net;
using Microsoft.Extensions.Logging;

// Cumulative-ack frontier for inbound DATA; echoed as the ack on every outbound. OOO arrivals queue in ClientOooSeqs.
namespace Arcadia.Hosting.Lobby.Reliability
{
    public static class InboundAckTracker
    {
        public readonly record struct AdvanceResult(bool FrontierAdvanced, bool GapOpened, uint? FirstMissingSeq);

        public static AdvanceResult RecordInboundSeq(LobbySession session, uint bareSeq, IPEndPoint ep, ILogger logger, int lobbyId, bool hasAppPayload = true)
        {
            uint prevAck = session.ClientAckSeq;

            if (!session.ClientAckInitialized)
            {
                session.ClientAckSeq = bareSeq;
                session.ClientAckInitialized = true;
                session.LastRelayedSrcSeq = bareSeq - 1;
                session.LastRelayedSrcSeqInitialized = true;
                logger.LogInformation(
                    "LobbyUdp[{lobby}] RX-INBOUND ep={ep} uid={uid} seq={seq} verdict=INIT newAck={na}",
                    lobbyId, ep, session.PlayerInfo?.UID ?? 0, bareSeq, session.ClientAckSeq);
                return new AdvanceResult(true, false, null);
            }

            if (bareSeq == session.ClientAckSeq + 1)
            {
                session.ClientAckSeq = bareSeq;
                int drained = 0;
                while (session.ClientOooSeqs.Count > 0)
                {
                    uint lowest = session.ClientOooSeqs.Min;
                    if (lowest != session.ClientAckSeq + 1) break;
                    session.ClientOooSeqs.Remove(lowest);
                    session.ClientAckSeq = lowest;
                    drained++;
                }
                if (drained > 0)
                {
                    session.OooGapCloses += drained;
                    logger.LogInformation(
                        "LobbyUdp[{lobby}] OOO-GAP-CLOSE {ep} uid={uid} drained={n} newFrontier={fr} oooRemaining={ooo}",
                        lobbyId, ep, session.PlayerInfo?.UID ?? 0,
                        drained, session.ClientAckSeq, session.ClientOooSeqs.Count);
                }
                if (session.ClientOooSeqs.Count == 0)
                {
                    session.OooGapOpenedAt = null;
                    session.OooGapHeadAtOpen = 0;
                }
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace(
                        "LobbyUdp[{lobby}] RX-INBOUND ep={ep} uid={uid} seq={seq} verdict=ADVANCE prevAck={pa} newAck={na} oooDrained={d} oooRemaining={ooo}",
                        lobbyId, ep, session.PlayerInfo?.UID ?? 0, bareSeq, prevAck, session.ClientAckSeq, drained, session.ClientOooSeqs.Count);
                }
                return new AdvanceResult(true, false, null);
            }

            if (bareSeq > session.ClientAckSeq)
            {
                uint firstMissing = session.ClientAckSeq + 1;
                long gapWidth = (long)bareSeq - session.ClientAckSeq - 1;
                session.OooGapOpens++;

                if (session.ClientOooSeqs.Count == 0
                    || session.OooGapHeadAtOpen != firstMissing
                    || session.OooGapOpenedAt is null)
                {
                    session.OooGapOpenedAt = DateTimeOffset.UtcNow;
                    session.OooGapHeadAtOpen = firstMissing;
                }

                logger.LogInformation(
                    "LobbyUdp[{lobby}] OOO-GAP-OPEN {ep} uid={uid} expectedNext={exp} gotSeq={got} missing={miss} oooSetSize={ooo}",
                    lobbyId, ep, session.PlayerInfo?.UID ?? 0,
                    firstMissing, bareSeq, gapWidth, session.ClientOooSeqs.Count + 1);

                session.ClientOooSeqs.Add(bareSeq);
                if (session.ClientOooSeqs.Count > LobbySession.ClientOooMaxSize)
                {
                    logger.LogWarning(
                        "LobbyUdp[{lobby}] OOO seq set hit cap={cap} ack stalled at {ack}, newest seq={new}",
                        lobbyId, LobbySession.ClientOooMaxSize, session.ClientAckSeq, bareSeq);
                    session.ClientOooSeqs.Remove(session.ClientOooSeqs.Min);
                }
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "LobbyUdp[{lobby}] RX-INBOUND ep={ep} uid={uid} seq={seq} verdict=OOO ack={ack} oooSet=[{ooo}]",
                        lobbyId, ep, session.PlayerInfo?.UID ?? 0, bareSeq, session.ClientAckSeq,
                        string.Join(",", session.ClientOooSeqs));
                }
                return new AdvanceResult(false, true, firstMissing);
            }

            session.RetransmitsSeen++;
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "LobbyUdp[{lobby}] RX-INBOUND ep={ep} uid={uid} seq={seq} verdict={verdict} ack={ack} retransmitsSeen={r}",
                    lobbyId, ep, session.PlayerInfo?.UID ?? 0, bareSeq,
                    hasAppPayload ? "PAYLOAD-PAIR" : "DUPLICATE",
                    session.ClientAckSeq, session.RetransmitsSeen);
            }
            return new AdvanceResult(false, false, null);
        }
    }
}
