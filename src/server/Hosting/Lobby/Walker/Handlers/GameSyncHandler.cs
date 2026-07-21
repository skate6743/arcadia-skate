using System.Buffers.Binary;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Microsoft.Extensions.Logging;

// MT_GameSync: Skate 1 mFrame==1 / Skate 2 FirstFrame=1 reset barrier clear.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class GameSyncHandler
    {
        public static bool HandleSkate1(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (ctx.MsgOffset + 1 + 9 > ctx.UserBodyLen)
                return false;

            uint mFrame = BinaryPrimitives.ReadUInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 1, 4));
            byte cmdCount = ctx.Work[ctx.PayloadBase + ctx.MsgOffset + 1 + 8];

            int cur = ctx.MsgOffset + 1 + 9;
            for (int c = 0; c < cmdCount; c++)
            {
                if (cur + 3 > ctx.UserBodyLen) return false;
                byte dataSize = ctx.Work[ctx.PayloadBase + cur + 2];
                cur += 3 + dataSize;
                if (cur > ctx.UserBodyLen) return false;
            }

            msgLen = cur - ctx.MsgOffset;
            ctx.Session.GameSyncsReceived++;

            if (ctx.Session.WaitingForFirstFrameAfterReset)
            {
                // Clear on the first low mFrame (post-reset epoch restarts at 1); a high mFrame is old-epoch.
                // The datagram must also ack the reset we sent, or a young previous epoch false-clears.
                bool ackCoversReset = ctx.Session.ResetSeqSent && ctx.DatagramAck >= ctx.Session.ResetSeqToDst;
                if (mFrame <= LobbySession.PostResetFrameWindow && ackCoversReset)
                {
                    ctx.Session.WaitingForFirstFrameAfterReset = false;
                    ctx.Session.WaitingForFirstFrameAfterResetSetAt = null;
                    ctx.Session.FirstFrameSrcSeq = ctx.SrcBareSeq;
                    ctx.Session.FirstFrameSrcSeqKnown = true;
                    ctx.Logger.LogInformation(
                        "LobbyUdp[{lobby}] Skate1 first post-reset GameSync mFrame={f} from uid={uid} srcSeq={seq} epoch={epoch} ack={ack}≥reset={rs} — filter cleared",
                        ctx.LobbyId, mFrame, ctx.Session.PlayerInfo?.UID ?? 0, ctx.SrcBareSeq,
                        ctx.Session.BarrierEpoch, ctx.DatagramAck, ctx.Session.ResetSeqToDst);
                }
                else
                {
                    DateTimeOffset? setAt = ctx.Session.WaitingForFirstFrameAfterResetSetAt;
                    if (setAt.HasValue
                        && (DateTimeOffset.UtcNow - setAt.Value).TotalSeconds >= LobbySession.WaitFirstFrameForceClearSeconds)
                    {
                        ctx.Session.WaitingForFirstFrameAfterReset = false;
                        ctx.Session.WaitingForFirstFrameAfterResetSetAt = null;
                        ctx.Logger.LogWarning(
                            "LobbyUdp[{lobby}] Skate1 WAIT-FF-TIMEOUT uid={uid} srcSeq={seq} mFrame={f} ackCovers={ac} — accepting GameSync",
                            ctx.LobbyId, ctx.Session.PlayerInfo?.UID ?? 0, ctx.SrcBareSeq, mFrame, ackCoversReset);
                    }
                    else
                    {
                        consumeOnly = true;
                    }
                }
            }
            else if (ctx.Session.FirstFrameSrcSeqKnown && ctx.SrcBareSeq < ctx.Session.FirstFrameSrcSeq)
            {
                ctx.Session.PreResetStragglerStrips++;
                consumeOnly = true;
            }

            return true;
        }

        public static bool HandleSkate2(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (!GameSyncMessage.TryParse(ctx.Work, ctx.PayloadBase + ctx.MsgOffset, out GameSyncHeader hdr))
                return false;

            msgLen = hdr.TotalMessageBytes;
            ctx.Session.GameSyncsReceived++;

            if (ctx.Session.WaitingForFirstFrameAfterReset)
            {
                // FirstFrame only counts once this datagram acks the reset we sent: a stale
                // FirstFrame from the previous epoch cannot clear the current barrier.
                bool ackCoversReset = ctx.Session.ResetSeqSent && ctx.DatagramAck >= ctx.Session.ResetSeqToDst;
                if (hdr.FirstFrame && ackCoversReset)
                {
                    ctx.Session.WaitingForFirstFrameAfterReset = false;
                    ctx.Session.WaitingForFirstFrameAfterResetSetAt = null;
                    ctx.Session.FirstFrameSrcSeq = ctx.SrcBareSeq;
                    ctx.Session.FirstFrameSrcSeqKnown = true;
                    ctx.Logger.LogInformation(
                        "LobbyUdp[{lobby}] FirstFrame seen from uid={uid} srcSeq={seq} epoch={epoch} ack={ack}≥reset={rs} — filter cleared",
                        ctx.LobbyId, ctx.Session.PlayerInfo?.UID ?? 0, ctx.SrcBareSeq,
                        ctx.Session.BarrierEpoch, ctx.DatagramAck, ctx.Session.ResetSeqToDst);
                }
                else
                {
                    DateTimeOffset? setAt = ctx.Session.WaitingForFirstFrameAfterResetSetAt;
                    if (setAt.HasValue
                        && (DateTimeOffset.UtcNow - setAt.Value).TotalSeconds >= LobbySession.WaitFirstFrameForceClearSeconds)
                    {
                        ctx.Session.WaitingForFirstFrameAfterReset = false;
                        ctx.Session.WaitingForFirstFrameAfterResetSetAt = null;
                        ctx.Logger.LogWarning(
                            "LobbyUdp[{lobby}] WAIT-FF-TIMEOUT uid={uid} srcSeq={seq} firstFrame={ff} ackCovers={ac} — accepting GameSync",
                            ctx.LobbyId, ctx.Session.PlayerInfo?.UID ?? 0, ctx.SrcBareSeq, hdr.FirstFrame, ackCoversReset);
                    }
                    else
                    {
                        consumeOnly = true;
                    }
                }
            }
            else if (ctx.Session.FirstFrameSrcSeqKnown && ctx.SrcBareSeq < ctx.Session.FirstFrameSrcSeq)
            {
                // Client-retransmitted pre-reset frame arriving after the clear; its own seq
                // space is the epoch fence (acks may be re-stamped on retransmit, seqs never).
                ctx.Session.PreResetStragglerStrips++;
                consumeOnly = true;
            }

            // Count only relayed (non-filtered) GameSyncs; feeds MT_GameRemovePlayer.mLastFrame.
            if (!consumeOnly) ctx.Session.LockstepFrame++;
            return true;
        }
    }
}
