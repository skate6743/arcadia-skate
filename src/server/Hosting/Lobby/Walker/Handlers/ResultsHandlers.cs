using System.Buffers.Binary;
using Arcadia.EA;
using Arcadia.Hosting.Lobby.Protocol;
using Microsoft.Extensions.Logging;

// MT_GameResults, MT_GameComplete, MT_GameFinalResults: RELAYED + server-side stash for post-challenge aggregation.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class ResultsHandlers
    {
        public static bool HandleGameResults(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;
            long uid = ctx.Session.PlayerInfo?.UID ?? 0;

            if (ctx.Server.Variant == GameVariant.Skate1)
            {
                int bodyLen = 16;
                if (ctx.MsgOffset + 1 + bodyLen > ctx.UserBodyLen) return false;
                int msgBase = ctx.PayloadBase + ctx.MsgOffset;
                uint eventTime = BinaryPrimitives.ReadUInt32BigEndian(ctx.Work.AsSpan(msgBase + 1, 4));
                int score = BinaryPrimitives.ReadInt32BigEndian(ctx.Work.AsSpan(msgBase + 5, 4));
                int finishReason = BinaryPrimitives.ReadInt32BigEndian(ctx.Work.AsSpan(msgBase + 9, 4));
                int ranking = BinaryPrimitives.ReadInt32BigEndian(ctx.Work.AsSpan(msgBase + 13, 4));
                ctx.Logger.LogInformation(
                    "LobbyUdp[{lobby}] APP-MSG GameResults(Skate1) {ep} uid={uid} eventTime={et} score={s} finishReason={fr} ranking={r}",
                    ctx.LobbyId, ctx.Ep, uid, eventTime, score, finishReason, ranking);
                if (uid != 0)
                    ctx.Server.OnGameResultsFromPeer(uid, eventTime, score, finishReason, ranking, false);
                msgLen = 1 + bodyLen;
                return true;
            }
            else
            {
                int bodyLen = 15;
                if (ctx.MsgOffset + 1 + bodyLen > ctx.UserBodyLen) return false;
                int msgBase = ctx.PayloadBase + ctx.MsgOffset;
                uint eventTimeBits = BinaryPrimitives.ReadUInt32BigEndian(ctx.Work.AsSpan(msgBase + 1, 4));
                int score = BinaryPrimitives.ReadInt32BigEndian(ctx.Work.AsSpan(msgBase + 5, 4));
                int finishReason = BinaryPrimitives.ReadInt32BigEndian(ctx.Work.AsSpan(msgBase + 9, 4));
                short ranking = BinaryPrimitives.ReadInt16BigEndian(ctx.Work.AsSpan(msgBase + 13, 2));
                bool isPlayersChoice = ctx.Work[msgBase + 15] != 0;
                ctx.Logger.LogInformation(
                    "LobbyUdp[{lobby}] APP-MSG GameResults {ep} uid={uid} eventTime={et} score={s} finishReason={fr} ranking={r} playersChoice={pc}",
                    ctx.LobbyId, ctx.Ep, uid, BitConverter.Int32BitsToSingle((int)eventTimeBits), score, finishReason, ranking, isPlayersChoice);
                if (uid != 0)
                    ctx.Server.OnGameResultsFromPeer(uid, eventTimeBits, score, finishReason, ranking, isPlayersChoice);
                msgLen = 1 + bodyLen;
                return true;
            }
        }

        public static bool HandleGameComplete(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 1;
            consumeOnly = false;
            long uid = ctx.Session.PlayerInfo?.UID ?? 0;
            ctx.Logger.LogInformation("LobbyUdp[{lobby}] APP-MSG GameComplete {ep} uid={uid}",
                ctx.LobbyId, ctx.Ep, uid);
            if (uid != 0) ctx.Server.OnGameCompleteFromPeer(uid);
            return true;
        }

        public static bool HandleGameFinalResultsSkate1(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;
            int needed = 1 + Sk8MessageLayout.FinalResultsSkate1FixedBodySize;
            if (ctx.MsgOffset + needed > ctx.UserBodyLen) return false;
            msgLen = needed;
            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameFinalResults(Skate1) {ep} uid={uid} fixed=160B",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0);
            return true;
        }

        public static bool HandleGameFinalResultsSkate2(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;
            if (ctx.MsgOffset + 1 + 4 > ctx.UserBodyLen) return false;
            int playerCount = (int)BinaryPrimitives.ReadUInt32BigEndian(
                ctx.Work.AsSpan(ctx.PayloadBase + ctx.MsgOffset + 1, 4));
            int stride = Sk8MessageLayout.FinalResultsPlayerStride;
            if (playerCount < 0 || playerCount > Sk8MessageLayout.FinalResultsSkate2MaxPlayers
                || ctx.MsgOffset + 1 + 4 + playerCount * stride > ctx.UserBodyLen)
                return false;
            msgLen = 1 + 4 + playerCount * stride;
            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameFinalResults {ep} uid={uid} players={n}",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, playerCount);
            return true;
        }
    }
}
