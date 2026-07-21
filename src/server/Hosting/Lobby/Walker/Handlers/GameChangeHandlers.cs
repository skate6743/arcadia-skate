using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

// MT_GameRequestChange: relayed verbatim + triggers the server-side map-change broadcaster.
namespace Arcadia.Hosting.Lobby.Walker.Handlers
{
    public static class GameChangeHandlers
    {
        public static bool HandleRequestChange(WalkContext ctx, out int msgLen, out bool consumeOnly)
        {
            msgLen = 0;
            consumeOnly = false;

            if (ctx.MsgOffset + 1 + 12 > ctx.UserBodyLen) return false;
            int msgBase = ctx.PayloadBase + ctx.MsgOffset;
            int reqType = BinaryPrimitives.ReadInt32BigEndian(ctx.Work.AsSpan(msgBase + 1, 4));
            ulong reqKey = BinaryPrimitives.ReadUInt64BigEndian(ctx.Work.AsSpan(msgBase + 5, 8));

            ctx.Logger.LogInformation(
                "LobbyUdp[{lobby}] APP-MSG GameRequestChange {ep} uid={uid} type={ct} key=0x{key:X16}",
                ctx.LobbyId, ctx.Ep, ctx.Session.PlayerInfo?.UID ?? 0, reqType, reqKey);
            ctx.Server.OnMapChangeRequested(reqType, reqKey);
            msgLen = 1 + 12;
            return true;
        }
    }
}
