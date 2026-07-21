using System.Net;
using Microsoft.Extensions.Logging;

// Unified reset gate: single close point for every MT_GameReset flavor.
namespace Arcadia.Hosting.Lobby.Reset
{
    public static class ResetGate
    {
        public static int Close(
            LobbyUdpServer server,
            IReadOnlyList<(IPEndPoint Ep, LobbySession Session)> eligible,
            string reason)
        {
            int epoch = Interlocked.Increment(ref server.ResetEpoch);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            foreach (var (_, peerSession) in eligible)
            {
                lock (peerSession.RelayLock)
                {
                    peerSession.ArmWaitFirstFrameBarrierLocked(nowUtc, epoch);
                }
            }
            server.Logger.LogInformation(
                "LobbyUdp[{lobby}] RESET-GATE closed epoch={epoch} peers={n} ({reason})",
                server.LobbyId, epoch, eligible.Count, reason);
            return epoch;
        }

        public static void RecordResetSent(LobbySession session, int epoch, uint seq)
        {
            if (seq == 0) return;
            lock (session.RelayLock)
            {
                if (session.BarrierEpoch != epoch) return;
                session.ResetSeqToDst = seq;
                session.ResetSeqSent = true;
            }
        }
    }
}
